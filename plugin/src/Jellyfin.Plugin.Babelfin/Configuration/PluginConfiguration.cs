using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Babelfin.Configuration;

/// <summary>Whether a library counts as "original-audio".</summary>
public enum OriginalAudioChoice
{
    /// <summary>Detected automatically from the name word list and the genre share.</summary>
    Auto,

    /// <summary>Always.</summary>
    Yes,

    /// <summary>Never.</summary>
    No,
}

/// <summary>The admin's defaults for one library (layer 6, admin side).</summary>
public class LibraryDefault
{
    /// <summary>Gets or sets the library id ("N" format GUID).</summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>Gets or sets the library name when saved (display only).</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets whether the library is an original-audio library.</summary>
    public OriginalAudioChoice OriginalAudio { get; set; } = OriginalAudioChoice.Auto;

    /// <summary>Gets or sets the settings everyone inherits in this library.</summary>
    public SettingsLayer Settings { get; set; } = new();
}

/// <summary>The admin's defaults for one title, season, series or collection (layers 2–4, admin side).</summary>
public class TitleDefault
{
    /// <summary>Gets or sets the item id ("N" format GUID).</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the scope the id stands for.</summary>
    public OverrideScope Scope { get; set; }

    /// <summary>Gets or sets the item's name when saved (display only).</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the settings.</summary>
    public SettingsLayer Settings { get; set; } = new();
}

/// <summary>The original-language library rules: off unless the admin switches them on (Babelfin is neutral by default).</summary>
public class StarterSettings
{
    /// <summary>Gets or sets a value indicating whether the original-language library rules apply at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the words that make a library an original-audio library by name (whole word, case-insensitive; CJK as substring).</summary>
    [XmlIgnore]
    public List<string> OriginalAudioLibraryWords { get; set; } = new() { "anime", "animé", "animes", "アニメ", "动漫", "動畫", "애니메이션" };

    /// <summary>XML form of <see cref="OriginalAudioLibraryWords"/>: an array, which XmlSerializer assigns instead of appending to the shipped defaults (a file's list therefore replaces them exactly).</summary>
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlArray("OriginalAudioLibraryWords")]
    [XmlArrayItem("string")]
    public string[] OriginalAudioLibraryWordsXml
    {
        get => OriginalAudioLibraryWords.ToArray();
        set => OriginalAudioLibraryWords = value is null ? new List<string>() : value.ToList();
    }

    /// <summary>Gets or sets the genres that mark a title as original-audio content.</summary>
    [XmlIgnore]
    public List<string> OriginalAudioGenres { get; set; } = new() { "Anime" };

    /// <summary>XML form of <see cref="OriginalAudioGenres"/>: an array, which XmlSerializer assigns instead of appending to the shipped defaults (a file's list therefore replaces them exactly).</summary>
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlArray("OriginalAudioGenres")]
    [XmlArrayItem("string")]
    public string[] OriginalAudioGenresXml
    {
        get => OriginalAudioGenres.ToArray();
        set => OriginalAudioGenres = value is null ? new List<string>() : value.ToList();
    }

    /// <summary>Gets or sets the share (0–100) of a library's titles that must have such a genre for the library to count as original-audio.</summary>
    public int GenreSharePercent { get; set; } = 50;

    /// <summary>Gets or sets the share (0–100) of titles (that have an original language) one language must reach to be the library's usual original language.</summary>
    public int UsualOriginalLanguagePercent { get; set; } = 60;

    /// <summary>Gets or sets a value indicating whether a title with an original-audio genre in a mixed library gets the original-audio chain (the starter smart rule).</summary>
    public bool GenreRule { get; set; } = true;

    /// <summary>Gets or sets the default policy for titles whose audio language is outside My languages.</summary>
    public ForeignTitlesRule ForeignTitles { get; set; } = ForeignTitlesRule.DubInMyLanguage;

    /// <summary>Removes duplicates (XmlSerializer appends a file's list to the property's default list on every load).</summary>
    public void Normalize()
    {
        OriginalAudioLibraryWords = PluginConfiguration.Distinct(OriginalAudioLibraryWords);
        OriginalAudioGenres = PluginConfiguration.Distinct(OriginalAudioGenres);
    }
}

