using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>One validation problem: where it is (a JSON-ish path such as "everyday.understood[2]") and what is wrong.</summary>
/// <param name="Path">The path of the offending value.</param>
/// <param name="Message">What is wrong, in plain English.</param>
public sealed record ValidationError(string Path, string Message);

/// <summary>The body of a validation (400), storage conflict (409), or temporary library-unavailable (503) answer.</summary>
/// <param name="Message">A short summary.</param>
/// <param name="Errors">The individual problems (may be empty).</param>
/// <param name="ItemIds">Item ids that failed the access check (only ids the caller sent), or null.</param>
/// <param name="Applied">For an Admin Import outcome, whether any settings were already saved; otherwise null.</param>
/// <param name="Import">The complete Admin Import outcome, including per-account actions, skipped reasons and warnings, when available.</param>
public sealed record ApiProblem(string Message, IReadOnlyList<ValidationError> Errors, IReadOnlyList<string>? ItemIds = null, bool? Applied = null, ImportPreview? Import = null)
{
    /// <summary>Gets the stable, language-independent problem identifier.</summary>
    public string Code { get; init; } = DefaultCode(Message, Errors);

    // Keep unclassified server guidance visible: a new endpoint must not look like a validation failure.
    private static string DefaultCode(string message, IReadOnlyList<ValidationError> errors)
    {
        if (message.StartsWith("The library is temporarily unavailable.", StringComparison.Ordinal)) return "library-unavailable";
        if (message.StartsWith("At most ", StringComparison.Ordinal))
            return message.Contains("burned-in", StringComparison.Ordinal) ? "burned-in-limit" : "override-limit";
        return message switch
        {
            "The settings are missing." or "The languages are missing." => "missing-settings",
            "Some items can't be found." or "Some items can't be found for this person." or "That person can't see this item." => "item-unavailable",
            "That person doesn't exist." => "person-unavailable",
            "Choose two different people." => "copy-target-required",
            "Choose a person and a library, series or title." => "simulation-target-required",
            "A simulation is already running." => "simulation-busy",
            "The file contains no importable settings." or "The import file is empty." or "The file is empty." or "The file is missing." => "empty-import",
            "The import file is not valid." or "The import file does not match the supported settings format." => "invalid-import",
            "Library settings are saved with Me/Settings (libraries), not as an override." => "invalid-override-scope",
            "This item has no season." or "This item has no series." or "Overrides are for shows and movies only." => "invalid-override-target",
            "Name the collection with collectionId." or "This item is not in that collection." => "invalid-collection-target",
            _ => errors.Count > 0 ? "invalid-settings" : "request-failed",
        };
    }

    /// <summary>Gets the field paths changed since the request's expected values.</summary>
    public IReadOnlyList<string>? ConflictingPaths { get; init; }

    /// <summary>Gets the latest values of conflicting fields, keyed by field path.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?>? CurrentValues { get; init; }

    /// <summary>Gets the most recent successful person write time (UTC), when available.</summary>
    public DateTime? LastChangedAt { get; init; }

    /// <summary>Gets the account id of that writer, prefixed with admin: for an administrator.</summary>
    public string? LastChangedBy { get; init; }

    /// <summary>Gets that account's display name, when available.</summary>
    public string? LastChangedByName { get; init; }
}

/// <summary>A resolved profile as the API shows it: the merged settings, where each field came from, and the item facts that shaped them.</summary>
/// <param name="Profile">The merged engine profile (language strings, steps and choices; no paths).</param>
/// <param name="Sources">Provenance per field name.</param>
/// <param name="PersonEnabled">Whether Babelfin acts for this person.</param>
/// <param name="Assumed">Languages Babelfin assumed and the person hasn't confirmed.</param>
/// <param name="LibraryIsOriginalAudio">Whether the item's library counts as original-audio.</param>
/// <param name="LibraryUsualOriginalLanguage">The library's usual original language, or null.</param>
/// <param name="BurnedInLanguages">Burned-in languages that apply to the item for this person.</param>
/// <param name="Manual">The remembered manual change that applies, or null.</param>
public sealed record EffectiveProfileDto(
    ResolvedProfile Profile,
    IReadOnlyDictionary<string, string> Sources,
    bool PersonEnabled,
    IReadOnlyList<AssumedLanguage> Assumed,
    bool LibraryIsOriginalAudio,
    string? LibraryUsualOriginalLanguage,
    IReadOnlyList<string> BurnedInLanguages,
    ManualOverride? Manual)
{
    /// <summary>Projects a resolver result (the item context is left out on purpose: the caller already knows the item).</summary>
    /// <param name="result">The resolver result.</param>
    /// <returns>The DTO.</returns>
    public static EffectiveProfileDto From(ResolvedProfileResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new EffectiveProfileDto(
            result.Profile,
            result.Sources,
            result.PersonEnabled,
            result.Assumed,
            result.LibraryIsOriginalAudio,
            result.LibraryUsualOriginalLanguage,
            result.BurnedInLanguages,
            result.Manual);
    }
}

