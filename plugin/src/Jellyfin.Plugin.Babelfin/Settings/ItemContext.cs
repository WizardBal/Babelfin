using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Babelfin.Settings;

/// <summary>The content type of an item as the rules see it.</summary>
public enum ContentKind
{
    /// <summary>Something else (a trailer, a home video…).</summary>
    Other,

    /// <summary>A movie.</summary>
    Movie,

    /// <summary>An episode.</summary>
    Episode,
}

/// <summary>What Babelfin knows about one library, computed in the background and cached.</summary>
public class LibraryFact
{
    /// <summary>Gets or sets the library id.</summary>
    public Guid LibraryId { get; set; }

    /// <summary>Gets or sets the library name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets when the facts were computed (UTC), or null when only the name is known.</summary>
    public DateTime? ComputedAt { get; set; }

    /// <summary>Gets or sets the number of titles counted (series and movies, not episodes).</summary>
    public int TitleCount { get; set; }

    /// <summary>Gets or sets the number of titles whose metadata has a recognised original language.</summary>
    public int TitlesWithOriginalLanguage { get; set; }

    /// <summary>Gets or sets the most common original language (canonical code) among the titles that have one, or null.</summary>
    public string? MostCommonOriginalLanguage { get; set; }

    /// <summary>Gets or sets the share (0–100) of titles with an original language that have <see cref="MostCommonOriginalLanguage"/>.</summary>
    public int MostCommonOriginalPercent { get; set; }

    /// <summary>Gets or sets the library's usual original language: <see cref="MostCommonOriginalLanguage"/> when its share reaches the admin's threshold, else null.</summary>
    public string? UsualOriginalLanguage { get; set; }

    /// <summary>Gets or sets the share (0–100) of titles with a genre in the admin's original-audio genre list.</summary>
    public int OriginalAudioGenrePercent { get; set; }

    /// <summary>Gets or sets a value indicating whether the library name matches the admin's original-audio word list.</summary>
    public bool NameMatchesWordList { get; set; }

    /// <summary>Gets or sets a value indicating whether the starter detection counts this as an original-audio library (name match, or genre share at or above the threshold).</summary>
    public bool DetectedOriginalAudio { get; set; }
}

/// <summary>
/// Facts about the item being decided that the resolver and rules need, built from Jellyfin by the adapter.
/// Pure data, no Jellyfin types, so the resolver and its tests stay free of the server.
/// </summary>
/// <param name="ItemId">The item id.</param>
/// <param name="Name">The item's name (an episode's own name).</param>
/// <param name="Kind">Movie, episode or other.</param>
/// <param name="SeasonId">The season id for an episode (or the season itself).</param>
/// <param name="SeriesId">The series id for an episode or season (or the series itself).</param>
/// <param name="SeriesName">The series name, for title matching.</param>
/// <param name="CollectionIds">The collections (box sets) the item is in.</param>
/// <param name="CollectionNames">Their names, same order.</param>
/// <param name="LibraryIds">The libraries (collection folders) the item belongs to; usually one.</param>
/// <param name="LibraryName">The first library's name.</param>
/// <param name="Genres">Genres of the item and its series.</param>
/// <param name="Tags">Tags of the item and its series.</param>
/// <param name="Studios">Studios and networks.</param>
/// <param name="Countries">Production countries.</param>
/// <param name="OriginalLanguage">The raw inherited original language from metadata.</param>
/// <param name="Client">The requesting app's name when known, else null.</param>
/// <param name="Library">The cached facts for the first library, or null when none are computed yet.</param>
public sealed record ItemContext(
    Guid ItemId,
    string? Name,
    ContentKind Kind,
    Guid? SeasonId,
    Guid? SeriesId,
    string? SeriesName,
    IReadOnlyList<Guid> CollectionIds,
    IReadOnlyList<string> CollectionNames,
    IReadOnlyList<Guid> LibraryIds,
    string? LibraryName,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Studios,
    IReadOnlyList<string> Countries,
    string? OriginalLanguage,
    string? Client = null,
    LibraryFact? Library = null)
{
    /// <summary>Gets a context with no item (resolves the everyday layers only).</summary>
    public static ItemContext None { get; } = new(
        Guid.Empty, null, ContentKind.Other, null, null, null,
        Array.Empty<Guid>(), Array.Empty<string>(), Array.Empty<Guid>(), null,
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null);

    /// <summary>Gets the first library id, or null.</summary>
    public Guid? LibraryId => LibraryIds.Count > 0 ? LibraryIds[0] : null;
}
