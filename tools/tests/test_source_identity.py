import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('identity_package', ROOT / 'tools/package.py')
pkg = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(pkg)


class SourceIdentityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='babelfin-source-identity-')
        self.root = Path(self.temp.name)
        # A no-Git archive fixture must stay independent of the repository that
        # contains TMPDIR. Keep real markers inside this fixture fully visible.
        lstat = Path.lstat
        surrounding = set(self.root.parents)
        def fixture_lstat(path, *args, **kwargs):
            if path.name == '.git' and path.parent in surrounding:
                raise FileNotFoundError(str(path))
            return lstat(path, *args, **kwargs)
        boundary = mock.patch.object(Path, 'lstat', fixture_lstat)
        boundary.start()
        self.addCleanup(boundary.stop)
        self.project = self.root / 'project.csproj'
        self.project.write_text('<Project><PropertyGroup><Version>1.2.3.4</Version></PropertyGroup></Project>', encoding='utf-8')

    def tearDown(self):
        self.temp.cleanup()

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.root), *args], text=True, encoding='utf-8').strip()

    def init(self):
        self.git('init', '-q')
        self.git('config', 'user.name', 'Fixture')
        self.git('config', 'user.email', 'fixture@example.invalid')
        self.git('config', 'core.autocrlf', 'false')
        self.git('add', '.')
        self.git('commit', '-qm', 'Source fixture')

    def test_clean_git_source_has_one_exact_commit_and_dirty_inputs_are_refused(self):
        self.init()
        self.assertEqual(self.git('rev-parse', 'HEAD'), pkg.source_commit(self.project))
        self.project.write_text(self.project.read_text() + '\n', encoding='utf-8')
        with self.assertRaisesRegex(pkg.PackageError, 'clean committed'):
            pkg.source_commit(self.project)

    def test_untracked_release_source_is_not_silently_ignored(self):
        self.init()
        (self.root / 'new-source.cs').write_text('class Pending {}', encoding='utf-8')
        with self.assertRaisesRegex(pkg.PackageError, 'clean committed'):
            pkg.source_commit(self.project)

    def test_corresponding_source_descriptor_supplies_identity_without_git(self):
        descriptor = self.root / 'SOURCE.json'
        descriptor.write_text(json.dumps({'commit': '1' * 40, 'version': '1.2.3.4'}), encoding='utf-8')
        self.assertEqual('1' * 40, pkg.source_commit(self.project))
        for data in ({'commit': '0' * 40, 'version': '1.2.3.4'},
                     {'commit': '1' * 40, 'version': '9.9.9.9'},
                     {'commit': 'HEAD', 'version': '1.2.3.4'}, []):
            with self.subTest(data=data):
                descriptor.write_text(json.dumps(data), encoding='utf-8')
                with self.assertRaises(pkg.PackageError):
                    pkg.source_commit(self.project)

    def test_failed_git_validation_cannot_fall_back_to_a_loose_descriptor(self):
        self.init()
        (self.root / 'SOURCE.json').write_text(json.dumps({'commit': '1' * 40, 'version': '1.2.3.4'}), encoding='utf-8')
        answers = [subprocess.CompletedProcess([], 0, stdout=str(self.root) + '\n'),
                   subprocess.CompletedProcess([], 0, stdout=b'project.csproj'),
                   subprocess.CalledProcessError(128, ['git', 'status'])]
        with mock.patch.object(pkg.subprocess, 'run', side_effect=answers):
            with self.assertRaises(pkg.PackageError):
                pkg.source_commit(self.project)

    def test_corrupt_git_config_cannot_fall_back_to_a_loose_descriptor(self):
        self.init()
        (self.root / 'SOURCE.json').write_text(json.dumps({'commit': '1' * 40, 'version': '1.2.3.4'}), encoding='utf-8')
        self.project.write_text(self.project.read_text() + '\n<!-- uncommitted source -->', encoding='utf-8')
        (self.root / '.git/config').write_text('[invalid config', encoding='utf-8')
        with self.assertRaisesRegex(pkg.PackageError, 'Git source identity could not be verified'):
            pkg.source_commit(self.project)

    def test_corrupt_git_index_cannot_fall_back_to_a_loose_descriptor(self):
        self.init()
        (self.root / 'SOURCE.json').write_text(json.dumps({'commit': '1' * 40, 'version': '1.2.3.4'}), encoding='utf-8')
        self.project.write_text(self.project.read_text() + '\n<!-- uncommitted source -->', encoding='utf-8')
        (self.root / '.git/index').write_bytes(b'corrupt index')
        with self.assertRaisesRegex(pkg.PackageError, 'Git source identity could not be verified'):
            pkg.source_commit(self.project)

    def test_untracked_project_inside_checkout_cannot_use_archive_descriptor(self):
        self.init()
        nested = self.root / 'nested'
        nested.mkdir()
        untracked = nested / 'project.csproj'
        untracked.write_bytes(self.project.read_bytes())
        (self.root / 'SOURCE.json').write_text(json.dumps({'commit': '1' * 40, 'version': '1.2.3.4'}), encoding='utf-8')
        with self.assertRaisesRegex(pkg.PackageError, 'Git source identity could not be verified'):
            pkg.source_commit(untracked)

    def test_missing_git_with_directory_or_worktree_marker_is_an_explicit_error(self):
        for kind in ('directory', 'worktree-file'):
            with self.subTest(marker=kind):
                context = self.root / kind
                nested = context / 'nested'
                nested.mkdir(parents=True)
                project = nested / 'project.csproj'
                project.write_bytes(self.project.read_bytes())
                (context / 'SOURCE.json').write_text(json.dumps({'commit': '1' * 40, 'version': '1.2.3.4'}), encoding='utf-8')
                marker = context / '.git'
                if kind == 'directory':
                    marker.mkdir()
                else:
                    marker.write_text('gitdir: missing-worktree-metadata', encoding='utf-8')
                with mock.patch.object(pkg.subprocess, 'run', side_effect=FileNotFoundError('git unavailable')):
                    with self.assertRaisesRegex(pkg.PackageError, 'Git is required'):
                        pkg.source_commit(project)

    def test_broken_worktree_marker_cannot_use_archive_descriptor(self):
        (self.root / '.git').write_text('gitdir: missing-worktree-metadata', encoding='utf-8')
        (self.root / 'SOURCE.json').write_text(json.dumps({'commit': '1' * 40, 'version': '1.2.3.4'}), encoding='utf-8')
        with self.assertRaisesRegex(pkg.PackageError, 'Git source identity could not be verified'):
            pkg.source_commit(self.project)

    def test_clean_linked_worktree_has_the_exact_commit(self):
        self.init()
        commit = self.git('rev-parse', 'HEAD')
        linked = self.root / 'linked'
        self.git('worktree', 'add', '--quiet', '--detach', str(linked), 'HEAD')
        self.assertTrue((linked / '.git').is_file())
        self.assertEqual(commit, pkg.source_commit(linked / 'project.csproj'))

    def test_source_archive_descriptor_is_valid_when_git_is_unavailable(self):
        (self.root / 'SOURCE.json').write_text(json.dumps({'commit': '1' * 40, 'version': '1.2.3.4'}), encoding='utf-8')
        with mock.patch.object(pkg.subprocess, 'run', side_effect=FileNotFoundError('git unavailable')):
            self.assertEqual('1' * 40, pkg.source_commit(self.project))

    def test_failed_head_probe_cannot_fall_back_to_a_loose_descriptor(self):
        self.init()
        (self.root / '.git/info/exclude').write_text('/SOURCE.json\n', encoding='utf-8')
        (self.root / 'SOURCE.json').write_text(json.dumps({'commit': '1' * 40, 'version': '1.2.3.4'}), encoding='utf-8')
        with mock.patch.object(pkg.subprocess, 'check_output', side_effect=subprocess.CalledProcessError(128, ['git', 'rev-parse', 'HEAD'])):
            with self.assertRaisesRegex(pkg.PackageError, 'Git source identity could not be verified'):
                pkg.source_commit(self.project)

    def test_uncommitted_source_without_a_descriptor_is_refused(self):
        with self.assertRaisesRegex(pkg.PackageError, 'clean Git checkout'):
            pkg.source_commit(self.project)


if __name__ == '__main__':
    unittest.main()
