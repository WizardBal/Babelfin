using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Resolution;

/// <summary>
/// Merges a person's layers and the admin's into one <see cref="ResolvedProfile"/> for an item, recording for every
/// field which layer set it. Pure: no I/O, no Jellyfin types, no plugin instance; the same inputs always give the same result.
/// </summary>
/// <remarks>
/// Table fields resolve independently: person's title/season/show choice and manual memory;
/// person's ordered genre rows; person's active library/all-libraries row; admin's ordered genre rows;
/// admin's active library/all-libraries defaults; Video default. General rules do not apply.
/// Genre rows contribute table fields only. Language lists and Advanced fields retain their existing
/// scope behavior, while archived rules, title defaults and locks never supply active table fields.
/// </remarks>
public static class ProfileResolver
{
    /// <summary>The label of the admin's locks.</summary>
    public const string LockLabel = "an admin lock";

    /// <summary>The label of the person's everyday layer.</summary>
    public const string PersonEverydayLabel = "your everyday settings";

    /// <summary>The label of the admin's everyday layer.</summary>
    public const string AdminEverydayLabel = "the admin's everyday defaults";

    private static readonly StarterSettings DefaultStarter = new();

    private static readonly AudioStep[] VideoDefaultChain = { new(AudioStepKind.VideoDefault) };

    /// <summary>Resolves a person's settings for an item.</summary>
    /// <param name="person">The person's document.</param>
    /// <param name="admin">The admin's configuration.</param>
    /// <param name="context">The item (<see cref="ItemContext.None"/> for the everyday layers alone).</param>
    /// <param name="languages">The language table (rule conditions on languages, burned-in de-duplication).</param>
    /// <returns>The profile with its provenance; <see cref="ResolvedProfileResult.Sources"/> is the profile's <see cref="ResolvedProfile.FieldSources"/>.</returns>
    public static ResolvedProfileResult Resolve(PersonSettings person, PluginConfiguration admin, ItemContext context, LanguageTable languages)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(languages);

        var personPerLibrary = TableModes.PersonPerLibrary(person);
        var adminPerLibrary = TableModes.AdminPerLibrary(admin);
        var starter = admin.Starter ?? DefaultStarter;
        var layers = new List<Entry>(16);

        // 1. Locks.
        Add(layers, TableModes.WithoutTableFields(person.Locks), LockLabel);

        // 2–4a. Title, season, show.
        var itemId = context.ItemId;
        var seasonId = NonEmpty(context.SeasonId);
        var seriesId = NonEmpty(context.SeriesId);
        if (itemId != Guid.Empty && itemId != seasonId && itemId != seriesId)
        {
            AddScope(layers, person, admin, itemId, "this title");
        }

        if (seasonId is { } season && season != seriesId)
        {
            AddScope(layers, person, admin, season, "this season");
        }

        if (seriesId is { } series)
        {
            AddScope(layers, person, admin, series, "this show");
        }

        // 4b. Collections, in the person's order, then the item's remaining ones.
        var collections = OrderCollections(person, context);
        foreach (var (id, name) in collections)
        {
            var what = string.IsNullOrWhiteSpace(name) ? "this collection" : "the " + name.Trim() + " collection";
            AddScope(layers, person, admin, id, what);
        }