/// <summary>The editable keyword lists. Fresh settings carry the built-in multilingual defaults.</summary>
public class KeywordSettings
{
    /// <summary>Gets or sets the words that make a subtitle partial.</summary>
    [XmlIgnore]
    public List<string> PartialWords { get; set; } = KeywordTables.Default.PartialWords.ToList();

    /// <summary>XML form of <see cref="PartialWords"/>: an array, which XmlSerializer assigns instead of appending to the shipped defaults (a file's list therefore replaces them exactly).</summary>
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlArray("PartialWords")]
    [XmlArrayItem("string")]
    public string[] PartialWordsXml
    {
        get => PartialWords.ToArray();
        set => PartialWords = value is null ? new List<string>() : value.ToList();
    }

    /// <summary>Gets or sets the words that make a subtitle SDH.</summary>
    [XmlIgnore]
    public List<string> SdhWords { get; set; } = KeywordTables.Default.SdhWords.ToList();

    /// <summary>XML form of <see cref="SdhWords"/>: an array, which XmlSerializer assigns instead of appending to the shipped defaults (a file's list therefore replaces them exactly).</summary>
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlArray("SdhWords")]
    [XmlArrayItem("string")]
    public string[] SdhWordsXml
    {
        get => SdhWords.ToArray();
        set => SdhWords = value is null ? new List<string>() : value.ToList();
    }

    /// <summary>Gets or sets the words that make a subtitle full.</summary>
    [XmlIgnore]
    public List<string> FullWords { get; set; } = KeywordTables.Default.FullWords.ToList();

    /// <summary>XML form of <see cref="FullWords"/>: an array, which XmlSerializer assigns instead of appending to the shipped defaults (a file's list therefore replaces them exactly).</summary>
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlArray("FullWords")]
    [XmlArrayItem("string")]
    public string[] FullWordsXml
    {
        get => FullWords.ToArray();
        set => FullWords = value is null ? new List<string>() : value.ToList();
    }

    /// <summary>Gets or sets the words that make an audio track a commentary.</summary>
    [XmlIgnore]
    public List<string> CommentaryWords { get; set; } = KeywordTables.Default.CommentaryWords.ToList();

    /// <summary>XML form of <see cref="CommentaryWords"/>: an array, which XmlSerializer assigns instead of appending to the shipped defaults (a file's list therefore replaces them exactly).</summary>
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlArray("CommentaryWords")]
    [XmlArrayItem("string")]
    public string[] CommentaryWordsXml
    {
        get => CommentaryWords.ToArray();
        set => CommentaryWords = value is null ? new List<string>() : value.ToList();
    }

    /// <summary>Gets or sets the words that make an audio track audio description.</summary>
    [XmlIgnore]
    public List<string> AudioDescriptionWords { get; set; } = KeywordTables.Default.AudioDescriptionWords.ToList();

    /// <summary>XML form of <see cref="AudioDescriptionWords"/>: an array, which XmlSerializer assigns instead of appending to the shipped defaults (a file's list therefore replaces them exactly).</summary>
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlArray("AudioDescriptionWords")]
    [XmlArrayItem("string")]
    public string[] AudioDescriptionWordsXml
    {
        get => AudioDescriptionWords.ToArray();
        set => AudioDescriptionWords = value is null ? new List<string>() : value.ToList();
    }

    /// <summary>Removes duplicates (XmlSerializer appends a file's list to the property's default list on every load).</summary>
    public void Normalize()
    {
        PartialWords = PluginConfiguration.Distinct(PartialWords);
        SdhWords = PluginConfiguration.Distinct(SdhWords);
        FullWords = PluginConfiguration.Distinct(FullWords);
        CommentaryWords = PluginConfiguration.Distinct(CommentaryWords);
        AudioDescriptionWords = PluginConfiguration.Distinct(AudioDescriptionWords);
    }

    /// <summary>Converts to the engine's tables.</summary>
    /// <returns>The tables.</returns>
    public KeywordTables ToTables() => new(
        Array.AsReadOnly(PartialWords.ToArray()),
        Array.AsReadOnly(SdhWords.ToArray()),
        Array.AsReadOnly(FullWords.ToArray()),
        Array.AsReadOnly(CommentaryWords.ToArray()),
        Array.AsReadOnly(AudioDescriptionWords.ToArray()));
}

