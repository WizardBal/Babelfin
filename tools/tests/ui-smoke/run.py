"""Dependency-free runner for the maintained Babelfin UI smoke suites."""
import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
DEFAULT_ASSETS = REPO / 'plugin/src/Jellyfin.Plugin.Babelfin/Assets'


def source_hashes(assets):
    files = {p.relative_to(assets).as_posix(): p for p in assets.rglob('*') if p.is_file()}
    for name in ['Configuration/configPage.html', 'Engine/Languages/IsoLanguages.g.cs', 'Engine/Languages/IsoLanguages.Extra.cs']:
        files['@plugin/' + name] = assets.parent / name
    return {name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in files.items()}


def run_job(name, script, cwd, assets, output, node, omit=False, negative=False):
    env = os.environ.copy()
    env['BF_ASSETS'] = str(assets) + os.sep
    env['BF_OMIT_NULLS'] = '1' if omit else '0'
    env.pop('BF_ROOT', None)
    command = [node, str(script)]
    try:
        result = subprocess.run(command, cwd=cwd, env=env, encoding='utf-8',
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=600)
        text, code = result.stdout, result.returncode
    except subprocess.TimeoutExpired as error:
        text = error.stdout or b''
        if isinstance(text, bytes):
            text = text.decode('utf-8', errors='replace')
        text += '\nRunner timeout after 600 seconds.\n'
        code = -1
    log = output / (name + '.log')
    log.write_text(text, encoding='utf-8')
    row = {'name': name, 'exit_code': code, 'log': str(log)}
    summary = re.search(r'\n(\d+) passed, (\d+) failed\s*$', text)
    if negative:
        # A missing path or unrelated crash is not a successful semantic rejection.
        meaningful = re.search(r'Semantic guard: required audio option "(?:mine|original)"', text)
        row.update(expected_rejection=True, ok=code != 0 and bool(meaningful) and summary is None)
    elif name.startswith(('person-', 'admin-')):
        row.update(complete=summary is not None, passed=int(summary[1]) if summary else None,
                   failed=int(summary[2]) if summary else None,
                   ok=code == 0 and summary is not None and int(summary[2]) == 0)
    else:
        row['ok'] = code == 0
    return row


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assets', type=Path, help='Asset directory; overrides BF_ASSETS and the repository-relative default')
    parser.add_argument('--output', type=Path, help='Explicit log directory; otherwise create a fresh system temporary directory')
    parser.add_argument('--negative-assets', type=Path, help='Optional older asset tree which both suites must reject for missing semantic audio options')
    args = parser.parse_args()
    assets = (args.assets or Path(os.environ.get('BF_ASSETS') or DEFAULT_ASSETS)).resolve()
    node = shutil.which('node')
    if not node:
        parser.error('Node.js must be installed and available on PATH')
    try:
        before = source_hashes(assets)
        if not (assets / 'ui.js').is_file():
            raise FileNotFoundError(assets / 'ui.js')
    except OSError as error:
        parser.error('The selected source tree is incomplete: ' + str(error))
    negative_assets = args.negative_assets.resolve() if args.negative_assets else None
    if negative_assets and not (negative_assets / 'ui.js').is_file():
        parser.error('--negative-assets must contain ui.js')
    output = args.output.resolve() if args.output else Path(tempfile.mkdtemp(prefix='babelfin-ui-smoke-'))
    output.mkdir(parents=True, exist_ok=True)
    print('Assets: ' + str(assets), flush=True)
    print('Logs: ' + str(output), flush=True)
    jobs = []
    for audience, script in [('person', 'smoke.js'), ('admin', 'test.js')]:
        for omit in [False, True]:
            jobs.append((audience + ('-omit-nulls' if omit else '-nulls'), HERE / audience / script,
                         HERE / audience, assets, output, node, omit, False))
        if negative_assets:
            jobs.append(('negative-' + audience, HERE / audience / script, HERE / audience,
                         negative_assets, output, node, False, True))
    for name, relative in [('keycheck-person', 'person/keycheck-person.js'), ('keycheck-admin', 'admin/keycheck.js'),
                           ('i18n-person', 'person/validate-i18n.js'), ('i18n-admin', 'admin/validate.js')]:
        jobs.append((name, HERE / relative, HERE, assets, output, node, False, False))
    results = []
    with ThreadPoolExecutor(max_workers=4) as pool:
        futures = {pool.submit(run_job, *job): job[0] for job in jobs}
        for future in as_completed(futures):
            try:
                row = future.result()
            except Exception as error:
                row = {'name': futures[future], 'ok': False, 'error': str(error)}
            results.append(row)
            print(json.dumps(row), flush=True)
    after = source_hashes(assets)
    changed = [name for name in sorted(set(before) | set(after)) if before.get(name) != after.get(name)]
    results.append({'name': 'source-stability', 'ok': not changed, 'changed_during_run': changed})
    (output / 'source-target.json').write_text(json.dumps({'assets': str(assets), 'before': before, 'after': after,
                                                         'changed_during_run': changed}, indent=2), encoding='utf-8')
    (output / 'summary.json').write_text(json.dumps(sorted(results, key=lambda row: row['name']), indent=2), encoding='utf-8')
    return 0 if all(row['ok'] for row in results) else 1


if __name__ == '__main__':
    sys.exit(main())
