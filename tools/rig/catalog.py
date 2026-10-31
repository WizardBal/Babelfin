#!/usr/bin/env python3
"""Real Jellyfin 12.2 clean catalog install/update acceptance (Windows).

Run only when the final ZIP and generated manifest are ready::

    python -B tools/rig/catalog.py --jellyfin RUNTIME_DIRECTORY \
        --previous-package PREVIOUS_ZIP --package FINAL_ZIP \
        --manifest FINAL_MANIFEST --output NEW_REPORT_DIRECTORY [--port 8103]

No existing server/data directory is accepted. Only a newly created temporary
data directory and the child process handles created here are used. An occupied
port is an error. Logs and temporary data are retained even on failure.

The catalog is served by a loopback SimpleHTTPRequestHandler on a random port.
Only URL fields in the supplied final manifest are replaced for local transport;
its checksum and all ZIP bytes are preserved. The old-only catalog is synthesized
from the previous ZIP's meta.json. Anonymous GitHub availability is NOT tested.
The report distinguishes a verification download from the real installer's GET.
No API headers, credentials, response bodies, or original URLs are recorded.
"""

import argparse
import copy
import ctypes
import hashlib
import io
import json
import os
from pathlib import Path
import re
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from unittest.mock import patch
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile

# Importing the existing rig must not write __pycache__ into the shared checkout.
sys.dont_write_bytecode = True
from rig import Api, complete_wizard, login, start_jellyfin, wait_healthy  # noqa: E402


GUID = "dc7f86a9-90f3-4282-93d9-424fb4c64b4d"
DLL = "Jellyfin.Plugin.Babelfin.dll"
INSTALL_TIMEOUT = 120


class AcceptanceError(RuntimeError):
    """A safe-to-record diagnostic; never include HTTP bodies or local paths."""


def require(condition, message):
    if not condition:
        raise AcceptanceError(message)


def field(row, key, default=None):
    if not isinstance(row, dict):
        return default
    return next((v for k, v in row.items() if k.casefold() == key.casefold()), default)


def same_guid(value):
    try:
        return uuid.UUID(str(value)) == uuid.UUID(GUID)
    except (ValueError, TypeError, AttributeError):
        return False


def md5(data):
    return hashlib.md5(data, usedforsecurity=False).hexdigest()


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def json_bytes(value):
    return (json.dumps(value, indent=2, ensure_ascii=True) + "\n").encode("utf-8")


def load_package(path):
    payload = path.read_bytes()
    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        names = archive.namelist()
        require(len(names) == 5 and set(names) == {DLL, "meta.json", "logo.png", "LICENSE", "NOTICE"},
                "Expected the flat five-file release ZIP")
        require(archive.testzip() is None, "ZIP CRC check failed")
        files = {name: archive.read(name) for name in names}
    require(files["LICENSE"].strip() and files["NOTICE"].strip(), "ZIP license and provenance must be nonempty")
    meta = json.loads(files["meta.json"].decode("utf-8-sig"))
    version = field(meta, "version")
    require(isinstance(version, str) and re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+", version)
            and same_guid(field(meta, "guid")), "ZIP metadata GUID/version is invalid")
    require(field(meta, "name") == "Babelfin" and files[DLL] and files["logo.png"],
            f"{version}: ZIP name or payload is invalid")
    digest = md5(payload)
    sidecar = path.with_suffix(path.suffix + ".md5")
    if sidecar.is_file():
        recorded = sidecar.read_text(encoding="utf-8-sig").split()[0].lower()
        require(recorded == digest, f"{version}: adjacent MD5 sidecar mismatch")
    return {"version": version, "bytes": payload, "files": files, "meta": meta,
            "md5": digest, "sha256": sha256(payload), "sidecar_checked": sidecar.is_file(),
            "route": f"/Babelfin-{version}.zip"}


