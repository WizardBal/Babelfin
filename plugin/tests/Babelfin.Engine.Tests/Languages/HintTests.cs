using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Xunit;

namespace Babelfin.Engine.Tests.Languages;

public class HintTests
{
    private static readonly LanguageTable Table = LanguageTable.Default;

    private static string? Hint(string? raw, string? title, string? file = null) => Table.ResolveWithHints(raw, title, file)?.Code;

    public static IEnumerable<object[]> DefaultKeywords() =>
        VarietyKeywordTable.Default.Keywords.Select(k => new object[] { k.Keyword, k.AppliesToBase, k.Variety ?? string.Empty, k.Script ?? string.Empty, k.Region ?? string.Empty });

    [Theory]
    [MemberData(nameof(DefaultKeywords))]
    public void Every_default_keyword_refines_its_base(string keyword, string appliesTo, string variety, string script, string region)
    {
        var raw = appliesTo == "*" ? "en" : appliesTo;
        var tag = Table.ResolveWithHints(raw, "Track " + keyword + " 2.0", null);
        Assert.NotNull(tag);
        Assert.Equal(variety.Length == 0 ? null : variety, tag!.Variety);
        Assert.Equal(script.Length == 0 ? null : script, tag.Script);
        Assert.Equal(region.Length == 0 ? null : region, tag.Region);
    }

    [Theory]
    [InlineData("chi", "Cantonese", "yue")]
    [InlineData("chi", "粵語", "yue")]
    [InlineData("zho", "粤语 5.1", "yue")]
    [InlineData("chi", "Mandarin", "cmn")]
    [InlineData("chi", "國語", "cmn")]
    [InlineData("chi", "普通话", "cmn")]
    [InlineData("chi", "简体中文", "zh-Hans")]
    [InlineData("chi", "繁體中文", "zh-Hant")]
    [InlineData("chi", "Chinese (Simplified)", "zh-Hans")]
    [InlineData("chi", "CHT", "zh-Hant")]
    [InlineData("por", "Português (Brasil)", "pt-BR")]
    [InlineData("por", "Portuguese BR", "pt-BR")]
    [InlineData("spa", "Español Latino", "es-419")]
    [InlineData("spa", "Spanish (Latin America)", "es-419")]
    [InlineData("spa", "Castellano", "es-ES")]
    [InlineData("spa", "Español (España)", "es-ES")]
    [InlineData("fre", "VFQ", "fr-CA")]
    [InlineData("fre", "Français (Québec)", "fr-CA")]
    [InlineData("fre", "VFF", "fr-FR")]
    [InlineData("srp", "Serbian Latin", "sr-Latn")]
    public void Title_keywords_refine_generic_tags(string raw, string title, string expected)
    {
        Assert.Equal(expected, Hint(raw, title));
    }

    [Fact]
    public void Hint_never_overrides_an_explicit_variety()
    {
        Assert.Equal("yue", Hint("yue", "Mandarin"));
        Assert.Equal("pt-PT", Hint("pt-PT", "Brasil"));
        Assert.Equal("zh-Hans", Hint("chs", "Traditional"));
    }

    [Fact]
    public void Hint_applies_only_to_its_base()
    {
        Assert.Equal("ja", Hint("jpn", "Brasil"));
        Assert.Equal("en", Hint("eng", "Latino"));
        Assert.Equal("de", Hint("ger", "VFQ"));
    }

    [Fact]
    public void Script_hint_and_variety_hint_combine()
    {
        Assert.Equal("yue-Hant", Hint("chi", "Cantonese (繁體)"));
    }

    [Fact]
    public void Conflicting_hints_leave_the_axis_generic()
    {
        Assert.Equal("zh", Hint("chi", "简体 & 繁體"));
    }

    [Theory]
    [InlineData("Japanese", "ja")]
    [InlineData("Japanese 2.0", "ja")]
    [InlineData("[CloudHarbor] Japanese 5.1", "ja")]
    [InlineData("English DTS 5.1", "en")]
    [InlineData("Cantonese", "yue")]
    [InlineData("Chinese (Mandarin)", "cmn")]
    public void Und_audio_resolves_from_a_name_in_the_title(string title, string expected)
    {
        Assert.Equal(expected, Hint("und", title));
        Assert.Equal(expected, Hint(null, title));
    }

    [Theory]
    [InlineData("Surround")]
    [InlineData("AD")]
    [InlineData("Hi")]
    [InlineData("Stereo 2.0")]
    [InlineData("in")]
    public void A_title_without_a_language_name_stays_unknown(string title)
    {
        Assert.Null(Hint("und", title));
    }

    [Theory]
    [InlineData(null, "Movie.pt-BR.srt", "pt-BR")]
    [InlineData("por", "Movie.pt-BR.srt", "pt-BR")]
    [InlineData(null, "movie.chs.srt", "zh-Hans")]
    [InlineData("chi", "movie.chs.srt", "zh-Hans")]
    [InlineData(null, "movie.en.forced.srt", "en")]
    [InlineData(null, "Movie.Name.2019.ja.ass", "ja")]
    [InlineData(null, "movie.en.hi.srt", "en")]
    [InlineData(null, "/media/films/Movie (2020)/Movie (2020).es-419.srt", "es-419")]
    [InlineData(null, @"C:\media\Movie.fr-CA.srt", "fr-CA")]
    [InlineData("chi", "movie.zh.Cantonese.srt", "yue")]
    public void File_name_hints(string? raw, string file, string expected)
    {
        Assert.Equal(expected, Hint(raw, null, file));
    }

    [Fact]
    public void File_name_hi_segment_is_a_flag_not_hindi()
    {
        Assert.Null(Hint(null, null, "movie.hi.srt"));
    }

    [Fact]
    public void File_name_of_another_base_does_not_refine()
    {
        Assert.Equal("ja", Hint("jpn", null, "Movie.pt-BR.srt"));
    }

    [Fact]
    public void Nothing_at_all_gives_null()
    {
        Assert.Null(Hint(null, null, null));
        Assert.Null(Hint("und", "", "movie.srt"));
    }

    [Fact]
    public void Hints_use_the_given_keyword_table()
    {
        var custom = new LanguageTable(
            new[] { new LanguageEntry("pt", "por", null, "Portuguese") },
            new AliasTable(new Dictionary<string, string>()),
            new VarietyKeywordTable(new[] { new VarietyKeyword("Tuga", "pt", null, null, "PT") }));
        Assert.Equal("pt-PT", custom.ResolveWithHints("por", "Dobragem Tuga", null)?.Code);
        Assert.Equal("pt", custom.ResolveWithHints("por", "Brasil", null)?.Code);
    }
}
