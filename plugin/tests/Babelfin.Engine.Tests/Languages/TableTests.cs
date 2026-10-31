using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Xunit;

namespace Babelfin.Engine.Tests.Languages;

public class TableTests
{
    private static readonly LanguageTable Table = LanguageTable.Default;

    public static IEnumerable<object[]> AllCodes() => LanguageTable.Default.Codes.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Every_code_round_trips_and_has_a_name(string code)
    {
        var tag = Table.Resolve(code);
        Assert.NotNull(tag);
        Assert.Equal(code, tag!.Code);
        Assert.False(string.IsNullOrWhiteSpace(Table.EnglishName(tag)));
    }

    [Fact]
    public void Codes_are_unique_and_plentiful()
    {
        var codes = Table.Codes;
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(codes.Count > 480, "expected the full ISO table, got " + codes.Count);
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("zh")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hant-HK")]
    [InlineData("cmn")]
    [InlineData("yue")]
    [InlineData("pt-BR")]
    [InlineData("pt-PT")]
    [InlineData("es-419")]
    [InlineData("es-ES")]
    [InlineData("fr-CA")]
    [InlineData("fr-FR")]
    [InlineData("sr-Latn")]
    [InlineData("sr-Cyrl")]
    [InlineData("uz-Latn")]
    [InlineData("uz-Cyrl")]
    [InlineData("pa-Arab")]
    [InlineData("pa-Guru")]
    [InlineData("fil")]
    [InlineData("cnr")]
    [InlineData("sco")]
    public void Required_codes_are_listed(string code)
    {
        Assert.Contains(code, Table.Codes);
    }

    [Fact]
    public void Undefined_codes_are_not_listed()
    {
        Assert.DoesNotContain("und", Table.Codes);
        Assert.DoesNotContain("mul", Table.Codes);
        Assert.DoesNotContain("zxx", Table.Codes);
        Assert.DoesNotContain("mis", Table.Codes);
    }

    [Theory]
    [InlineData("ja", "Japanese")]
    [InlineData("yue", "Chinese (Cantonese)")]
    [InlineData("cmn", "Chinese (Mandarin)")]
    [InlineData("pt-BR", "Portuguese (Brazil)")]
    [InlineData("es-419", "Spanish (Latin America)")]
    [InlineData("es", "Spanish")]
    [InlineData("el", "Greek")]
    [InlineData("zh-Hant-MO", "Chinese (Traditional, MO)")]
    [InlineData("fr-BE", "French (BE)")]
    [InlineData("yue-Hant", "Chinese (Cantonese, Hant)")]
    public void English_names(string code, string expected)
    {
        Assert.Equal(expected, Table.EnglishName(Table.Resolve(code)!));
    }

    [Fact]
    public void English_name_of_an_unknown_base_is_null()
    {
        Assert.Null(Table.EnglishName(new LanguageTag("qq", null, null, null)));
    }

    [Fact]
    public void Tag_code_puts_variety_in_place_of_base()
    {
        Assert.Equal("yue", new LanguageTag("zh", "yue", null, null).Code);
        Assert.Equal("cmn-Hans-SG", new LanguageTag("zh", "cmn", "Hans", "SG").Code);
        Assert.Equal("pt-BR", new LanguageTag("pt", null, null, "BR").Code);
        Assert.True(new LanguageTag("pt", null, null, null).IsGeneric);
        Assert.Equal("es-419", new LanguageTag("es", null, null, "419").ToString());
    }

    [Theory]
    [InlineData("chs", "zh-Hans")]
    [InlineData("cht", "zh-Hant")]
    [InlineData("sc", "zh-Hans")]
    [InlineData("tc", "zh-Hant")]
    [InlineData("pob", "pt-BR")]
    [InlineData("pb", "pt-BR")]
    [InlineData("spa-419", "es-419")]
    [InlineData("iw", "he")]
    [InlineData("in", "id")]
    [InlineData("ji", "yi")]
    [InlineData("jp", "ja")]
    [InlineData("ger", "de")]
    public void Default_alias_table_contains_the_required_spellings(string raw, string code)
    {
        Assert.True(AliasTable.Default.RawToCode.TryGetValue(raw, out var value));
        Assert.Equal(code, value);
    }

    [Fact]
    public void Default_alias_table_contains_english_names()
    {
        Assert.True(AliasTable.Default.RawToCode.ContainsKey("english"));
        Assert.True(AliasTable.Default.RawToCode.ContainsKey("castilian"));
        Assert.True(AliasTable.Default.RawToCode.ContainsKey("portuguese (brazil)"));
        Assert.False(AliasTable.Default.RawToCode.ContainsKey("latino"));
        Assert.Equal("la", Table.Resolve(AliasTable.Default.RawToCode["latin"])!.Code);
    }

    [Fact]
    public void Every_hand_written_alias_value_resolves()
    {
        foreach (var pair in AliasTable.Default.RawToCode)
        {
            if (pair.Value.Length == 0)
            {
                Assert.Null(Table.Resolve(pair.Key));
            }
            else
            {
                Assert.NotNull(Table.Resolve(pair.Value));
                Assert.NotNull(Table.Resolve(pair.Key));
            }
        }
    }

    [Fact]
    public void A_custom_table_is_data_driven()
    {
        var table = new LanguageTable(
            new[]
            {
                new LanguageEntry("xx", "xxa", null, "Examplish"),
                new LanguageEntry("xx", "xxa", null, "Examplish (North)", Region: "NO"),
            },
            new AliasTable(new Dictionary<string, string> { ["ex"] = "xx-NO", ["nothing"] = string.Empty }),
            new VarietyKeywordTable(Array.Empty<VarietyKeyword>()));

        Assert.Equal("xx", table.Resolve("xxa")!.Code);
        Assert.Equal("xx", table.Resolve("Examplish")!.Code);
        Assert.Equal("xx-NO", table.Resolve("EX")!.Code);
        Assert.Null(table.Resolve("nothing"));
        Assert.Null(table.Resolve("ja"));
        Assert.Equal(new[] { "xx", "xx-NO" }, table.Codes);
        Assert.Equal("Examplish (North)", table.EnglishName(table.Resolve("xx-no")!));
    }

    [Fact]
    public void Table_is_safe_to_use_from_many_threads()
    {
        var results = Enumerable.Range(0, 2000).AsParallel()
            .Select(i => Table.ResolveWithHints(i % 2 == 0 ? "chi" : "por", i % 2 == 0 ? "粵語" : "Brasil", null)!.Code)
            .Distinct()
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "pt-BR", "yue" }, results);
    }
}