def load_manifest(path, package):
    raw = path.read_bytes()
    manifest = json.loads(raw.decode("utf-8-sig"))
    require(isinstance(manifest, list) and len(manifest) == 1,
            "Final manifest must contain exactly the Babelfin package")
    entry = manifest[0]
    require(same_guid(field(entry, "guid")) and field(entry, "name") == "Babelfin",
            "Final manifest GUID/name mismatch")
    versions = field(entry, "versions")
    require(isinstance(versions, list) and len(versions) == 1,
            "Final generated manifest must contain exactly the final release")
    release = versions[0]
    require(field(release, "version") == package["version"], "Final manifest version mismatch")
    checksum = field(release, "checksum")
    require(isinstance(checksum, str) and re.fullmatch(r"[0-9a-fA-F]{32}", checksum)
            and checksum.lower() == package["md5"], "Final manifest MD5 does not match exact ZIP bytes")
    require(field(release, "targetAbi") == field(package["meta"], "targetAbi"),
            "Final manifest and ZIP targetAbi mismatch")
    require(isinstance(field(release, "sourceUrl"), str) and field(release, "sourceUrl"),
            "Final manifest sourceUrl is missing")
    return manifest, sha256(raw)


def free_port(port):
    # Never probe an already running HTTP server, even for health/discovery.
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        try:
            probe.bind(("127.0.0.1", port))
        except OSError:
            raise AcceptanceError("Requested loopback port is occupied; no server was contacted") from None


def listener_owners(port):
    """IPv4 listener address/PID from Windows IP Helper; no HTTP requests."""
    size = ctypes.c_ulong(0)
    table = ctypes.WinDLL("iphlpapi").GetExtendedTcpTable
    table.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong), ctypes.c_int,
                      ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    table.restype = ctypes.c_ulong
    result = table(None, ctypes.byref(size), False, socket.AF_INET, 3, 0)
    require(result in (0, 122), "Unable to inspect listener process ownership")
    for _ in range(3):
        buffer = ctypes.create_string_buffer(max(size.value, 4))
        result = table(buffer, ctypes.byref(size), False, socket.AF_INET, 3, 0)
        if result == 122:
            continue
        require(result == 0, "Unable to inspect listener process ownership")
        count = struct.unpack_from("=I", buffer)[0]
        rows = [struct.unpack_from("=6I", buffer, 4 + i * 24) for i in range(count)]
        return [(socket.inet_ntoa(struct.pack("=I", row[1])), row[5]) for row in rows
                if socket.ntohs(row[2] & 0xffff) == port]
    raise AcceptanceError("Listener process ownership changed during inspection")


class OwnedServer:
    def __init__(self, runtime, data_dir, port, report):
        self.runtime, self.data_dir, self.port = runtime, data_dir, port
        self.report = report
        self.proc = None
        self.generation = 0

    def guard(self):
        require(self.proc is not None and self.proc.poll() is None,
                "Owned Jellyfin process is not running; refusing API access")
        owners = listener_owners(self.port)
        if not owners:
            raise ConnectionError("Owned listener is not ready")
        require(all(address == "127.0.0.1" and pid == self.proc.pid for address, pid in owners),
                "Listener is not exclusively the owned loopback process; refusing API access")

    def start(self):
        require(self.proc is None, "Owned process must be stopped before restart")
        free_port(self.port)
        self.generation += 1
        stdout = self.data_dir / "stdout.log"
        if stdout.exists():
            stdout.rename(self.data_dir / f"stdout-{self.generation - 1}.log")
        original_popen = subprocess.Popen

        def launch(command, **kwargs):
            # rig.start_jellyfin writes network.xml just before Popen. Patch only
            # that launch boundary so the existing helper still owns its setup.
            config = self.data_dir / "config" / "network.xml"
            tree = ET.parse(config)
            root = tree.getroot()
            for name, value in (("EnableIPv4", "true"), ("EnableIPv6", "false")):
                node = root.find(name)
                if node is None:
                    node = ET.SubElement(root, name)
                node.text = value
            addresses = ET.SubElement(root, "LocalNetworkAddresses")
            ET.SubElement(addresses, "string").text = "127.0.0.1"
            tree.write(config, encoding="utf-8", xml_declaration=True)
            environment = os.environ.copy()
            for key in list(environment):
                if key.upper().endswith("_PROXY") or key.upper().startswith("JELLYFIN_"):
                    del environment[key]
            environment["NO_PROXY"] = "127.0.0.1,localhost,::1"
            kwargs["env"] = environment
            self.proc = original_popen(command, **kwargs)
            return self.proc

        with patch("rig.subprocess.Popen", launch):
            self.proc = start_jellyfin(self.runtime, self.data_dir, self.port)
        self.report["processes"].append({"generation": self.generation, "pid": self.proc.pid})

    def stop(self):
        if self.proc is None:
            return
        proc = self.proc
        if proc.poll() is None:
            proc.terminate()  # Popen handle, never a process-name or global kill.
            try:
                proc.wait(timeout=20)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=20)
        self.report["processes"][-1]["exit_code"] = proc.returncode
        self.proc = None


class NoRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise AcceptanceError("Unexpected HTTP redirect refused")


def opener():
    return urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirects())


class OwnedApi(Api):
    """Keep rig.Api's helpers/auth format, with owned-origin transport and audit."""

    def __init__(self, server, report):
        super().__init__(f"http://127.0.0.1:{server.port}")
        self.server, self.report, self.transport = server, report, opener()

    def call(self, method, path, body=None, params=None, headers=None, raw=False, timeout=60):
        self.server.guard()
        parsed = urllib.parse.urlsplit(path)
        require(path.startswith("/") and not path.startswith("//") and not parsed.scheme
                and not parsed.netloc and not parsed.query and not parsed.fragment,
                "API path must be relative to the owned loopback server")
        suffix = "?" + urllib.parse.urlencode(params) if params else ""
        # Record only an allowlist of non-secret query fields; never body/headers.
        safe = {k: v for k, v in (params or {}).items()
                if k in ("assemblyGuid", "version", "repositoryUrl")}
        event = {"generation": self.server.generation, "method": method, "path": path}
        if safe:
            event["query"] = safe
        self.report["api_requests"].append(event)
        hdrs = {"Authorization": self.auth_header(), "Accept": "application/json"}
        if headers:
            hdrs.update(headers)
        data = None if body is None else json.dumps(body).encode("utf-8")
        if data is not None:
            hdrs["Content-Type"] = "application/json"
        request = urllib.request.Request(self.base + path + suffix, data=data, method=method, headers=hdrs)
        try:
            response = self.transport.open(request, timeout=timeout)
        except urllib.error.HTTPError as error:
            # No response body can escape into a diagnostic (auth can contain tokens).
            event["status"] = error.code
            error.close()
            return error.code, None, {}
        with response:
            payload = response.read()
            event["status"] = response.status
            return response.status, (payload if raw else (json.loads(payload) if payload.strip() else None)), {}


class Catalog(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, packages, output, report):
        self.packages, self.output, self.report = packages, output, report
        self.assets = {p["route"]: (p["bytes"], "application/zip") for p in packages}
        self.assets["/logo.png"] = (packages[1]["files"]["logo.png"], "image/png")
        self.lock = threading.Lock()
        self.phase = "baseline"
        super().__init__(("127.0.0.1", 0), CatalogHandler)
        self.base = f"http://127.0.0.1:{self.server_address[1]}"
        self.url = self.base + "/manifest.json"
        self.worker = threading.Thread(target=self.serve_forever, daemon=True)
        self.worker.start()

    def publish(self, source, include_new):
        manifest = copy.deepcopy(source)
        entry = manifest[0]
        new_release = copy.deepcopy(entry["versions"][0])
        rewrites = []
        for name, value in (("sourceUrl", self.base + self.packages[1]["route"]),
                            ("repositoryUrl", self.url)):
            if name in new_release:
                new_release[name] = value
                rewrites.append("versions[0]." + name)
        for name, value in (("imageUrl", self.base + "/logo.png"), ("projectUrl", self.url)):
            if name in entry:
                entry[name] = value
                rewrites.append(name)
        old = self.packages[0]
        old_release = {name: old["meta"][name]
                       for name in ("version", "changelog", "targetAbi", "timestamp")}
        old_release.update(sourceUrl=self.base + old["route"], checksum=old["md5"], repositoryUrl=self.url)
        entry["versions"] = [new_release, old_release] if include_new else [old_release]
        phase = "upgrade" if include_new else "baseline"
        payload = json_bytes(manifest)
        (self.output / f"manifest-{phase}.json").write_bytes(payload)
        with self.lock:
            self.phase = phase
            self.assets["/manifest.json"] = (payload, "application/json")
        self.report["transport"] = {
            "repository_url": self.url, "url_fields_rewritten": rewrites,
            "only_url_fields_changed_in_final_release": True,
            "baseline": "Version metadata from previous ZIP; checksum computed from its exact bytes",
            "github_anonymous_availability": "NOT TESTED; original URLs were never requested",
        }

    def close(self):
        self.shutdown()
        self.server_close()
        self.worker.join(timeout=5)


