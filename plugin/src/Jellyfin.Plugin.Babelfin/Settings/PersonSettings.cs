using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Selection;

namespace Jellyfin.Plugin.Babelfin.Settings;

/// <summary>The scope an override or burned-in flag applies to.</summary>
public enum OverrideScope
{
    /// <summary>This episode or movie.</summary>
    Title,

    /// <summary>This season.</summary>
    Season,

    /// <summary>This series.</summary>
    Series,

    /// <summary>This collection (box set).</summary>
    Collection,

    /// <summary>A whole library (admin-only; burned-in flags).</summary>
    Library,
}

/// <summary>A person's settings for one library.</summary>
public class LibraryLayer
{
    /// <summary>Gets or sets the library id ("N" format GUID).</summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>Gets or sets the library name at the time it was saved (display only).</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the settings.</summary>
    public SettingsLayer Settings { get; set; } = new();

    /// <summary>A deep copy.</summary>
    /// <returns>The copy.</returns>
    public LibraryLayer Clone() => new() { LibraryId = LibraryId, Name = Name, Settings = Settings.Clone() };
}

/// <summary>A remembered track (mutable twin of <see cref="TrackDescriptor"/> for storage).</summary>
public class RememberedTrack
{
    /// <summary>Gets or sets the language base.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the variety or script code when more specific than the base.</summary>
    public string? Variety { get; set; }

    /// <summary>Gets or sets the kind word ("full", "partial", "sdh", "normal", "commentary", "ad").</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the raw track title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the codec.</summary>
    public string? Codec { get; set; }

    /// <summary>Gets or sets the channel count.</summary>
    public int? Channels { get; set; }

    /// <summary>Builds a stored track from an engine descriptor.</summary>
    /// <param name="descriptor">The descriptor.</param>
    /// <returns>The stored form.</returns>
    public static RememberedTrack From(TrackDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new RememberedTrack
        {
            Language = descriptor.Language,
            Variety = descriptor.Variety,
            Kind = descriptor.Kind,
            Title = descriptor.Title,
            Codec = descriptor.Codec,
            Channels = descriptor.Channels,
        };
    }

    /// <summary>Converts to the engine descriptor.</summary>
    /// <returns>The descriptor.</returns>
    public TrackDescriptor ToDescriptor() => new(Language, Variety, Kind, Title, Codec, Channels);

    /// <summary>A copy.</summary>
    /// <returns>The copy.</returns>
    public RememberedTrack Clone() => new() { Language = Language, Variety = Variety, Kind = Kind, Title = Title, Codec = Codec, Channels = Channels };
}

/// <summary>What a manual track change remembered; written by the playback recorder and read by the decider.</summary>
public class ManualMemory
{
    /// <summary>Gets or sets the remembered audio track, or null when audio wasn't changed.</summary>
    public RememberedTrack? Audio { get; set; }

    /// <summary>Gets or sets a value indicating whether subtitles were turned off by hand.</summary>
    public bool SubtitlesOff { get; set; }

    /// <summary>Gets or sets the remembered subtitle track, or null.</summary>
    public RememberedTrack? Subtitle { get; set; }

    /// <summary>Gets or sets when the change was made (UTC).</summary>
    public DateTime At { get; set; }

    /// <summary>Gets or sets the app that reported the change.</summary>
    public string? Client { get; set; }

    /// <summary>Gets or sets the process that last recorded audio ordering metadata.</summary>
    public Guid AudioOrderEpoch { get; set; }

    /// <summary>Gets or sets the audio event order within that process.</summary>
    public long AudioOrder { get; set; }

    /// <summary>Gets or sets the process that last recorded subtitle ordering metadata.</summary>
    public Guid SubtitleOrderEpoch { get; set; }

    /// <summary>Gets or sets the subtitle event order within that process.</summary>
    public long SubtitleOrder { get; set; }

    /// <summary>Converts to the engine's override.</summary>
    /// <returns>The engine override.</returns>
    public ManualOverride ToEngine() => new(Audio?.ToDescriptor(), SubtitlesOff, Subtitle?.ToDescriptor());

    /// <summary>A deep copy.</summary>
    /// <returns>The copy.</returns>
    public ManualMemory Clone() => new() { Audio = Audio?.Clone(), SubtitlesOff = SubtitlesOff, Subtitle = Subtitle?.Clone(), At = At, Client = Client, AudioOrderEpoch = AudioOrderEpoch, AudioOrder = AudioOrder, SubtitleOrderEpoch = SubtitleOrderEpoch, SubtitleOrder = SubtitleOrder };
}

