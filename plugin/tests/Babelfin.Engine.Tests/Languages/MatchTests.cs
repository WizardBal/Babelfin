using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Xunit;

namespace Babelfin.Engine.Tests.Languages;

public class MatchTests
{
    private static readonly LanguageTable Table = LanguageTable.Default;

    private static LanguageTag Tag(string code) => Table.Resolve(code) ?? throw new Xunit.Sdk.XunitException("unresolved " + code);

    [Theory]
    [InlineData("zh", "zh", MatchQuality.Exact)]
    [InlineData("zh", "cmn", MatchQuality.Exact)]
    [InlineData("zh", "yue", MatchQuality.Exact)]
    [InlineData("zh", "zh-Hant", MatchQuality.Exact)]
    [InlineData("yue", "cmn", MatchQuality.None)]
    [InlineData("cmn", "yue", MatchQuality.None)]
    [InlineData("yue", "zh", MatchQuality.Generic)]
    [InlineData("cmn", "zh", MatchQuality.Generic)]
    [InlineData("yue", "yue", MatchQuality.Exact)]
    [InlineData("zh-Hant", "zh-Hans", MatchQuality.None)]
    [InlineData("zh-Hans", "zh-Hant", MatchQuality.None)]
    [InlineData("zh-Hant", "zh", MatchQuality.Generic)]
    [InlineData("zh-Hans", "zh", MatchQuality.Generic)]
    [InlineData("zh-Hant", "zh-Hant-HK", MatchQuality.Exact)]
    [InlineData("zh-Hant-HK", "zh-Hant", MatchQuality.Generic)]
    [InlineData("zh-Hant-HK", "zh-Hant-HK", MatchQuality.Exact)]
    [InlineData("zh-Hant-HK", "zh-Hans", MatchQuality.None)]
    [InlineData("pt-BR", "pt", MatchQuality.Generic)]
    [InlineData("pt-BR", "pt-PT", MatchQuality.None)]
    [InlineData("pt", "pt-BR", MatchQuality.Exact)]
    [InlineData("pt-BR", "pt-BR", MatchQuality.Exact)]
    [InlineData("es-419", "es-ES", MatchQuality.None)]
    [InlineData("es-419", "es", MatchQuality.Generic)]
    [InlineData("fr-CA", "fr-FR", MatchQuality.None)]
    [InlineData("ja", "en", MatchQuality.None)]
    [InlineData("en", "en-US", MatchQuality.Exact)]
    [InlineData("en-US", "en", MatchQuality.Generic)]
    [InlineData("sr-Latn", "sr-Cyrl", MatchQuality.None)]
    [InlineData("sr-Latn", "sr", MatchQuality.Generic)]
    public void Truth_table(string wanted, string actual, MatchQuality expected)
    {
        Assert.Equal(expected, Table.Match(Tag(wanted), Tag(actual)));
    }

    [Fact]
    public void Generic_ranks_below_exact()
    {
        Assert.True(MatchQuality.Exact > MatchQuality.Generic);
        Assert.True(MatchQuality.Generic > MatchQuality.None);
    }

    [Fact]
    public void Hand_built_tags_compare_case_insensitively()
    {
        Assert.Equal(MatchQuality.Exact, Table.Match(new LanguageTag("ZH", "YUE", null, null), new LanguageTag("zh", "yue", null, null)));
    }
}
