import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import runpy
import tempfile
import threading
import unittest
from unittest import mock
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('staged_package', ROOT / 'tools/package.py')
pkg = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(pkg)
VERIFY_DLL = pkg.verify_dll


class PackageTests(unittest.TestCase):
    def setUp(self):
        # Packaging byte fixtures are deliberately not executable assemblies; identity cases exercise the real validator separately.
        patcher = mock.patch.object(pkg, "verify_dll", return_value="1" * 40)
        patcher.start()
        self.addCleanup(patcher.stop)
        parent = Path(os.environ.get('BABELFIN_TEST_TMP', tempfile.gettempdir()))
        parent.mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=parent, prefix='package-')
        self.root = Path(self.temp.name)
        self.csproj = self.root / 'input.csproj'
        self.csproj.write_text('<Project><PropertyGroup><Version>1.2.3.4</Version><AssemblyVersion>1.2.3.4</AssemblyVersion><FileVersion>1.2.3.4</FileVersion></PropertyGroup></Project>')
        self.dll = self.root / pkg.DLL_NAME
        self.dll.write_bytes(b'BOUNDED TEST DLL - NOT A RELEASE')
        self.logo = self.root / 'logo.png'
        self.logo.write_bytes(b'test-logo')
        self.license = self.root / 'LICENSE'
        self.license.write_bytes(b'Bounded license fixture\r\nExact bytes preserved.\r\n')
        self.notice = self.root / 'NOTICE'
        self.notice.write_bytes(b'Upstream provenance fixture\n')
        self.stamp = pkg.release_time('2026-10-23T00:00:00Z')

    def tearDown(self):
        self.assertEqual(self.root.resolve().parent, Path(os.environ.get('BABELFIN_TEST_TMP', tempfile.gettempdir())).resolve())
        self.temp.cleanup()

    def package(self, folder='out', **kwargs):
        result = pkg.create_package(csproj=self.csproj, dll=self.dll, logo=self.logo,
                                  license_file=self.license, output=self.root / folder,
                                  stamp=self.stamp, **kwargs)
        path = Path(result['zip'])
        expected = hashlib.sha256(path.read_bytes()).hexdigest()
        self.assertEqual(expected, result['sha256'])
        self.assertEqual(f'{expected}  {path.name}\n', path.with_suffix('.zip.sha256').read_text())
        return result

    def test_notice_is_required_and_preserved_in_the_flat_package(self):
        result = self.package('notice-present')
        with zipfile.ZipFile(result['zip']) as archive:
            self.assertEqual({'Jellyfin.Plugin.Babelfin.dll', 'LICENSE', 'NOTICE', 'logo.png', 'meta.json'}, set(archive.namelist()))
            self.assertEqual(self.notice.read_bytes(), archive.read('NOTICE'))
        self.notice.unlink()
        with self.assertRaises(pkg.PackageError):
            self.package('notice-missing')

    def test_empty_notice_is_refused_before_output_is_created(self):
        self.notice.write_bytes(b'   \n')
        with self.assertRaises(pkg.PackageError):
            self.package('notice-empty')
        self.assertFalse((self.root / 'notice-empty').exists())

    def test_release_metadata_declares_the_distribution_license(self):
        result = self.package('license-metadata')
        with zipfile.ZipFile(result['zip']) as archive:
            self.assertEqual('GPL-3.0-only', json.loads(archive.read('meta.json')).get('license'))

    def test_supplied_dll_identity_is_checked_before_creating_release_files(self):
        identity = {'assembly_name': 'Jellyfin.Plugin.Babelfin', 'assembly_version': '1.2.3.4',
                    'file_version': '1.2.3.4', 'assembly_file_version': '1.2.3.4',
                    'sha256': hashlib.sha256(self.dll.read_bytes()).hexdigest(),
                    'informational_version': '1.2.3.4+' + '1' * 40}
        for field, value in [('assembly_name', 'Another.Plugin'), ('assembly_version', '1.2.3.3'),
                             ('file_version', '1.2.3.3'), ('assembly_file_version', '1.2.3.3'), ('sha256', '0' * 64), ('informational_version', '1.2.3.4+' + '2' * 40)]:
            with self.subTest(field=field):
                output = self.root / ('wrong-' + field)
                with mock.patch.object(pkg, 'verify_dll', wraps=VERIFY_DLL), \
                     mock.patch.object(pkg, 'read_dll_identity', return_value={**identity, field: value}), \
                     mock.patch.object(pkg, 'source_commit', return_value='1' * 40):
                    with self.assertRaises(pkg.PackageError):
                        pkg.create_package(csproj=self.csproj, dll=self.dll, logo=self.logo,
                                           license_file=self.license, output=output,
                                           stamp=self.stamp)
                self.assertFalse(output.exists())

    def test_import_has_no_build_or_file_effects(self):
        with mock.patch('subprocess.run', side_effect=AssertionError('build on import')), mock.patch('pathlib.Path.mkdir', side_effect=AssertionError('write on import')):
            loaded = runpy.run_path(str(ROOT / 'tools/package.py'))
        self.assertTrue(callable(loaded['main']))
        self.assertTrue(callable(loaded['create_package']))

    def test_default_changelog_describes_first_public_beta_and_config_is_preserved(self):
        result = self.package()
        with zipfile.ZipFile(result['zip']) as archive:
            text = json.loads(archive.read('meta.json'))['changelog']
        self.assertIn('first public beta', text.lower())
        self.assertIn('audio', text.lower())
        self.assertIn('subtitles', text.lower())
        self.assertNotRegex(text, r'(?i)private|0\.14|\bF[0-9]+(?:\.[0-9]+)*\b')
        changelog = 'First public beta: audio and subtitles. Préférences per person.'
        explicit = self.package('explicit-changelog', changelog=changelog, source_url='https://example.invalid/beta.zip')
        with zipfile.ZipFile(explicit['zip']) as archive:
            self.assertEqual(changelog, json.loads(archive.read('meta.json'))['changelog'])
        self.assertEqual(changelog, json.loads(Path(explicit['manifest']).read_bytes())[0]['versions'][0]['changelog'])

    def test_flat_zip_md5_and_metadata(self):
        result = self.package()
        path = Path(result['zip'])
        with zipfile.ZipFile(path) as archive:
            self.assertEqual([pkg.DLL_NAME, 'LICENSE', 'NOTICE', 'logo.png', 'meta.json'], archive.namelist())
            self.assertEqual(self.license.read_bytes(), archive.read('LICENSE'))
            meta = json.loads(archive.read('meta.json'))
            self.assertEqual('WizardBal', meta['owner'])
            self.assertIs(True, meta['autoUpdate'], 'Catalog installs must remain eligible for Jellyfin updates')
            self.assertEqual('12.0.0.0', meta['targetAbi'])
            self.assertEqual('1.2.3.4', meta['version'])
            self.assertNotIn('rules', meta['description'].lower())
            self.assertNotIn('languages page', meta['description'].lower())
        digest = hashlib.md5(path.read_bytes(), usedforsecurity=False).hexdigest()
        self.assertEqual(digest, result['md5'])
        sha256 = hashlib.sha256(path.read_bytes()).hexdigest()
        self.assertEqual(sha256, result['sha256'])
        self.assertEqual(f'{sha256}  {path.name}\n', path.with_suffix('.zip.sha256').read_text())
        self.assertEqual(f'{digest}  {path.name}\n', path.with_suffix('.zip.md5').read_text())
        self.assertIsNone(result['manifest'])
        self.assertFalse((path.parent / 'manifest.json').exists())

    def test_reproducible_bytes_ignore_source_mtime_and_mode(self):
        options = dict(source_url='https://example.invalid/Babelfin.zip', repository_url='https://example.invalid/manifest.json', image_url='https://example.invalid/logo.png')
        first = self.package('one', **options)
        os.utime(self.dll, (1234567890, 1234567890))
        os.chmod(self.dll, 0o600)
        os.utime(self.license, (1234567890, 1234567890))
        os.chmod(self.license, 0o600)
        second = self.package('two', **options)
        self.assertEqual(Path(first['zip']).read_bytes(), Path(second['zip']).read_bytes())
        self.assertEqual(Path(first['manifest']).read_bytes(), Path(second['manifest']).read_bytes())
        self.assertEqual(Path(first['zip']).with_suffix('.zip.sha256').read_bytes(), Path(second['zip']).with_suffix('.zip.sha256').read_bytes())
        manifest = json.loads(Path(first['manifest']).read_text())[0]
        self.assertEqual('WizardBal', manifest['owner'])
        self.assertEqual(options['image_url'], manifest['imageUrl'])
        release = manifest['versions'][0]
        self.assertEqual(first['md5'], release['checksum'])
        self.assertEqual(options['source_url'], release['sourceUrl'])
        self.assertEqual(options['repository_url'], release['repositoryUrl'])
        self.assertEqual('12.0.0.0', release['targetAbi'])

    def test_missing_or_blank_license_rejected_before_distributable_outputs(self):
        self.license.unlink()
        with self.assertRaisesRegex(pkg.PackageError, 'LICENSE'):
            self.package()
        self.assertFalse((self.root / 'out').exists())
        self.license.write_bytes(b' \r\n\t')
        with self.assertRaisesRegex(pkg.PackageError, 'LICENSE'):
            self.package()
        self.assertFalse((self.root / 'out').exists())

    def test_license_content_changes_zip_checksum(self):
        first = self.package('one')
        self.license.write_bytes(b'Different bounded fixture')
        second = self.package('two')
        self.assertNotEqual(first['md5'], second['md5'])

    def test_source_alone_does_not_invent_repository_or_image_url(self):
        result = self.package(source_url='https://example.invalid/release.zip')
        entry = json.loads(Path(result['manifest']).read_text())[0]
        self.assertNotIn('imageUrl', entry)
        self.assertNotIn('projectUrl', entry)
        self.assertNotIn('repositoryUrl', entry['versions'][0])

    def test_repository_or_image_without_source_does_not_create_manifest(self):
        result = self.package(repository_url='https://example.invalid/manifest.json', image_url='https://example.invalid/image.png')
        self.assertIsNone(result['manifest'])

    def test_clean_output_allows_unrelated_json_filename_but_rejects_manifest(self):
        out = self.root / 'clean-output'
        out.mkdir()
        (out / 'json').write_text('unrelated')
        pkg.require_clean_output(out)
        (out / 'manifest.json').write_text('existing')
        with self.assertRaises(pkg.PackageError):
            pkg.require_clean_output(out)
        self.assertEqual('existing', (out / 'manifest.json').read_text())

    def test_stale_manifest_without_source_is_rejected(self):
        out = self.root / 'out'
        out.mkdir()
        (out / 'manifest.json').write_text('existing')
        with self.assertRaises(pkg.PackageError):
            self.package()
        self.assertEqual(['manifest.json'], sorted(p.name for p in out.iterdir()))
        self.assertEqual('existing', (out / 'manifest.json').read_text())

    def test_every_existing_release_artifact_is_rejected_before_writes(self):
        for i, name in enumerate(('Babelfin-1.2.3.4.zip', 'Babelfin-1.2.3.4.zip.md5', 'Babelfin-1.2.3.4.zip.sha256',
                                  'Babelfin-1.0.0.0.zip', 'manifest.json')):
            with self.subTest(name=name):
                out = self.root / str(i)
                out.mkdir()
                (out / name).write_bytes(b'prior release artifact')
                with mock.patch.object(pkg, 'atomic_write', side_effect=AssertionError('write before clean-output check')):
                    with self.assertRaisesRegex(pkg.PackageError, 'clean output'):
                        self.package(str(i), source_url='https://example.invalid/release.zip')
                self.assertEqual([name], [p.name for p in out.iterdir()])
                self.assertEqual(b'prior release artifact', (out / name).read_bytes())

    def test_manifest_write_failure_cannot_corrupt_an_existing_bundle(self):
        self.package(source_url='https://example.invalid/release.zip')
        out = self.root / 'out'
        original = {p.name: p.read_bytes() for p in out.iterdir()}
        self.dll.write_bytes(b'changed DLL for second invocation')
        write = pkg.atomic_write
        def fail_manifest(path, contents):
            if path.name == 'manifest.json':
                raise OSError('injected manifest write failure')
            return write(path, contents)
        with mock.patch.object(pkg, 'atomic_write', side_effect=fail_manifest) as attempted:
            with self.assertRaises(pkg.PackageError):
                self.package(source_url='https://example.invalid/release.zip')
            attempted.assert_not_called()
        self.assertEqual(original, {p.name: p.read_bytes() for p in out.iterdir()})

    def test_failed_fresh_bundle_is_not_overwritten_by_a_retry(self):
        write = pkg.atomic_write
        def fail_manifest(path, contents):
            if path.name == 'manifest.json':
                raise OSError('injected manifest write failure')
            return write(path, contents)
        with mock.patch.object(pkg, 'atomic_write', side_effect=fail_manifest):
            with self.assertRaises(OSError):
                self.package(source_url='https://example.invalid/release.zip')
        out = self.root / 'out'
        partial = {p.name: p.read_bytes() for p in out.iterdir()}
        self.assertNotIn('manifest.json', partial)
        self.dll.write_bytes(b'changed DLL after interrupted generation')
        with self.assertRaisesRegex(pkg.PackageError, 'clean output'):
            self.package(source_url='https://example.invalid/release.zip')
        self.assertEqual(partial, {p.name: p.read_bytes() for p in out.iterdir()})

    def test_cli_existing_artifacts_fail_before_build(self):
        out = self.root / 'prior'
        out.mkdir()
        (out / 'Babelfin-1.0.0.0.zip').write_bytes(b'prior artifact')
        with mock.patch.object(pkg, 'build_plugin', side_effect=AssertionError('build before output check')):
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                pkg.main(['--root', str(ROOT), '--timestamp', self.stamp.isoformat(), '--output', str(out)])
        self.assertEqual(1, error.exception.code)
        self.assertEqual(b'prior artifact', (out / 'Babelfin-1.0.0.0.zip').read_bytes())

    def test_independent_race_repro_rejects_competitor_for_both_public_entry_points(self):
        # Adapted from the independent review's real interleaving: both clean
        # checks precede A's ZIP, B replaces it, then A resumes MD5/manifest.
        # The fixed competitor must fail the O_EXCL claim before its clean check.
        for first, second in (('create', 'create'), ('main', 'main'), ('main', 'create'), ('create', 'main')):
            with self.subTest(first=first, second=second):
                out = self.root / ('race-' + first + '-' + second)
                dll_b = self.root / 'b.dll'
                dll_b.write_bytes(b'B DLL')
                a_clean, b_ready, a_zip, b_done = (threading.Event() for _ in range(4))
                real_clean, real_write = pkg.require_clean_output, pkg.atomic_write
                reports, errors = {}, {}

                def clean(output):
                    real_clean(output)
                    if threading.current_thread().name == 'A' and not a_clean.is_set():
                        a_clean.set()
                        if not b_ready.wait(10): raise AssertionError('competitor did not reach claim/clean')
                    elif threading.current_thread().name == 'B':
                        b_ready.set()

                def write(path, content):
                    if path.suffix == '.zip':
                        if threading.current_thread().name == 'B':
                            if not a_zip.wait(10): raise AssertionError('A ZIP not reached')
                            real_write(path, content)
                        else:
                            real_write(path, content)
                            a_zip.set()
                            if not b_done.wait(10): raise AssertionError('competitor did not complete')
                    else:
                        real_write(path, content)

                def generate(name, entry):
                    dll = self.dll if name == 'A' else dll_b
                    try:
                        if entry == 'main':
                            reports[name] = pkg.main(['--root', str(ROOT), '--dll', str(dll), '--logo', str(self.logo),
                                                      '--license', str(self.license), '--output', str(out),
                                                      '--timestamp', self.stamp.isoformat(), '--source-url', 'https://example.invalid/release.zip'])
                        else:
                            reports[name] = pkg.create_package(csproj=ROOT / pkg.PROJECT, dll=dll, logo=self.logo,
                                license_file=self.license,
                                output=out, stamp=self.stamp, source_url='https://example.invalid/release.zip')
                    except (Exception, SystemExit) as error:
                        errors[name] = error
                    finally:
                        if name == 'B':
                            b_ready.set()
                            b_done.set()

                with mock.patch.object(pkg, 'require_clean_output', side_effect=clean), mock.patch.object(pkg, 'atomic_write', side_effect=write), contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    a = threading.Thread(target=generate, args=('A', first), name='A')
                    b = threading.Thread(target=generate, args=('B', second), name='B')
                    a.start()
                    self.assertTrue(a_clean.wait(10))
                    b.start()
                    a.join(15); b.join(15)
                self.assertFalse(a.is_alive() or b.is_alive())
                self.assertEqual({'A'}, set(reports), errors)
                self.assertEqual({'B'}, set(errors))
                self.assertIsInstance(errors['B'], SystemExit if second == 'main' else pkg.PackageError)
                path = next(out.glob('*.zip'))
                digest = hashlib.md5(path.read_bytes(), usedforsecurity=False).hexdigest()
                self.assertEqual(digest, path.with_suffix('.zip.md5').read_text().split()[0])
                self.assertEqual(digest, json.loads((out / 'manifest.json').read_text())[0]['versions'][0]['checksum'])
                with zipfile.ZipFile(path) as archive:
                    self.assertEqual(self.dll.read_bytes(), archive.read(pkg.DLL_NAME))
                self.assertFalse((out.parent / f'.{out.name}.babelfin-package.lock').exists())

    def test_main_holds_claim_during_build_and_calls_private_packager_without_relocking(self):
        entered, release = threading.Event(), threading.Event()
        reports = []
        out = self.root / 'build-claim'
        def build(*args):
            entered.set()
            if not release.wait(10): raise AssertionError('build release missing')
            return self.dll
        def generate():
            try:
                reports.append(pkg.main(['--root', str(ROOT), '--output', str(out), '--timestamp', self.stamp.isoformat()]))
            except (Exception, SystemExit) as error:
                reports.append(error)
        with mock.patch.object(pkg, 'build_plugin', side_effect=build), contextlib.redirect_stdout(io.StringIO()):
            thread = threading.Thread(target=generate)
            thread.start()
            try:
                self.assertTrue(entered.wait(10))
                with self.assertRaisesRegex(pkg.PackageError, 'claimed'):
                    self.package('build-claim')
            finally:
                release.set()
                thread.join(15)
        self.assertFalse(thread.is_alive())
        self.assertEqual([0], reports)

    def test_preexisting_claim_is_never_stolen_or_removed(self):
        claim = self.root / '.out.babelfin-package.lock'
        claim.write_bytes(b'claim owned by another invocation')
        with self.assertRaisesRegex(pkg.PackageError, 'claimed'):
            self.package()
        self.assertEqual(b'claim owned by another invocation', claim.read_bytes())
        self.assertFalse((self.root / 'out').exists())

    def test_failed_generation_releases_its_claim_for_clean_retry(self):
        with mock.patch.object(pkg, 'atomic_write', side_effect=OSError('injected first write failure')):
            with self.assertRaises(OSError):
                self.package()
        self.assertFalse((self.root / '.out.babelfin-package.lock').exists())
        self.assertTrue(Path(self.package()['zip']).is_file())

    def test_unsafe_or_incomplete_urls_are_rejected_before_outputs(self):
        for url in ('', 'not-published', 'file:///tmp/package.zip', 'https://u:secret@example.invalid/pkg.zip', 'https://example.invalid/a.zip\n', 'https://example.invalid/a.zip#fragment', 'https://example.invalid:99999/a.zip', 'https://example.invalid/download'):
            with self.subTest(url=url), self.assertRaises(pkg.PackageError):
                self.package(source_url=url)
        self.assertFalse((self.root / 'out').exists())

    def test_namespaced_xml_comments_do_not_spoof_version(self):
        self.csproj.write_text('<Project xmlns="urn:test"><!-- <Version>9.9.9.9</Version> --><PropertyGroup><Version>1.2.3.4</Version></PropertyGroup></Project>')
        self.assertEqual('1.2.3.4', pkg.read_version(self.csproj))

    def test_bad_version_properties_are_rejected(self):
        fixtures = [
            '<Project/>',
            '<Project><PropertyGroup><Version>1.2.3.4</Version><Version>2.3.4.5</Version></PropertyGroup></Project>',
            '<Project><PropertyGroup Condition="true"><Version>1.2.3.4</Version></PropertyGroup></Project>',
            '<Project><PropertyGroup><Version Condition="true">1.2.3.4</Version></PropertyGroup></Project>',
            '<Project><PropertyGroup><Version>$(VersionPrefix)</Version></PropertyGroup></Project>',
            '<Project><PropertyGroup><Version>1.2.3.4</Version><AssemblyVersion>1.2.3.5</AssemblyVersion></PropertyGroup></Project>',
        ]
        fixtures += [f'<Project><PropertyGroup><Version>{value}</Version></PropertyGroup></Project>' for value in ('../evil', '1.2.3', '1.2.3.4-suffix', '01.2.3.4', '1.2.3.65535', '1.2.3.4/../../x')]
        for content in fixtures:
            with self.subTest(content=content):
                self.csproj.write_text(content)
                with self.assertRaises(pkg.PackageError):
                    pkg.read_version(self.csproj)

    def test_explicit_time_and_source_date_epoch(self):
        epoch = int(self.stamp.timestamp())
        self.assertEqual(self.stamp, pkg.release_time(environ={'SOURCE_DATE_EPOCH': str(epoch)}))
        self.assertEqual(self.stamp, pkg.release_time('2026-10-23T02:00:00+02:00'))
        for value in ('2026-10-23T00:00:00', 'not-a-date', '1970-01-01T00:00:00Z'):
            with self.subTest(value=value), self.assertRaises(pkg.PackageError):
                pkg.release_time(value)
        for env in ({}, {'SOURCE_DATE_EPOCH': 'bogus'}, {'SOURCE_DATE_EPOCH': '999999999999999999999'}):
            with self.subTest(env=env), self.assertRaises(pkg.PackageError):
                pkg.release_time(environ=env)

    def test_copied_translation_inputs_remain_consistent(self):
        pkg.validate_translations(ROOT)

    def test_cli_without_output_uses_dist(self):
        source = self.root / 'default-output-source'
        fixtures = {
            pkg.PROJECT: self.csproj.read_bytes(),
            pkg.PROJECT.parent / 'Assets/i18n.js': b"var SHIPPED = ['en'];",
            pkg.PROJECT.parent / 'Assets/i18n/en.json': b'{}',
            pkg.PROJECT.parent / 'Assets/i18n-admin/en.json': b'{}',
            Path('LICENSE'): self.license.read_bytes(),
            Path('NOTICE'): self.notice.read_bytes(),
            Path('plugin/package/logo.png'): self.logo.read_bytes(),
        }
        for relative, content in fixtures.items():
            target = source / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
        with mock.patch.object(pkg, 'build_plugin', side_effect=AssertionError('unexpected build')), contextlib.redirect_stdout(io.StringIO()):
            code = pkg.main(['--root', str(source), '--dll', str(self.dll), '--timestamp', self.stamp.isoformat()])
        self.assertEqual(0, code)
        self.assertTrue((source / 'dist' / 'Babelfin-1.2.3.4.zip').is_file())
        self.assertFalse((source / 'deliver').exists())

    def test_cli_config_explicit_urls_no_build(self):
        config = self.root / 'release.json'
        config.write_text(json.dumps({'timestamp': '2026-10-23T00:00:00Z', 'source_url': 'https://example.invalid/config.zip', 'repository_url': 'https://example.invalid/manifest.json'}))
        output = self.root / 'cli'
        with mock.patch.object(pkg, 'build_plugin', side_effect=AssertionError('unexpected build')), contextlib.redirect_stdout(io.StringIO()):
            code = pkg.main(['--root', str(ROOT), '--dll', str(self.dll), '--output', str(output), '--config', str(config), '--source-url', 'https://example.invalid/override.zip'])
        self.assertEqual(0, code)
        release = json.loads((output / 'manifest.json').read_text())[0]['versions'][0]
        self.assertEqual('https://example.invalid/override.zip', release['sourceUrl'])
        self.assertEqual(pkg.read_version(ROOT / pkg.PROJECT), release['version'])
        with zipfile.ZipFile(next(output.glob('*.zip'))) as archive:
            self.assertEqual((ROOT / 'LICENSE').read_bytes(), archive.read('LICENSE'))

    def test_cli_missing_license_fails_before_build(self):
        with mock.patch.object(pkg, 'build_plugin', side_effect=AssertionError('build before license check')):
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                pkg.main(['--root', str(ROOT), '--timestamp', self.stamp.isoformat(), '--license', str(self.root / 'missing')])
        self.assertEqual(1, error.exception.code)

    def test_cli_explicit_license_overrides_root_default(self):
        output = self.root / 'cli-license'
        with mock.patch.object(pkg, 'build_plugin', side_effect=AssertionError('unexpected build')), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, pkg.main(['--root', str(ROOT), '--dll', str(self.dll), '--output', str(output),
                                         '--timestamp', self.stamp.isoformat(), '--license', str(self.license)]))
        with zipfile.ZipFile(next(output.glob('*.zip'))) as archive:
            self.assertEqual(self.license.read_bytes(), archive.read('LICENSE'))

    def test_private_repository_identity_does_not_imply_published_artifacts(self):
        config = json.loads((ROOT / 'release-config.json').read_text())
        result = self.package(project_url=config['project_url'])
        self.assertEqual(config['project_url'], result['project_url'])
        self.assertIsNone(result['manifest'])
        self.assertFalse((self.root / 'out/manifest.json').exists())
        with zipfile.ZipFile(result['zip']) as archive:
            self.assertEqual('WizardBal', json.loads(archive.read('meta.json'))['owner'])

    def test_beta_config_uses_planned_release_asset_urls_and_plain_summary(self):
        config = json.loads((ROOT / 'release-config.json').read_text())
        self.assertEqual({
            'project_url': 'https://github.com/WizardBal/Babelfin',
            'source_url': 'https://github.com/WizardBal/Babelfin/releases/download/v0.19.0-beta.1/Babelfin-0.19.0.0.zip',
            'repository_url': 'https://github.com/WizardBal/Babelfin/releases/download/catalog/manifest.json',
            'image_url': 'https://raw.githubusercontent.com/WizardBal/Babelfin/main/plugin/package/logo.png',
        }, {key: value for key, value in config.items() if key != 'changelog'})
        self.assertIsInstance(config['changelog'], str)
        self.assertTrue(config['changelog'].strip())
        self.assertIn('beta', config['changelog'].lower())
        self.assertIn("android tv, swiftfin, streamyfin and physical tvs have not been tested", config['changelog'].lower())
        self.assertNotRegex(config['changelog'], r'\bF[0-9]+(?:\.[0-9]+)*\b')
        self.assertNotIn('timestamp', config)

    def test_beta_cli_final_commit_time_and_exact_zip_checksum(self):
        source = self.root / 'source'
        fixtures = {
            pkg.PROJECT: self.csproj.read_bytes().replace(b'1.2.3.4', b'0.19.0.0'),
            pkg.PROJECT.parent / 'Assets/i18n.js': b"var SHIPPED = ['en'];",
            pkg.PROJECT.parent / 'Assets/i18n/en.json': b'{}',
            pkg.PROJECT.parent / 'Assets/i18n-admin/en.json': b'{}',
            Path('LICENSE'): self.license.read_bytes(),
            Path('NOTICE'): self.notice.read_bytes(),
            Path('plugin/package/logo.png'): self.logo.read_bytes(),
        }
        for relative, content in fixtures.items():
            path = source / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        config = json.loads((ROOT / 'release-config.json').read_text())
        packages, catalogs = [], []
        # A final commit's supplied committer time wins over an inherited epoch.
        # No Git operation or build is needed for this bounded DLL fixture.
        for folder in ('first', 'second'):
            output = self.root / folder
            stdout = io.StringIO()
            with mock.patch.dict(os.environ, {'SOURCE_DATE_EPOCH': '0'}), mock.patch.object(pkg, 'build_plugin', side_effect=AssertionError('unexpected build')), mock.patch('subprocess.run', side_effect=AssertionError('unexpected subprocess')), contextlib.redirect_stdout(stdout):
                code = pkg.main(['--root', str(source), '--dll', str(self.dll), '--output', str(output),
                                 '--config', str(ROOT / 'release-config.json'),
                                 '--timestamp', '2026-10-22T23:41:08-06:00'])
            self.assertEqual(0, code)
            report, _ = json.JSONDecoder().raw_decode(stdout.getvalue())
            self.assertEqual(config['project_url'], report['project_url'])
            self.assertIn('planned publication targets', stdout.getvalue())
            self.assertIn('public availability has not been verified', stdout.getvalue())
            path = output / 'Babelfin-0.19.0.0.zip'
            digest = hashlib.md5(path.read_bytes(), usedforsecurity=False).hexdigest()
            self.assertEqual(digest, report['md5'])
            sha256 = hashlib.sha256(path.read_bytes()).hexdigest()
            self.assertEqual(sha256, report['sha256'])
            self.assertEqual(f'{sha256}  {path.name}\n', path.with_suffix('.zip.sha256').read_text())
            self.assertEqual(f'{digest}  {path.name}\n', path.with_suffix('.zip.md5').read_text())
            entry = json.loads((output / 'manifest.json').read_text())[0]
            self.assertEqual('WizardBal', entry['owner'])
            self.assertEqual(config['project_url'], entry['projectUrl'])
            self.assertEqual(config['image_url'], entry['imageUrl'])
            release = entry['versions'][0]
            self.assertEqual(config['source_url'], release['sourceUrl'])
            self.assertEqual(config['repository_url'], release['repositoryUrl'])
            self.assertEqual(config['changelog'], release['changelog'])
            self.assertEqual('0.19.0.0', release['version'])
            self.assertEqual('2026-10-23T05:41:08Z', release['timestamp'])
            self.assertEqual(digest, release['checksum'])
            with zipfile.ZipFile(path) as archive:
                meta = json.loads(archive.read('meta.json'))
                self.assertEqual('WizardBal', meta['owner'])
                self.assertEqual(release['changelog'], meta['changelog'])
                self.assertEqual(release['timestamp'], meta['timestamp'])
                self.assertEqual(release['version'], meta['version'])
                self.assertEqual(self.dll.read_bytes(), archive.read(pkg.DLL_NAME))
                self.assertTrue(all(info.date_time == (2026, 10, 23, 5, 41, 8) for info in archive.infolist()))
            packages.append(path.read_bytes())
            catalogs.append((output / 'manifest.json').read_bytes())
        self.assertEqual(packages[0], packages[1])
        self.assertEqual(catalogs[0], catalogs[1])
        # The generated checksum catalog must not change the source commit.
        self.assertEqual(fixtures, {path.relative_to(source): path.read_bytes() for path in source.rglob('*') if path.is_file()})

    def test_beta_config_requires_cli_commit_time_or_source_date_epoch_before_build(self):
        output = self.root / 'no-release-clock'
        stderr = io.StringIO()
        with mock.patch.dict(os.environ, {}, clear=True), mock.patch.object(pkg, 'build_plugin', side_effect=AssertionError('build before timestamp validation')), contextlib.redirect_stderr(stderr):
            with self.assertRaises(SystemExit) as error:
                pkg.main(['--root', str(ROOT), '--config', str(ROOT / 'release-config.json'), '--output', str(output)])
        self.assertEqual(1, error.exception.code)
        self.assertIn('Supply --timestamp or SOURCE_DATE_EPOCH', stderr.getvalue())
        self.assertFalse(output.exists())
        self.assertFalse((output.parent / f'.{output.name}.babelfin-package.lock').exists())

    def test_cli_rejects_unknown_or_nonstring_config_fields_before_build(self):
        config = self.root / 'invalid-config.json'
        output = self.root / 'invalid-config-output'
        for value in ([], {'availability': 'planned'}, {'owner': 'WizardBal'}, {'timestamp': 123}, {'source_url': None}):
            with self.subTest(config=value):
                config.write_text(json.dumps(value))
                with mock.patch.object(pkg, 'build_plugin', side_effect=AssertionError('build before config validation')), contextlib.redirect_stderr(io.StringIO()):
                    with self.assertRaises(SystemExit) as error:
                        pkg.main(['--root', str(ROOT), '--config', str(config), '--output', str(output)])
                self.assertEqual(1, error.exception.code)
                self.assertFalse(output.exists())

    def test_build_uses_only_supplied_root_and_rejects_extra_output(self):
        output = self.root / 'build'
        output.mkdir()
        (output / pkg.DLL_NAME).write_bytes(b'fixture')
        with mock.patch.object(pkg, 'find_dotnet', return_value='fake-dotnet'), mock.patch('subprocess.run') as run:
            self.assertEqual(output / pkg.DLL_NAME, pkg.build_plugin(self.root, output))
            self.assertEqual(self.root / 'plugin', run.call_args.kwargs['cwd'])
            self.assertIn(str(self.root / pkg.PROJECT), run.call_args.args[0])
            (output / 'unexpected.dll').write_bytes(b'extra')
            with self.assertRaises(pkg.PackageError):
                pkg.build_plugin(self.root, output)


