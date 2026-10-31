# Contributing

Report reproducible problems with the bug-report template. Include Jellyfin, Babelfin and app versions, player mode, expected and actual tracks, and a sanitized selection explanation. Remove tokens, names, addresses, paths and real library titles before sharing logs or screenshots. See [SECURITY.md](SECURITY.md) for suspected vulnerabilities.

Discuss substantial behavior changes in an issue before implementing them. Keep pull requests focused, explain the behavior they change, and include a regression that fails before the fix. Follow the existing formatting and run the relevant checks listed in the [README](README.md#development-and-testing). For playback changes, include the app and version, player mode, expected and actual tracks, and results from a disposable Jellyfin server.

The rig's default `.rig/` directory is a local folder for the Jellyfin test runtime; `--jellyfin` can select another location. Keep downloaded runtimes, generated media, release output and test reports out of commits. Public ignore rules cover the standard local runtime and verification directories. Put private planning notes in `.private/` and machine-specific configuration in `*.local.*` files; check `git status` before committing. Machine-specific locations can also be ignored in `.git/info/exclude`.

## Translations

All translations were initially machine-drafted. Native-speaker corrections are welcome. The “Machine translated: help improve it” link leads here because that notice is an invitation to review wording, not a claim of human translation.

Person/client strings live in `plugin/src/Jellyfin.Plugin.Babelfin/Assets/i18n/`; administrator strings live in `Assets/i18n-admin/`. Compare a locale with `en.json`, keep its keys and placeholders such as `{name}` unchanged, and use natural wording for the setting's actual behavior. Refer to “My languages” and “Languages for subtitles”; do not describe the person as “understanding” or “reading” languages. Leave `_machineTranslated` set until the complete locale has had a documented human review.

Describe the correction and locale in a pull request or issue. Run `python -X utf8 tools/tests/ui-smoke/run.py` to check keys, placeholders and affected UI. Do not include real server addresses, account names or file paths in issues or fixtures.

## AI tools and attribution

AI tools are used for development, review and all translations. Tested with Jellyfin 12.4 and the Jellyfin Android app on Google Pixel 11 Pro XL. Automated integration tests use Jellyfin Web on a disposable Jellyfin 12.2 test instance. Android TV, Swiftfin, Streamyfin and physical TVs have not been tested.

Disclose substantial AI-generated contributions and explain how you checked them. Tool output still needs review, relevant tests and accurate attribution. Preserve third-party licenses and contributor credit; do not submit code you cannot explain or claim that a native app was tested when it was not.

Contributions to original Babelfin code are under GPL-3.0-only. Preserve upstream notices and document any adapted code in NOTICE, with an immutable source reference and its license.
