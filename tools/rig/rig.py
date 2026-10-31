"""Integration rig for a disposable Jellyfin 12.2 Windows instance with the plugin and generated sample library.
It drives checks through the HTTP API and prints PASS/FAIL lines. Run it against a disposable instance when evaluating
server integration; record the commands, source revision and results.

Usage (from the Babelfin folder):
  python tools/rig/rig.py --jellyfin <folder with jellyfin.exe> [--plugin <folder with Jellyfin.Plugin.Babelfin.dll>] [--media <folder>] [--port 8097] [--keep] [--e2e]
  --e2e: after the API checks pass, with Jellyfin still running, also run the browser tests (node tools/rig/e2e/run.js).

What it does:
  1. Fresh data folder under %TEMP%/babelfin-rig/run-<n>; network.xml with the chosen port; the plugin copied into plugins/Babelfin_<ver>/.
  2. Starts jellyfin.exe; waits up to STARTUP_TIMEOUT seconds for GET /health == Healthy (a hung startup fails the check).
  3. Completes the startup wizard, authenticates, adds the libraries (Anime, TV Shows, Movies) pointing at the sample media, waits for the scan.
  4. Sets the original language on the test series, then runs the checks (items, PlaybackInfo, Explain, settings API).
  5. Stops Jellyfin, scans its log for Babelfin errors, and prints ALL PASS or the failure count.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
STARTUP_TIMEOUT = 120
SCAN_TIMEOUT = 180
failures = 0


def check(ok, what):
    global failures
    print(("PASS " if ok else "FAIL ") + what, flush=True)
    if not ok:
        failures += 1


class Api:
    def __init__(self, base):
        self.base = base.rstrip("/")
        self.token = None
        self.user_id = None

    def auth_header(self):
        h = 'MediaBrowser Client="BabelfinRig", Device="rig", DeviceId="babelfin-rig", Version="1.0"'
        if self.token:
            h += f', Token="{self.token}"'
        return h

    def call(self, method, path, body=None, params=None, headers=None, raw=False, timeout=60):
        url = self.base + path
        if params:
            url += ("&" if "?" in url else "?") + urllib.parse.urlencode(params)
        data = None
        hdrs = {"Authorization": self.auth_header(), "Accept": "application/json"}
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            hdrs["Content-Type"] = "application/json"
        if headers:
            hdrs.update(headers)
        req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                payload = r.read()
                return r.status, (payload if raw else (json.loads(payload) if payload.strip() else None)), dict(r.headers)
        except urllib.error.HTTPError as e:
            payload = e.read()
            try:
                parsed = json.loads(payload) if payload.strip() else None
            except json.JSONDecodeError:
                parsed = payload.decode("utf-8", "replace")
            return e.code, parsed, dict(e.headers)

    def get(self, path, **params):
        return self.call("GET", path, params=params or None)

    def post(self, path, body=None, **params):
        return self.call("POST", path, body=body, params=params or None)

    def put(self, path, body=None, **params):
        return self.call("PUT", path, body=body, params=params or None)

    def delete(self, path, **params):
        return self.call("DELETE", path, params=params or None)


def wait_healthy(api, timeout):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            status, body, _ = api.call("GET", "/health", raw=True, timeout=5)
            if status == 200 and b"Healthy" in body:
                return True
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError):
            pass
        time.sleep(2)
    return False


def plugin_version(dll_folder):
    meta = dll_folder / "meta.json"
    if meta.is_file():
        return json.loads(meta.read_text(encoding="utf-8"))["version"]
    text = (ROOT / "plugin/src/Jellyfin.Plugin.Babelfin/Jellyfin.Plugin.Babelfin.csproj").read_text(encoding="utf-8")
    return re.search(r"<Version>([^<]+)</Version>", text).group(1)


def install_plugin(data_dir, plugin_folder):
    version = plugin_version(plugin_folder)
    target = data_dir / "plugins" / f"Babelfin_{version}"
    target.mkdir(parents=True)
    shutil.copy2(plugin_folder / "Jellyfin.Plugin.Babelfin.dll", target)
    meta = plugin_folder / "meta.json"
    if meta.is_file():
        shutil.copy2(meta, target)
    else:
        (target / "meta.json").write_text(json.dumps({
            "category": "General", "changelog": "rig", "description": "rig", "guid": "dc7f86a9-90f3-4282-93d9-424fb4c64b4d",
            "name": "Babelfin", "overview": "rig", "owner": "rig", "targetAbi": "12.0.0.0", "timestamp": "2026-01-01T00:00:00Z",
            "version": version, "status": "Active", "autoUpdate": False, "imagePath": "", "assemblies": []}, indent=2), encoding="utf-8")
    return version


def start_jellyfin(jellyfin_dir, data_dir, port):
    config_dir = data_dir / "config"
    config_dir.mkdir(parents=True, exist_ok=True)
    (config_dir / "network.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<NetworkConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">\n'
        f'  <InternalHttpPort>{port}</InternalHttpPort>\n  <InternalHttpsPort>{port + 1}</InternalHttpsPort>\n  <PublicHttpPort>{port}</PublicHttpPort>\n  <PublicHttpsPort>{port + 1}</PublicHttpsPort>\n'
        '  <EnableHttps>false</EnableHttps>\n  <AutoDiscovery>false</AutoDiscovery>\n  <EnableUPnP>false</EnableUPnP>\n  <EnableRemoteAccess>false</EnableRemoteAccess>\n</NetworkConfiguration>\n',
        encoding="utf-8")
    exe = jellyfin_dir / "jellyfin.exe"
    web = jellyfin_dir / "jellyfin-web"
    ffmpeg = next(iter(jellyfin_dir.rglob("ffmpeg.exe")), None)
    args = [str(exe), "--datadir", str(data_dir), "--configdir", str(config_dir), "--cachedir", str(data_dir / "cache"), "--logdir", str(data_dir / "log"), "--webdir", str(web), "--nowebclient"]
    if ffmpeg:
        args += ["--ffmpeg", str(ffmpeg)]
    args.remove("--nowebclient")  # the web client is needed for the index.html injection check
    log = open(data_dir / "stdout.log", "w", encoding="utf-8")
    proc = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, cwd=str(jellyfin_dir))
    return proc


def complete_wizard(api):
    api.post("/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
    api.get("/Startup/User")
    api.post("/Startup/User", {"Name": "rigadmin", "Password": "rig-pass-1"})
    api.post("/Startup/RemoteAccess", {"EnableRemoteAccess": False, "EnableAutomaticPortMapping": False})
    status, _, _ = api.post("/Startup/Complete")
    return status in (200, 204)


def login(api, name, password):
    status, body, _ = api.post("/Users/AuthenticateByName", {"Username": name, "Pw": password})
    if status != 200:
        return False
    api.token = body["AccessToken"]
    api.user_id = body["User"]["Id"]
    return True


def add_library(api, name, collection_type, path):
    options = {
        "LibraryOptions": {
            "EnableRealtimeMonitor": False, "EnableInternetProviders": False, "EnableAutomaticSeriesGrouping": False,
            "SaveLocalMetadata": False, "EnableChapterImageExtraction": False, "ExtractChapterImagesDuringLibraryScan": False,
            "EnableTrickplayImageExtraction": False, "ExtractTrickplayImagesDuringLibraryScan": False, "SkipSubtitlesIfEmbeddedSubtitlesPresent": False,
            "SkipSubtitlesIfAudioTrackMatches": False, "RequirePerfectSubtitleMatch": False, "SaveSubtitlesWithMedia": True,
            "PathInfos": [{"Path": str(path)}],
            "TypeOptions": [], "DisabledLocalMetadataReaders": [], "DisabledSubtitleFetchers": [], "SubtitleFetcherOrder": [],
            "MetadataSavers": [], "DisabledMediaSegmentProviders": [], "MediaSegmentProviderOrder": [],
        }
    }
    status, _, _ = api.call("POST", "/Library/VirtualFolders", body=options, params={"name": name, "collectionType": collection_type, "refreshLibrary": "false"})
    return status in (200, 204)


def wait_for_scan(api, expected_files, timeout):
    api.post("/Library/Refresh")
    deadline = time.time() + timeout
    while time.time() < deadline:
        status, body, _ = api.get("/Items", userId=api.user_id, IncludeItemTypes="Episode,Movie", Recursive="true", Limit=1)
        if status == 200 and body.get("TotalRecordCount", 0) >= expected_files:
            status, tasks, _ = api.get("/ScheduledTasks")
            scanning = any(t.get("Key") == "RefreshLibrary" and t.get("State") != "Idle" for t in (tasks or []))
            if not scanning:
                return True
        time.sleep(3)
    return False


def main():
    global failures
    ap = argparse.ArgumentParser()
    ap.add_argument("--jellyfin", required=True)
    ap.add_argument("--plugin", default=str(ROOT / "plugin/src/Jellyfin.Plugin.Babelfin/bin/Release/net10.0"))
    ap.add_argument("--media", default=None)
    ap.add_argument("--port", type=int, default=8097)
    ap.add_argument("--keep", action="store_true", help="keep Jellyfin running after the checks (for a look in the browser)")
    ap.add_argument("--e2e", action="store_true", help="after the API checks pass, run the browser tests (tools/rig/e2e/run.js) against this Jellyfin")
    args = ap.parse_args()

    jellyfin_dir = Path(args.jellyfin).resolve()
    plugin_folder = Path(args.plugin).resolve()
    base_tmp = Path(os.environ.get("TEMP", "/tmp")) / "babelfin-rig"
    base_tmp.mkdir(parents=True, exist_ok=True)
    media = Path(args.media).resolve() if args.media else base_tmp / "media"
    if not media.is_dir():
        subprocess.run([sys.executable, "-I", str(ROOT / "tools/rig/make_media.py"), str(media)], check=True)
    run_no = len([p for p in base_tmp.iterdir() if p.name.startswith("run-")]) + 1
    data_dir = base_tmp / f"run-{run_no}"
    data_dir.mkdir()
    version = install_plugin(data_dir, plugin_folder)
    print(f"rig: Jellyfin at {jellyfin_dir}, plugin {version}, data {data_dir}, port {args.port}")

    proc = start_jellyfin(jellyfin_dir, data_dir, args.port)
    api = Api(f"http://127.0.0.1:{args.port}")
    try:
        healthy = wait_healthy(api, STARTUP_TIMEOUT)
        check(healthy and proc.poll() is None, f"Jellyfin reports Healthy within {STARTUP_TIMEOUT} s with the plugin loaded (a hung startup fails here)")
        if not healthy:
            return finish(proc, data_dir, args.keep)

        check(complete_wizard(api), "startup wizard completed through the API")
        check(login(api, "rigadmin", "rig-pass-1"), "admin signed in")
        status, ping, _ = api.get("/Babelfin/Ping")
        check(status == 200 and ping and ping.get("version") == version and ping.get("wrapped") is True, f"GET /Babelfin/Ping: {status} {ping}")

        for name, ctype, sub in (("Anime", "tvshows", "Anime"), ("TV Shows", "tvshows", "TV Shows"), ("Movies", "movies", "Movies")):
            check(add_library(api, name, ctype, media / sub), f"library {name} added")
        expected = sum(1 for _ in media.rglob("*.mkv"))
        check(wait_for_scan(api, expected, SCAN_TIMEOUT), f"library scan found all {expected} sample files")

        # Original language on the test series (no internet metadata in the rig).
        status, series, _ = api.get("/Items", userId=api.user_id, IncludeItemTypes="Series", Recursive="true", Fields="OriginalLanguage")
        by_name = {s["Name"]: s for s in (series or {}).get("Items", [])}
        for sname, lang in (("Astercoil Test", "ja"), ("Velmora Test", "ja"), ("Quorim Test", "ja"), ("Untagged Test", "ja"), ("Commentary Test", "ja"), ("Plain Test", "en")):
            s = by_name.get(sname)
            if s is None:
                check(False, f"series {sname} present after the scan")
                continue
            status, full, _ = api.get(f"/Users/{api.user_id}/Items/{s['Id']}")
            full["OriginalLanguage"] = lang
            status, _, _ = api.post(f"/Items/{s['Id']}", full)
            check(status in (200, 204), f"original language of {sname} set to {lang} ({status})")

        from checks import run_checks  # noqa: E402  (sibling file; keeps the driver readable)
        run_checks(api, check, media)
        if args.e2e:
            run_e2e(api.base)
        return finish(proc, data_dir, args.keep)
    finally:
        if not args.keep and proc.poll() is None:
            proc.kill()


def run_e2e(base):
    """The browser tests (tools/rig/e2e/run.js), run once the API checks passed and while Jellyfin still runs; they fail the rig run if they fail."""
    if failures:
        print(f"rig: browser tests not run: {failures} API check(s) failed", flush=True)
        return
    node = shutil.which("node")
    if node is None:
        check(False, "browser tests: node is on the PATH")
        return
    print(f"rig: browser tests against {base}", flush=True)
    result = subprocess.run([node, str(ROOT / "tools/rig/e2e/run.js"), "--base", base], cwd=str(ROOT))
    check(result.returncode == 0, f"browser tests (tools/rig/e2e/run.js) pass (exit code {result.returncode})")


def finish(proc, data_dir, keep):
    if keep:
        print(f"rig: Jellyfin left running (pid {proc.pid}); data in {data_dir}")
    else:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(15)
            except subprocess.TimeoutExpired:
                proc.kill()
    log_text = ""
    for f in sorted((data_dir / "log").glob("*.log")) if (data_dir / "log").is_dir() else []:
        log_text += f.read_text(encoding="utf-8", errors="replace")
    stdout = data_dir / "stdout.log"
    if stdout.is_file():
        log_text += stdout.read_text(encoding="utf-8", errors="replace")
    errors = [l for l in log_text.splitlines() if "Babelfin" in l and ("[ERR]" in l or "[FTL]" in l or "Unhandled" in l)]
    check(not errors, "no Babelfin errors in the server log" + ("" if not errors else ": " + errors[0][:200]))
    print("ALL PASS" if failures == 0 else f"{failures} FAILURES")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    sys.exit(main())
