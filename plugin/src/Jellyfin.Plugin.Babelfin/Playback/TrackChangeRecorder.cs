using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Playback;

/// <summary>Identifies the order in which a manual change was observed in this process.</summary>
public readonly record struct ManualChangeOrder(Guid Epoch, long Sequence)
{
    private static readonly Guid ProcessEpoch = Guid.NewGuid();
    private static long _next;

    /// <summary>Allocates an order at the point a change is observed.</summary>
    /// <returns>A process-local ordering token.</returns>
    public static ManualChangeOrder Next() => new(ProcessEpoch, Interlocked.Increment(ref _next));
}

/// <summary>
/// Turns a manual track change into a remembered override: the person's memory scope decides where
/// it is stored (the rest of the show, the season, the title, or nowhere), the tracks are stored as descriptors (language,
/// variety, kind, title, codec, channels), never as indices, and an activity entry says what was remembered.
/// </summary>
/// <remarks>
/// How changes are recorded:
/// <list type="bullet">
/// <item>The resolved profile carries no memory scope, so it is read from the layer its provenance label names
/// (<see cref="MemoryFor"/>): locks, the person's or the admin's scopes, libraries and everyday layers, the starter, else
/// Babelfin's default (the rest of the show).</item>
/// <item>Genre rules contribute only audio and subtitle table fields, so they cannot choose a memory scope.</item>
/// <item>The caller passes the previous indices (the expected or the last reported ones) so the recorder knows which of the two
/// changed; only a changed stream is stored, the other keeps what the stored memory had.</item>
/// <item>A movie's (or any non-episode's) "rest of show" and "season" scopes store under its own id as a title override.</item>
/// <item>An index that names no stream of the source is ignored (nothing is remembered for it).</item>
/// <item>No person file yet (the resolver seeds one on its first use, so this is rare): nothing is recorded.</item>
/// <item>The change is made on the person's document as the store holds it when the update runs (<see cref="IPersonStore.Update"/>), not on the copy read to
/// find the memory scope, so a "remove all" or another save in between is neither overwritten nor lost. Remembered changes are not capped: the cap of
/// <see cref="Api.SettingsValidator.MaxOverrides"/> counts explicit settings only.</item>
/// </list>
/// </remarks>
public sealed class TrackChangeRecorder
{
    private readonly IProfileResolver _resolver;
    private readonly IPersonStore _store;
    private readonly IActivityLog _activity;
    private readonly ILogger<TrackChangeRecorder> _logger;

    /// <summary>Initializes a new instance of the <see cref="TrackChangeRecorder"/> class.</summary>
    /// <param name="resolver">The profile resolver (for the memory scope and the item's context).</param>
    /// <param name="store">The per-person store.</param>
    /// <param name="activity">Babelfin's activity log.</param>
    /// <param name="logger">Logger.</param>
    public TrackChangeRecorder(IProfileResolver resolver, IPersonStore store, IActivityLog activity, ILogger<TrackChangeRecorder> logger)
    {
        _resolver = resolver;
        _store = store;
        _activity = activity;
        _logger = logger;
    }

    /// <summary>
    /// Records a manual track change. Never throws (failures are logged and nothing is stored).
    /// </summary>
    /// <param name="user">The person.</param>
    /// <param name="item">The item that was playing (or requested).</param>
    /// <param name="source">The media source the indices refer to.</param>
    /// <param name="audioIndex">The audio index now chosen (null = unknown).</param>
    /// <param name="subtitleIndex">The subtitle index now chosen (null = not reported, negative = off).</param>
    /// <param name="previousAudio">The audio index before the change (the expected or last reported one).</param>
    /// <param name="previousSubtitle">The subtitle index before the change (null = unknown, negative = off).</param>
    /// <param name="client">The app that reported the change.</param>
    /// <param name="order">The order assigned when the change was observed; omitted callers receive a new order.</param>
    /// <returns>The stored memory, or null when nothing was stored.</returns>
    public ManualMemory? Record(User user, BaseItem item, MediaSourceInfo source, int? audioIndex, int? subtitleIndex, int? previousAudio, int? previousSubtitle, string? client, ManualChangeOrder? order = null)
    {
        try
        {
            return RecordCore(user, item, source, audioIndex, subtitleIndex, previousAudio, previousSubtitle, client, order ?? ManualChangeOrder.Next());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't remember a track change for {Item}", item?.Name);
            return null;
        }
    }

