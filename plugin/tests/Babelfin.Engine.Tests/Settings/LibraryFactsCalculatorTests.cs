using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>library facts from a library's name and titles.</summary>
public class LibraryFactsCalculatorTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    // the default word list matches whole Latin words and CJK substrings.
    [Theory]
    [InlineData("Anime", true)]
    [InlineData("anime", true)]
    [InlineData("Anime Movies", true)]
    [InlineData("Kids' Anime", true)]
    [InlineData("Anime_Series", true)]
    [InlineData("Animes", true)]
    [InlineData("Animé", true)]
    [InlineData("アニメ", true)]
    [InlineData("日本のアニメ映画", true)]
    [InlineData("动漫", true)]
    [InlineData("애니메이션", true)]
    [InlineData("Animated Classics", false)]
    [InlineData("Animation", false)]
    [InlineData("Movies", false)]
    [InlineData("TV Shows", false)]
    [InlineData("", false)]
    public void NameMatches_DefaultWordList(string name, bool expected)
    {
        Assert.Equal(expected, LibraryFactsCalculator.NameMatches(name, new StarterSettings().OriginalAudioLibraryWords));
    }

    // the word list is editable; an edited list is what counts.
    [Fact]
    public void NameMatches_EditedWordList()
    {
        var words = new[] { "Original Audio", "  ", "Cloudvale" };

        Assert.True(LibraryFactsCalculator.NameMatches("Studio Cloudvale", words));
        Assert.True(LibraryFactsCalculator.NameMatches("original-audio films", words));
        Assert.False(LibraryFactsCalculator.NameMatches("Anime", words));
        Assert.False(LibraryFactsCalculator.NameMatches("Anime", null));
    }

    // A name match makes the library original-audio even with no titles.
    [Fact]
    public void Calculate_NameMatch_DetectedWithoutTitles()
    {
        var fact = Calc("Anime", Array.Empty<LibraryTitle>());

        Assert.True(fact.NameMatchesWordList);
        Assert.True(fact.DetectedOriginalAudio);
        Assert.Equal(0, fact.TitleCount);
    }

    // No titles: everything zero or null, nothing detected without a name match.
    [Fact]
    public void Calculate_NoTitles()
    {
        var fact = Calc("Movies", Array.Empty<LibraryTitle>(), new StarterSettings { GenreSharePercent = 0 });

        Assert.Equal(0, fact.TitleCount);
        Assert.Equal(0, fact.TitlesWithOriginalLanguage);
        Assert.Null(fact.MostCommonOriginalLanguage);
        Assert.Equal(0, fact.MostCommonOriginalPercent);
        Assert.Null(fact.UsualOriginalLanguage);
        Assert.Equal(0, fact.OriginalAudioGenrePercent);
        Assert.False(fact.NameMatchesWordList);
        Assert.False(fact.DetectedOriginalAudio);
        Assert.Equal(Now, fact.ComputedAt);
        Assert.Equal("Movies", fact.Name);
    }

    // at least 50% of titles with an "Anime" genre makes the library original-audio.
    [Fact]
    public void Calculate_GenreShareAtThreshold_Detected()
    {
        var fact = Calc("Cartoons", new[]
        {
            Title("ja", "Animation", "anime"),
            Title("ja", "Anime"),
            Title("en", "Comedy"),
            Title(null),
        });

        Assert.Equal(4, fact.TitleCount);
        Assert.Equal(50, fact.OriginalAudioGenrePercent);
        Assert.False(fact.NameMatchesWordList);
        Assert.True(fact.DetectedOriginalAudio);
    }

    // Below the share threshold nothing is detected; percentages round down.
    [Fact]
    public void Calculate_GenreShareBelowThreshold_NotDetected()
    {
        var fact = Calc("Cartoons", new[]
        {
            Title("ja", " Anime "),
            Title("en", "Comedy"),
            Title("en", "Drama"),
        });

        Assert.Equal(33, fact.OriginalAudioGenrePercent);
        Assert.False(fact.DetectedOriginalAudio);
    }

    // The genre list and the threshold are editable.
    [Fact]
    public void Calculate_EditedGenresAndThreshold()
    {
        var starter = new StarterSettings { OriginalAudioGenres = new List<string> { "Foreign" }, GenreSharePercent = 30 };
        var fact = Calc("Films", new[] { Title("fr", "Foreign"), Title("en", "Anime"), Title("en") }, starter);

        Assert.Equal(33, fact.OriginalAudioGenrePercent);
        Assert.True(fact.DetectedOriginalAudio);
    }

    // The usual original language needs 60% (editable) of the titles that have one.
    [Fact]
    public void Calculate_UsualOriginalLanguage_AtThreshold()
    {
        var fact = Calc("Shows", new[] { Title("ja"), Title("ja"), Title("ja"), Title("en"), Title("ko"), Title(null), Title("und") });

        Assert.Equal(7, fact.TitleCount);
        Assert.Equal(5, fact.TitlesWithOriginalLanguage);
        Assert.Equal("ja", fact.MostCommonOriginalLanguage);
        Assert.Equal(60, fact.MostCommonOriginalPercent);
        Assert.Equal("ja", fact.UsualOriginalLanguage);
    }

    // Below the threshold the most common language is reported but not "usual".
    [Fact]
    public void Calculate_UsualOriginalLanguage_BelowThreshold()
    {
        var fact = Calc("Shows", new[] { Title("en"), Title("en"), Title("ja"), Title("fr") });

        Assert.Equal("en", fact.MostCommonOriginalLanguage);
        Assert.Equal(50, fact.MostCommonOriginalPercent);
        Assert.Null(fact.UsualOriginalLanguage);
    }

    // "ja", "jpn" and "Japanese" are one language; the canonical code is stored.
    [Fact]
    public void Calculate_MixedCodes_CountedTogether()
    {
        var fact = Calc("Shows", new[] { Title("ja"), Title("jpn"), Title("Japanese"), Title("eng"), Title("en") });

        var ja = LanguageTable.Default.Resolve("ja")!.Code;
        Assert.Equal(ja, fact.MostCommonOriginalLanguage);
        Assert.Equal(60, fact.MostCommonOriginalPercent);
        Assert.Equal(ja, fact.UsualOriginalLanguage);
    }

    // Unrecognised languages don't count as having one.
    [Fact]
    public void Calculate_UnknownLanguages_Ignored()
    {
        var fact = Calc("Shows", new[] { Title("xx-nonsense"), Title(""), Title("zxx"), Title("ko") });

        Assert.Equal(4, fact.TitleCount);
        Assert.Equal(1, fact.TitlesWithOriginalLanguage);
        Assert.Equal("ko", fact.MostCommonOriginalLanguage);
        Assert.Equal(100, fact.MostCommonOriginalPercent);
    }

    // A tie goes to the code that sorts first, so the result is stable between refreshes.
    [Fact]
    public void Calculate_Tie_IsDeterministic()
    {
        var first = Calc("Shows", new[] { Title("ko"), Title("ja") });
        var second = Calc("Shows", new[] { Title("ja"), Title("ko") });

        Assert.Equal(first.MostCommonOriginalLanguage, second.MostCommonOriginalLanguage);
        Assert.Equal("ja", first.MostCommonOriginalLanguage);
        Assert.Null(first.UsualOriginalLanguage);
    }

    // The facts carry no file paths: only the id, the name and numbers.
    [Fact]
    public void Calculate_KeepsIdAndName()
    {
        var id = Guid.NewGuid();
        var fact = LibraryFactsCalculator.Calculate(id, "アニメ", new[] { Title("ja", "Anime") }, new StarterSettings(), LanguageTable.Default, Now);

        Assert.Equal(id, fact.LibraryId);
        Assert.Equal("アニメ", fact.Name);
        Assert.True(fact.NameMatchesWordList);
        Assert.Equal(100, fact.OriginalAudioGenrePercent);
        var stringProps = typeof(LibraryFact).GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).ToArray();
        Assert.DoesNotContain(stringProps, n => n.Contains("Path", StringComparison.OrdinalIgnoreCase));
    }

    private static LibraryTitle Title(string? language, params string[] genres) => new(genres, language);

    private static LibraryFact Calc(string name, IReadOnlyCollection<LibraryTitle> titles, StarterSettings? starter = null) =>
        LibraryFactsCalculator.Calculate(Guid.NewGuid(), name, titles, starter ?? new StarterSettings(), LanguageTable.Default, Now);
}
