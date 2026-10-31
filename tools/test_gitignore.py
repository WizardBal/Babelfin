"""Keep generated build and test files out of Git.

Run from any directory: python -I tools/test_gitignore.py
The checks inspect ignore rules and tracked paths without creating files.
"""
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class RepositoryIgnoreTests(unittest.TestCase):
    def ignored(self, paths):
        result = subprocess.run(
            ["git", "-C", str(ROOT), "check-ignore", "--no-index", "-z", "--stdin"],
            input=b"\0".join(path.encode("utf-8") for path in paths) + b"\0",
            capture_output=True, check=False,
        )
        self.assertIn(result.returncode, (0, 1), result.stderr)
        return {path.decode("utf-8") for path in result.stdout.split(b"\0") if path}

    def test_generated_outputs_are_ignored(self):
        paths = [
            "dist/Babelfin-1.2.3.4.zip",
            "package/stage/plugin.dll",
            "plugin/package/stage/plugin.dll",
            "plugin/src/Jellyfin.Plugin.Babelfin/bin/Release/plugin.dll",
            "plugin/src/Jellyfin.Plugin.Babelfin/obj/project.assets.json",
            "plugin/tests/Babelfin.Engine.Tests/TestResults/results.trx",
            "plugin/harness/interop/CompanionFixture.dll",
            "harness/interop/CompanionFixture.dll",
            "tools/__pycache__/example.cpython-314.pyc",
        ]
        self.assertEqual(set(paths), self.ignored(paths))

    def test_source_tests_and_release_docs_remain_trackable(self):
        paths = [
            ".gitignore", "README.md", "CHANGELOG.md", "LICENSE", "NOTICE",
            "plugin/src/Jellyfin.Plugin.Babelfin/Plugin.cs",
            "plugin/tests/Babelfin.Engine.Tests/Settings/OverrideListTests.cs",
            "plugin/package/logo.png",
            "tools/rig/e2e/run.js", "tools/test_gitignore.py",
            ".github/workflows/verify.yml", ".github/ISSUE_TEMPLATE/bug_report.yml",
            "docs/images/person-settings-genres-desktop.png",
        ]
        self.assertEqual(set(), self.ignored(paths))

    def test_local_private_and_release_material_is_ignored_by_public_rules(self):
        # Require public rules as the source even when per-clone excludes exist.
        paths = [
            ".rig/server/config/system.xml", ".verification/result.json",
            ".artifacts/report.txt", ".test-tmp/session.json",
            "plugin/harness/.verification/result.json",
            "plugin/harness/.test-tmp/session.json",
            "deliver/release.zip", "evidence/screenshot.png",
            ".private/planning.md", ".env", ".env.production",
            "server.local.json", "identity.pem", "identity.key",
            "identity.pfx", "identity.p12", "project-brief.md",
            "server-notes-local.md", "M1-findings.md", "M1-design.md",
            "UI-spec.md", "docs/phase-c-results.md",
            "docs/release-acceptance.md", "docs/provider-spike.md",
        ]
        result = subprocess.run(
            ["git", "-C", str(ROOT), "-c", "core.excludesFile=",
             "check-ignore", "--no-index", "--verbose", "-z", "--stdin"],
            input=b"\0".join(path.encode("utf-8") for path in paths) + b"\0",
            capture_output=True, check=False,
        )
        self.assertIn(result.returncode, (0, 1), result.stderr)
        fields = result.stdout.decode("utf-8").split("\0")
        records = [fields[i:i + 4] for i in range(0, len(fields) - 1, 4)]
        self.assertEqual(set(paths), {record[3] for record in records})
        self.assertTrue(all(record[0] == ".gitignore" for record in records),
                        "Private and generated material must be covered by the public .gitignore")

    def test_no_excluded_file_is_already_tracked(self):
        result = subprocess.run(
            ["git", "-C", str(ROOT), "ls-files", "-z"],
            capture_output=True, check=False,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        paths = [path.decode("utf-8") for path in result.stdout.split(b"\0") if path]
        self.assertTrue(paths, "The repository has no tracked baseline")
        self.assertEqual(set(), self.ignored(paths))


if __name__ == "__main__":
    unittest.main(verbosity=2)

