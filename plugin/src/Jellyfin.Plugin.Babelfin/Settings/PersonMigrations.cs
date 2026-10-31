using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Storage;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Resolution;

namespace Jellyfin.Plugin.Babelfin.Settings;

/// <summary>A layer retained only for recovery; never passed to the resolver.</summary>
public class RetiredLayer
{
    public string Location { get; set; } = string.Empty;
    public SettingsLayer Settings { get; set; } = new();
    public RetiredLayer Clone() => new() { Location = Location, Settings = (Settings ?? new SettingsLayer()).Clone() };
}

/// <summary>Inactive legacy choices, preserved in exports and backups.</summary>
public class RetiredPersonSettings
{
    public List<SmartRule> Rules { get; set; } = new();
    public List<SmartRule> GenreRules { get; set; } = new();
    public List<ItemOverride> Collections { get; set; } = new();
    public List<string> CollectionOrder { get; set; } = new();
    public SettingsLayer Locks { get; set; } = new();
    public List<RetiredLayer> Layers { get; set; } = new();
    public RetiredPersonSettings Clone() => new()
    {
        Rules = CopyRules(Rules), GenreRules = CopyRules(GenreRules),
        Collections = (Collections ?? new()).Where(o => o is not null).Select(o => new ItemOverride
        {
            ItemId = o.ItemId, Scope = o.Scope, Name = o.Name, UpdatedAt = o.UpdatedAt,
            Settings = (o.Settings ?? new()).Clone(), Manual = o.Manual?.Clone(),
        }).ToList(),
        CollectionOrder = (CollectionOrder ?? new()).ToList(), Locks = (Locks ?? new()).Clone(),
        Layers = (Layers ?? new()).Where(l => l is not null).Select(l => l.Clone()).ToList(),
    };

    private static List<SmartRule> CopyRules(List<SmartRule>? rules) => (rules ?? new()).Where(r => r is not null).Select(r => new SmartRule
    {
        Id = r.Id, Name = r.Name, Enabled = r.Enabled, Match = r.Match, Settings = (r.Settings ?? new()).Clone(),
        Conditions = (r.Conditions ?? new()).Where(c => c is not null).Select(c => new RuleCondition
        {
            Kind = c.Kind, Negate = c.Negate, IsRegex = c.IsRegex, Values = (c.Values ?? new()).ToList(),
        }).ToList(),
    }).ToList();
}

/// <summary>Schema 2/3 migrations. The store backs up the original before invoking this on a loaded document.</summary>
public static class PersonMigrations
{
    // The shared genre component caps visible rows at 100 (the general API rule limit is 200).
    internal const int MaxActiveGenreRows = 100;
    private static readonly string[] EverydayHidden = { SettingsFields.TieBreaks, SettingsFields.OriginalSource };

    public static bool Upgrade(PersonSettings person)
    {
        if (person.SchemaVersion >= PersonSettings.CurrentSchemaVersion) return false;
        person.MigrationNotes ??= new();
        person.Retired ??= new();
        person.SmartRules ??= new();
        person.Retired.GenreRules ??= new();
        person.Retired.Rules ??= new();
        // Capture order before schema 2's archive classification separates table rows from rows with extras.
        var legacyOrder = person.SchemaVersion < 2 ? person.SmartRules.Select(r => r.Clone()).ToArray() : null;
        if (person.SchemaVersion < 2) UpgradeLegacy(person);
        ActivateGenreRows(person.SmartRules, person.Retired.GenreRules, person.Retired.Rules, person.MigrationNotes, legacyOrder);
        person.SchemaVersion = PersonSettings.CurrentSchemaVersion;
        return true;
    }

