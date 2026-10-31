"""Create reproducible flat Babelfin ZIPs, MD5/SHA-256 sidecars and optional manifests.

Importing this module never builds or writes. Supply --timestamp with the final
source commit's ISO 8601 committer time, or set SOURCE_DATE_EPOCH to that time.
Keep release-config.json free of static timestamps. --dll packages an existing
build without rebuilding the plugin. The .NET SDK reads its metadata without executing it.
Only --source-url enables a local manifest.json;
keep that checksum manifest out of main and publish it as a catalog release asset.
--repository-url is that catalog asset URL; --project-url identifies the source
project in the catalog and build report. Release options may come from --config
JSON. Configured URLs are planned publication targets, not availability checks.
Check repository and release-asset access before sharing their URLs.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1]
PROJECT = Path("plugin/src/Jellyfin.Plugin.Babelfin/Jellyfin.Plugin.Babelfin.csproj")
GUID = "dc7f86a9-90f3-4282-93d9-424fb4c64b4d"
DLL_NAME = "Jellyfin.Plugin.Babelfin.dll"
OWNER = "WizardBal"
TARGET_ABI = "12.0.0.0"
OVERVIEW = "The right audio and subtitles, for every person, in every library"
DESCRIPTION = (
    "Babelfin chooses audio and subtitles from each person's preferences. "
    "Choose languages, library defaults, and show or movie overrides; "
    "leave choices on Video default to keep Jellyfin's selections."
)
DEFAULT_CHANGELOG = (
    "Track choices across autoplay and native players. In \"Use Babelfin's choice when playing the next episode\" mode, tracks that an app copies from the previous episode are replaced with Babelfin's choice, including tracks of the same language and kind. Apps that start playback without naming tracks, such as the Jellyfin Android app's built-in player, keep Babelfin's audio and subtitle choices instead of Jellyfin's default-flagged audio track or an equally ranked subtitle track. Jellyfin's \"Set audio track based on previous item\" and \"Set subtitle track based on previous item\" are turned off by default for people Babelfin chooses for, and their original values are restored when the switch, the person or the server is turned off, or when Babelfin is uninstalled. A track change made during playback is remembered once it has stayed selected for 10 seconds. The Android app is recognised as \"Jellyfin for Android\". Person and server settings are converted to a newer format on first start; download all settings before installing. Tested with Jellyfin 12.4 and the Jellyfin Android app on Google Pixel 11 Pro XL. Jellyfin Desktop 2.0.0 was also tested on Linux. Automated integration tests use Jellyfin Web on a disposable Jellyfin 12.2 test instance. Android TV, Swiftfin, Streamyfin and physical TVs have not been tested."
)
VERSION_RE = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+", re.ASCII)


class PackageError(ValueError):
    """An invalid or incomplete release input."""


def read_version(csproj: Path = ROOT / PROJECT) -> str:
    """Read literal XML properties, never comments, expressions, or paths."""
    root = ET.parse(csproj).getroot()
    values = {name: [] for name in ("Version", "AssemblyVersion", "FileVersion")}
    for group in root:
        if group.tag.rsplit("}", 1)[-1] != "PropertyGroup":
            continue
        for item in group:
            name = item.tag.rsplit("}", 1)[-1]
            if name in values:
                if group.get("Condition") or item.get("Condition") or len(item):
                    raise PackageError(f"<{name}> must be an unconditional literal")
                values[name].append((item.text or "").strip())
    if len(values["Version"]) != 1:
        raise PackageError("Exactly one <Version> is required")
    version = values["Version"][0]
    if not VERSION_RE.fullmatch(version) or any(int(part) > 65534 for part in version.split(".")):
        raise PackageError("Version must contain four assembly-compatible decimal parts (0..65534)")
    if any(str(int(part)) != part for part in version.split(".")):
        raise PackageError("Version parts must use canonical decimal spelling")
    for name in ("AssemblyVersion", "FileVersion"):
        if len(values[name]) > 1 or (values[name] and values[name][0] != version):
            raise PackageError(f"<{name}> must match <Version> exactly")
    return version


def release_time(value: str | None = None, environ: dict | None = None) -> datetime:
    """Require a reproducible release time, normalized to whole UTC seconds."""
    env = os.environ if environ is None else environ
    if value is not None:
        try:
            stamp = datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError as exc:
            raise PackageError("--timestamp must be an ISO 8601 timestamp with a timezone") from exc
        if stamp.tzinfo is None:
            raise PackageError("--timestamp must include a timezone")
    else:
        epoch = env.get("SOURCE_DATE_EPOCH", "")
        if not re.fullmatch(r"[0-9]+", epoch):
            raise PackageError("Supply --timestamp or SOURCE_DATE_EPOCH for reproducible release metadata")
        try:
            stamp = datetime.fromtimestamp(int(epoch), timezone.utc)
        except (OverflowError, OSError, ValueError) as exc:
            raise PackageError("SOURCE_DATE_EPOCH is out of range") from exc
    stamp = stamp.astimezone(timezone.utc).replace(microsecond=0)
    if not 1980 <= stamp.year <= 2107:
        raise PackageError("Release time must be within ZIP's 1980..2107 date range")
    return stamp


def public_url(value: str | None, label: str) -> str | None:
    if value is None:
        return None
    if not value or any(ord(char) <= 32 or ord(char) == 127 for char in value):
        raise PackageError(f"{label} must be a nonempty absolute HTTP(S) URL")
    try:
        parts = urlsplit(value)
        port = parts.port
    except ValueError as exc:
        raise PackageError(f"{label} is not a valid URL") from exc
    if parts.scheme not in ("http", "https") or not parts.hostname or parts.username or parts.password or parts.fragment:
        raise PackageError(f"{label} must be absolute HTTP(S), without credentials or a fragment")
    if port is not None and not 1 <= port <= 65535:
        raise PackageError(f"{label} has an invalid port")
    return value


def validate_asset_line_endings(root: Path) -> None:
    project = root / PROJECT
    tree = ET.parse(project)
    for item in tree.iter():
        if item.tag.rsplit("}", 1)[-1] != "EmbeddedResource":
            continue
        for pattern in (item.get("Include") or "").split(";"):
            if not pattern:
                continue
            pattern = pattern.replace("\\", "/")
            for path in project.parent.glob(pattern):
                if path.is_file() and b"\r\n" in path.read_bytes():
                    raise PackageError(f"Embedded resource {path} contains CRLF line endings; build from a clean checkout.")


def validate_translations(root: Path) -> None:
    assets = root / PROJECT.parent / "Assets"
    loader = (assets / "i18n.js").read_text(encoding="utf-8-sig")
    match = re.search(r"var SHIPPED\s*=\s*\[([^\]]*)\]", loader)
    if match is None:
        raise PackageError("i18n.js has no SHIPPED locale list")
    shipped = sorted(re.findall(r"'([A-Za-z-]+)'", match[1]))
    if not shipped or len(shipped) != len(set(shipped)):
        raise PackageError("SHIPPED must list distinct locales")
    for folder in ("i18n", "i18n-admin"):
        actual = sorted(path.stem for path in (assets / folder).glob("*.json"))
        if actual != shipped:
            raise PackageError(f"{folder} locales differ from i18n.js SHIPPED: {actual}")


def find_dotnet() -> str:
    found = shutil.which("dotnet")
    if found:
        return found
    fallback = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "dotnet/dotnet.exe"
    if os.name == "nt" and fallback.is_file():
        return str(fallback)
    raise PackageError("dotnet not found; install the .NET 10 SDK or supply --dll")


def source_commit(csproj: Path) -> str:
    """Require clean Git identity in a checkout; use source records only outside Git."""
    csproj = csproj.resolve()
    in_checkout = False
    for folder in csproj.parents:
        try:
            (folder / ".git").lstat()
        except FileNotFoundError:
            continue
        except OSError as exc:
            raise PackageError("Git source identity could not be verified") from exc
        in_checkout = True
        break
    if in_checkout:
        try:
            found = subprocess.run(["git", "-C", str(csproj.parent), "rev-parse", "--show-toplevel"],
                                   capture_output=True, text=True, encoding="utf-8", check=True)
            root = Path(found.stdout.strip())
            relative = csproj.relative_to(root).as_posix()
            subprocess.run(["git", "-C", str(root), "ls-files", "--error-unmatch", "--", relative],
                           capture_output=True, check=True)
            status = subprocess.run(["git", "-C", str(root), "status", "--porcelain", "--untracked-files=all"],
                                    capture_output=True, check=True)
            if status.stdout.strip():
                raise PackageError("Release packaging requires a clean committed source tree")
            return subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
        except PackageError:
            raise
        except FileNotFoundError as exc:
            raise PackageError("Git is required to verify source identity in an existing checkout") from exc
        except (subprocess.CalledProcessError, OSError, ValueError) as exc:
            raise PackageError("Git source identity could not be verified") from exc
    for folder in csproj.parents:
        descriptor = folder / "SOURCE.json"
        if not descriptor.is_file():
            continue
        try:
            data = json.loads(descriptor.read_text(encoding="utf-8"))
            commit = data["commit"]
            if (not isinstance(commit, str) or not re.fullmatch(r"[0-9a-f]{40}", commit)
                    or commit == "0" * 40 or data["version"] != read_version(csproj)):
                raise ValueError()
            return commit
        except (KeyError, TypeError, ValueError, OSError) as exc:
            raise PackageError("The corresponding-source identity record is invalid") from exc
    raise PackageError("Build from a clean Git checkout or a corresponding-source archive with SOURCE.json")


def read_dll_identity(dll: Path, content: bytes) -> dict:
    """Inspect the exact packaged bytes through metadata APIs, never execute the DLL."""
    dotnet = find_dotnet()
    project = ROOT / "tools/release-identity/ReleaseIdentity.csproj"
    with tempfile.TemporaryDirectory(prefix="babelfin-dll-identity-") as temporary:
        folder = Path(temporary)
        supplied = folder / "input.dll"
        supplied.write_bytes(content)
        build = folder / "build"
        compiled = subprocess.run([dotnet, "build", str(project), "-c", "Release", "--artifacts-path", str(build),
                                   "--disable-build-servers", "--warnaserror", "-v:q"], capture_output=True, text=True)
        if compiled.returncode:
            raise PackageError("The metadata inspection tool could not be built; use the .NET SDK selected by global.json")
        tool = build / "bin/ReleaseIdentity/release/ReleaseIdentity.dll"
        inspected = subprocess.run([dotnet, str(tool), str(supplied)], capture_output=True, text=True)
        if inspected.returncode:
            raise PackageError("The supplied DLL is not a readable managed assembly")
        try:
            result = json.loads(inspected.stdout)
            if not isinstance(result, dict):
                raise ValueError()
            return result
        except (ValueError, TypeError) as exc:
            raise PackageError("The supplied DLL identity could not be read") from exc


def verify_dll(dll: Path, csproj: Path, version: str, content: bytes) -> str:
    commit = source_commit(csproj)
    if not re.fullmatch(r"[0-9a-f]{40}", commit) or commit == "0" * 40:
        raise PackageError("The expected source commit is invalid")
    identity = read_dll_identity(dll, content)
    expected = {"assembly_name": DLL_NAME[:-4], "assembly_version": version, "file_version": version,
                "assembly_file_version": version, "informational_version": version + "+" + commit,
                "sha256": hashlib.sha256(content).hexdigest()}
    for key, value in expected.items():
        if identity.get(key) != value:
            raise PackageError("The supplied DLL's " + key.replace("_", " ") + " does not match the release source")
    return commit


def build_plugin(root: Path, build_dir: Path) -> Path:
    """Build only when executing main without --dll; never on module import."""
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    subprocess.run([find_dotnet(), "build", str(root / PROJECT), "-c", "Release", "-v", "q", "-nologo", "-o", str(build_dir)], check=True, env=env, cwd=root / "plugin")
    if sorted(path.name for path in build_dir.iterdir()) != [DLL_NAME]:
        raise PackageError("Build output must contain only the plugin DLL")
    return build_dir / DLL_NAME


def metadata(version: str, stamp: datetime, changelog: str) -> dict:
    return {
        "category": "General", "changelog": changelog, "description": DESCRIPTION,
        "guid": GUID, "name": "Babelfin", "overview": OVERVIEW, "owner": OWNER,
        "license": "GPL-3.0-only",
        "targetAbi": TARGET_ABI, "timestamp": stamp.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "version": version, "status": "Active", "autoUpdate": True,
        "imagePath": "logo.png", "assemblies": [],
    }


def json_bytes(value: object) -> bytes:
    return (json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8")


def atomic_write(path: Path, content: bytes) -> None:
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".babelfin-", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(content)
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def read_license(path: Path) -> bytes:
    """Require the supplied license for new distributables; preserve its exact bytes."""
    if not path.is_file():
        raise PackageError("A nonempty LICENSE file is required for distributables")
    contents = path.read_bytes()
    if not contents.strip():
        raise PackageError("A nonempty LICENSE file is required for distributables")
    return contents


def read_notice(path: Path) -> bytes:
    """Require upstream provenance alongside the license, preserving exact bytes."""
    if not path.is_file() or not path.read_bytes().strip():
        raise PackageError("A nonempty NOTICE file is required for distributables")
    return path.read_bytes()


def require_clean_output(output: Path) -> None:
    """Never replace a previous or partial release bundle."""
    if not output.exists():
        return
    if not output.is_dir():
        raise PackageError("Output must be a directory without release artifacts")
    for path in output.iterdir():
        if path.name == "manifest.json" or path.match("Babelfin-*.zip") or path.match("Babelfin-*.zip.md5") or path.match("Babelfin-*.zip.sha256"):
            raise PackageError("Release artifacts already exist; select a clean output directory")


@contextmanager
def claim_output(output: Path):
    """Serialize both entry points before their clean check, build or writes.

    Claim beside the canonical output so validation failures need not create it.
    A crashed process may leave a claim; it is never stolen automatically.
    """
    output = output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    claim = output.parent / f".{output.name}.babelfin-package.lock"
    try:
        descriptor = os.open(claim, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError as exc:
        raise PackageError("Output is claimed by another generation; do not remove a claim until its owner has stopped") from exc
    try:
        yield output
    finally:
        os.close(descriptor)
        claim.unlink()


def create_package(*, csproj: Path, dll: Path, logo: Path, license_file: Path,
                   output: Path, stamp: datetime, notice_file: Path | None = None, source_url: str | None = None,
                   repository_url: str | None = None, image_url: str | None = None,
                   project_url: str | None = None,
                   changelog: str = DEFAULT_CHANGELOG) -> dict:
    """Package supplied files under an exclusive output claim; never build or use a network."""
    with claim_output(output) as claimed:
        return _create_package(csproj=csproj, dll=dll, logo=logo, license_file=license_file, notice_file=notice_file,
                               output=claimed, stamp=stamp, source_url=source_url, repository_url=repository_url,
                               image_url=image_url, project_url=project_url, changelog=changelog)


def _create_package(*, csproj: Path, dll: Path, logo: Path, license_file: Path,
                   output: Path, stamp: datetime, notice_file: Path | None = None, source_url: str | None = None,
                   repository_url: str | None = None, image_url: str | None = None,
                   project_url: str | None = None,
                   changelog: str = DEFAULT_CHANGELOG) -> dict:
    """Claim-holder implementation shared by main and create_package."""
    version = read_version(csproj)
    stamp = release_time(stamp.isoformat())
    project_url = public_url(project_url, "project URL")
    source_url = public_url(source_url, "source URL")
    repository_url = public_url(repository_url, "repository URL")
    image_url = public_url(image_url, "image URL")
    if source_url and not urlsplit(source_url).path.lower().endswith(".zip"):
        raise PackageError("source URL must identify a .zip file")
    require_clean_output(output)
    manifest_path = output / "manifest.json"
    contents = {
        DLL_NAME: dll.read_bytes(), "logo.png": logo.read_bytes(),
        "meta.json": json_bytes(metadata(version, stamp, changelog)),
        "LICENSE": read_license(license_file),
        "NOTICE": read_notice(notice_file or license_file.with_name("NOTICE")),
    }
    if not contents[DLL_NAME] or not contents["logo.png"]:
        raise PackageError("The DLL and logo must be nonempty")
    commit = verify_dll(dll, csproj, version, contents[DLL_NAME])
    output.mkdir(parents=True, exist_ok=True)
    package_path = output / f"Babelfin-{version}.zip"
    buffer = io.BytesIO()
    # No source mtimes, platform permissions, folder prefix, or wall clock in ZIP bytes.
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, content in sorted(contents.items()):
            info = zipfile.ZipInfo(name, stamp.timetuple()[:6])
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, content, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    package_bytes = buffer.getvalue()
    digest = hashlib.md5(package_bytes, usedforsecurity=False).hexdigest()
    sha256 = hashlib.sha256(package_bytes).hexdigest()
    manifest = None
    if source_url:
        release = {
            "version": version, "changelog": changelog, "targetAbi": TARGET_ABI,
            "sourceUrl": source_url, "checksum": digest,
            "timestamp": stamp.strftime("%Y-%m-%dT%H:%M:%SZ"),
        }
        if repository_url:
            release["repositoryUrl"] = repository_url
        entry = {
            "guid": GUID, "name": "Babelfin", "description": DESCRIPTION,
            "overview": OVERVIEW, "owner": OWNER, "category": "General", "versions": [release],
        }
        if image_url:
            entry["imageUrl"] = image_url
        if project_url:
            entry["projectUrl"] = project_url
        manifest = [entry]
    atomic_write(package_path, package_bytes)
    atomic_write(package_path.with_suffix(".zip.md5"), f"{digest}  {package_path.name}\n".encode("ascii"))
    atomic_write(package_path.with_suffix(".zip.sha256"), f"{sha256}  {package_path.name}\n".encode("ascii"))
    if manifest is not None:
        atomic_write(manifest_path, json_bytes(manifest))
    return {"version": version, "source_commit": commit, "zip": str(package_path), "md5": digest, "sha256": sha256,
            "manifest": str(manifest_path) if manifest is not None else None,
            "project_url": project_url}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--dll", type=Path)
    parser.add_argument("--logo", type=Path)
    parser.add_argument("--license", type=Path, dest="license_file", help="License file to include (default: <root>/LICENSE)")
    parser.add_argument("--notice", type=Path, dest="notice_file", help="Provenance file (default: <root>/NOTICE)")
    parser.add_argument("--config", type=Path, help="JSON with explicit URLs and changelog; supply the final commit time via --timestamp")
    parser.add_argument("--timestamp", help="Final source commit's ISO 8601 committer time with timezone (or use SOURCE_DATE_EPOCH)")
    for name in ("source-url", "repository-url", "image-url", "project-url", "changelog"):
        parser.add_argument("--" + name)
    args = parser.parse_args(argv)
    try:
        allowed = {"timestamp", "source_url", "repository_url", "image_url", "project_url", "changelog"}
        config = json.loads(args.config.read_text(encoding="utf-8-sig")) if args.config else {}
        if not isinstance(config, dict) or set(config) - allowed or any(not isinstance(value, str) for value in config.values()):
            raise PackageError("Release config accepts only string timestamp/source_url/repository_url/image_url/project_url/changelog values")
        options = {name: getattr(args, name) if getattr(args, name) is not None else config.get(name) for name in allowed}
        stamp = release_time(options.pop("timestamp"))
        root = args.root.resolve()
        read_version(root / PROJECT)
        validate_asset_line_endings(root)
        validate_translations(root)
        for name in ("source_url", "repository_url", "image_url", "project_url"):
            public_url(options[name], name)
        license_file = args.license_file or root / "LICENSE"
        read_license(license_file)
        notice_file = args.notice_file or root / "NOTICE"
        read_notice(notice_file)
        output = (args.output or root / "dist").resolve()
        with claim_output(output) as claimed:
            require_clean_output(claimed)
            dll = args.dll or build_plugin(root, root / "plugin/package/stage/build")
            if options["changelog"] is None:
                options["changelog"] = DEFAULT_CHANGELOG
            result = _create_package(csproj=root / PROJECT, dll=dll, logo=args.logo or root / "plugin/package/logo.png",
                                     license_file=license_file, notice_file=notice_file,
                                     output=claimed, stamp=stamp, **options)
        print(json.dumps(result, indent=2))
        if result["manifest"] is None:
            print("No catalog manifest generated: --source-url was not supplied.")
        else:
            print("Catalog generated locally. Configured URLs are planned publication targets; public availability has not been verified.")
        return 0
    except (PackageError, OSError, ET.ParseError, json.JSONDecodeError, subprocess.CalledProcessError) as exc:
        parser.exit(1, f"Packaging failed: {exc}\n")


if __name__ == "__main__":
    raise SystemExit(main())