class AssetLineEndingTests(unittest.TestCase):
    def invoke(self, root, supplied_dll):
        project = root / pkg.PROJECT
        project.parent.mkdir(parents=True, exist_ok=True)
        project.write_text('<Project><ItemGroup><EmbeddedResource Include="Assets/**/*"/><EmbeddedResource Include="Configuration/configPage.html"/></ItemGroup></Project>')
        output = root / 'out'
        args = ['--root', str(root), '--output', str(output), '--timestamp', '2026-10-24T00:00:00Z']
        if supplied_dll:
            args += ['--dll', str(root / 'existing.dll')]
        result = {'manifest': None}
        stderr = io.StringIO()
        with (mock.patch.object(pkg, 'read_version', return_value='1.0.0.0'),
              mock.patch.object(pkg, 'validate_translations'),
              mock.patch.object(pkg, 'read_license'), mock.patch.object(pkg, 'read_notice'),
              mock.patch.object(pkg, 'build_plugin', return_value=root / 'built.dll') as build,
              mock.patch.object(pkg, '_create_package', return_value=result) as package,
              contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(stderr)):
            try:
                status = pkg.main(args)
            except SystemExit as exc:
                status = exc.code
            return status, stderr.getvalue(), build.call_count, package.call_count

    def test_lf_assets_are_accepted_with_build_and_supplied_dll(self):
        for supplied_dll in (False, True):
            with self.subTest(supplied_dll=supplied_dll), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                asset = root / pkg.PROJECT.parent / 'Assets' / 'nested' / 'fixture.js'
                asset.parent.mkdir(parents=True)
                asset.write_bytes(b'first\nsecond\n')
                status, error, builds, packages = self.invoke(root, supplied_dll)
                self.assertEqual(0, status, error)
                self.assertEqual(0 if supplied_dll else 1, builds)
                self.assertEqual(1, packages)

    def test_crlf_asset_is_rejected_before_build_or_supplied_dll_packaging(self):
        for supplied_dll in (False, True):
            with self.subTest(supplied_dll=supplied_dll), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                asset = root / pkg.PROJECT.parent / 'Assets' / 'nested' / 'fixture.js'
                asset.parent.mkdir(parents=True)
                asset.write_bytes(b'first\r\nsecond\r\n')
                status, error, builds, packages = self.invoke(root, supplied_dll)
                self.assertEqual(1, status)
                self.assertIn('clean checkout', error.lower())
                self.assertEqual(0, builds)
                self.assertEqual(0, packages)
                self.assertFalse((root / 'out').exists())


    def test_all_embedded_resource_definitions_are_checked(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            project = root / pkg.PROJECT
            project.parent.mkdir(parents=True)
            project.write_text('<Project><ItemGroup><EmbeddedResource Include="Other/*.txt"/></ItemGroup></Project>')
            resource = project.parent / 'Other' / 'extra.txt'
            resource.parent.mkdir()
            resource.write_bytes(b'first\nsecond\n')
            pkg.validate_asset_line_endings(root)
            resource.write_bytes(b'first\r\nsecond\r\n')
            with self.assertRaises(pkg.PackageError):
                pkg.validate_asset_line_endings(root)

    def test_config_page_line_endings_follow_embedded_resource_declarations(self):
        for content, expected in ((b'first\nsecond\n', 0), (b'first\r\nsecond\r\n', 1)):
            for supplied_dll in (False, True):
                with self.subTest(content=content, supplied_dll=supplied_dll), tempfile.TemporaryDirectory() as temp:
                    root = Path(temp)
                    page = root / pkg.PROJECT.parent / 'Configuration' / 'configPage.html'
                    page.parent.mkdir(parents=True)
                    page.write_bytes(content)
                    status, error, builds, packages = self.invoke(root, supplied_dll)
                    self.assertEqual(expected, status, error)
                    self.assertEqual(0 if expected or supplied_dll else 1, builds)
                    self.assertEqual(0 if expected else 1, packages)

if __name__ == '__main__':
    unittest.main(verbosity=2)
