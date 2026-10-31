# Changelog

## v0.19.0-beta.1 — First public beta

- Personal audio and subtitle preferences, ordered languages, library and genre defaults, and show/movie overrides.
- Full-dialogue, signs-and-songs and SDH subtitle choices, editable track recognition and accessibility preferences.
- Remembered track changes where the player reports them, with separate handling for startup corrections and later manual picks.
- Administrator defaults, new-account languages, People management and title-selection explanations.
- Edited-field saves with conflict choices, readable settings exports, recovery backups and import previews that identify blocked accounts.
- Jellyfin Web menus and settings, including keyboard navigation in its TV layout. Tested with Jellyfin 12.4 and the Jellyfin Android app on Google Pixel 11 Pro XL. Automated integration tests use Jellyfin Web on a disposable Jellyfin 12.2 test instance. Android TV, Swiftfin, Streamyfin and physical TVs have not been tested.
- Manual file-copy installation and a plugin catalog for updates.
- Daily and manual missing-item reports preserve overrides and burned-in flags.
- Recent activity held in memory for the current Jellyfin session, with decisions and settings changes in Jellyfin's own logs. Separate settings backups are retained. See [activity history](README.md#activity-history-and-retained-data).

Jellyfin checks the plugin catalog for updates. Babelfin selects existing tracks; it does not download, translate or generate subtitles.
