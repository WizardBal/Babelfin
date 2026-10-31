"""Reproducible whole-plugin coverage. Run with Python -Xutf8."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent


def read_json(file):
    return json.loads(Path(file).read_text(encoding="utf-8"))


def write_json(file, data):
    Path(file).write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


def manifest(root):
    files = list((root / "plugin").rglob("*.cs")) + list((root / "plugin").rglob("*.csproj"))
    files += [p for p in (root / "plugin/src").rglob("*") if p.is_file()]
    files += list((root / "tools/tests").rglob("*.js")) + list((root / "plugin/harness").glob("*.html"))
    files += [root / "global.json", root / ".gitattributes"]
    if (root / "tools/coverage").exists():
        files += [p for p in (root / "tools/coverage").iterdir() if p.is_file()]
    files += [root / "tools/rig/e2e/cdp.js", root / "tools/rig/e2e/run.js"]
    return {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(files) if not {"bin", "obj"}.intersection(p.relative_to(root).parts)}


def source_digests(hashes):
    def digest(values):
        return hashlib.sha256(json.dumps(values, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()
    return {"all_measured_inputs_sha256": digest(hashes),
            "production_sha256": digest({k: v for k, v in hashes.items() if k.startswith("plugin/src/")})}


def run(args, root, log, env=None):
    print(subprocess.list2cmdline([str(a) for a in args]), flush=True)
    with open(log, "w", encoding="utf-8") as stream:
        stream.write("Command: " + json.dumps([str(a) for a in args]) + "\n")
        stream.flush()
        result = subprocess.run([str(a) for a in args], cwd=root, env=env,
                                stdout=stream, stderr=subprocess.STDOUT, encoding="utf-8", errors="replace")
    if result.returncode:
        print(Path(log).read_text(encoding="utf-8")[-5000:], file=sys.stderr)
        raise RuntimeError(f"Command failed ({result.returncode}); see {Path(log).name}")


def summarize(xml, root):
    tree = ET.parse(xml).getroot()
    result = {key: int(tree.get(key)) for key in ("lines-valid", "lines-covered", "branches-valid", "branches-covered")}
    # Keep the collector's method denominator separate from JS functions.
    data = read_json(Path(xml).with_name(Path(xml).name.replace(".cobertura.xml", ".json")))
    files = []
    for module in data.values():
        for name, classes in module.items():
            lines = {}
            for members in classes.values():
                for method in members.values():
                    for line, hits in method["Lines"].items():
                        lines[line] = lines.get(line, False) or hits > 0
            file = Path(name)
            relative = file.relative_to(root).as_posix() if file.is_relative_to(root) else str(file)
            files.append({"file": relative, "lines": len(lines), "covered": sum(lines.values())})
    methods = [m for module in data.values() for classes in module.values() for members in classes.values() for m in members.values() if m["Lines"]]
    result["methods-valid"] = len(methods)
    result["methods-covered"] = sum(any(h > 0 for h in m["Lines"].values()) for m in methods)
    result["top_line_gaps"] = sorted(files, key=lambda f: f["lines"] - f["covered"], reverse=True)[:12]
    measured = {Path(name).resolve() for module in data.values() for name in module}
    source_files = {p.resolve() for p in (root / "plugin/src/Jellyfin.Plugin.Babelfin").rglob("*.cs") if not {"bin", "obj"}.intersection(p.parts)}
    result["source_cs_files"] = len(source_files)
    result["measured_source_cs_files"] = len(measured & source_files)
    result["unmapped_source_cs_files"] = [p.relative_to(root).as_posix() for p in sorted(source_files - measured)]
    result["generated_documents"] = [str(p) for p in sorted(measured - source_files)]
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--tools", type=Path, required=True)
    parser.add_argument("--mode", choices=["setup", "all", "dotnet", "node", "report-js"], default="all")
    parser.add_argument("--raw", type=Path, action="append", default=[], help="Additional browser or Node raw directories")
    opts = parser.parse_args()
    root, out, tools = opts.root.resolve(), opts.out.resolve(), opts.tools.resolve()
    if out.is_relative_to(root) or tools.is_relative_to(root):
        parser.error("Keep --out and --tools outside the source tree")
    out.mkdir(parents=True, exist_ok=True)
    tools.mkdir(parents=True, exist_ok=True)
    if opts.mode == "setup":
        run(["dotnet", "tool", "install", "coverlet.console", "--version", "10.0.0", "--tool-path", tools / "dotnet"], root, out / "setup-dotnet.log")
        js = tools / "js"
        js.mkdir(exist_ok=True)
        import shutil
        for name in ("package.json", "package-lock.json"):
            shutil.copyfile(HERE / name, js / name)
        run(["npm.cmd" if os.name == "nt" else "npm", "ci", "--ignore-scripts", "--no-audit", "--no-fund"], js, out / "setup-js.log")
        return
    before = manifest(root)
    write_json(out / "source-before.json", before)
    write_json(out / "source-digests.json", source_digests(before))
    run(["dotnet", "--version"], root, out / "dotnet-version.log")
    run(["dotnet", "--list-runtimes"], root, out / "dotnet-runtimes.log")
    run(["node", "--version"], root, out / "node-version.log")
    if opts.mode in ("all", "dotnet"):
        build = out / "build"
        for name, project in [("unit", "plugin/tests/Babelfin.Engine.Tests/Babelfin.Engine.Tests.csproj"), ("http", "plugin/harness/Harness.csproj")]:
            run(["dotnet", "build", project, "-c", "Release", "--artifacts-path", build,
                 "-p:DebugType=portable", "-p:DebugSymbols=true", "-v:q"], root, out / f"build-{name}.log")
        unit = build / "bin/Babelfin.Engine.Tests/release/Babelfin.Engine.Tests.dll"
        harness = build / "bin/Harness/release"
        coverlet = tools / "dotnet" / ("coverlet.exe" if os.name == "nt" else "coverlet")
        common = ["--include", "[Jellyfin.Plugin.Babelfin]*", "--format", "json", "--format", "cobertura"]
        run([coverlet, unit, "--target", "dotnet", "--targetargs", subprocess.list2cmdline(["vstest", str(unit)]),
             *common, "--output", out / "unit"], root, out / "unit.log")
        # The existing harness creates and deletes its data directory. Only a fresh child of this private output is supplied.
        http_data = out / "http-data"
        if http_data.exists():
            raise RuntimeError("Use a fresh --out directory; HTTP data already exists")
        run([coverlet, harness, "--target", "dotnet", "--targetargs", subprocess.list2cmdline([str(harness / "Harness.dll"), str(http_data)]),
             *common, "--merge-with", out / "unit.json", "--output", out / "merged"], root, out / "http.log")
        write_json(out / "dotnet-summary.json", {"unit": summarize(out / "unit.cobertura.xml", root), "merged": summarize(out / "merged.cobertura.xml", root)})
    if opts.mode in ("all", "node"):
        env = os.environ.copy()
        env["BF_COVERAGE_ROOT"], env["BF_COVERAGE_DIR"] = str(root), str(out / "js-raw")
        preload = (HERE / "node-preload.cjs").as_posix()
        env["NODE_OPTIONS"] = (env.get("NODE_OPTIONS", "") + f' --require="{preload}"').strip()
        tests = sorted((root / "tools/tests").glob("*.test.js"))
        # Run each node:test module directly. Node 25's --test coordinator can preload
        # before its inspector exists; direct modules retain the normal test runner.
        for test in tests:
            run(["node", test], root, out / (test.name + ".log"), env)
        for name in ("admin/test.js", "person/smoke.js"):
            run(["node", root / "tools/tests/ui-smoke" / name], root, out / (name.replace("/", "-") + ".log"), env)
    if opts.mode in ("all", "node", "report-js"):
        # Consolidate raw inputs by copying under fresh unique names; never remove evidence.
        import shutil
        import uuid
        raw = out / "js-inputs"
        raw.mkdir(exist_ok=True)
        for directory in ([out / "js-raw"] if (out / "js-raw").exists() else []) + opts.raw:
            for file in directory.glob("*.json"):
                shutil.copyfile(file, raw / (uuid.uuid4().hex + ".json"))
        run(["node", HERE / "report-js.cjs", tools / "js", root, raw, out / "js-report"], root, out / "js-report.log")
    after = manifest(root)
    write_json(out / "source-after.json", after)
    if before != after:
        write_json(out / "SOURCE-CHANGED.json", sorted(k for k in before.keys() | after.keys() if before.get(k) != after.get(k)))
        raise RuntimeError("Source changed during measurement; archive/snapshot and rerun before publishing percentages")


if __name__ == "__main__":
    main()