/// <summary>A person's override for a title, season, series or collection (an explicit one, a remembered track change, or both).</summary>
public class ItemOverride
{
    /// <summary>Gets or sets the id of the item the override is stored under (the episode, season, series or collection; "N" format GUID).</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the scope the id stands for.</summary>
    public OverrideScope Scope { get; set; }

    /// <summary>Gets or sets the item's name when saved (display only).</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the explicit settings (may be empty when only <see cref="Manual"/> is present).</summary>
    public SettingsLayer Settings { get; set; } = new();

    /// <summary>Gets or sets the remembered manual track change, if any.</summary>
    public ManualMemory? Manual { get; set; }

    /// <summary>Gets or sets when it was last changed (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>A deep copy.</summary>
    /// <returns>The copy.</returns>
    public ItemOverride Clone() => new() { ItemId = ItemId, Scope = Scope, Name = Name, Settings = Settings.Clone(), Manual = Manual?.Clone(), UpdatedAt = UpdatedAt };
}

/// <summary>A "burned-in subtitles in &lt;language&gt;" flag.</summary>
public class BurnedInFlag
{
    /// <summary>Gets or sets the id of the title, season, series, collection or library the flag is on.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the scope.</summary>
    public OverrideScope Scope { get; set; }

    /// <summary>Gets or sets the item's name when saved (display only).</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the burned-in languages.</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Gets or sets when it was set (UTC).</summary>
    public DateTime SetAt { get; set; }

    /// <summary>Gets or sets the id of the person who set it (empty for an admin flag in the plugin configuration).</summary>
    public string? SetBy { get; set; }

    /// <summary>A deep copy.</summary>
    /// <returns>The copy.</returns>
    public BurnedInFlag Clone() => new() { ItemId = ItemId, Scope = Scope, Name = Name, Languages = Languages.ToList(), SetAt = SetAt, SetBy = SetBy };
}

/// <summary>What Babelfin changed in a person's Jellyfin settings and must restore.</summary>
public class JellyfinChanges
{
    /// <summary>Gets or sets the person's "remember audio selections" flag before Babelfin turned it off.</summary>
    public bool? RememberAudioSelections { get; set; }

    /// <summary>Gets or sets the person's "remember subtitle selections" flag before Babelfin turned it off.</summary>
    public bool? RememberSubtitleSelections { get; set; }

    /// <summary>Gets or sets when the change was made (UTC).</summary>
    public DateTime ChangedAt { get; set; }

    /// <summary>A copy.</summary>
    /// <returns>The copy.</returns>
    public JellyfinChanges Clone() => new() { RememberAudioSelections = RememberAudioSelections, RememberSubtitleSelections = RememberSubtitleSelections, ChangedAt = ChangedAt };
}

/// <summary>A language Babelfin guessed for a person (always shown as detected).</summary>
public class AssumedLanguage
{
    /// <summary>Gets or sets the field it was put in (<see cref="SettingsFields.Understood"/> or <see cref="SettingsFields.Reads"/>).</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Gets or sets the canonical language code.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets where it came from: "server" (the server's metadata language), "ui" (the web client's language) or "other-field" (copied from the person's other language list).</summary>
    public string From { get; set; } = string.Empty;
}

/// <summary>How a person's settings were seeded from Jellyfin.</summary>
public class SeedRecord
{
    /// <summary>Gets or sets when the seed was made (UTC).</summary>
    public DateTime SeededAt { get; set; }

    /// <summary>Gets or sets the Jellyfin audio language preference at seed time.</summary>
    public string? JellyfinAudioLanguage { get; set; }

    /// <summary>Gets or sets the Jellyfin subtitle language preference at seed time.</summary>
    public string? JellyfinSubtitleLanguage { get; set; }

    /// <summary>Gets or sets the Jellyfin subtitle mode at seed time ("Default", "Always", "OnlyForced", "None", "Smart").</summary>
    public string? JellyfinSubtitleMode { get; set; }

