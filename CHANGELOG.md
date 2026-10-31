# Changelog

## v0.20.0-beta.1 — Track choices across autoplay and native players

- Tracks an app copies from the previous episode are replaced with Babelfin's choice in **Use Babelfin's choice when playing the next episode** mode, including tracks of the same language and kind.
- Apps that start playback without naming tracks, such as the Jellyfin Android app's built-in player, keep Babelfin's audio and subtitle choices instead of Jellyfin's default-flagged audio track or an equally ranked subtitle track.
- Jellyfin's **Set audio track based on previous item** and **Set subtitle track based on previous item** are turned off by default for people Babelfin chooses for, and their original values are restored when the switch, the person or the server is turned off, or when Babelfin is uninstalled.
- A track change made during playback is remembered once it has stayed selected for 10 seconds.
- The Android app is recognised as "Jellyfin for Android".
- Person and server settings are converted to a newer format on first start. Download all settings before installing.
- Tested with Jellyfin 12.4, the Jellyfin Android app 2.7.3 on Google Pixel 11 Pro XL and Jellyfin Desktop 2.0.0 on Linux. Automated integration tests use Jellyfin Web on a disposable Jellyfin 12.2 test instance. Android TV, Swiftfin, Streamyfin and physical TVs have not been tested.

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