class CatalogHandler(SimpleHTTPRequestHandler):
    def log_message(self, format, *args):
        pass  # Default logging could expose arbitrary request text.

    def do_GET(self):
        self.serve_asset(False)

    def do_HEAD(self):
        self.serve_asset(True)

    def serve_asset(self, head):
        path = urllib.parse.urlsplit(self.path).path
        with self.server.lock:
            asset = self.server.assets.get(path)
            event = {"method": self.command, "path": path if asset else "<unknown route>", "phase": self.server.phase,
                     "client": "verification" if self.headers.get("X-Catalog-Verification") == "1" else "catalog-client",
                     "status": 200 if asset else 404, "complete": False}
            self.server.report["catalog_requests"].append(event)
        if asset is None:
            self.send_response(404)
            self.end_headers()
            return
        payload, content_type = asset
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store, no-cache, max-age=0")
        self.end_headers()
        try:
            if not head:
                self.wfile.write(payload)
                self.wfile.flush()
            with self.server.lock:
                event.update(complete=True, bytes=0 if head else len(payload))
                if not head and content_type == "application/zip":
                    event.update(md5=md5(payload), sha256=sha256(payload))
        except (BrokenPipeError, ConnectionResetError, OSError):
            pass


def expect(api, method, path, body=None, params=None, statuses=(200,), timeout=60):
    status, result, _ = api.call(method, path, body=body, params=params, timeout=timeout)
    require(status in statuses, f"{method} {path} returned HTTP {status}")
    return result


def boot(server, api, first=False):
    server.start()
    api.token = None
    require(wait_healthy(api, 120), "Owned Jellyfin did not become Healthy within 120 seconds")
    info = expect(api, "GET", "/System/Info/Public")
    require(re.match(r"^12\.2(?:\.|$)", str(field(info, "Version", ""))), "Expected the real Jellyfin 12.2 runtime")
    if first:
        require(complete_wizard(api), "Startup wizard failed")
    require(login(api, "rigadmin", "rig-pass-1"), "Owned test administrator login failed")


def catalog_version(api, catalog, versions, report):
    rows = expect(api, "GET", "/Packages")
    require(isinstance(rows, list), "GET /Packages did not return a package array")
    matches = [r for r in rows if same_guid(field(r, "guid", field(r, "Id")))]
    require(len(matches) == 1, "Catalog must expose exactly one Babelfin package")
    releases = field(matches[0], "versions", [])
    require({field(r, "version") for r in releases} == set(versions), "Catalog exposed unexpected release versions")
    for release in releases:
        package = next(p for p in catalog.packages if p["version"] == field(release, "version"))
        require(str(field(release, "checksum", "")).lower() == package["md5"], "Catalog MD5 changed")
        require(field(release, "sourceUrl") == catalog.base + package["route"], "Catalog sourceUrl is not the owned mirror")
    report["checks"].append("Catalog versions: " + ", ".join(sorted(versions)))