    /// <summary>Gets or sets the languages Babelfin assumed. Empty when everything came from the person's own Jellyfin preferences.</summary>
    public List<AssumedLanguage> Assumed { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the person confirmed (or changed) the assumed languages; the banner then disappears.</summary>
    public bool Confirmed { get; set; }

    /// <summary>Gets or sets a value indicating whether the new-user template was applied on top of the seed.</summary>
    public bool FromTemplate { get; set; }
}

/// <summary>
/// Everything stored for one person: their layers, overrides, burned-in flags and the admin's locks.
/// Saved as JSON in the data folder; also the shape of the admin's new-user template (XML in the plugin configuration).
/// Never contains file paths.
/// </summary>
public class PersonSettings
{
    // Process-local account incarnation, carried by clones but never trusted from JSON or XML.
    [System.Text.Json.Serialization.JsonIgnore]
    [System.Xml.Serialization.XmlIgnore]
    internal Guid StorageEpoch { get; set; }

    /// <summary>The current schema version of the per-person file.</summary>
    public const int CurrentSchemaVersion = 4;

    /// <summary>Gets or sets the schema version this document was written with.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Gets or sets the Jellyfin user id (empty in a template).</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user name when saved (used to match users on import; display only otherwise).</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets a value indicating whether Babelfin acts for this person at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a retired switch ("use my settings everywhere"); read from old files and ignored since the table's two modes
    /// (<see cref="PerLibrary"/>) replaced it.</summary>
    public bool UseEverydayEverywhere { get; set; }

    /// <summary>Gets or sets the "Audio and subtitles" table's mode: true = a choice per library, false = the same for every
    /// library; null = automatic (<see cref="TableModes.PersonPerLibrary"/>).</summary>
    public bool? PerLibrary { get; set; }

    /// <summary>Gets or sets a value indicating whether Babelfin handles next-episode tracks for this person by turning off their Jellyfin remember flags
    /// ("Set audio track based on previous item" and "Set subtitle track based on previous item"); on by default, and the old values are kept in
    /// <see cref="JellyfinChanges"/>.</summary>
    public bool HandleNextEpisode { get; set; } = true;

    /// <summary>Gets or sets what Babelfin changed in this person's Jellyfin settings, or null.</summary>
    public JellyfinChanges? JellyfinChanges { get; set; }

    /// <summary>Gets or sets how the settings were seeded; null in a template.</summary>
    public SeedRecord? Seed { get; set; }

    /// <summary>Gets or sets the person's everyday settings (layer 7).</summary>
    public SettingsLayer Everyday { get; set; } = new();

    /// <summary>Gets or sets the person's per-library settings (layer 6).</summary>
    public List<LibraryLayer> Libraries { get; set; } = new();

    /// <summary>Gets or sets ordered genre rows in smart-rule storage; only positive, single-name Genre rows contribute table fields.</summary>
    public List<SmartRule> SmartRules { get; set; } = new();

    private List<ItemOverride> _overrides = new();

    /// <summary>Gets or sets the person's title, season, series and collection overrides (layers 2–4).</summary>
    public List<ItemOverride> Overrides { get => _overrides; set { _overrides = value; OverridesPresent = true; } }

    internal bool OverridesPresent { get; private set; }

    /// <summary>Gets or sets the person's ordered collection ids: for a title in several collections the first listed wins, field by field.</summary>
    public List<string> CollectionOrder { get; set; } = new();

    private List<BurnedInFlag> _burnedIn = new();

    /// <summary>Gets or sets the burned-in flags this person set (they apply to this person only until an admin approves them).</summary>
    public List<BurnedInFlag> BurnedIn { get => _burnedIn; set { _burnedIn = value; BurnedInPresent = true; } }

    internal bool BurnedInPresent { get; private set; }

    /// <summary>Gets or sets the admin's locks for this person (layer 1): every set field wins over everything else.</summary>
    public SettingsLayer Locks { get; set; } = new();

    /// <summary>Gets or sets inactive legacy settings retained for recovery.</summary>
    public RetiredPersonSettings Retired { get; set; } = new();

    /// <summary>Gets or sets migration details awaiting administrator acknowledgement.</summary>
    public List<string> MigrationNotes { get; set; } = new();

    /// <summary>Gets or sets when a successful write last changed this document (UTC).</summary>
    public DateTime LastChangedAt { get; set; }

    /// <summary>Gets or sets the saving account id, prefixed with admin: for an administrator.</summary>
    public string? LastChangedBy { get; set; }

    /// <summary>Gets or sets the saving account's display name, when available.</summary>
    public string? LastChangedByName { get; set; }