/// <summary>What Babelfin does with the track indices an app sends to PlaybackInfo.</summary>
public enum AppModeKind
{
    /// <summary>Keep the app's indices (the default for apps not listed).</summary>
    Trust,

    /// <summary>Replace them only when the request is the next item in a queue and they differ from Babelfin's choice, even when they
    /// name a track of the same language and kind (the app copies the previous item's track; a person's picks carry over through
    /// Babelfin's remembered changes).</summary>
    ReplaceOnAutoplay,

    /// <summary>Replace them unless the request is a manual choice (a repeat during play, or a change from what Babelfin served).</summary>
    ReplaceUnlessManual,
}

/// <summary>Whether Babelfin sends a track-switch command to an app whose first progress report differs from the expected tracks.</summary>
public enum CorrectAfterStartMode
{
    /// <summary>Only when the app advertises the commands (default).</summary>
    Auto,

    /// <summary>Also when the app's capabilities are unknown.</summary>
    On,

    /// <summary>Never.</summary>
    Off,
}

/// <summary>One row of the per-app mode table, keyed by the client name Jellyfin reports.</summary>
public partial class AppMode
{
    /// <summary>Gets or sets the client name as Jellyfin reports it ("Jellyfin Web", "Android TV"…), matched case-insensitively.</summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>Gets or sets the mode.</summary>
    public AppModeKind Mode { get; set; } = AppModeKind.Trust;

    /// <summary>Gets or sets a value indicating whether this client has been seen on this server.</summary>
    public bool Seen { get; set; }

    /// <summary>Gets or sets when it was last seen (UTC).</summary>
    public DateTime? LastSeen { get; set; }
}

/// <summary>An admin-added language alias: a raw spelling found on tracks mapped to a canonical code (empty code = "not a language").</summary>
public class LanguageAlias
{
    /// <summary>Gets or sets the raw spelling.</summary>
    public string Raw { get; set; } = string.Empty;

    /// <summary>Gets or sets the canonical code ("zh-Hant", "pt-BR", "ja"); empty means the spelling is not a language.</summary>
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// Settings edited on Dashboard &gt; Plugins &gt; Babelfin and through the admin API. Saved by Jellyfin as XML.
/// Version 2: the admin's layered defaults, locks live in each person's file, the starter rules, keyword and alias tables,
/// the new-user template and the shared burned-in flags. Version 3: the per-app mode table, correction after start,
/// compatibility mode, the playback timings and the activity log. Retired fields (the retired startup probe, the version-2 replace switch)
/// are read once from an old file and never written again.
/// </summary>
public partial class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The current settings layout. Older saved settings are upgraded on startup.</summary>
    public const int CurrentVersion = 9;

    /// <summary>Gets or sets the settings layout version (used to upgrade older saved settings).</summary>
    public int ConfigVersion { get; set; }

    /// <summary>Gets or sets the Libraries table's mode: true = a default per library, false = the same default for every
    /// library (<see cref="Everyday"/>); null = automatic (<see cref="TableModes.AdminPerLibrary"/>).</summary>
    public bool? PerLibraryDefaults { get; set; }