/// <summary>A library as a person sees it (no folder locations, ever).</summary>
/// <param name="Id">The library id ("N" format).</param>
/// <param name="Name">The library name.</param>
/// <param name="CollectionType">Jellyfin's collection type ("movies", "tvshows"…), or null for a mixed library.</param>
/// <param name="IsOriginalAudio">Whether the library counts as original-audio (the admin's choice, else detection).</param>
/// <param name="UsualOriginalLanguage">The library's usual original language, or null.</param>
public sealed record LibraryInfoDto(string Id, string Name, string? CollectionType, bool IsOriginalAudio, string? UsualOriginalLanguage);

/// <summary>A library as the admin sees it: the person view plus the admin's choice and the computed facts.</summary>
/// <param name="Id">The library id ("N" format).</param>
/// <param name="Name">The library name.</param>
/// <param name="CollectionType">Jellyfin's collection type, or null.</param>
/// <param name="OriginalAudio">The admin's original-audio choice (Auto when none is stored).</param>
/// <param name="IsOriginalAudio">The effective answer.</param>
/// <param name="UsualOriginalLanguage">The library's usual original language, or null.</param>
/// <param name="Facts">The computed facts, or null when not computed yet.</param>
public sealed record AdminLibraryDto(string Id, string Name, string? CollectionType, OriginalAudioChoice OriginalAudio, bool IsOriginalAudio, string? UsualOriginalLanguage, LibraryFact? Facts);

/// <summary>The answer of <c>GET Me/Settings</c> (and of <c>PUT Me/Settings</c>).</summary>
/// <param name="Settings">The person's own document (locks included, read-only for the person).</param>
/// <param name="Inherited">What the everyday screen inherits, and from where (resolved with no item).</param>
/// <param name="Libraries">The libraries the person can see.</param>
/// <param name="Assumed">The assumed languages not confirmed yet.</param>
/// <param name="Defaults">What each row of the "Audio and subtitles" table resolves to when left on Default.</param>
/// <param name="AdvancedDefaults">What every setting is when the person doesn't set it: resolved with no item and without their own everyday
/// fields (their languages kept). The Advanced options show these when nothing is stored.</param>
public sealed record MeSettingsResponse(PersonSettings Settings, EffectiveProfileDto Inherited, IReadOnlyList<LibraryInfoDto> Libraries, IReadOnlyList<AssumedLanguage> Assumed, TableDefaultsDto? Defaults = null, EffectiveProfileDto? AdvancedDefaults = null);

/// <summary>The body of <c>POST Me/BurnedIn/{itemId}</c>.</summary>
public sealed class BurnedInRequest
{
    /// <summary>Gets or sets the burned-in languages (any spelling the language table resolves).</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Gets or sets the scope (title, season, series or collection; library is admin-only).</summary>
    public OverrideScope Scope { get; set; } = OverrideScope.Title;

    /// <summary>Gets or sets the collection id when <see cref="Scope"/> is collection (also accepted as a query parameter).</summary>
    public Guid? CollectionId { get; set; }
}

/// <summary>One override as the "Show and movie overrides" list shows it (<c>GET Me/Overrides</c>, <c>GET Admin/Users/{userId}/Overrides</c>).</summary>
/// <param name="ItemId">The id the override is stored under ("N" format): the movie, show, season or episode.</param>
/// <param name="Scope">What the id stands for.</param>
/// <param name="Name">The item's current name, else the name stored with the override (empty when neither is known).</param>
/// <param name="Type">The item's current type ("Movie", "Series", "Season", "Episode"…), or null when the item is gone or the person can't see it.</param>
/// <param name="ProductionYear">The item's production year, or null.</param>
/// <param name="SeriesName">The show's name for a season or an episode, or null.</param>
/// <param name="LibraryId">The id ("N" format) of the library the item is in, or null when the item is gone or in no library.</param>
/// <param name="LibraryName">That library's name, or null.</param>
/// <param name="Settings">The explicit settings as stored (empty when only a remembered track change is stored).</param>
/// <param name="Manual">The remembered track change as stored, or null.</param>
/// <param name="UpdatedAt">When the override was last changed (UTC).</param>
public sealed record OverrideSummary(
    string ItemId,
    OverrideScope Scope,
    string Name,
    string? Type,
    int? ProductionYear,
    string? SeriesName,
    string? LibraryId,
    string? LibraryName,
    SettingsLayer Settings,
    ManualMemory? Manual,
    DateTime UpdatedAt);

