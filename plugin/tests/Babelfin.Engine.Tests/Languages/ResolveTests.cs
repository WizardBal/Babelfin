using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Xunit;

namespace Babelfin.Engine.Tests.Languages;

public class ResolveTests
{
    private static readonly LanguageTable Table = LanguageTable.Default;

    private static string? Code(string? raw) => Table.Resolve(raw)?.Code;

    [Theory]
    [InlineData("ja")]
    [InlineData("jpn")]
    [InlineData("Japanese")]
    [InlineData("japanese")]
    [InlineData("JA")]
    [InlineData(" jpn ")]
    [InlineData("jp")]
    [InlineData("jap")]
    public void Japanese_spellings_resolve_to_ja(string raw)
    {
        Assert.Equal("ja", Code(raw));
    }

    [Fact]
    public void Ja_JP_keeps_its_region()
    {
        var tag = Table.Resolve("JA-jp");
        Assert.NotNull(tag);
        Assert.Equal("ja", tag!.Base);
        Assert.Equal("JP", tag.Region);
        Assert.Equal("ja-JP", tag.Code);
    }

    [Theory]
    [InlineData("chi", "zh")]
    [InlineData("zho", "zh")]
    [InlineData("zh", "zh")]
    [InlineData("Chinese", "zh")]
    [InlineData("cn", "zh")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh_hans", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zho-TW", "zh-Hant")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant-HK")]
    [InlineData("zh-Hant-HK", "zh-Hant-HK")]
    [InlineData("chs", "zh-Hans")]
    [InlineData("cht", "zh-Hant")]
    [InlineData("sc", "zh-Hans")]
    [InlineData("tc", "zh-Hant")]
    [InlineData("zhs", "zh-Hans")]
    [InlineData("zht", "zh-Hant")]
    [InlineData("tw", "zh-Hant")]
    [InlineData("hk", "zh-Hant")]
    [InlineData("Chinese (Simplified)", "zh-Hans")]
    public void Chinese_spellings(string raw, string expected)
    {
        Assert.Equal(expected, Code(raw));
    }

    [Fact]
    public void Zh_HK_is_traditional_chinese_with_region_not_cantonese()
    {
        var tag = Table.Resolve("zh-hk");
        Assert.Equal(new LanguageTag("zh", null, "Hant", "HK"), tag);
    }

    [Theory]
    [InlineData("cmn", "cmn")]
    [InlineData("yue", "yue")]
    [InlineData("nan", "nan")]
    [InlineData("hak", "hak")]
    [InlineData("wuu", "wuu")]
    [InlineData("gan", "gan")]
    [InlineData("zh-yue", "yue")]
    [InlineData("Mandarin", "cmn")]
    [InlineData("Cantonese", "yue")]
    [InlineData("cmn-Hans", "cmn-Hans")]
    [InlineData("yue-HK", "yue-HK")]
    public void Chinese_varieties(string raw, string expected)
    {
        Assert.Equal(expected, Code(raw));
    }

    [Fact]
    public void Varieties_keep_zh_as_base()
    {
        var yue = Table.Resolve("yue")!;
        Assert.Equal("zh", yue.Base);
        Assert.Equal("yue", yue.Variety);
        Assert.False(yue.IsGeneric);
    }

    [Theory]
    [InlineData("pt", "pt")]
    [InlineData("por", "pt")]
    [InlineData("Portuguese", "pt")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt_br", "pt-BR")]
    [InlineData("por-BR", "pt-BR")]
    [InlineData("pob", "pt-BR")]
    [InlineData("pb", "pt-BR")]
    [InlineData("ptb", "pt-BR")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("ptp", "pt-PT")]
    [InlineData("Portuguese (Brazil)", "pt-BR")]
    [InlineData("Brazilian Portuguese", "pt-BR")]
    public void Portuguese_spellings(string raw, string expected)
    {
        Assert.Equal(expected, Code(raw));
    }

    [Theory]
    [InlineData("es", "es")]
    [InlineData("spa", "es")]
    [InlineData("Spanish", "es")]
    [InlineData("Castilian", "es")]
    [InlineData("Spanish; Castilian", "es")]
    [InlineData("es-419", "es-419")]
    [InlineData("spa-419", "es-419")]
    [InlineData("es_419", "es-419")]
    [InlineData("es-ES", "es-ES")]
    [InlineData("es-MX", "es-419")]
    [InlineData("spa-MX", "es-419")]
    [InlineData("es-LA", "es-419")]
    public void Spanish_spellings(string raw, string expected)
    {
        Assert.Equal(expected, Code(raw));
    }

    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("fre", "fr")]
    [InlineData("fra", "fr")]
    [InlineData("fr-CA", "fr-CA")]
    [InlineData("fr-FR", "fr-FR")]
    [InlineData("frc", "fr-CA")]
    [InlineData("French (Canada)", "fr-CA")]
    public void French_spellings(string raw, string expected)
    {
        Assert.Equal(expected, Code(raw));
    }

