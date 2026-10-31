import json
from pathlib import Path
import runpy
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
with patch.object(sys, 'path', [str(ROOT / 'tools/rig'), *sys.path]):
    catalog = runpy.run_path(str(ROOT / 'tools/rig/catalog.py'))


class CatalogPackageTests(unittest.TestCase):
    def test_catalog_accepts_only_the_five_flat_release_members(self):
        meta = json.dumps({'version': '1.2.3.4', 'guid': catalog['GUID'], 'name': 'Babelfin'})
        original = {catalog['DLL']: b'dll fixture', 'LICENSE': b'license fixture',
                    'NOTICE': b'provenance fixture', 'logo.png': b'logo fixture', 'meta.json': meta}
        with tempfile.TemporaryDirectory(prefix='babelfin-catalog-package-') as folder:
            package = Path(folder) / 'release.zip'
            def write(files):
                with zipfile.ZipFile(package, 'w') as archive:
                    for name, contents in files.items():
                        archive.writestr(name, contents)
            write(original)
            self.assertEqual('1.2.3.4', catalog['load_package'](package)['version'])
            for changed in ({k: v for k, v in original.items() if k != 'NOTICE'},
                            {**original, 'extra.txt': b'no'},
                            {**original, 'NOTICE': b''}):
                with self.subTest(names=list(changed)):
                    write(changed)
                    with self.assertRaises(catalog['AcceptanceError']):
                        catalog['load_package'](package)


if __name__ == '__main__':
    unittest.main()