def installed_directory(server, package):
    directory = server.data_dir / "plugins" / ("Babelfin_" + package["version"])
    require(not directory.is_symlink(), "Installed plugin directory must not be a symlink")
    return directory


def install(api, server, catalog, package, report, output):
    # Verify the HTTP payload independently, and label this GET so it cannot be
    # mistaken for evidence that Jellyfin's real installer downloaded the ZIP.
    request = urllib.request.Request(catalog.base + package["route"], headers={"X-Catalog-Verification": "1"})
    with opener().open(request, timeout=30) as response:
        downloaded = response.read()
    require(downloaded == package["bytes"] and md5(downloaded) == package["md5"], "Loopback download differs from the exact ZIP")
    (output / f"download-{package['version']}.zip").write_bytes(downloaded)
    with catalog.lock:
        before = len(report["catalog_requests"])
    expect(api, "POST", "/Packages/Installed/Babelfin", params={
        "assemblyGuid": GUID, "version": package["version"], "repositoryUrl": catalog.url,
    }, statuses=(204,), timeout=INSTALL_TIMEOUT)
    directory = installed_directory(server, package)
    deadline = time.monotonic() + INSTALL_TIMEOUT
    while time.monotonic() < deadline:
        require(server.proc.poll() is None, "Owned process exited during package installation")
        with catalog.lock:
            real_get = any(e["method"] == "GET" and e["path"] == package["route"]
                           and e["client"] == "catalog-client" and e["complete"]
                           and e.get("md5") == package["md5"] for e in report["catalog_requests"][before:])
        dll = directory / DLL
        if real_get and dll.is_file() and dll.read_bytes() == package["files"][DLL] and (directory / "meta.json").is_file():
            break
        time.sleep(0.5)
    else:
        raise AcceptanceError("Real catalog download/install did not finish with the exact DLL within 120 seconds")
    report["checks"].append(f"Real catalog install {package['version']}: HTTP 204, installer ZIP GET, exact DLL")


def active(api, server, package, report):
    rows = expect(api, "GET", "/Plugins")
    require(isinstance(rows, list), "GET /Plugins did not return an array")
    babelfin = [r for r in rows if same_guid(field(r, "Id", field(r, "guid")))]
    require(any(field(r, "Version") == package["version"] and field(r, "Status") == "Active" for r in babelfin),
            f"Expected {package['version']} Active in /Plugins")
    require(not any(field(r, "Status") == "Active" and field(r, "Version") != package["version"] for r in babelfin),
            "An unexpected Babelfin version is also Active")
    ping = expect(api, "GET", "/Babelfin/Ping")
    require(field(ping, "version") == package["version"] and field(ping, "wrapped") is True,
            f"{package['version']}: Ping version/wrapper check failed")
    directory = installed_directory(server, package)
    installed = (directory / DLL).read_bytes()
    require(installed == package["files"][DLL], "Active installed DLL does not match the exact ZIP")
    meta = json.loads((directory / "meta.json").read_text(encoding="utf-8-sig"))
    require(field(meta, "version") == package["version"] and same_guid(field(meta, "guid")),
            "Installed metadata GUID/version mismatch")
    report["installed"].append({"version": package["version"], "status": "Active", "ping_wrapped": True,
                                "dll_sha256": sha256(installed), "zip_dll_sha256": sha256(package["files"][DLL])})
    report["checks"].append(f"After owned restart: {package['version']} Active, Ping wrapped, exact ZIP DLL")


