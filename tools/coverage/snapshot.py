"""Copy a stable working tree to fresh private staging, honoring Git text/EOL attributes."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess


def digest(data):
    return hashlib.sha256(data).hexdigest()


def snapshot(root, out):
    if out.exists() or out.is_relative_to(root):
        raise ValueError("Snapshot must be a fresh directory outside the checkout")
    names = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=root).decode("utf-8").split("\0")
    names = sorted(set(n for n in names if n and (root / n).is_file()))
    attrs = subprocess.check_output(["git", "check-attr", "-z", "--stdin", "text", "eol"], cwd=root,
                                    input=("\0".join(names) + "\0").encode("utf-8")).decode("utf-8").split("\0")
    rules = {}
    for i in range(0, len(attrs) - 1, 3):
        name, attribute, value = attrs[i:i + 3]
        rules.setdefault(name, {})[attribute] = value
    manifest = {}
    out.mkdir(parents=True)
    for name in names:
        file, dest = root / name, out / name
        if not dest.resolve().is_relative_to(out):
            raise ValueError("File escapes snapshot")
        data = file.read_bytes()
        text = rules[name].get("text")
        normalized = data
        if rules[name].get("eol") == "lf" and text != "unset" and (text != "auto" or b"\0" not in data[:8000]):
            normalized = data.replace(b"\r\n", b"\n")
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(normalized)
        manifest[name] = {"original": digest(data), "snapshot": digest(normalized)}
    changed = [name for name in names if not (root / name).is_file() or digest((root / name).read_bytes()) != manifest[name]["original"]]
    (out / "snapshot-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    if changed:
        raise RuntimeError("Source changed while copying; use a new snapshot after freeze: " + ", ".join(changed))
    print(f"Copied {len(names)} files with Git text/EOL attributes; source bytes stayed stable")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    snapshot(args.root.resolve(), args.out.resolve())