        // Table precedence: person genre > person library/all > admin genre > admin library/all.
        // A genre row is deliberately limited to one positive Genre condition and table fields.
        AddRules(layers, person.SmartRules, context, isAdmin: false);
        if (!personPerLibrary)
            Add(layers, AdminOnlyFields.Without(TableModes.TableOnly(person.Everyday)), "your settings for every library");
        for (var i = 0; i < context.LibraryIds.Count; i++)
        {
            var id = context.LibraryIds[i];
            if (IndexOf(context.LibraryIds, id) != i) continue;
            var mine = person.Libraries is null ? null : person.FindLibrary(id);
            var theirs = admin.Libraries is null ? null : admin.FindLibrary(id);
            var name = i == 0 ? FirstNonBlank(context.LibraryName, context.Library?.Name, mine?.Name, theirs?.Name) : FirstNonBlank(mine?.Name, theirs?.Name);
            Add(layers, AdminOnlyFields.Without(personPerLibrary ? mine?.Settings : TableModes.WithoutTableFields(mine?.Settings)),
                name is null ? "your settings for this library" : "your " + name + " library settings");
        }
        Add(layers, AdminOnlyFields.Without(TableModes.WithoutTableFields(person.Everyday)), PersonEverydayLabel);
        AddRules(layers, admin.SmartRules, context, isAdmin: true);
        for (var i = 0; i < context.LibraryIds.Count; i++)
        {
            var id = context.LibraryIds[i];
            if (IndexOf(context.LibraryIds, id) != i) continue;
            var theirs = admin.Libraries is null ? null : admin.FindLibrary(id);
            var name = i == 0 ? FirstNonBlank(context.LibraryName, context.Library?.Name, theirs?.Name) : FirstNonBlank(theirs?.Name);
            Add(layers, adminPerLibrary ? theirs?.Settings : TableModes.WithoutTableFields(theirs?.Settings),
                name is null ? "the admin's defaults for this library" : "the admin's " + name + " library defaults");
        }
        Add(layers, adminPerLibrary ? TableModes.WithoutTableFields(admin.Everyday) : admin.Everyday, AdminEverydayLabel);
        var originalAudioLibrary = StarterRules.IsOriginalAudioLibrary(admin, context);
        if (starter.Enabled)
        {
            if (originalAudioLibrary) Add(layers, StarterRules.OriginalAudioLayer, StarterRules.LibraryLabel);
            else if (starter.GenreRule && StarterRules.HasOriginalAudioGenre(context.Genres, starter.OriginalAudioGenres))
                Add(layers, StarterRules.OriginalAudioLayer, StarterRules.GenreLabel);
        }
        Add(layers, StarterRules.Defaults(starter.ForeignTitles), StarterRules.DefaultsLabel);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var understood = Strings(Pick(layers, sources, SettingsFields.Understood)!.Understood);
        var reads = Strings(Pick(layers, sources, SettingsFields.Reads)!.Reads);
        var usesAudioDescription = Pick(layers, sources, SettingsFields.UsesAudioDescription)!.UsesAudioDescription!.Value;
        var prefersSdh = Pick(layers, sources, SettingsFields.PrefersSdh)!.PrefersSdh!.Value;
        var foreignTitles = Pick(layers, sources, SettingsFields.ForeignTitles)!.ForeignTitles!.Value;
        var chainLayer = Pick(layers, sources, SettingsFields.AudioChain);
        IReadOnlyList<AudioStep> chain;
        if (chainLayer is null && string.Equals(sources[SettingsFields.ForeignTitles], StarterRules.DefaultsLabel, StringComparison.Ordinal))
        {
            // Nobody chose an audio order or a rule for foreign titles: the audio is left to the video.
            chain = VideoDefaultChain;
            sources[SettingsFields.AudioChain] = StarterRules.DefaultsLabel;
        }
        else if (chainLayer is null)
        {
            chain = StarterRules.ChainFor(foreignTitles);
            sources[SettingsFields.AudioChain] = sources[SettingsFields.ForeignTitles];
        }
        else
        {
            chain = Steps(chainLayer.AudioChain!);
        }

        var avoidCommentary = Pick(layers, sources, SettingsFields.AvoidCommentary)!.AvoidCommentary!.Value;
        var tieBreaks = (TieBreak[])Pick(layers, sources, SettingsFields.TieBreaks)!.TieBreaks!.Clone();
        var unlabelled = Pick(layers, sources, SettingsFields.UnlabelledAudio)!;
        var originalSource = Pick(layers, sources, SettingsFields.OriginalSource)!.OriginalSource!.Value;
        var whenUnderstood = Pick(layers, sources, SettingsFields.WhenAudioUnderstood)!.WhenAudioUnderstood!.Value;
        var whenNotUnderstood = Pick(layers, sources, SettingsFields.WhenAudioNotUnderstood)!.WhenAudioNotUnderstood!.Value;
        var unknownUnderstood = Pick(layers, sources, SettingsFields.UnknownAudioCountsAsUnderstood)!.UnknownAudioCountsAsUnderstood!.Value;
        var subtitleLanguages = Strings(Pick(layers, sources, SettingsFields.SubtitleLanguages)!.SubtitleLanguages);
        var sdh = Pick(layers, sources, SettingsFields.Sdh)!.Sdh!.Value;
        var format = Pick(layers, sources, SettingsFields.Format)!.Format!.Value;
        var embedded = Pick(layers, sources, SettingsFields.Embedded)!.Embedded!.Value;
        var ifNothing = Pick(layers, sources, SettingsFields.IfNothingMatches)!.IfNothingMatches!.Value;
        _ = Pick(layers, sources, SettingsFields.Memory);