def preferences(api, expected=None):
    view = expect(api, "GET", "/Babelfin/Me/Settings")
    settings = copy.deepcopy(field(view, "Settings"))
    require(isinstance(settings, dict), "Personal settings response is missing its document")
    wanted = {"Understood": ["ja", "en"], "Reads": ["en"],
              "AudioChain": [{"Kind": "Language", "Language": "ja"}, {"Kind": "VideoDefault"}],
              "SubtitleLanguages": ["en"], "WhenAudioNotUnderstood": "Full"}
    if expected is None:
        settings.setdefault("Everyday", {}).update(wanted)
        expect(api, "PUT", "/Babelfin/Me/Settings", body=settings, params={"keepOverrides": "true"})
        settings = field(expect(api, "GET", "/Babelfin/Me/Settings"), "Settings")
    everyday = field(settings, "Everyday", {})

    def without_nulls(value):
        if isinstance(value, dict):
            return {k: without_nulls(v) for k, v in value.items() if v is not None}
        if isinstance(value, list):
            return [without_nulls(v) for v in value]
        return value

    actual = without_nulls({key: field(everyday, key) for key in wanted})
    require(actual == (wanted if expected is None else expected), "Saved personal audio/subtitle preferences differ")
    return actual


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--jellyfin", required=True, type=Path, help="Directory containing the Windows Jellyfin 12.2 runtime")
    parser.add_argument("--previous-package", required=True, type=Path, help="Existing exact older Babelfin release ZIP")
    parser.add_argument("--package", required=True, type=Path, help="Final exact Babelfin release ZIP")
    parser.add_argument("--manifest", required=True, type=Path, help="Final generated manifest matching --package MD5")
    parser.add_argument("--output", required=True, type=Path, help="New report directory; existing paths are refused")
    parser.add_argument("--port", default=8103, type=int, help="Unused IPv4 loopback Jellyfin port (default: 8103)")
    args = parser.parse_args(argv)
    if not 1024 <= args.port <= 65534:
        parser.error("--port must be between 1024 and 65534")
    return args


