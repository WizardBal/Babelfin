from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class LanguageInputTests(unittest.TestCase):
    def test_pinned_language_input_reproduces_the_entire_generated_file(self):
        with tempfile.TemporaryDirectory(prefix='babelfin-language-') as folder:
            output = Path(folder) / 'IsoLanguages.g.cs'
            subprocess.run([sys.executable, '-X', 'utf8', str(ROOT / 'tools/gen_languages.py'),
                            str(ROOT / 'tools/data/iso6392.txt'), str(output)],
                           check=True, capture_output=True)
            expected = ROOT / 'plugin/src/Jellyfin.Plugin.Babelfin/Engine/Languages/IsoLanguages.g.cs'
            self.assertEqual(0, output.read_bytes().count(b"\r"), "Generated input must have identical LF bytes on both build platforms")
            self.assertEqual(expected.read_bytes().replace(b"\r\n", b"\n"), output.read_bytes())


if __name__ == '__main__':
    unittest.main()