        var profile = new ResolvedProfile(
            Understood: understood,
            Reads: reads,
            UsesAudioDescription: usesAudioDescription,
            PrefersSdh: prefersSdh,
            AudioChain: chain,
            AvoidCommentary: avoidCommentary,
            TieBreaks: tieBreaks,
            UnlabelledAudio: unlabelled.UnlabelledAudio!.Value,
            UnlabelledAudioLanguage: string.IsNullOrWhiteSpace(unlabelled.UnlabelledAudioLanguage) ? null : unlabelled.UnlabelledAudioLanguage.Trim(),
            OriginalSource: originalSource,
            WhenAudioUnderstood: whenUnderstood,
            WhenAudioNotUnderstood: whenNotUnderstood,
            UnknownAudioCountsAsUnderstood: unknownUnderstood,
            SubtitleLanguages: subtitleLanguages,
            Sdh: sdh,
            Format: format,
            Embedded: embedded,
            IfNothingMatches: ifNothing,
            FieldSources: sources);

        IReadOnlyList<AssumedLanguage> assumed = person.Seed is { Confirmed: false, Assumed.Count: > 0 } seed
            ? seed.Assumed.ToArray()
            : Array.Empty<AssumedLanguage>();

        return new ResolvedProfileResult(
            profile,
            sources,
            person.Enabled,
            assumed,
            originalAudioLibrary,
            context.Library?.UsualOriginalLanguage,
            BurnedIn(person, admin, context, collections, languages),
            Manual(person, context, collections),
            context);
    }

    /// <summary>Resolves the selected field's winning layer, records its label, and returns it (null when no layer sets the field).</summary>
    private static SettingsLayer? Pick(List<Entry> layers, Dictionary<string, string> sources, string field)
    {
        foreach (var entry in layers)
        {
            if (entry.Layer.IsSet(field))
            {
                sources[field] = entry.Label;
                return entry.Layer;
            }
        }

        return null;
    }

    private static void Add(List<Entry> layers, SettingsLayer? layer, string label)
    {
        if (layer is not null)
        {
            layers.Add(new Entry(layer, label));
        }
    }

    private static void AddScope(List<Entry> layers, PersonSettings person, PluginConfiguration admin, Guid id, string what)
    {
        if (person.Overrides is not null)
        {
            Add(layers, AdminOnlyFields.Without(person.FindOverride(id)?.Settings), "your settings for " + what);
        }

        if (admin.Titles is not null)
        {
            Add(layers, TableModes.WithoutTableFields(admin.FindTitle(id)?.Settings), "the admin's defaults for " + what);
        }
    }

    private static void AddRules(List<Entry> target, List<SmartRule>? rules, ItemContext context, bool isAdmin)
    {
        if (rules is null)
        {
            return;
        }

        foreach (var rule in rules)
        {
            if (rule?.Settings is null || !RuleMatcher.MatchesGenre(rule, context))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(rule.Name) ? null : rule.Name.Trim();
            var label = isAdmin
                ? (name is null ? "the admin's unnamed genre defaults" : "the admin's " + name + " genre defaults")
                : (name is null ? "your unnamed genre settings" : "your " + name + " genre settings");

            // Understood and Reads are already resolved when rules join the layers, so a rule never decides them. A person's rule
            // never sets an admin-only field.
            var table = TableModes.TableOnly(rule.Settings);
            if (table is not null) target.Add(new Entry(table, label));
        }
    }

    private static List<(Guid Id, string? Name)> OrderCollections(PersonSettings person, ItemContext context)
    {
        var result = new List<(Guid Id, string? Name)>(context.CollectionIds.Count);
        if (context.CollectionIds.Count == 0)
        {
            return result;
        }

        if (person.CollectionOrder is { Count: > 0 } order)
        {
            foreach (var stored in order)
            {
                if (Guid.TryParse(stored, out var id))
                {
                    AddCollection(result, context, id);
                }
            }
        }

        foreach (var id in context.CollectionIds)
        {
            AddCollection(result, context, id);
        }

        return result;
    }

    private static void AddCollection(List<(Guid Id, string? Name)> result, ItemContext context, Guid id)
    {
        if (id == Guid.Empty)
        {
            return;
        }

        var index = IndexOf(context.CollectionIds, id);
        if (index < 0)
        {
            return;
        }

        foreach (var existing in result)
        {
            if (existing.Id == id)
            {
                return;
            }
        }

        result.Add((id, index < context.CollectionNames.Count ? context.CollectionNames[index] : null));
    }

    private static string[] BurnedIn(PersonSettings person, PluginConfiguration admin, ItemContext context, List<(Guid Id, string? Name)> collections, LanguageTable languages)
    {
        List<string>? result = null;
        HashSet<string>? seen = null;

        void Take(BurnedInFlag flag)
        {
            if (flag.Languages is null)
            {
                return;
            }

            foreach (var raw in flag.Languages)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var code = languages.Resolve(raw)?.Code ?? raw.Trim();
                seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (seen.Add(code))
                {
                    (result ??= new List<string>()).Add(code);
                }
            }
        }

        bool OnItem(BurnedInFlag flag)
        {
            if (!Guid.TryParse(flag.ItemId, out var id) || id == Guid.Empty)
            {
                return false;
            }

            if (id == context.ItemId || id == context.SeasonId || id == context.SeriesId)
            {
                return true;
            }

            foreach (var collection in collections)
            {
                if (collection.Id == id)
                {
                    return true;
                }
            }

            return false;
        }

        if (person.BurnedIn is { } mine)
        {
            foreach (var flag in mine)
            {
                if (flag is not null && flag.Scope != OverrideScope.Library && OnItem(flag))
                {
                    Take(flag);
                }
            }
        }

        if (admin.BurnedIn is { } shared)
        {
            foreach (var flag in shared)
            {
                if (flag is null)
                {
                    continue;
                }

                var applies = flag.Scope == OverrideScope.Library
                    ? Guid.TryParse(flag.ItemId, out var libraryId) && IndexOf(context.LibraryIds, libraryId) >= 0
                    : OnItem(flag);
                if (applies)
                {
                    Take(flag);
                }
            }
        }

        return result?.ToArray() ?? Array.Empty<string>();
    }

    private static ManualOverride? Manual(PersonSettings person, ItemContext context, List<(Guid Id, string? Name)> collections)
    {
        if (person.Overrides is null || person.Overrides.Count == 0)
        {
            return null;
        }

        ManualOverride? From(Guid? id) =>
            id is { } value && value != Guid.Empty && person.FindOverride(value)?.Manual is { } manual ? manual.ToEngine() : null;

        var found = From(context.ItemId) ?? From(context.SeasonId) ?? From(context.SeriesId);
        if (found is not null)
        {
            return found;
        }

        foreach (var collection in collections)
        {
            found = From(collection.Id);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string[] Strings(string[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(values.Length);
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value.Trim());
            }
        }

        return result.ToArray();
    }

    private static AudioStep[] Steps(AudioStepSetting[] steps)
    {
        var result = new List<AudioStep>(steps.Length);
        foreach (var step in steps)
        {
            if (step is not null)
            {
                result.Add(step.ToEngine());
            }
        }

        return result.ToArray();
    }

    private static Guid? NonEmpty(Guid? id) => id is { } value && value != Guid.Empty ? value : null;

    private static int IndexOf(IReadOnlyList<Guid> ids, Guid id)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i] == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private readonly record struct Entry(SettingsLayer Layer, string Label);
}