/// <summary>What the "Audio and subtitles (Babelfin)" dialog needs to know about an item (<c>GET Me/Overrides/Target/{itemId}</c>).</summary>
/// <param name="TargetId">The id an override for the item is stored under ("N" format): the item itself for a movie, a video or a show; the show for a season or an episode.</param>
/// <param name="Scope">"Title" for a movie or a video, "Series" for a show and its seasons and episodes.</param>
/// <param name="Name">The target's name.</param>
/// <param name="Type">The target's type ("Movie", "Video" or "Series").</param>
/// <param name="LibraryId">The id ("N" format) of the target's library, or null when it is in none.</param>
/// <param name="LibraryName">That library's name, or null.</param>
/// <param name="Override">The person's stored override for the target, or null.</param>
/// <param name="Inherited">What applies to the target without the person's override (what "Default" means in the dialog).</param>
/// <param name="HasNarrower">True when the person has a season or episode override under this show, which comes first for that season or episode.</param>
public sealed record OverrideTarget(
    string TargetId,
    OverrideScope Scope,
    string Name,
    string Type,
    string? LibraryId,
    string? LibraryName,
    ItemOverride? Override,
    EffectiveProfileDto Inherited,
    bool HasNarrower);

/// <summary>One row of the admin's people list (the first-run card).</summary>
/// <param name="UserId">The Jellyfin user id.</param>
/// <param name="Name">The user name.</param>
/// <param name="HasSettings">Whether the person has a Babelfin file yet (it is created on first playback or first visit).</param>
/// <param name="Enabled">Whether Babelfin acts for the person (true when no file exists yet).</param>
/// <param name="UseEverydayEverywhere">Whether the person skips the admin's library, rule and title defaults.</param>
/// <param name="Assumed">Unconfirmed assumed languages.</param>
/// <param name="SeedConfirmed">Whether the person confirmed the seeded languages.</param>
/// <param name="UpdatedAt">When the file was last saved, or null.</param>
/// <param name="PerLibrary">The person's table mode as it applies (their choice, or the automatic one).</param>
/// <param name="Table">The person's "every library" audio and subtitle fields (only the table's five), or null when none are set.</param>
/// <param name="LibraryRows">How many libraries the person set a choice for.</param>
public sealed record AdminUserSummary(Guid UserId, string Name, bool HasSettings, bool Enabled, bool UseEverydayEverywhere, IReadOnlyList<AssumedLanguage> Assumed, bool SeedConfirmed, DateTime? UpdatedAt, bool PerLibrary = false, SettingsLayer? Table = null, int LibraryRows = 0);

/// <summary>One line of the engine's structured trace as Explain shows it (the engine's track list, which holds external file names, is never sent).</summary>
/// <param name="Code">The fixed trace code ("audio.step.matched"…).</param>
/// <param name="Text">The sentence.</param>
/// <param name="TrackIndex">The stream index the line is about, or null.</param>
public sealed record EngineTraceLineDto(string Code, string Text, int? TrackIndex);

/// <summary>One media source's choice as the item chip shows it (<c>GET Me/Choice</c>).</summary>
/// <param name="SourceId">The media source id.</param>
/// <param name="Outcome">"Applied", "DryRun", "PassedThrough" or "None".</param>
/// <param name="AudioIndex">The chosen audio index.</param>
/// <param name="AudioLanguage">Its canonical language code, or null.</param>
/// <param name="AudioName">Its English language name, or null.</param>
/// <param name="SubtitleIndex">The chosen subtitle index, or null for none.</param>
/// <param name="SubtitleLanguage">Its canonical language code, or null.</param>
/// <param name="SubtitleName">Its English language name, or null.</param>
/// <param name="SubtitleKind">"full", "partial" or "sdh", or null.</param>
/// <param name="Explanation">The one-paragraph explanation.</param>
/// <param name="ForItemId">The item the choice was made for: the item itself, or the first episode of a series or season page.</param>
public sealed record ChoiceDto(string SourceId, string Outcome, int? AudioIndex, string? AudioLanguage, string? AudioName, int? SubtitleIndex, string? SubtitleLanguage, string? SubtitleName, string? SubtitleKind, string? Explanation, string? ForItemId = null);

/// <summary>One language of <c>GET Languages</c>.</summary>
/// <param name="Code">The canonical code ("ja", "zh-Hant", "pt-BR").</param>
/// <param name="Name">The English name (the source string translators work from).</param>
/// <param name="Base">The base language code.</param>
/// <param name="Variety">The spoken variety, or null.</param>
/// <param name="Script">The script, or null.</param>
/// <param name="Region">The region, or null.</param>
public sealed record LanguageDto(string Code, string Name, string Base, string? Variety, string? Script, string? Region);

