"""Keep multilingual source and UI wording in UTF-8 without accidental codepage conversions."""
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
TEXT_EXTENSIONS = {'.cs', '.js', '.json', '.py', '.md', '.yml', '.yaml', '.sh', '.html', '.css', '.xml', '.csproj', '.txt'}
# Escapes keep the detector itself readable by the same check.
BROKEN_PREFIXES = ('\u00e2\u20ac', '\u00e2\u2020', '\u00e2\u201d', '\u00c3\u00a2', '\u00c3\u0192', '\u00c2\u00b7', '\u00e2\u2039')


def checked_text(data):
    text = data.decode('utf-8-sig')
    if any(prefix in text for prefix in BROKEN_PREFIXES):
        raise ValueError('Likely UTF-8 text decoded through a legacy Windows codepage')
    return text


class SourceEncodingTests(unittest.TestCase):
    def test_repository_text_is_valid_utf8_without_accidental_codepage_conversion(self):
        names = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=ROOT).decode('utf-8').split('\0')
        for name in names:
            path = ROOT / name
            if not name or not path.is_file() or (path.suffix.lower() not in TEXT_EXTENSIONS and name != 'LICENSE'):
                continue
            with self.subTest(path=name):
                checked_text(path.read_bytes())

    def test_real_unicode_and_supported_locales_remain_valid(self):
        for text in ('Audio \u2192 Japanese', '\u22ee', 'Audio \u00b7 Subtitles', 'Fran\u00e7ais', 'Portugu\u00eas', '\u65e5\u672c\u8a9e', '\u00c2ge', '\u20ac'):
            self.assertEqual(text, checked_text(text.encode('utf-8')))

    def test_regression_detects_mangled_punctuation_and_legacy_byte_encoding(self):
        for text in ('Audio \u2192 Japanese', '\u22ee', 'Audio \u00b7 Subtitles', '\u2014'):
            bad = text.encode('utf-8').decode('cp1252').encode('utf-8')
            with self.subTest(text=text), self.assertRaises(ValueError):
                checked_text(bad)
        with self.assertRaises(UnicodeDecodeError):
            checked_text(b'Caf\xe9')


if __name__ == '__main__':
    unittest.main()