    /// <summary>Gets or sets a value indicating whether Babelfin changes track choices at all (the server switch).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether Babelfin only computes and logs its choices without applying them.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets the retired version-2 "replace request indices" switch (read from an old file, never written; the per-app modes replaced it).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool? ReplaceRequestIndices { get; set; }

    /// <summary>Keeps the retired switch out of saved files.</summary>
    /// <returns>Always false.</returns>
    public bool ShouldSerializeReplaceRequestIndices() => false;

    /// <summary>Gets or sets the per-app modes: what to do with the indices each app sends to PlaybackInfo. Rows are added as apps are seen.</summary>
    [XmlIgnore]
    public List<AppMode> AppModes { get; set; } = DefaultAppModes();

    /// <summary>XML form of <see cref="AppModes"/>: an array, which XmlSerializer assigns instead of appending to the shipped defaults (a file's list therefore replaces them exactly).</summary>
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlArray("AppModes")]
    [XmlArrayItem("AppMode")]
    public AppMode[] AppModesXml
    {
        get => AppModes.ToArray();
        set => AppModes = value is null ? new List<AppMode>() : value.ToList();
    }

    /// <summary>Gets or sets the mode for apps with no row.</summary>
    public AppModeKind UnknownAppMode { get; set; } = AppModeKind.Trust;

    /// <summary>Gets or sets whether Babelfin sends a track-switch command after playback starts when the app plays other tracks than expected.</summary>
    public CorrectAfterStartMode CorrectAfterStart { get; set; } = CorrectAfterStartMode.Auto;

    /// <summary>Gets or sets a value indicating whether Babelfin also writes its choice into Jellyfin's remembered selection at playback start (recorded so it can be undone).</summary>
    public bool CompatibilityMode { get; set; }

    /// <summary>Gets or sets how many seconds after playback starts a track change in a progress report counts as a manual change.</summary>
    public int ManualChangeAfterSeconds { get; set; } = 5;

    /// <summary>Gets or sets for how many minutes the indices Babelfin served count when telling a manual choice from an app's own request.</summary>
    public int ServedWindowMinutes { get; set; } = 30;

    /// <summary>Gets or sets how many seconds after the previous item ended a request counts as "the next item in a queue".</summary>
    public int AutoplayWindowSeconds { get; set; } = 120;

    /// <summary>Gets or sets the retired activity retention setting, read from old files and never written.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [JsonIgnore]
    public int ActivityRetentionDays { get; set; } = 90;

    /// <summary>Keeps the retired setting out of saved files.</summary>
    /// <returns>Always false.</returns>
    public bool ShouldSerializeActivityRetentionDays() => false;

    /// <summary>Gets or sets a retired switch (Babelfin no longer writes to Jellyfin's activity log); read from old files, never written.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool? SendImportantEventsToJellyfinActivity { get; set; }

    /// <summary>Keeps the retired switch out of saved files.</summary>
    /// <returns>Always false.</returns>
    public bool ShouldSerializeSendImportantEventsToJellyfinActivity() => false;

    /// <summary>Gets or sets the admin's everyday defaults (layer 7, admin side).</summary>
    public SettingsLayer Everyday { get; set; } = new();

    /// <summary>Gets or sets the admin's per-library defaults and original-audio choices (layer 6, admin side).</summary>
    public List<LibraryDefault> Libraries { get; set; } = new();

    /// <summary>Gets or sets the admin's smart rules (layer 5, admin side; regex patterns allowed).</summary>
    public List<SmartRule> SmartRules { get; set; } = new();

    /// <summary>Gets or sets the admin's title, season, series and collection defaults (layers 2–4, admin side).</summary>
    public List<TitleDefault> Titles { get; set; } = new();

    /// <summary>Gets or sets the burned-in flags that apply to everyone (admin-set or admin-approved; library scope allowed).</summary>
    public List<BurnedInFlag> BurnedIn { get; set; } = new();

    /// <summary>Gets or sets the template applied to a person created after install (null = seed from their Jellyfin settings only).</summary>
    public PersonSettings? NewUserTemplate { get; set; }

    /// <summary>Gets or sets the smart starter rules.</summary>
    public StarterSettings Starter { get; set; } = new();

    /// <summary>Gets or sets the keyword lists.</summary>
    public KeywordSettings Keywords { get; set; } = new();

    /// <summary>Gets or sets admin-added language aliases (they win over the built-in table).</summary>
    public List<LanguageAlias> LanguageAliases { get; set; } = new();

    /// <summary>Gets or sets the number of days after which library facts are recomputed in the background.</summary>
    public int LibraryFactsRefreshDays { get; set; } = 1;

    /// <summary>Gets or sets a retired field (upgrade notes are no longer shown); always empty and never written.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string LastUpgradeNote { get; set; } = string.Empty;

    /// <summary>Keeps the retired field out of saved files.</summary>
    /// <returns>Always false.</returns>
    public bool ShouldSerializeLastUpgradeNote() => !string.IsNullOrEmpty(LastUpgradeNote);

    /// <summary>Gets or sets the retired startup probe audio language (read from a version-1 file, never written).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? ProbeAudioLanguage { get; set; }

    /// <summary>Gets or sets the retired startup probe subtitle language (read from a version-1 file, never written).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? ProbeSubtitleLanguage { get; set; }

    /// <summary>Gets or sets the retired "replace request indices" switch (read from a version-1 file, never written).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool? ProbeReplaceRequestIndices { get; set; }

    /// <summary>Keeps the retired probe field out of saved files (XmlSerializer convention).</summary>
    /// <returns>Always false.</returns>
    public bool ShouldSerializeProbeAudioLanguage() => false;

    /// <summary>Keeps the retired probe field out of saved files.</summary>
    /// <returns>Always false.</returns>
    public bool ShouldSerializeProbeSubtitleLanguage() => false;

    /// <summary>Keeps the retired probe field out of saved files.</summary>
    /// <returns>Always false.</returns>
    public bool ShouldSerializeProbeReplaceRequestIndices() => false;

    /// <summary>
    /// Removes the duplicates XmlSerializer creates: it appends a file's list items to a property that already holds the shipped defaults,
    /// so without this every server start doubled the keyword, starter and per-app lists. Runs on every load (from <see cref="Upgrade"/>) and
    /// before every save through the API. Keeps the first row per app and the first spelling of each word; order is kept.
    /// </summary>
    public void Normalize()
    {
        Keywords ??= new KeywordSettings();
        Keywords.Normalize();
        Starter ??= new StarterSettings();
        Starter.Normalize();
        var seenClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Keep the LAST row per client: on an XML load the file's rows follow the shipped defaults.
        AppModes = (AppModes ?? new List<AppMode>()).Where(a => a is not null).Reverse().Where(a => seenClients.Add((a.Client ?? string.Empty).Trim())).Reverse().ToList();
        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LanguageAliases = (LanguageAliases ?? new List<LanguageAlias>()).Where(a => a is not null && seenAliases.Add((a.Raw ?? string.Empty).Trim())).ToList();
    }

    /// <summary>Distinct, case-insensitive, order kept, blanks dropped.</summary>
    /// <param name="list">The list.</param>
    /// <returns>A new list.</returns>
    internal static List<string> Distinct(IEnumerable<string>? list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (list ?? Array.Empty<string>()).Where(w => !string.IsNullOrWhiteSpace(w) && seen.Add(w.Trim())).ToList();
    }

    /// <summary>The shipped per-app rows (editable): jellyfin-web apps, including the Android app (which reports itself as
    /// "Jellyfin for Android"), replace on autoplay; Android TV and Streamyfin replace unless manual (unchecked); the rest trust.</summary>
    /// <returns>The rows.</returns>
    public static List<AppMode> DefaultAppModes() => new()
    {
        new AppMode { Client = "Jellyfin Web", Mode = AppModeKind.ReplaceOnAutoplay },
        new AppMode { Client = "Jellyfin Media Player", Mode = AppModeKind.ReplaceOnAutoplay },
        new AppMode { Client = "Jellyfin Desktop", Mode = AppModeKind.ReplaceOnAutoplay },
        new AppMode { Client = "Jellyfin Android", Mode = AppModeKind.ReplaceOnAutoplay },
        new AppMode { Client = "Jellyfin for Android", Mode = AppModeKind.ReplaceOnAutoplay },
        new AppMode { Client = "Android TV", Mode = AppModeKind.ReplaceUnlessManual },
        new AppMode { Client = "Streamyfin", Mode = AppModeKind.ReplaceUnlessManual },
        new AppMode { Client = "Swiftfin", Mode = AppModeKind.Trust },
    };

    /// <summary>Finds the per-app row for a client name.</summary>
    /// <param name="client">The client name Jellyfin reports.</param>
    /// <returns>The row, or null.</returns>
    public AppMode? FindAppMode(string? client) => string.IsNullOrWhiteSpace(client) ? null : AppModes.FirstOrDefault(a => string.Equals(a.Client?.Trim(), client.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds the admin's default for a library.</summary>
    /// <param name="libraryId">The library id.</param>
    /// <returns>The default, or null.</returns>
    public LibraryDefault? FindLibrary(Guid libraryId) => Libraries.FirstOrDefault(l => PersonSettings.SameId(l.LibraryId, libraryId));

    /// <summary>Finds the admin's default for an item.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The default, or null.</returns>
    public TitleDefault? FindTitle(Guid itemId) => Titles.FirstOrDefault(t => PersonSettings.SameId(t.ItemId, itemId));

    /// <summary>The fields of the "Audio and subtitles" table: what the version 4 upgrade clears on the admin's layers.</summary>
    public static IReadOnlyList<string> TrackDefaultFields { get; } = new[]
    {
        SettingsFields.AudioChain, SettingsFields.ForeignTitles, SettingsFields.WhenAudioUnderstood, SettingsFields.WhenAudioNotUnderstood, SettingsFields.SubtitleLanguages,
    };

    private static void ClearTrackDefaults(SettingsLayer? layer)
    {
        if (layer is null)
        {
            return;
        }

        foreach (var field in TrackDefaultFields)
        {
            layer.Clear(field);
        }
    }

    /// <summary>
    /// Upgrades settings saved by an older version. Safe to run more than once.
    /// Version 1 → 2: the probe fields are retired (their values are noted once); everything new starts at its default.
    /// </summary>
    /// <returns>True when something changed and the settings should be saved.</returns>
    public bool Upgrade()
    {
        // A downgrade may read known fields, but must never rewrite or normalize a future format.
        if (ConfigVersion > CurrentVersion) return false;
        var changed = false;

        // 0 = a fresh install (no file yet): nothing to tell the admin. 1 and 2 are files written by earlier Babelfin builds.
        var from = ConfigVersion;
        if (from < 2)
        {
            changed = true;
        }

        if (from < 3)
        {
            // Version 2 -> 3: the server-wide "replace request indices" switch becomes the per-app mode table.
            if (AppModes is null || AppModes.Count == 0)
            {
                AppModes = DefaultAppModes();
            }

            if (ReplaceRequestIndices == true)
            {
                UnknownAppMode = AppModeKind.ReplaceUnlessManual;
                foreach (var row in AppModes)
                {
                    row.Mode = AppModeKind.ReplaceUnlessManual;
                }
            }

            changed = true;
        }

        ProbeAudioLanguage = null;
        ProbeSubtitleLanguage = null;
        ProbeReplaceRequestIndices = null;
        ReplaceRequestIndices = null;
        AppModes ??= new List<AppMode>();
        Normalize();

        // Lists and nested objects can come back null from a hand-edited or partial file.
        Everyday ??= new SettingsLayer();
        Libraries ??= new List<LibraryDefault>();
        SmartRules ??= new List<SmartRule>();
        Titles ??= new List<TitleDefault>();
        BurnedIn ??= new List<BurnedInFlag>();
        Starter ??= new StarterSettings();
        Keywords ??= new KeywordSettings();
        LanguageAliases ??= new List<LanguageAlias>();

        // Preserve hidden values before historical upgrades clear or replace their old containers.
        var legacyGenreOrder = from >= 1 && from < 7 ? SmartRules.Select(r => r.Clone()).ToArray() : null;
        if (from >= 1 && from < 7) RetireHiddenSettings();

        if (from >= 1 && from < 4)
        {
            // Version 3 -> 4: Babelfin is neutral until someone chooses (the video's own tracks play). The original-language library rule
            // is switched off, and the server-wide audio and subtitle defaults are cleared; Plugin copies the previous file to the
            // backups folder before this runs, so nothing is lost.
            Starter.Enabled = false;
            ClearTrackDefaults(Everyday);
            foreach (var library in Libraries)
            {
                ClearTrackDefaults(library?.Settings);
            }

            changed = true;
        }

        if (from >= 1 && from < 5)
        {
            // Version 4 -> 5: the admin page no longer offers rules for everyone, show and movie defaults, burned-in approvals, the
            // "if there are no full subtitles" default, per-library extras or test mode. What they held is cleared so nothing hidden keeps
            // acting (Plugin copies the previous file to the backups folder first). People's own files are not touched.
            SmartRules.Clear();
            Titles.Clear();
            BurnedIn.Clear();
            Everyday.IfNothingMatches = null;
            foreach (var library in Libraries)
            {
                if (library is not null)
                {
                    library.Settings = TableModes.TableOnly(library.Settings) ?? new SettingsLayer();
                }
            }

            DryRun = false;
            changed = true;
        }

        if (from >= 1 && from < 6)
        {
            // Version 5 -> 6: "New users" keeps only languages; the top table ("Defaults for everyone") already applies to new people
            // and keeps applying when it changes. Jellyfin Desktop runs jellyfin-web, so it copies the previous
            // episode's tracks like the web app: an automatically added "keep the app's choice" row becomes "next episode".
            if (NewUserTemplate is not null)
            {
                NewUserTemplate.Everyday = TableModes.WithoutTableFields(NewUserTemplate.Everyday) ?? new SettingsLayer();
                NewUserTemplate.Libraries.Clear();
                NewUserTemplate.PerLibrary = null;
            }

            var desktop = FindAppMode("Jellyfin Desktop");
            if (desktop is null)
            {
                AppModes.Add(new AppMode { Client = "Jellyfin Desktop", Mode = AppModeKind.ReplaceOnAutoplay });
            }
            else if (desktop.Mode == AppModeKind.Trust)
            {
                desktop.Mode = AppModeKind.ReplaceOnAutoplay;
            }

            changed = true;
        }

        if (from < 8) ActivateArchivedGenreRows(legacyGenreOrder);

        if (from >= 1 && from < 9)
        {
            // Version 8 -> 9: the Android app reports itself as "Jellyfin for Android", which no shipped row named, so it ran as an
            // unknown app. A missing row is added as shipped; a row that was only discovered becomes the shipped row (keeping when it
            // was seen); a row the admin set is kept.
            var android = FindAppMode("Jellyfin for Android");
            if (android is null)
            {
                AppModes.Add(new AppMode { Client = "Jellyfin for Android", Mode = AppModeKind.ReplaceOnAutoplay });
            }
            else if (android.SeenOnly)
            {
                android.Mode = AppModeKind.ReplaceOnAutoplay;
                android.RememberPrePlayChoices = null;
                android.SeenOnly = false;
            }

            changed = true;
        }

        SendImportantEventsToJellyfinActivity = null;

        if (ConfigVersion != CurrentVersion)
        {
            ConfigVersion = CurrentVersion;
            changed = true;
        }

        return changed;
    }
}