    /// <summary>
    /// The memory scope that applies, read from the layer the resolved profile's provenance names for the Memory field.
    /// </summary>
    /// <param name="person">The person's document.</param>
    /// <param name="admin">The admin's configuration.</param>
    /// <param name="result">The resolved profile for the item.</param>
    /// <returns>The scope.</returns>
    public static MemoryScope MemoryFor(PersonSettings person, PluginConfiguration admin, ResolvedProfileResult result)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(result);
        const MemoryScope Default = MemoryScope.RestOfShow;
        if (!result.Sources.TryGetValue(SettingsFields.Memory, out var label) || string.IsNullOrWhiteSpace(label))
        {
            return Default;
        }

        var context = result.Context;
        if (label == ProfileResolver.LockLabel)
        {
            return person.Locks?.Memory ?? Default;
        }

        if (label == ProfileResolver.PersonEverydayLabel)
        {
            return person.Everyday?.Memory ?? Default;
        }

        if (label == ProfileResolver.AdminEverydayLabel)
        {
            return admin.Everyday?.Memory ?? Default;
        }

        if (label == StarterRules.LibraryLabel || label == StarterRules.GenreLabel)
        {
            return StarterRules.OriginalAudioLayer.Memory ?? Default;
        }

        var isAdmin = label.StartsWith("the admin's ", StringComparison.Ordinal);
        var isPerson = label.StartsWith("your ", StringComparison.Ordinal);
        if (!isAdmin && !isPerson)
        {
            return StarterRules.Defaults(admin.Starter?.ForeignTitles ?? ForeignTitlesRule.DubInMyLanguage).Memory ?? Default;
        }

        // A scope (title, season, show, collection) or library layer from the named settings source:
        // the first layer in precedence order that sets the field is the one the label names.
        foreach (var layer in OwnerLayers(person, admin, context, isAdmin))
        {
            if (layer?.Memory is { } memory)
            {
                return memory;
            }
        }

