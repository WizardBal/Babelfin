# Babelfin HTTP API

These routes support integrations and diagnostics in addition to the settings pages. Prefix each path with the Jellyfin base URL and `/Babelfin`. Use a Jellyfin access token in the `X-Emby-Token` header. Do not put tokens in shared URLs or logs.

Settings and diagnostic routes require authentication. A `Me` route always acts as the signed-in person. `Admin` routes require a Jellyfin administrator. Items hidden from a person are reported as not found. Static assets and the health ping have separate access rules.

## Personal settings

| Method and path | Purpose |
| --- | --- |
| `GET Me/Settings` | Stored settings, inherited defaults and visible libraries. |
| `PUT Me/Settings?keepOverrides=true` | Save settings while retaining the latest show/movie overrides and burned-in flags. Send the settings document, not the whole GET response. |
| `GET Me/Effective?itemId={itemId}` | Resolved preferences and the source of each field. |
| `GET Me/Libraries` | Visible libraries without server folder paths. |
| `GET Me/Explain?itemId={itemId}&mediaSourceId={optionalSourceId}` | Decisions and explanations for a visible item. |
| `GET Me/Choice?itemId={itemId}&mediaSourceId={optionalSourceId}` | Chosen stream indices and languages for a visible item. |
| `GET Me/ChoiceStatus` | Whether the person has preferences that affect track selection. |
| `GET Me/Activity?limit={count}&kind={optionalKind}` | The person's activity since Jellyfin last started, newest first, from the shared 5,000-entry memory ring. Default 50, maximum 200 per request. |
| `GET Me/Export` | Export personal settings. |
| `POST Me/Import` | Validate and import a personal export. |
| `POST Me/Reset` | Remove personal preferences. `HandleNextEpisode` returns to its default, `true`. |

The existing JSON field names `Understood` and `Reads` mean **My languages** and **Languages for subtitles**. Language codes are listed by `GET Languages`. Keep codes from that catalogue rather than translating them.

`HandleNextEpisode` defaults to `true`. While it is `true` and Babelfin is enabled for the server and the person, Babelfin turns off the person's Jellyfin `RememberAudioSelections` and `RememberSubtitleSelections` (**Set audio track based on previous item** and **Set subtitle track based on previous item**) and records their values; setting it to `false` writes the recorded values back. A person document stored with an older `SchemaVersion` is upgraded when first read or imported, and that upgrade sets `HandleNextEpisode` to `true` once; a later `false` is kept.

Genre rows match one positive genre name and set audio/subtitle table fields. Other stored rule shapes are retained for backup compatibility and remain inactive.

An unset field inherits its default. Explicit video-default audio is `AudioChain: [{"Kind":"VideoDefault"}]`. An ordered language preference is an array of `Language` steps followed by `VideoDefault`. The settings pages can generate valid documents for each supported choice.

## Save only edited fields

The personal, administrator-per-person and server settings PUT routes accept an optional `fields` query. Send the usual settings document and name only the changed properties, for example:

```text
PUT Me/Settings?keepOverrides=true&fields=everyday.reads,everyday.audioChain
PUT Admin/Users/{userId}/Settings?keepOverrides=true&fields=everyday.memory
PUT Admin/Settings?fields=keywords.commentaryWords,everyday.unlabelledAudio
```

Field paths use exact camelCase even when the body uses Jellyfin's PascalCase. The server applies these fields to its latest document under the write lock. Unlisted fields are preserved; a named nullable field set to `null` inherits, `[]` explicitly clears a list, and `false` remains an explicit value. Without expected values in the body or legacy header, two writers changing the same named field use the last accepted value.

Use `everyday.<field>` and, for an administrator editing a person, `locks.<field>`; whole-object `everyday` or `locks` intent is rejected. Personal callers cannot edit locks. Library cells use stable IDs:

```text
fields=libraries.{libraryId}.settings.audioChain,libraries.{libraryId}.settings.subtitleLanguages
```

The body keeps its normal `Libraries` array. Only the named leaves of matching rows change; other rows, hidden fields and concurrent changes survive. Omitting that row from the body clears only the named leaf. Administrators can also name `libraries.{libraryId}.originalAudio`.

The New users language editor uses `newUserTemplate.everyday.understood`. This changes only that language list, retaining the latest hidden template preferences. A `null` list clears it without deleting other template settings.