/// <summary>The answer of <c>POST Admin/Users/{userId}/CopyTo/{targetUserId}</c>.</summary>
/// <param name="SourceUserId">The person the settings were copied from.</param>
/// <param name="TargetUserId">The person the settings were copied to.</param>
/// <param name="Settings">The target's document after the copy.</param>
/// <param name="SkippedItemIds">Item and library ids the target can't see; their entries were not copied.</param>
public sealed record CopySettingsResult(Guid SourceUserId, Guid TargetUserId, PersonSettings Settings, IReadOnlyList<string> SkippedItemIds)
{
    /// <summary>Gets the recovery export file name inside Babelfin's backups folder.</summary>
    public string? BackupFile { get; init; }
}

/// <summary>The answer of <c>POST Admin/ResetAll</c>.</summary>
/// <param name="BackupFile">The backup's file name: every setting as it was before the reset, in the <c>GET Admin/Export</c> format
/// (<c>POST Admin/Import</c> restores it).</param>
/// <param name="BackupPath">The backup's path relative to the Babelfin data folder.</param>
/// <param name="People">How many people's settings were reset.</param>
public sealed record ResetAllResult(string BackupFile, string BackupPath, int People);

/// <summary>One row of <c>GET Admin/Apps</c>: an app as the per-app mode table holds it.</summary>
/// <param name="Client">The client name as Jellyfin reports it.</param>
/// <param name="Mode">What Babelfin does with the indices this app sends.</param>
/// <param name="Seen">Whether the app has been seen on this server.</param>
/// <param name="LastSeen">When it was last seen (UTC), or null.</param>
public sealed record AppModeDto(string Client, AppModeKind Mode, bool Seen, DateTime? LastSeen, bool RememberPrePlayChoices = false, bool SeenOnly = false);

/// <summary>Shared helpers that turn library facts and the admin's choices into the library views.</summary>
internal static class LibraryView
{
    /// <summary>Whether a library counts as original-audio: the admin's Yes/No wins; Auto uses the computed facts, or (before any facts exist) the starter word list on the name.</summary>
    /// <param name="choice">The admin's choice.</param>
    /// <param name="fact">The computed facts, or null.</param>
    /// <param name="name">The library name.</param>
    /// <param name="starter">The starter settings (word list).</param>
    /// <returns>True for an original-audio library.</returns>
    internal static bool IsOriginalAudio(OriginalAudioChoice choice, LibraryFact? fact, string? name, StarterSettings? starter)
    {
        switch (choice)
        {
            case OriginalAudioChoice.Yes:
                return true;
            case OriginalAudioChoice.No:
                return false;
            default:
                if (fact?.ComputedAt is not null)
                {
                    return fact.DetectedOriginalAudio;
                }

                return NameMatches(name, starter?.OriginalAudioLibraryWords);
        }
    }

    /// <summary>Whether a library name contains a word of the list (Latin words whole, CJK as substrings), the calculator's rule.</summary>
    /// <param name="name">The name.</param>
    /// <param name="words">The word list.</param>
    /// <returns>True on a match.</returns>
    internal static bool NameMatches(string? name, IEnumerable<string>? words)
    {
        if (string.IsNullOrWhiteSpace(name) || words is null)
        {
            return false;
        }

        var normalized = TextMatcher.Normalize(name);
        return words.Where(w => !string.IsNullOrWhiteSpace(w)).Any(w => TextMatcher.Contains(normalized, new PreparedKeyword(w)));
    }
}

/// <summary>A bounded, report-only inspection of missing item references. Existing recovery files are retained without alteration.</summary>
/// <param name="RemovedOverrides">Always zero: deleted-item inspection is report-only.</param>
/// <param name="RemovedBurnedIn">Always zero: burned-in flags are preserved.</param>
/// <param name="BackupFile">Always null: inspection writes no files; existing recovery archives are retained.</param>
/// <param name="NextAfter">Continue with this id to inspect the next page, or null when complete or skipped.</param>
public sealed record PruneOverridesReport(int RemovedOverrides, int RemovedBurnedIn, string? BackupFile, Guid? NextAfter)
{
    /// <summary>Gets whether this operation is read-only.</summary>
    public bool ReportOnly => true;
    /// <summary>Gets whether the library was ready and this page was inspected.</summary>
    public bool Ready { get; init; } = true;
    /// <summary>Gets the reason the page was skipped, without claiming any items were missing.</summary>
    public string? SkippedReason { get; init; }
    // Account storage blocks skip only this person; library readiness failures stop the task.
    internal bool AccountSkipped { get; init; }
    /// <summary>Gets the snapshot override entries referring to items absent from both library probes.</summary>
    public int MissingOverrides { get; init; }
    /// <summary>Gets the snapshot burned-in flags referring to those items.</summary>
    public int MissingBurnedIn { get; init; }
    /// <summary>Gets the distinct missing item ids on this bounded page. No reference is removed.</summary>
    public IReadOnlyList<Guid> MissingItemIds { get; init; } = Array.Empty<Guid>();
}