    /// <summary>Gets or sets when the document was last saved (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Finds the person's layer for a library.</summary>
    /// <param name="libraryId">The library id.</param>
    /// <returns>The layer, or null.</returns>
    public LibraryLayer? FindLibrary(Guid libraryId) => Libraries.FirstOrDefault(l => SameId(l.LibraryId, libraryId));

    /// <summary>Finds the person's override for an item id.</summary>
    /// <param name="itemId">The episode, season, series or collection id.</param>
    /// <returns>The override, or null.</returns>
    public ItemOverride? FindOverride(Guid itemId) => Overrides.FirstOrDefault(o => SameId(o.ItemId, itemId));

    /// <summary>Returns a deep copy.</summary>
    /// <returns>The copy.</returns>
    public PersonSettings Clone() => new()
    {
        StorageEpoch = StorageEpoch,
        SchemaVersion = SchemaVersion,
        UserId = UserId,
        UserName = UserName,
        Enabled = Enabled,
        UseEverydayEverywhere = UseEverydayEverywhere,
        PerLibrary = PerLibrary,
        HandleNextEpisode = HandleNextEpisode,
        JellyfinChanges = JellyfinChanges?.Clone(),
        Seed = Seed is null ? null : new SeedRecord
        {
            SeededAt = Seed.SeededAt,
            JellyfinAudioLanguage = Seed.JellyfinAudioLanguage,
            JellyfinSubtitleLanguage = Seed.JellyfinSubtitleLanguage,
            JellyfinSubtitleMode = Seed.JellyfinSubtitleMode,
            Assumed = Seed.Assumed.Select(a => new AssumedLanguage { Field = a.Field, Language = a.Language, From = a.From }).ToList(),
            Confirmed = Seed.Confirmed,
            FromTemplate = Seed.FromTemplate,
        },
        Everyday = Everyday.Clone(),
        Libraries = Libraries.Select(l => l.Clone()).ToList(),
        SmartRules = SmartRules.Select(r => r.Clone()).ToList(),
        Overrides = Overrides.Select(o => o.Clone()).ToList(),
        CollectionOrder = CollectionOrder.ToList(),
        BurnedIn = BurnedIn.Select(b => b.Clone()).ToList(),
        Locks = Locks.Clone(),
        Retired = Retired?.Clone() ?? new(),
        MigrationNotes = (MigrationNotes ?? new()).ToList(),
        UpdatedAt = UpdatedAt,
        OverridesPresent = OverridesPresent,
        BurnedInPresent = BurnedInPresent,
        LastChangedAt = LastChangedAt,
        LastChangedBy = LastChangedBy,
        LastChangedByName = LastChangedByName,
    };

    /// <summary>
    /// Applies the admin's new-user template on top of this (seeded) document: set template everyday fields win over seeded ones,
    /// the template's libraries, rules, overrides, collection order, locks and the two switches are copied when present, and the seed is marked.
    /// </summary>
    /// <param name="template">The template (its UserId and UserName are ignored).</param>
    public void ApplyTemplate(PersonSettings template)
    {
        ArgumentNullException.ThrowIfNull(template);
        Everyday = Everyday.With(template.Everyday);
        foreach (var library in template.Libraries)
        {
            Libraries.RemoveAll(l => string.Equals(l.LibraryId, library.LibraryId, StringComparison.OrdinalIgnoreCase));
            Libraries.Add(library.Clone());
        }

        if (template.SmartRules.Count > 0)
        {
            SmartRules = template.SmartRules.Select(r => r.Clone()).ToList();
        }

        foreach (var o in template.Overrides)
        {
            Overrides.RemoveAll(x => string.Equals(x.ItemId, o.ItemId, StringComparison.OrdinalIgnoreCase));
            Overrides.Add(o.Clone());
        }

        if (template.CollectionOrder.Count > 0)
        {
            CollectionOrder = template.CollectionOrder.ToList();
        }

        Locks = Locks.With(template.Locks);
        Enabled = template.Enabled;
        UseEverydayEverywhere = template.UseEverydayEverywhere;
        PerLibrary = template.PerLibrary;
        Seed ??= new SeedRecord { SeededAt = DateTime.UtcNow };
        Seed.FromTemplate = true;
    }

    /// <summary>Whether a stored id string names the GUID (either GUID format, any case).</summary>
    /// <param name="stored">The stored id.</param>
    /// <param name="id">The GUID.</param>
    /// <returns>True when equal.</returns>
    public static bool SameId(string? stored, Guid id) => Guid.TryParse(stored, out var parsed) && parsed == id;
}