Person-facing settings responses, personal exports and field-conflict problems identify administrator changes as `admin` without the administrator account ID or name. Administrator responses retain the full identity.

Other explicitly named list fields are atomic replacements. Omitting `fields` uses the legacy settings save: every omitted top-level property keeps its stored value. An explicitly present empty list or `null` clears an editable list; `null` also clears nullable fields. Empty, overlapping, unknown or server-owned paths, or more than 100 paths, return `400`. `keepOverrides=true` preserves both stored overrides and burned-in flags; use their dedicated endpoints to remove them.

## Show and movie overrides

| Method and path | Purpose |
| --- | --- |
| `GET Me/Overrides` | List stored explicit and remembered overrides. |
| `GET Me/Overrides/Target/{itemId}` | Resolve a movie/video or the whole show containing a season/episode. Returns the target, inherited settings and any existing override. |
| `PUT Me/Overrides/{itemId}?scope={scope}` | Save a settings layer for the target. Use `Series` for a show and `Title` for a movie/video. |
| `DELETE Me/Overrides/{itemId}?scope={scope}` | Remove one stored override. A missing or invalid item ID must never mean remove all. |
| `DELETE Me/Overrides/All` | Explicitly remove all overrides for the signed-in person. |
| `POST Me/Overrides/Prune?after={optionalGuid}` | Report a bounded page of missing item references without changing settings. |

The bare `DELETE Me/Overrides` route is not supported. The final `/All` segment is required for bulk removal. Validate IDs before constructing single-item requests.

Burned-in flags are separate from track overrides: `POST Me/BurnedIn/{itemId}?scope={scope}` and `DELETE Me/BurnedIn/{itemId}?scope={scope}`. Removing an override does not remove a burned-in flag.

## Administration

| Method and path | Purpose |
| --- | --- |
| `GET Admin/Settings`, `PUT Admin/Settings` | Read or save server defaults. |
| `GET Admin/Defaults` | Shipped defaults for restore controls. |
| `GET Admin/Users` | People and their preference summaries. |
| `GET Admin/Users/{userId}/Settings`, `PUT Admin/Users/{userId}/Settings?keepOverrides=true` | Read or save one person's settings. |
| `GET Admin/Users/{userId}/Defaults` | Defaults inherited by that person. |
| `GET Admin/Users/{userId}/Overrides` | Overrides as visible to that person. |
| `DELETE Admin/Users/{userId}/Overrides/{itemId}?scope={scope}` | Remove one override. |
| `DELETE Admin/Users/{userId}/Overrides/All` | Explicitly remove all of that person's overrides. |
| `POST Admin/Users/{userId}/Overrides/Prune?after={optionalGuid}` | Report missing item references for that person without changing settings. |
| `POST Admin/Users/{userId}/CopyTo/{targetUserId}` | Copy supported preferences without replacing the target's locks or remembered track changes. |
| `GET Admin/Apps` | Detected and configured client behavior. |
| `GET Admin/Status` | Plugin status and integration diagnostics. |
| `GET Admin/Playback` | Playback sessions currently followed by Babelfin. |
| `GET Admin/Libraries`, `POST Admin/Libraries/Refresh` | Inspect or refresh library facts. |
| `GET Admin/Activity?limit={count}&kind={optionalKind}&userId={optionalUserId}` | Filter activity since Jellyfin last started, newest first, from the 5,000-entry memory ring. Default 100, maximum 5,000 per request. |
| `GET Admin/Export`, `POST Admin/Import` | Back up or validate and restore settings. Export accepts `allowPartial=true` to report and skip unreadable accounts. |
| `POST Admin/ResetAll` | Back up settings, then reset users. `allowPartial=true` permits readable accounts to proceed while unavailable accounts remain untouched. |
| `POST Admin/Users/{userId}/BurnedIn/{itemId}/Approve` | Approve a reported burned-in subtitle language. |
| `POST Admin/Simulate` | Start an asynchronous check for a selected person and item/search. |
| `GET Admin/Simulate/{jobId}`, `DELETE Admin/Simulate/{jobId}` | Poll or cancel that check. |
| `GET Admin/ChoiceStatus` | Whether server defaults affect selection. |

## Partial backups, reset and maintenance

Import files use the export JSON contract independently of the host's request naming policy: both camelCase exports and PascalCase documents are accepted, including compact and hyphenated GUIDs. Downloaded exports retain their existing format. Empty or unrelated JSON objects are rejected instead of being reported as applied. Preview the file before applying it.