    private static void UpgradeLegacy(PersonSettings person)
    {
        var notes = person.MigrationNotes;
        var retired = person.Retired;
        foreach (var rule in person.SmartRules)
        {
            var genre = IsLegacyTableGenreRule(rule);
            (genre ? retired.GenreRules : retired.Rules).Add(rule.Clone());
            notes.Add(genre ? $"Genre choice '{rule.Name}' was preserved for the genre table."
                : $"Hidden rule '{rule.Name}' was archived and no longer applies.");
        }
        person.SmartRules.Clear();
        foreach (var row in person.Overrides.Where(o => o.Scope == OverrideScope.Collection).ToArray())
        {
            retired.Collections.Add(row.Clone());
            person.Overrides.Remove(row);
            notes.Add($"Hidden collection choice '{row.Name ?? row.ItemId}' was archived and no longer applies.");
        }
        if (person.CollectionOrder.Count > 0)
        {
            retired.CollectionOrder.AddRange(person.CollectionOrder);
            person.CollectionOrder.Clear();
            notes.Add("The hidden collection order was archived.");
        }
        if (!person.Locks.IsEmpty)
        {
            retired.Locks = retired.Locks.With(person.Locks);
            person.Locks = new SettingsLayer();
            notes.Add("Hidden administrator locks were archived and no longer apply.");
        }
        RetireFields(person.Everyday, EverydayHidden, "everyday", retired, notes);
        foreach (var library in person.Libraries)
            RetireFields(library.Settings, SettingsFields.All.Except(PluginConfiguration.TrackDefaultFields), "library " + library.LibraryId, retired, notes);
        foreach (var row in person.Overrides)
        {
            RetireFields(row.Settings, SettingsFields.All.Except(PluginConfiguration.TrackDefaultFields), "override " + row.ItemId, retired, notes);
            ReconcileMemory(row, notes);
        }
    }

    // Matches the shared UI component's enabled, single-name row shape.
    internal static bool IsGenreRow(SmartRule rule) => rule.Enabled && IsLegacyGenreRule(rule)
        && rule.Conditions[0].Values.Count == 1 && (rule.Match == RuleMatch.All || rule.Match == RuleMatch.Any);

    private static bool IsLegacyGenreRule(SmartRule rule) => rule.Conditions is { Count: 1 }
        && rule.Conditions[0] is { Kind: ConditionKind.Genre, Negate: false, IsRegex: false }
        && rule.Conditions[0].Values is { } values && values.Any(v => !string.IsNullOrWhiteSpace(v));
    // Legacy genre rules allowed an OR-list of names when all settings belonged to the table.
    internal static bool IsLegacyTableGenreRule(SmartRule rule) => IsLegacyGenreRule(rule)
        && (rule.Match == RuleMatch.All || rule.Match == RuleMatch.Any)
        && rule.Settings.SetFields().All(TableModes.Fields.Contains);

