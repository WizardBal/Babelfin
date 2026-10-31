using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Library;

/// <summary>One title (a series or a movie) as the library facts see it.</summary>
/// <param name="Genres">Its genres.</param>
/// <param name="OriginalLanguage">Its own raw original language from metadata ("ja", "jpn", "Japanese"…), or null.</param>
public sealed record LibraryTitle(IReadOnlyList<string>? Genres, string? OriginalLanguage);

/// <summary>
/// Computes a library's <see cref="LibraryFact"/> from its name and titles. Pure: no Jellyfin I/O.
/// </summary>
/// <remarks>
/// Behavior:
/// <list type="bullet">
/// <item>Percentages are whole numbers rounded down, and thresholds compare against them, so the stored percent and the
/// verdict always agree (59.9% shows as 59 and doesn't reach 60).</item>
/// <item>Original languages are counted by canonical code (<see cref="LanguageTag.Code"/>), so "ja", "jpn" and "Japanese" count
/// together while "zh-Hans" and "zh-Hant" stay apart. A tie for the most common language goes to the code that sorts first (ordinal).</item>
/// <item>Genres compare trimmed and case-insensitively; a title counts once however many listed genres it has.</item>
/// <item>The word list is matched with the engine's <c>TextMatcher</c>: Latin words as whole words ("Anime" and "Anime Movies"
/// match "anime", "Animated Classics" doesn't), words with CJK characters as substrings ("アニメ映画" matches "アニメ").</item>
/// </list>
/// </remarks>
public static class LibraryFactsCalculator
{
    /// <summary>Computes the facts.</summary>
    /// <param name="libraryId">The library id.</param>
    /// <param name="name">The library name.</param>
    /// <param name="titles">Its series and movies.</param>
    /// <param name="starter">The admin's starter settings (word list, genres, thresholds).</param>
    /// <param name="languages">The language table.</param>
    /// <param name="computedAt">The time to record (UTC).</param>
    /// <returns>The facts.</returns>
    public static LibraryFact Calculate(
        Guid libraryId,
        string? name,
        IReadOnlyCollection<LibraryTitle> titles,
        StarterSettings starter,
        LanguageTable languages,
        DateTime computedAt)
    {
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentNullException.ThrowIfNull(languages);

        var genres = new HashSet<string>(
            (starter.OriginalAudioGenres ?? new List<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var withLanguage = 0;
        var withGenre = 0;
        foreach (var title in titles)
        {
            if (title is null)
            {
                continue;
            }

            if (genres.Count > 0 && title.Genres is not null && title.Genres.Any(g => g is not null && genres.Contains(g.Trim())))
            {
                withGenre++;
            }

            var tag = languages.ResolveMetadataOriginalLanguage(title.OriginalLanguage);
            if (tag is not null)
            {
                withLanguage++;
                counts[tag.Code] = counts.TryGetValue(tag.Code, out var n) ? n + 1 : 1;
            }
        }

        var total = titles.Count(t => t is not null);
        var fact = new LibraryFact
        {
            LibraryId = libraryId,
            Name = name ?? string.Empty,
            ComputedAt = computedAt,
            TitleCount = total,
            TitlesWithOriginalLanguage = withLanguage,
            OriginalAudioGenrePercent = Percent(withGenre, total),
            NameMatchesWordList = NameMatches(name, starter.OriginalAudioLibraryWords),
        };

        if (counts.Count > 0)
        {
            var top = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
            fact.MostCommonOriginalLanguage = top.Key;
            fact.MostCommonOriginalPercent = Percent(top.Value, withLanguage);
            if (fact.MostCommonOriginalPercent >= starter.UsualOriginalLanguagePercent)
            {
                fact.UsualOriginalLanguage = top.Key;
            }
        }

        fact.DetectedOriginalAudio = fact.NameMatchesWordList
            || (fact.TitleCount > 0 && fact.OriginalAudioGenrePercent >= starter.GenreSharePercent);
        return fact;
    }

    /// <summary>
    /// Whether a library name matches the original-audio word list: Latin-script words as whole words, case-insensitive;
    /// words with CJK characters as substrings. The resolver uses this too while no facts are computed yet.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <param name="words">The word list.</param>
    /// <returns>True on a match.</returns>
    public static bool NameMatches(string? name, IEnumerable<string>? words)
    {
        if (string.IsNullOrWhiteSpace(name) || words is null)
        {
            return false;
        }

        var text = TextMatcher.Normalize(name);
        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word))
            {
                continue;
            }

            if (TextMatcher.Contains(text, new PreparedKeyword(word)))
            {
                return true;
            }
        }

        return false;
    }

    private static int Percent(int part, int whole) => whole <= 0 ? 0 : (int)(part * 100L / whole);
}