Full export/reset retains the strict failure behavior when an account cannot be read safely. Quarantined accounts remain reported until their settings are repaired; retained recovery archives do not prevent a valid repair from being used. Newer-format documents are omitted with an explicit incomplete-backup warning rather than exported with unknown fields lost. With `allowPartial=true`, an export includes `Complete: false` and `SkippedUsers` entries with account IDs, names and reasons. An incomplete export is not a backup of every account. Reset only processes successfully backed-up account snapshots; accounts changed since backup are retained and reported. Inspect `FailedUsers`, `FailedNames`, `SkippedUsers`, `AdminReset` and `AdminResetReason` as well as the HTTP status. Never treat a partial response as an all-settings reset.

Prune endpoints inspect at most 500 distinct item IDs per call and return `Ready`, `SkippedReason`, `MissingOverrides`, `MissingBurnedIn`, `MissingItemIds` and optional `NextAfter`. Continue with `NextAfter` until it is absent. Existence checks are global, so an item hidden from the person is not reported as deleted. A skipped report returns **200 with `Ready: false`**. No settings, overrides, burned-in flags or recovery exports are changed.

The **Babelfin: report missing-item overrides** scheduled task provides the same report daily and through Jellyfin's manual Run control. It has no startup trigger and skips active scans or empty/unavailable library readiness checks. A report can read a newer-format document only as far as safely supported; unavailable accounts are skipped with a reason.

## Concurrent edits

For edited-field PUTs, include an `expectedValues` object in the otherwise unchanged flat settings body. Its keys are the same camelCase field paths listed in `fields`; its values are the original values loaded before editing, for example `{"everyday.subtitleLanguages":["en"]}`. Compare original values, not the proposed replacements. The server compares them with the latest stored fields under the write lock. This request-only metadata is never saved or exported; it is limited to 8 MiB of UTF-8 JSON and requires `fields`. Missing metadata retains the legacy unconditional-save contract used by Keep mine. The legacy `X-Babelfin-Expected` base64url header remains accepted, but HTTP server and proxy header limits make it unsuitable for larger tables. Supply expectations in the body or the header, never both; null, duplicate metadata properties and missing or additional field paths return 400.

For example, a `PUT Me/Settings?fields=everyday.subtitleLanguages` changing an original list of `["en"]` to `["de"]` sends:

```json
{
  "Everyday": { "SubtitleLanguages": ["de"] },
  "ExpectedValues": { "everyday.subtitleLanguages": ["en"] }
}
```

A mismatch returns `409` with code `field-conflict`, the conflicting paths and information about the last change. No fields are saved. The settings pages offer **Reload** to replace only conflicting draft fields, or **Keep mine** to resend the edited fields without expected values in either transport. A change to a different field does not conflict. Person documents record the last successful change's timestamp and actor. Dedicated override and burned-in endpoints do not use this expected-values check. Legacy callers that omit both body metadata and the header retain the usual save behavior.

## Import and recovery responses

Import previews serialize `StorageBlocked` when a person's stored settings cannot be read. Do not offer Apply for such a preview, even if other people could be updated. A failure containing only `Applied: false` is not an import report; keep the last preview unless an actual `Import` report is returned.

Copy settings saves the target's current document in a restorable export first and returns its backup filename. References to items hidden from the target are preserved. Reset-all responses identify their backup by filename and a path relative to Babelfin's data folder, never by an absolute server path.

## Responses and safety

Jellyfin normally serializes property names in PascalCase. A successful deletion returns `204`; authentication and elevation failures return `401` or `403`. Invalid input returns `400`, invisible or missing targets return `404`, and busy simulator jobs can return `409`. Unavailable, unreadable or newer-format settings also return an actionable `409`; the original file remains untouched. A temporarily unavailable library returns `503` with code `library-unavailable`; retry later. Known API errors also carry stable codes such as `newer-format`, `account-recreated`, `invalid-settings` and `field-conflict`, which the settings pages translate. Unknown codes retain their server message. Reload after a concurrent account/settings change rather than treating the request as saved. Inspect the response body for validation details and partial-reset failures.

Export before destructive operations. Treat exports and activity responses as private: they can identify people, titles and viewing activity. The activity list clears on restart; see the README for settings backup and cleanup behavior.