    [Theory]
    [InlineData("sr-Latn", "sr-Latn")]
    [InlineData("sr-latn", "sr-Latn")]
    [InlineData("srp-Cyrl", "sr-Cyrl")]
    [InlineData("uz-Latn", "uz-Latn")]
    [InlineData("pa-Guru", "pa-Guru")]
    [InlineData("sr", "sr")]
    public void Scripts(string raw, string expected)
    {
        Assert.Equal(expected, Code(raw));
    }

    [Theory]
    [InlineData("iw", "he")]
    [InlineData("in", "id")]
    [InlineData("ji", "yi")]
    [InlineData("jw", "jv")]
    [InlineData("mo", "ro")]
    [InlineData("scc", "sr")]
    [InlineData("scr", "hr")]
    public void Deprecated_codes(string raw, string expected)
    {
        Assert.Equal(expected, Code(raw));
    }

    [Theory]
    [InlineData("gre", "el")]
    [InlineData("ell", "el")]
    [InlineData("Greek", "el")]
    [InlineData("ger", "de")]
    [InlineData("deu", "de")]
    [InlineData("German", "de")]
    [InlineData("dut", "nl")]
    [InlineData("Flemish", "nl")]
    [InlineData("cze", "cs")]
    [InlineData("per", "fa")]
    [InlineData("Persian", "fa")]
    public void Bibliographic_codes_and_names(string raw, string expected)
    {
        Assert.Equal(expected, Code(raw));
    }

    [Theory]
    [InlineData("English", "en")]
    [InlineData("ENGLISH", "en")]
    [InlineData("eng", "en")]
    [InlineData("en-US", "en-US")]
    [InlineData("en-GB", "en-GB")]
    [InlineData("Korean", "ko")]
    [InlineData("Hindi", "hi")]
    [InlineData("Filipino", "fil")]
    [InlineData("Scots", "sco")]
    [InlineData("Montenegrin", "cnr")]
    [InlineData("enm", "enm")]
    [InlineData("Latin", "la")]
    public void Omdb_style_names_and_rare_codes(string raw, string expected)
    {
        Assert.Equal(expected, Code(raw));
    }

    [Theory]
    [InlineData("und")]
    [InlineData("UND")]
    [InlineData("mul")]
    [InlineData("zxx")]
    [InlineData("mis")]
    [InlineData("unknown")]
    [InlineData("none")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Undefined_values_resolve_to_null(string? raw)
    {
        Assert.Null(Table.Resolve(raw));
    }

    [Theory]
    [InlineData("klingonese")]
    [InlineData("xx")]
    [InlineData("zz-ZZ")]
    [InlineData("qwerty")]
    [InlineData("12")]
    [InlineData("--")]
    public void Garbage_resolves_to_null(string raw)
    {
        Assert.Null(Table.Resolve(raw));
    }

    [Fact]
    public void Aliases_shadow_two_letter_codes_whose_language_keeps_its_three_letter_code()
    {
        // "sc" and "tw" are Simplified and Traditional Chinese on tracks; Sardinian and Twi stay reachable.
        Assert.Equal("srd", Code("srd"));
        Assert.Equal("srd", Code("Sardinian"));
        Assert.Equal("twi", Code("twi"));
    }

    [Fact]
    public void Unknown_variant_subtags_are_ignored()
    {
        Assert.Equal("de", Code("de-1996"));
        Assert.Equal("en", Code("en-x-custom"));
    }
}
