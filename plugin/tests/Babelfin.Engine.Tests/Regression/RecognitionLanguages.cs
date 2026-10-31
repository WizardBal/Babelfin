using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

public class RecognitionLanguages
{
    private static readonly LanguageTable Table = LanguageTable.Default;

    [Theory]
    [InlineData("nb", "nob")]
    [InlineData("nob", "nob")]
    [InlineData("Norwegian (Bokmal)", "nob")]
    [InlineData("nn", "nno")]
    [InlineData("nno", "nno")]
    [InlineData("Norwegian (Nynorsk)", "nno")]
    public void NorwegianKeepsSpecificVarietiesUnderTheGenericBase(string raw, string variety)
    {
        var tag = Table.Resolve(raw)!;
        Assert.Equal("no", tag.Base);
        Assert.Equal(variety, tag.Variety);
        Assert.Equal(MatchQuality.Exact, Table.Match(Table.Resolve("no")!, tag));
        Assert.Equal(MatchQuality.Exact, Table.Match(Table.Resolve("nor")!, tag));
        Assert.Equal(MatchQuality.Generic, Table.Match(tag, Table.Resolve("nor")!));
        Assert.Equal(MatchQuality.None, Table.Match(tag, Table.Resolve(variety == "nob" ? "nno" : "nob")!));
    }

    public static IEnumerable<object[]> LatinAmericanRegions()
    {
        // All UN M49 419 members, including non-Spanish-speaking territories; Spain, US and Canada stay outside the group.
        foreach (var region in "BZ CR GT HN MX NI PA SV AG AI AW BB BL BQ BS CU CW DM DO GD GP HT JM KN KY LC MF MQ MS PR SX TC TT VC VG VI AR BO BR BV CL CO EC FK GF GS GY PE PY SR UY VE 005 013 029".Split(' '))
        {
            yield return new object[] { region };
        }
    }

    [Theory]
    [MemberData(nameof(LatinAmericanRegions))]
    public void LatinAmericanSpanishRegionsMatch419(string region)
    {
        Assert.Equal("es-419", Table.Resolve("es-" + region)?.Code);
        Assert.Equal("es-419", Table.Resolve("SPA_" + region.ToLowerInvariant())?.Code);
        Assert.Equal(MatchQuality.Exact, Table.Match(Table.Resolve("es-419")!, Table.Resolve("es-" + region)!));
    }

    [Theory]
    [InlineData("ES")]
    [InlineData("US")]
    [InlineData("CA")]
    [InlineData("BM")]
    [InlineData("GL")]
    [InlineData("GQ")]
    [InlineData("PH")]
    public void OtherSpanishRegionsStayDistinct(string region)
    {
        Assert.Equal("es-" + region, Table.Resolve("es-" + region)?.Code);
        Assert.Equal(MatchQuality.None, Table.Match(Table.Resolve("es-419")!, Table.Resolve("es-" + region)!));
    }

    [Fact]
    public void RegionalNormalizationKeepsScriptAndOtherLanguages()
    {
        Assert.Equal("es-Latn-419", Table.Resolve("spa-Latn-AR")?.Code);
        Assert.Equal("pt-BR", Table.Resolve("por-BR")?.Code);
        Assert.Equal(MatchQuality.Generic, Table.Match(Table.Resolve("es-419")!, Table.Resolve("es")!));
    }

    [Theory]
    [InlineData("cn")]
    [InlineData(" CN ")]
    public void CnIsCantoneseOnlyForMetadataOriginalLanguages(string raw)
    {
        Assert.Equal("yue", Table.ResolveMetadataOriginalLanguage(raw)?.Code);
        Assert.Equal("zh", Table.Resolve(raw)?.Code);
        Assert.Equal("zh", Table.ResolveWithHints(raw, null, null)?.Code);
    }

    [Theory]
    [InlineData("zh", "zh")]
    [InlineData("cmn", "cmn")]
    [InlineData("Cantonese", "yue")]
    [InlineData("ja", "ja")]
    [InlineData(null, null)]
    [InlineData("und", null)]
    public void MetadataResolutionPreservesOrdinaryInputs(string? raw, string? expected)
    {
        Assert.Equal(expected, Table.ResolveMetadataOriginalLanguage(raw)?.Code);
    }

    [Theory]
    [InlineData("sh")]
    [InlineData("hbs")]
    [InlineData("Serbo-Croatian")]
    public void SerboCroatianResolvesWithoutInventingASpecificLanguage(string raw)
    {
        Assert.Equal("sh", Table.Resolve(raw)?.Code);
        Assert.Equal(MatchQuality.Exact, Table.Match(Table.Resolve("sh")!, Table.Resolve(raw)!));
        foreach (var specific in new[] { "sr", "hr", "bs" })
        {
            Assert.Equal(MatchQuality.None, Table.Match(Table.Resolve(specific)!, Table.Resolve(raw)!));
            Assert.Equal(MatchQuality.None, Table.Match(Table.Resolve(raw)!, Table.Resolve(specific)!));
        }
    }

    [Fact]
    public void FilipinoAndTagalogRemainDistinctConservatively()
    {
        Assert.Equal("fil", Table.Resolve("Filipino")?.Code);
        Assert.Equal("tl", Table.Resolve("tgl")?.Code);
        Assert.Equal("tl", Table.Resolve("Tagalog")?.Code);
        Assert.Equal(MatchQuality.None, Table.Match(Table.Resolve("fil")!, Table.Resolve("tgl")!));
        Assert.Equal(MatchQuality.None, Table.Match(Table.Resolve("tgl")!, Table.Resolve("fil")!));
    }
}