        return Default;
    }

    /// <summary>Where a change is stored for a scope: the item id, the override scope and a display name.</summary>
    /// <param name="memory">The memory scope (not <see cref="MemoryScope.DontRemember"/>).</param>
    /// <param name="context">The item's context.</param>
    /// <param name="item">The item (for names).</param>
    /// <returns>The target.</returns>
    public static (Guid Id, OverrideScope Scope, string? Name) TargetFor(MemoryScope memory, ItemContext context, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(item);
        var episode = context.Kind == ContentKind.Episode;
        if (memory == MemoryScope.RestOfShow && episode && context.SeriesId is { } series && series != Guid.Empty)
        {
            return (series, OverrideScope.Series, context.SeriesName ?? item.Name);
        }

        if (memory == MemoryScope.Season && episode && context.SeasonId is { } season && season != Guid.Empty)
        {
            var seasonName = (item as Episode)?.SeasonName;
            var name = string.IsNullOrWhiteSpace(context.SeriesName)
                ? seasonName
                : string.IsNullOrWhiteSpace(seasonName) ? context.SeriesName : context.SeriesName + ", " + seasonName;
            return (season, OverrideScope.Season, name ?? item.Name);
        }

        var id = context.ItemId != Guid.Empty ? context.ItemId : item.Id;
        return (id, OverrideScope.Title, item.Name ?? context.Name);
    }

    private static IEnumerable<SettingsLayer?> OwnerLayers(PersonSettings person, PluginConfiguration admin, ItemContext context, bool isAdmin)
    {
        var ids = new List<Guid>();
        if (context.ItemId != Guid.Empty)
        {
            ids.Add(context.ItemId);
        }

        if (context.SeasonId is { } season && season != Guid.Empty)
        {
            ids.Add(season);
        }

        if (context.SeriesId is { } series && series != Guid.Empty)
        {
            ids.Add(series);
        }

        // Collections: the person's own order first, then the item's remaining ones (as the resolver orders them).
        var order = person.CollectionOrder ?? new List<string>();
        ids.AddRange(context.CollectionIds
            .Select((id, i) => (Id: id, Rank: order.FindIndex(o => PersonSettings.SameId(o, id)), Index: i))
            .OrderBy(c => c.Rank < 0 ? int.MaxValue : c.Rank)
            .ThenBy(c => c.Index)
            .Select(c => c.Id));

        foreach (var id in ids)
        {
            yield return isAdmin ? admin.FindTitle(id)?.Settings : person.FindOverride(id)?.Settings;
        }

        foreach (var libraryId in context.LibraryIds)
        {
            yield return isAdmin ? admin.FindLibrary(libraryId)?.Settings : person.FindLibrary(libraryId)?.Settings;
        }

        yield return isAdmin ? admin.Everyday : person.Everyday;
    }

    private static int Off(int? subtitle) => subtitle is { } s && s >= 0 ? s : -1;

    private static string ScopeWord(OverrideScope scope) => scope switch
    {
        OverrideScope.Series => "the show",
        OverrideScope.Season => "the season",
        _ => "the title",
    };

    private static string Describe(RememberedTrack? track) => track is null
        ? string.Empty
        : string.Join(' ', new[] { track.Variety ?? track.Language ?? "unlabelled", track.Kind }.Where(s => !string.IsNullOrEmpty(s)));

    private ManualMemory? RecordCore(User user, BaseItem item, MediaSourceInfo source, int? audioIndex, int? subtitleIndex, int? previousAudio, int? previousSubtitle, string? client, ManualChangeOrder order)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(source);

        var audioChanged = audioIndex is >= 0 && audioIndex != previousAudio;
        var subtitleChanged = subtitleIndex.HasValue && (!previousSubtitle.HasValue || Off(subtitleIndex) != Off(previousSubtitle));
        if (!audioChanged && !subtitleChanged)
        {
            return null;
        }

        var result = _resolver.Resolve(user, item, client);
        if (!result.PersonEnabled)
        {
            return null;
        }

        var person = _store.TryGet(user.Id);
        if (person is null)
        {
            _logger.LogDebug("[Babelfin] No settings file for {User} yet; the track change on {Item} isn't remembered", user.Username, item.Name);
            return null;
        }

        var admin = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var memory = MemoryFor(person, admin, result);
        if (memory == MemoryScope.DontRemember)
        {
            return null;
        }

        var classifier = EngineServices.Current.Classifier;
        RememberedTrack? audio = null;
        if (audioChanged)
        {
            var descriptor = TrackComparer.Describe(source, audioIndex, TrackType.Audio, classifier);
            audio = descriptor is null ? null : RememberedTrack.From(descriptor);
            audioChanged = audio is not null;
        }

        var subtitlesOff = false;
        RememberedTrack? subtitle = null;
        if (subtitleChanged)
        {
            if (Off(subtitleIndex) < 0)
            {
                // Subtitles were on (a track was expected) and are now off.
                subtitlesOff = true;
            }
            else
            {
                var descriptor = TrackComparer.Describe(source, subtitleIndex, TrackType.Subtitle, classifier);
                subtitle = descriptor is null ? null : RememberedTrack.From(descriptor);
                subtitleChanged = subtitle is not null;
            }
        }

        if (!audioChanged && !subtitleChanged)
        {
            return null;
        }

        var (targetId, scope, name) = TargetFor(memory, result.Context, item);
        var now = DateTime.UtcNow;

        // Changed on the stored document as it is when the store's lock is taken, not on the copy read above: a "remove all" or a save meeting this one
        // can't be overwritten by it, nor overwrite it.
        ManualMemory? manual = null;
        var saved = _store.Update(user.Id, current =>
        {
            // Profile/descriptor work happens before this atomic update. Disabling selection
            // during that work must also stop the pending preference write.
            if (current is null || !current.Enabled || Plugin.Instance?.Configuration.Enabled == false)
            {
                return null;
            }

            var document = current.Clone();
            var existing = document.FindOverride(targetId);
            var old = existing?.Manual;
            if (old is not null)
            {
                if (audioChanged && old.AudioOrderEpoch == order.Epoch && old.AudioOrder > order.Sequence) audioChanged = false;
                if (subtitleChanged && old.SubtitleOrderEpoch == order.Epoch && old.SubtitleOrder > order.Sequence) subtitleChanged = false;
            }
            if (!audioChanged && !subtitleChanged) return null;
            manual = new ManualMemory
            {
                Audio = audioChanged ? audio : old?.Audio?.Clone(),
                SubtitlesOff = subtitleChanged ? subtitlesOff : old?.SubtitlesOff ?? false,
                Subtitle = subtitleChanged ? subtitle : old?.Subtitle?.Clone(),
                At = now,
                Client = string.IsNullOrWhiteSpace(client) ? old?.Client : client.Trim(),
                AudioOrderEpoch = audioChanged ? order.Epoch : old?.AudioOrderEpoch ?? Guid.Empty,
                AudioOrder = audioChanged ? order.Sequence : old?.AudioOrder ?? 0,
                SubtitleOrderEpoch = subtitleChanged ? order.Epoch : old?.SubtitleOrderEpoch ?? Guid.Empty,
                SubtitleOrder = subtitleChanged ? order.Sequence : old?.SubtitleOrder ?? 0,
            };

            if (existing is null)
            {
                document.Overrides.Add(new ItemOverride
                {
                    ItemId = targetId.ToString("N"),
                    Scope = scope,
                    Name = name,
                    Settings = new SettingsLayer(),
                    Manual = manual,
                    UpdatedAt = now,
                });
            }
            else
            {
                // Explicit settings on the same override stay as they are.
                existing.Manual = manual;
                existing.Name ??= name;
                existing.UpdatedAt = now;
            }

            return document;
        });
        if (!saved || manual is null)
        {
            return null;
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Scope"] = scope.ToString(),
            ["Memory"] = memory.ToString(),
        };
        if (audioChanged)
        {
            details["AudioIndex"] = audioIndex!.Value.ToString(CultureInfo.InvariantCulture);
            details["Audio"] = Describe(audio);
        }

        if (subtitleChanged)
        {
            details["SubtitleIndex"] = Off(subtitleIndex).ToString(CultureInfo.InvariantCulture);
            details["Subtitle"] = subtitlesOff ? "off" : Describe(subtitle);
        }

        if (!string.IsNullOrWhiteSpace(client))
        {
            details["Client"] = client.Trim();
        }

        var what = string.IsNullOrWhiteSpace(name) ? ScopeWord(scope) : ScopeWord(scope) + " " + name.Trim();
        _activity.Add(new ActivityEntry(now, ActivityKinds.OverrideCreated, user.Id, item.Id, "Remembered your track change for " + what, details));
        _logger.LogInformation(
            "[Babelfin] Remembered {User}'s track change on {Item} for {Scope} {Name}: audio {Audio}, subtitles {Subtitle}",
            user.Username,
            item.Name,
            scope,
            name,
            audioChanged ? Describe(audio) : "unchanged",
            subtitleChanged ? (subtitlesOff ? "off" : Describe(subtitle)) : "unchanged");
        return manual;
    }
}
