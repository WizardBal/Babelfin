using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>language codes that don't match today (chi/zho/yue, pt-BR/pt-PT, es-419/es-ES, fr-CA).</summary>
public class LanguageCodeSelectionTests
{
    [Theory]
    [InlineData("jpn")]
    [InlineData("ja")]
    [InlineData("Japanese")]
    public void JapaneseTrackCodes_AllCountAsUnderstood(string trackCode)
    {
        var d = Decide(new[] { Audio(1, trackCode) }, Starter("ja"));

        Assert.True(d.AudioUnderstood);
    }

    [Theory]
    [InlineData("jpn")]
    [InlineData("ja")]
    [InlineData("Japanese")]
    public void OriginalLanguageMetadata_AnyForm_PicksJapanese(string original)
    {
        var d = Decide(Slv(), OriginalAudio("en"), Item(original: original));

        Assert.Equal(2, d.AudioIndex);
    }

    [Theory]
    [InlineData("chi")]
    [InlineData("zho")]
    [InlineData("cmn")]
    [InlineData("yue")]
    [InlineData("zh-Hans")]
    public void WantedZh_MatchesAnyChinese(string trackCode)
    {
        var tracks = new[] { Audio(1, "eng"), Audio(2, trackCode) };

        Assert.Equal(2, Decide(tracks, Starter("en").Chain(Lang("zh"))).AudioIndex);
    }

    [Fact]
    public void WantedYue_NeverPicksMandarinTitledTrack()
    {
        var tracks = new[] { Audio(1, "chi", "Mandarin"), Audio(2, "eng") };

        var d = Decide(tracks, Starter("en").Chain(Lang("yue"), Lang("en")));

        Assert.Equal(2, d.AudioIndex);
        Assert.True(d.Has("audio.step.skipped"));
    }

    [Fact]
    public void WantedYue_ExactBeatsGenericChi()
    {
        var tracks = new[] { Audio(1, "chi", channels: 6), Audio(2, "chi", "Cantonese", channels: 2) };

        Assert.Equal(2, Decide(tracks, Starter("yue").Chain(Lang("yue"))).AudioIndex);
    }

    [Fact]
    public void WantedYue_PicksGenericChiWhenNoExact()
    {
        var tracks = new[] { Audio(1, "eng"), Audio(2, "chi") };

        var d = Decide(tracks, Starter("yue").Chain(Lang("yue"), Lang("en")));

        Assert.Equal(2, d.AudioIndex);
        Assert.True(d.AudioUnderstood);
    }

    // Cantonese speaker doesn't understand Mandarin.
    [Fact]
    public void CantoneseSpeaker_DoesNotUnderstandMandarin()
    {
        var tracks = new[] { Audio(1, "chi", "Mandarin"), Sub(2, "chi", "繁體") };
        var profile = Fx.Starter(new[] { "yue" }, new[] { "zh-Hant" });

        var d = Decide(tracks, profile);

        Assert.False(d.AudioUnderstood);
        Assert.Equal(2, d.SubtitleIndex);
    }

    [Fact]
    public void WantedZhHant_PicksTraditionalOverSimplified()
    {
        var tracks = new[] { Audio(1, "chi", "Mandarin"), Sub(2, "chi", "简体"), Sub(3, "chi", "繁體") };
        var profile = Fx.Starter(new[] { "en" }, new[] { "zh-Hant" });

        Assert.Equal(3, Decide(tracks, profile).SubtitleIndex);
    }

    [Fact]
    public void WantedZhHans_PicksSimplifiedOverTraditional()
    {
        var tracks = new[] { Audio(1, "chi", "Mandarin"), Sub(2, "chi", "繁體"), Sub(3, "chi", "简体") };
        var profile = Fx.Starter(new[] { "en" }, new[] { "zh-Hans" });

        Assert.Equal(3, Decide(tracks, profile).SubtitleIndex);
    }

    // an exact script beats a generic "chi" subtitle track.
    [Fact]
    public void WantedZhHant_ExactBeatsGenericChi()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "chi"), Sub(3, "chi", "繁中") };
        var profile = Fx.Starter(new[] { "en" }, new[] { "zh-Hant" });

        Assert.Equal(3, Decide(tracks, profile).SubtitleIndex);
    }

    [Fact]
    public void WantedPtBR_PicksBrasilOverPtPT()
    {
        var tracks = new[] { Audio(1, "pt-PT"), Audio(2, "por", "Português (Brasil)") };

        Assert.Equal(2, Decide(tracks, Starter("en").Chain(Lang("pt-BR"))).AudioIndex);
    }

    [Fact]
    public void WantedPtBR_GenericPorUsedAsGeneric_NeverPtPT()
    {
        var tracks = new[] { Audio(1, "pt-PT"), Audio(2, "por") };

        Assert.Equal(2, Decide(tracks, Starter("en").Chain(Lang("pt-BR"))).AudioIndex);
    }

    [Fact]
    public void WantedPtBR_OnlyPtPT_StepSkipped()
    {
        var tracks = new[] { Audio(1, "eng"), Audio(2, "pt-PT") };

        var d = Decide(tracks, Starter("en").Chain(Lang("pt-BR"), Lang("en")));

        Assert.Equal(1, d.AudioIndex);
    }

    [Theory]
    [InlineData("es-419", 2)]
    [InlineData("es-ES", 1)]
    public void Spanish_LatinoVersusCastellano(string wanted, int expected)
    {
        var tracks = new[] { Audio(1, "spa", "Castellano"), Audio(2, "spa", "Latino") };

        Assert.Equal(expected, Decide(tracks, Starter("en").Chain(Lang(wanted))).AudioIndex);
    }

    [Fact]
    public void WantedFrCA_PicksVFQ()
    {
        var tracks = new[] { Audio(1, "fre", "VFF"), Audio(2, "fre", "VFQ") };

        Assert.Equal(2, Decide(tracks, Starter("en").Chain(Lang("fr-CA"))).AudioIndex);
    }

    // a Brazilian subtitle request is matched separately from Portugal.
    [Fact]
    public void Subtitles_PtBR_PicksBrasilTrack()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "por", "Portugal"), Sub(3, "por", "Brasil"), Sub(4, "pt-PT") };
        var profile = Fx.Starter(new[] { "pt-BR" }, new[] { "pt-BR" });

        Assert.Equal(3, Decide(tracks, profile).SubtitleIndex);
    }
}