    internal static void ActivateGenreRows(List<SmartRule> active, List<SmartRule> genreArchive, List<SmartRule> otherArchive,
        List<string> notes, IReadOnlyList<SmartRule>? legacyOrder = null)
    {
        var candidates = (legacyOrder ?? active).Where(r => r is not null).Select(r => r.Clone()).ToList();
        foreach (var rule in candidates)
        {
            var archive = IsLegacyTableGenreRule(rule) ? genreArchive : otherArchive;
            if (!archive.Any(old => SameRule(old, rule))) archive.Add(rule.Clone());
        }
        if (legacyOrder is not { Count: > 0 } && genreArchive.Any(r => r is not null && IsLegacyGenreRule(r))
            && otherArchive.Any(r => r is not null && IsLegacyGenreRule(r) && !genreArchive.Any(g => g is not null && SameRule(g, r))))
            notes.Add("Older genre archives used separate lists, so their cross-list order cannot be reconstructed. "
                + "Preserved the known order within each list: genre archive, then other rules. Original archives and backups remain unchanged.");
        candidates.AddRange(genreArchive.Where(r => r is not null).Select(r => r.Clone()));
        // Legacy archives placed single-genre rules with extra fields in Rules; recover their table choices too.
        candidates.AddRange(otherArchive.Where(r => r is not null && IsLegacyGenreRule(r)).Select(r => r.Clone()));
        active.Clear();
        var seen = new List<SmartRule>();
        var overflow = new List<string>();
        foreach (var rule in candidates)
        {
            if (!rule.Enabled || !IsLegacyGenreRule(rule) || seen.Any(old => SameRule(old, rule))) continue;
            seen.Add(rule);
            var table = TableModes.TableOnly(rule.Settings);
            if (table is null) continue;
            // Materialise the legacy choice so a lower explicit chain cannot bypass this higher genre row.
            // Keep the legacy fallback steps; the original ForeignTitles value remains in the archive.
            if (table.AudioChain is null && table.ForeignTitles is { } foreign)
            {
                table.AudioChain = StarterRules.ChainFor(foreign).Select(step => new AudioStepSetting(step.Kind, step.Language)).ToArray();
                table.ForeignTitles = null;
            }
            // A legacy OR-list becomes separate visible rows with identical choices, in original order.
            var names = rule.Conditions[0].Values.Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var activated = 0;
            for (var i = 0; i < names.Length; i++)
            {
                if (active.Count >= MaxActiveGenreRows) { overflow.Add(names[i]); continue; }
                var row = rule.Clone();
                row.Settings = table.Clone();
                row.Name = names[i];
                row.Conditions[0].Values = new List<string> { names[i] };
                row.Match = RuleMatch.All;
                if (i > 0) row.Id = string.Empty;
                row.EnsureId();
                active.Add(row);
                activated++;
            }
            if (activated > 0) notes.Add("Activated " + activated + " of " + names.Length + " genre rows from '" + rule.Name + "'; the original settings remain archived.");
        }
        if (overflow.Count > 0) notes.Add("The genre table supports " + MaxActiveGenreRows + " active rows. " + overflow.Count
            + " additional genre rows remain archived and inactive: " + string.Join(", ", overflow.Take(8))
            + (overflow.Count > 8 ? " and " + (overflow.Count - 8) + " more" : string.Empty) + ". No archived choices were deleted.");
    }

    private static bool SameRule(SmartRule a, SmartRule b) => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(b.Id)
        ? string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
        : string.Equals(System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(b), StringComparison.Ordinal);
    private static void RetireFields(SettingsLayer layer, IEnumerable<string> fields, string location, RetiredPersonSettings retired, List<string> notes)
    {
        var moved = new SettingsLayer();
        foreach (var field in fields)
        {
            if (!layer.IsSet(field)) continue;
            moved.CopyField(layer, field);
            layer.Clear(field);
        }
        if (moved.IsEmpty) return;
        retired.Layers.Add(new RetiredLayer { Location = location, Settings = moved });
        notes.Add($"Hidden settings at {location} were archived and no longer apply.");
    }

    private static void ReconcileMemory(ItemOverride row, List<string> notes)
    {
        var memory = row.Manual;
        // Equal or missing timestamps are ambiguous: preserve the person's remembered action.
        if (memory is null || row.UpdatedAt <= memory.At) return;
        var changed = new List<string>();
        if ((row.Settings.AudioChain is not null || row.Settings.ForeignTitles.HasValue) && memory.Audio is not null)
        {
            memory.Audio = null;
            changed.Add("audio");
        }
        if ((row.Settings.WhenAudioUnderstood.HasValue || row.Settings.WhenAudioNotUnderstood.HasValue || row.Settings.SubtitleLanguages is not null)
            && (memory.Subtitle is not null || memory.SubtitlesOff))
        {
            memory.Subtitle = null;
            memory.SubtitlesOff = false;
            changed.Add("subtitles");
        }
        if (memory.Audio is null && memory.Subtitle is null && !memory.SubtitlesOff) row.Manual = null;
        if (changed.Count > 0) notes.Add($"Newer {string.Join(" and ", changed)} choices for '{row.Name ?? row.ItemId}' replaced older remembered tracks.");
    }
}
