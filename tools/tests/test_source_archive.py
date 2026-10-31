import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('source_archive', ROOT / 'tools/source_archive.py')
source = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(source)


class SourceArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='babelfin-source-')
        self.root = Path(self.temp.name) / 'repository'
        self.root.mkdir()
        self.git('init', '-q')
        self.git('config', 'core.autocrlf', 'false')
        self.git('config', 'user.name', 'Fixture')
        self.git('config', 'user.email', 'fixture@example.invalid')
        self.put('LICENSE', 'license fixture\n')
        self.put('NOTICE', 'provenance fixture\n')
        self.put('README.md', 'Build instructions\n')
        self.put('tools/data/iso6392.txt', 'eng||en|English|anglais\n')
        self.put('tools/gen_languages.py', 'print("generator fixture")\n')
        self.put(str(source.PROJECT), '<Project><PropertyGroup><Version>1.2.3.0</Version></PropertyGroup></Project>\n')
        self.git('add', '.')
        self.git('commit', '-qm', 'Source fixture')
        self.commit = self.git('rev-parse', 'HEAD').strip()
        self.output = Path(self.temp.name) / 'out'

    def tearDown(self):
        self.temp.cleanup()

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.root), *args], text=True, encoding='utf-8')

    def put(self, path, text):
        p = self.root / path
        p.parent.mkdir(parents=True, exist_ok=True)
        with p.open('w', encoding='utf-8', newline='\n') as stream:
            stream.write(text)

    def make(self, folder='out'):
        return source.create_source_archive(self.root, self.commit, Path(self.temp.name) / folder,
                                            tag='v1.2.3-beta.1')

    def test_archive_has_exact_commit_and_generator_inputs_without_history(self):
        result = self.make()
        archive = Path(result['archive'])
        with zipfile.ZipFile(archive) as z:
            names = z.namelist()
            prefix = 'Babelfin-1.2.3.0-source/'
            self.assertIn(prefix + 'NOTICE', names)
            self.assertIn(prefix + 'tools/data/iso6392.txt', names)
            self.assertTrue(all(n.startswith(prefix) and '/.git/' not in n for n in names))
            info = json.loads(z.read(prefix + 'SOURCE.json'))
            self.assertEqual(self.commit, info['commit'])
            self.assertEqual(self.git('rev-parse', self.commit + '^{tree}').strip(), info['tree'])
            self.assertEqual('v1.2.3-beta.1', info['tag'])
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        self.assertEqual(digest, result['sha256'])
        self.assertEqual(f'{digest}  {archive.name}\n', archive.with_suffix('.zip.sha256').read_text())

    def test_ignored_files_and_untracked_private_input_are_not_archived(self):
        self.put('secret-untracked.txt', 'must not enter source archive')
        result = self.make()
        with zipfile.ZipFile(result['archive']) as z:
            self.assertFalse(any(n.endswith('secret-untracked.txt') for n in z.namelist()))

    def test_checkout_newline_settings_cannot_change_committed_source_bytes(self):
        first = self.make('lf')
        self.git('config', 'core.autocrlf', 'true')
        self.git('config', 'core.eol', 'crlf')
        second = self.make('crlf')
        self.assertEqual(Path(first['archive']).read_bytes(), Path(second['archive']).read_bytes())

    def test_reproducible_and_never_replaces_an_archive(self):
        one = self.make('one')
        two = self.make('two')
        self.assertEqual(Path(one['archive']).read_bytes(), Path(two['archive']).read_bytes())
        with self.assertRaises(source.SourceArchiveError):
            self.make('one')

    def test_rejects_noncommit_input_and_a_tag_for_the_wrong_version(self):
        for value in ('HEAD', '--all', '../secret', '0' * 40):
            with self.subTest(value=value), self.assertRaises(source.SourceArchiveError):
                source.create_source_archive(self.root, value, self.output)
        for tag in ('v9.9.9-beta.1', 'v1.2.3.0-beta', 'v1.2.3-beta',
                    'v1.2.3-beta.0', 'v1.2.3-beta.01', 'v1.2.3-beta.1+extra'):
            with self.subTest(tag=tag), self.assertRaises(source.SourceArchiveError):
                source.create_source_archive(self.root, self.commit, self.output, tag=tag)

    def test_default_and_later_numbered_beta_tags(self):
        default = source.create_source_archive(self.root, self.commit, Path(self.temp.name) / 'default')
        later = source.create_source_archive(self.root, self.commit, Path(self.temp.name) / 'later', tag='v1.2.3-beta.2')
        self.assertEqual('v1.2.3-beta.1', default['tag'])
        self.assertEqual('v1.2.3-beta.2', later['tag'])

    def test_rejects_nonzero_revision_that_cannot_map_to_semver(self):
        self.put(str(source.PROJECT), '<Project><PropertyGroup><Version>1.2.3.4</Version></PropertyGroup></Project>\n')
        self.git('add', '.')
        self.git('commit', '-qm', 'Nonzero revision')
        commit = self.git('rev-parse', 'HEAD').strip()
        with self.assertRaises(source.SourceArchiveError):
            source.create_source_archive(self.root, commit, self.output, tag='v1.2.3-beta.1')

    def test_archive_uses_committed_bytes_when_working_tree_has_edits(self):
        self.put('NOTICE', 'unsaved replacement\n')
        result = self.make()
        with zipfile.ZipFile(result['archive']) as z:
            self.assertEqual(b'provenance fixture\n', z.read('Babelfin-1.2.3.0-source/NOTICE'))


if __name__ == '__main__':
    unittest.main()
