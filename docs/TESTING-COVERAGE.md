# Testing Babelfin

Tests cover track classification and selection, preference inheritance, concurrent saves, playback reports, storage recovery, HTTP access rules and browser controls. Tested with Jellyfin 12.4 and the Jellyfin Android app on Google Pixel 11 Pro XL. Browser playback tests use a disposable Jellyfin 12.2 test instance with generated media and synthetic accounts. Android TV, Swiftfin, Streamyfin and physical TVs have not been tested.

## Run the checks

Use the .NET SDK selected by `global.json`, Node.js 24 and Python 3.9+. Chrome is required for layout checks; set `CHROME_PATH` if needed.

```sh
dotnet build plugin/src/Jellyfin.Plugin.Babelfin/Jellyfin.Plugin.Babelfin.csproj -c Release --warnaserror
dotnet test plugin/tests/Babelfin.Engine.Tests/Babelfin.Engine.Tests.csproj -c Release
dotnet run --project plugin/bindtest/app/App.csproj -c Release -- /path/to/plugin-output --source-commit COMMIT
dotnet run --project plugin/harness/Harness.csproj -c Release -- /path/to/fresh-test-output
node --test tools/tests/*.test.js tools/coverage/coverage.test.cjs
python -X utf8 tools/tests/ui-smoke/run.py
python -m unittest tools.tests.test_package tools.tests.test_source_archive tools.tests.test_source_identity tools.tests.test_catalog_package tools.tests.test_language_input tools.tests.test_source_encoding
python -I tools/test_gitignore.py
```

The [UI smoke suites](../tools/tests/ui-smoke/README.md) check explicit and omitted JSON nulls, translations and wording. CI runs Windows and Linux independently and compares plugin DLL hashes.

For browser playback checks, generate media with `tools/rig/make_media.py`, then run:

```sh
python tools/rig/rig.py --jellyfin /path/to/jellyfin-runtime --media /path/to/generated-test-media --port 8097 --e2e
```

The [native-policy checker](../tools/native-policy-check/README.md) compares subtitle selection with Jellyfin's own policy and records the loaded assembly identity.

## Coverage

Measurements from an earlier source snapshot; these are not measurements of the current release:

| Suite | Lines | Branches |
| --- | --- | --- |
| C# unit tests and HTTP harness | 9,644 / 10,575 (91.20%) | 7,041 / 8,440 (83.42%) |
| Node tests and UI smoke JavaScript | 8,335 / 9,872 (84.43%) | V8 runtime ranges; no exhaustive branch percentage |

All seven production JavaScript/inline sources are included. The unloaded standalone bootstrap contributes 0 of 19 covered lines. CSS, translation quality, browser playback and physical devices are outside these percentages.

To collect coverage, choose output and tool directories outside the source checkout:

```sh
python tools/coverage/coverage.py --root /path/to/source --tools /path/to/coverage-tools --out /path/to/coverage-output --mode setup
python tools/coverage/coverage.py --root /path/to/source --tools /path/to/coverage-tools --out /path/to/coverage-output --mode all
```

Use a fresh output directory for another measurement. Reports retain raw collector data and source hashes. Line and branch coverage measure code reached by a particular run. Native-client, physical-remote, screen-reader and codec support require separate acceptance checks.
