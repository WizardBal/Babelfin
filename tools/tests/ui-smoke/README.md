Babelfin maintained UI smoke tests
=================================

From the repository root, run:

```sh
python -X utf8 tools/tests/ui-smoke/run.py
```

Requires Python 3.9+ and Node.js 24 on PATH, matching CI. No npm packages, Python packages, browser, server, network, or build step are needed. The suites use fake DOMs and fake APIs. They read product source and never write it.

The default assets are `<repo>/plugin/src/Jellyfin.Plugin.Babelfin/Assets`, resolved from the installed test directory rather than the working directory. `BF_ASSETS` or `--assets PATH` can select another source tree; `--assets` takes precedence. Paths are normalized and the required trailing separator is added automatically. The admin configuration page and real language catalogue are read from `Configuration/` and `Engine/Languages/` beside the selected Assets directory.

Logs go to a fresh system temporary directory by default; the runner prints its location. Use `--output PATH` for an explicit results directory. Output includes per-job logs, `summary.json`, source hashes and a check that the selected source stayed unchanged throughout the run. A suite must finish and print its final check count; an early zero exit cannot produce a green result.

For an old source tree regression check, add `--negative-assets PATH`. Both suites must fail specifically because their runtime audio controls lack `mine` or `original`. A missing file, unrelated exception, checkout name, or directory location cannot satisfy that negative check. The normal semantic guard verifies both options, exact stored chains, round trips without selected languages, absence from subtitle menus, and the genre/status components.

Coverage and maintained files
-----------------------------

- `person/smoke.js`, `person/fakedom.js`: person UI, settings round trips, accessibility, translations, genre editing, overrides, deferred optional status, errors and saves.
- `admin/test.js`, `admin/fakedom.js`: admin defaults and People editor, concurrent settings preservation, explicit genre priority through Save, optional status and unchanged admin behavior.
- `person/keycheck-person.js`, `admin/keycheck.js`, `person/validate-i18n.js`, `admin/validate.js`: used/missing/retired translation keys, dynamic key families, locale parity and wording. Consistent LF or CRLF is accepted; malformed or mixed endings are rejected by the admin formatting check.
- `shared/contracts.js`, `shared/target.js`, `shared/languages.js`: common feature contracts, portable source resolution and runtime guard, and the language fixture derived from the selected engine source.
- The library-choice contract matrix checks exact stored values, change events and refresh behavior independently of the UI implementation. The single-choice editor is also compared with the library table.
- `run.py`: both suites with explicit nulls and omitted nulls, all four validators, optional semantic negative checks, and source-stability reporting.

Run both suites and validators with the command above. Use `--output PATH` to retain their logs and source hashes. Keep this directory at `tools/tests/ui-smoke` in the source tree.

License
-------

GPL-3.0-only. See the repository [LICENSE](../../../LICENSE).