def main(argv=None):
    args = parse_args(argv)
    report = {"result": "FAIL", "checks": [], "processes": [], "api_requests": [],
              "catalog_requests": [], "installed": [], "errors": [],
              "scope": "Disposable Windows Jellyfin 12.2, clean catalog install followed by a catalog update",
              "github_anonymous_availability": "NOT TESTED"}
    server = catalog = output = None
    exit_code = 1
    try:
        require(os.name == "nt", "Execution requires the Windows Jellyfin 12.2 runtime; --help works anywhere")
        runtime = args.jellyfin.resolve(strict=True)
        require(runtime.is_dir() and (runtime / "jellyfin.exe").is_file(), "--jellyfin must contain jellyfin.exe")
        packages = [load_package(args.previous_package.resolve(strict=True)),
                    load_package(args.package.resolve(strict=True))]
        old, new = (package["version"] for package in packages)
        require(tuple(map(int, old.split("."))) < tuple(map(int, new.split("."))),
                "The previous package must have an older version than the final package")
        source, manifest_hash = load_manifest(args.manifest.resolve(strict=True), packages[1])
        free_port(args.port)
        destination = args.output.resolve()
        require(not destination.exists(), "--output must be a new report directory")
        destination.mkdir(parents=True, exist_ok=False)
        output = destination
        report["manifest_sha256"] = manifest_hash
        report["packages"] = [{k: p[k] for k in ("version", "md5", "sha256", "sidecar_checked")} for p in packages]
        data_dir = Path(tempfile.mkdtemp(prefix="babelfin-catalog-"))
        report["retained_temp_directory_name"] = data_dir.name
        report["logs"] = "Retained in the system temporary directory named above: stdout*.log and log/"
        config_dir = data_dir / "config"
        config_dir.mkdir()
        # An explicitly empty repository list prevents the default public catalog
        # from being part of this disposable server's package discovery.
        (config_dir / "system.xml").write_text(
            '<?xml version="1.0" encoding="utf-8"?>\n'
            '<ServerConfiguration><PluginRepositories /></ServerConfiguration>\n', encoding="utf-8")
        server = OwnedServer(runtime, data_dir, args.port, report)
        catalog = Catalog(packages, output, report)
        catalog.publish(source, include_new=False)
        api = OwnedApi(server, report)
        boot(server, api, first=True)
        initial_plugins = expect(api, "GET", "/Plugins")
        require(isinstance(initial_plugins, list)
                and not any(same_guid(field(r, "Id", field(r, "guid"))) for r in initial_plugins),
                "Fresh data unexpectedly contains Babelfin")
        expect(api, "POST", "/Repositories", body=[{"Name": "Babelfin catalog acceptance", "Url": catalog.url, "Enabled": True}], statuses=(204,))
        repositories = expect(api, "GET", "/Repositories")
        require(isinstance(repositories, list) and len(repositories) == 1
                and field(repositories[0], "Url") == catalog.url and field(repositories[0], "Enabled") is True,
                "Disposable server repository list is not exclusively the loopback catalog")
        catalog_version(api, catalog, [old], report)
        install(api, server, catalog, packages[0], report, output)
        server.stop()
        boot(server, api)
        active(api, server, packages[0], report)
        saved = preferences(api)
        report["preferences_before"] = saved
        report["checks"].append("Personal audio/subtitle preferences saved and read back on the older version")
        catalog.publish(source, include_new=True)
        # Jellyfin caches package discovery briefly; this owned process restart
        # guarantees a fresh GET /Packages without touching private server caches.
        server.stop()
        boot(server, api)
        catalog_version(api, catalog, [old, new], report)
        install(api, server, catalog, packages[1], report, output)
        server.stop()
        boot(server, api)
        active(api, server, packages[1], report)
        report["preferences_after"] = preferences(api, saved)
        report["checks"].append("Same tested personal preferences survived real catalog update and restart")
        report["result"] = "PASS"
        exit_code = 0
    except KeyboardInterrupt:
        report["errors"].append("Interrupted; owned resources will be stopped")
        exit_code = 130
    except Exception as error:
        # Unexpected exception strings may contain paths/HTTP bodies/secrets.
        report["errors"].append(str(error) if isinstance(error, AcceptanceError)
                                else f"{type(error).__name__}; inspect retained temporary server logs")
    finally:
        for resource in (server, catalog):
            if resource is None:
                continue
            try:
                resource.stop() if isinstance(resource, OwnedServer) else resource.close()
            except Exception as error:
                report["errors"].append(f"Owned resource shutdown failed: {type(error).__name__}")
                report["result"] = "FAIL"
                exit_code = 1
        report["cleanup"] = {"owned_server_stopped": server is None or server.proc is None,
                             "catalog_stopped": catalog is None or not catalog.worker.is_alive(),
                             "temporary_data_retained": server is not None}
        if server is not None:
            error_count = 0
            try:
                for path in [*server.data_dir.glob("stdout*.log"), *(server.data_dir / "log").glob("*.log")]:
                    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
                        if "Babelfin" in line and any(word in line for word in ("[ERR]", "[FTL]", "Unhandled")):
                            error_count += 1
                report["babelfin_server_log_error_count"] = error_count
                require(error_count == 0, "Babelfin errors found in retained server logs")
            except Exception as error:
                report["errors"].append(str(error) if isinstance(error, AcceptanceError)
                                        else f"Server log inspection failed: {type(error).__name__}")
                report["result"] = "FAIL"
                exit_code = 1
        if output is not None:
            lines = [f"Catalog acceptance: {report['result']}", *report["checks"], *report["errors"],
                     "Original catalog/package URLs and anonymous GitHub availability: NOT TESTED.",
                     "Only URL fields were rewritten for loopback transport; exact ZIP/checksum bytes were preserved.",
                     "HTTP verification downloads are separate from the real catalog installer's recorded ZIP GETs.",
                     "Only the listed personal preference fields were checked for persistence.",
                     "Temporary data and server logs retained: " + report.get("retained_temp_directory_name", "not created")]
            try:
                (output / "report.json").write_bytes(json_bytes(report))
                (output / "report.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
            except OSError:
                report["result"] = "FAIL"
                report["errors"].append("Unable to write reports to --output")
                exit_code = 1
        print(f"Catalog acceptance: {report['result']}")
        for error in report["errors"]:
            print(error)
        if output is not None:
            print("Reports written to --output; temporary server data/logs retained.")
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
