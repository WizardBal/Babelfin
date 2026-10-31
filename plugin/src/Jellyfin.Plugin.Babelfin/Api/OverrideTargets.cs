using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>
/// Which stored override stands for an item in the "Audio and subtitles (Babelfin)" dialog: a movie or a video stands for
/// itself, a show for itself, and a season or an episode for its show. There are no per-episode or per-season overrides from the dialog.
/// </summary>
public static class OverrideTargets
{
    /// <summary>Whether the dialog works for items of this type (music, books, folders, collections and people have no audio and subtitle choice to override).</summary>
    /// <param name="kind">The item's type.</param>
    /// <returns>True for a movie, video, show, season or episode.</returns>
    public static bool IsSupported(BaseItemKind kind)
        => kind is BaseItemKind.Movie or BaseItemKind.Video or BaseItemKind.Series or BaseItemKind.Season or BaseItemKind.Episode;

    /// <summary>Works out the id an override for an item is stored under, and its scope.</summary>
    /// <param name="kind">The item's type.</param>
    /// <param name="itemId">The item's id.</param>
    /// <param name="seriesId">The show's id for a season or an episode (empty when it has none).</param>
    /// <returns>The id and scope: <see cref="OverrideScope.Title"/> for a movie or a video, <see cref="OverrideScope.Series"/> for a show, season or episode; null for another type or a season or episode with no show.</returns>
    public static (Guid Id, OverrideScope Scope)? For(BaseItemKind kind, Guid itemId, Guid seriesId)
    {
        switch (kind)
        {
            case BaseItemKind.Movie:
            case BaseItemKind.Video:
                return itemId == Guid.Empty ? null : (itemId, OverrideScope.Title);
            case BaseItemKind.Series:
                return itemId == Guid.Empty ? null : (itemId, OverrideScope.Series);
            case BaseItemKind.Season:
            case BaseItemKind.Episode:
                return seriesId == Guid.Empty ? null : (seriesId, OverrideScope.Series);
            default:
                return null;
        }
    }

    /// <summary>
    /// What applies to a movie, video or show without the person's own override for it ("Default" in the dialog): the person's document minus the entry stored
    /// under the target's id, resolved with the admin's configuration. Every other layer still counts: the person's other overrides (the target's collections),
    /// libraries, rules and everyday settings, and the admin's title and library defaults. A show is resolved as its episodes are played, so a rule on the
    /// content type "episode" applies to it; a movie or a video keeps its own type.
    /// </summary>
    /// <param name="person">The person's stored document (not changed).</param>
    /// <param name="admin">The admin's configuration.</param>
    /// <param name="context">The target's context (its library, collections, genres…).</param>
    /// <param name="target">The id and scope the target's override is stored under (<see cref="For"/>).</param>
    /// <param name="languages">The language table.</param>
    /// <returns>The profile with its provenance.</returns>
    public static EffectiveProfileDto Inherited(PersonSettings person, PluginConfiguration admin, ItemContext context, (Guid Id, OverrideScope Scope) target, LanguageTable languages)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(languages);
        var without = person.Clone();
        without.Overrides.RemoveAll(o => PersonSettings.SameId(o.ItemId, target.Id));
        var resolved = target.Scope == OverrideScope.Series ? context with { Kind = ContentKind.Episode } : context;
        return EffectiveProfileDto.From(ProfileResolver.Resolve(without, admin, resolved, languages));
    }

    /// <summary>
    /// Whether the person has a season or title override on a season or an episode of a show. Only the season and title overrides are looked at, their ids
    /// asked in batches of <see cref="PersonLookups.BatchSize"/> until a batch holds one that belongs to the show.
    /// </summary>
    /// <param name="overrides">The person's overrides.</param>
    /// <param name="seriesId">The show.</param>
    /// <param name="anyInShow">Says whether any of the stored ids is a season or an episode of the show (false for an item that is gone, hidden from the person or of another type).</param>
    /// <returns>True when a narrower override exists.</returns>
    public static bool HasNarrower(IEnumerable<ItemOverride> overrides, Guid seriesId, Func<IReadOnlyList<Guid>, bool> anyInShow)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(anyInShow);
        var candidates = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var o in overrides)
        {
            if (o is not null && o.Scope is (OverrideScope.Season or OverrideScope.Title) && Guid.TryParse(o.ItemId, out var id) && id != Guid.Empty && id != seriesId && seen.Add(id))
            {
                candidates.Add(id);
            }
        }

        return candidates.Chunk(PersonLookups.BatchSize).Any(batch => anyInShow(batch));
    }

    /// <summary>Whether any of the ids is a season or an episode of the show, as the person sees them.</summary>
    /// <param name="lookups">The request's item lookups.</param>
    /// <param name="ids">The ids to look at (at most <see cref="PersonLookups.BatchSize"/>).</param>
    /// <param name="seriesId">The show.</param>
    /// <returns>True when one is.</returns>
    internal static bool AnyInShow(PersonLookups lookups, IReadOnlyList<Guid> ids, Guid seriesId)
        => lookups.Items(ids, BaseItemKind.Season, BaseItemKind.Episode).Values.Any(item => SeriesOf(item) == seriesId);

    /// <summary>The show a season or an episode belongs to.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The show's id; empty for anything else.</returns>
    internal static Guid SeriesOf(BaseItem item) => item switch
    {
        Episode episode => episode.SeriesId,
        Season season => season.SeriesId,
        _ => Guid.Empty,
    };
}
