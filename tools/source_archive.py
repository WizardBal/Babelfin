"""Export reproducible corresponding source from one explicit Git commit.

No history, ignored files, runtime downloads or working-tree edits are included.
The intended tag is recorded, never created or pushed. Use the public release's
final source commit when preparing its source and binary archives.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
from datetime import datetime, timezone
import xml.etree.ElementTree as ET
import zipfile

from contextlib import contextmanager
import os

PROJECT = Path('plugin/src/Jellyfin.Plugin.Babelfin/Jellyfin.Plugin.Babelfin.csproj')


class SourceArchiveError(ValueError):
    """The requested source cannot be exported safely."""


def git(root: Path, *arguments: str) -> bytes:
    try:
        return subprocess.check_output(['git', '-C', str(root), *arguments], stderr=subprocess.PIPE)
    except subprocess.CalledProcessError as exc:
        raise SourceArchiveError('The requested Git object could not be read') from exc


@contextmanager
def claim(output: Path):
    output.parent.mkdir(parents=True, exist_ok=True)
    path = output.parent / f'.{output.name}.babelfin-package.lock'
    try:
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError as exc:
        raise SourceArchiveError('Output is claimed by another generation') from exc
    try:
        yield
    finally:
        os.close(fd)
        path.unlink()


def create_source_archive(root: Path, commit: str, output: Path, *, tag: str | None = None) -> dict:
    """Read committed blobs only, rejecting links and noncanonical object ids."""
    if not re.fullmatch('[0-9a-f]{40}', commit):
        raise SourceArchiveError('Supply the full lowercase source commit id')
    if git(root, 'cat-file', '-t', commit).strip() != b'commit':
        raise SourceArchiveError('Source must identify a commit')
    tree = git(root, 'rev-parse', commit + '^{tree}').decode('ascii').strip()
    stamp = datetime.fromtimestamp(int(git(root, 'show', '-s', '--format=%ct', commit)), timezone.utc)
    if not 1980 <= stamp.year <= 2107:
        raise SourceArchiveError('Commit time is outside the ZIP timestamp range')
    files = {}
    entries = []
    for row in git(root, 'ls-tree', '-rz', '--full-tree', commit).split(b'\0'):
        if not row:
            continue
        metadata, raw_name = row.split(b'\t', 1)
        mode, kind, oid = metadata.decode('ascii').split()
        name = raw_name.decode('utf-8')
        path = PurePosixPath(name)
        if (kind != 'blob' or mode not in ('100644', '100755') or path.is_absolute()
                or '..' in path.parts or '.git' in path.parts or '\\' in name):
            raise SourceArchiveError('Source contains an unsafe path or link')
        entries.append((name, int(mode, 8), oid))
    try:
        response = subprocess.run(['git', '-C', str(root), 'cat-file', '--batch'],
                                  input=('\n'.join(e[2] for e in entries) + '\n').encode('ascii'),
                                  capture_output=True, check=True).stdout
    except subprocess.CalledProcessError as exc:
        raise SourceArchiveError('Committed source blobs could not be read') from exc
    blobs = io.BytesIO(response)
    for name, mode, oid in entries:
        header = blobs.readline().decode('ascii').strip().split()
        if len(header) != 3 or header[:2] != [oid, 'blob']:
            raise SourceArchiveError('Committed source blob metadata is invalid')
        size = int(header[2])
        content = blobs.read(size)
        if len(content) != size or blobs.read(1) != b'\n' or name in files:
            raise SourceArchiveError('Source contains a duplicate or unreadable member')
        files[name] = (content, mode)
    for name in ('LICENSE', 'NOTICE', PROJECT.as_posix(), 'tools/data/iso6392.txt', 'tools/gen_languages.py'):
        if name not in files or not files[name][0].strip():
            raise SourceArchiveError('Corresponding source is missing ' + name)
    try:
        project = ET.fromstring(files[PROJECT.as_posix()][0])
        versions = [item.text for item in project.iter() if item.tag.rsplit('}', 1)[-1] == 'Version']
    except ET.ParseError as exc:
        raise SourceArchiveError('Source project is not valid XML') from exc
    if len(versions) != 1 or not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+', versions[0] or ''):
        raise SourceArchiveError('Source needs one literal four-part version')
    version = versions[0]
    parts = version.split('.')
    if parts[3] != '0' or any(str(int(part)) != part for part in parts):
        raise SourceArchiveError('Source version must have canonical components and a zero revision for a SemVer tag')
    tag_prefix = 'v' + '.'.join(parts[:3]) + '-beta.'
    if tag is not None and not re.fullmatch(re.escape(tag_prefix) + r'[1-9][0-9]*', tag):
        raise SourceArchiveError('Intended tag must match the source version and use a numbered beta prerelease')
    descriptor = {'commit': commit, 'tree': tree, 'tag': tag or tag_prefix + '1',
                  'version': version, 'timestamp': stamp.strftime('%Y-%m-%dT%H:%M:%SZ')}
    if 'SOURCE.json' in files:
        raise SourceArchiveError('SOURCE.json is reserved for archive provenance')
    files['SOURCE.json'] = ((json.dumps(descriptor, indent=2, sort_keys=True) + '\n').encode('utf-8'), 0o644)
    prefix = f'Babelfin-{version}-source/'
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, (content, mode) in sorted(files.items()):
            info = zipfile.ZipInfo(prefix + name, stamp.timetuple()[:6])
            info.create_system = 3
            info.external_attr = (0o100755 if mode & 0o111 else 0o100644) << 16
            archive.writestr(info, content, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    content = buffer.getvalue()
    digest = hashlib.sha256(content).hexdigest()
    output = output.resolve()
    path = output / f'Babelfin-{version}-source.zip'
    checksum = path.with_suffix('.zip.sha256')
    with claim(output):
        if path.exists() or checksum.exists():
            raise SourceArchiveError('Source artifacts already exist; select a clean output directory')
        output.mkdir(parents=True, exist_ok=True)
        # Exclusive creation also protects against a non-cooperating publisher.
        with path.open('xb') as stream:
            stream.write(content)
        with checksum.open('x', encoding='ascii', newline='\n') as stream:
            stream.write(f'{digest}  {path.name}\n')
    return {**descriptor, 'archive': str(path), 'sha256': digest, 'files': len(files)}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--commit', required=True)
    parser.add_argument('--tag')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        print(json.dumps(create_source_archive(args.root, args.commit, args.output, tag=args.tag), indent=2))
        return 0
    except (SourceArchiveError, OSError) as exc:
        parser.exit(1, f'Source export failed: {exc}\n')


if __name__ == '__main__':
    raise SystemExit(main())
