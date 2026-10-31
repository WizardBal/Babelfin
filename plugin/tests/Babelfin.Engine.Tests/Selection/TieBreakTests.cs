using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>same-language tie-breaks (ordered, editable).</summary>
public class TieBreakTests
{
    [Fact]
    public void MoreChannels_Wins()
    {
        var tracks = new[] { Audio(1, "eng", channels: 2), Audio(2, "eng", channels: 8) };

        Assert.Equal(2, Decide(tracks, Starter("en")).AudioIndex);
    }

    [Fact]
    public void Lossless_TrueHdBeatsAc3()
    {
        var tracks = new[] { Audio(1, "eng", codec: "ac3", channels: 6), Audio(2, "eng", codec: "truehd", channels: 6) };

        Assert.Equal(2, Decide(tracks, Starter("en")).AudioIndex);
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("alac")]
    [InlineData("mlp")]
    [InlineData("pcm_s24le")]
    [InlineData("TrueHD")]
    public void Lossless_CodecsRecognised(string codec)
    {
        var tracks = new[] { Audio(1, "eng", codec: "aac", channels: 2), Audio(2, "eng", codec: codec, channels: 2) };

        Assert.Equal(2, Decide(tracks, Starter("en")).AudioIndex);
    }

    [Fact]
    public void Lossless_FlagCounts()
    {
        var tracks = new[] { Audio(1, "eng", codec: "dts", channels: 6), Audio(2, "eng", codec: "dts", channels: 6, isLossless: true) };

        Assert.Equal(2, Decide(tracks, Starter("en")).AudioIndex);
    }

    [Fact]
    public void IsOriginalFlag_BreaksTie()
    {
        var tracks = new[] { Audio(1, "eng", channels: 2), Audio(2, "eng", channels: 2, isOriginal: true) };

        Assert.Equal(2, Decide(tracks, Starter("en")).AudioIndex);
    }

    [Fact]
    public void LowestIndex_IsTheFinalTieBreak()
    {
        var tracks = new[] { Audio(5, "eng"), Audio(3, "eng") };

        Assert.Equal(3, Decide(tracks, Starter("en")).AudioIndex);
    }

    [Fact]
    public void CustomOrder_DefaultBeforeChannels_Respected()
    {
        var tracks = new[] { Audio(1, "eng", channels: 8), Audio(2, "eng", channels: 2, isDefault: true) };
        var profile = Starter("en") with { TieBreaks = new[] { TieBreak.DefaultFlag, TieBreak.MoreChannels } };

        Assert.Equal(1, Decide(tracks, Starter("en")).AudioIndex);
        Assert.Equal(2, Decide(tracks, profile).AudioIndex);
    }

    [Fact]
    public void CustomOrder_LowestIndexOnly_IgnoresChannels()
    {
        var tracks = new[] { Audio(1, "eng", channels: 2), Audio(2, "eng", channels: 8, codec: "truehd") };

        Assert.Equal(1, Decide(tracks, Starter("en") with { TieBreaks = new[] { TieBreak.LowestIndex } }).AudioIndex);
    }

    [Fact]
    public void CustomOrder_LosslessBeforeChannels()
    {
        var tracks = new[] { Audio(1, "eng", codec: "eac3", channels: 8), Audio(2, "eng", codec: "flac", channels: 2) };

        Assert.Equal(1, Decide(tracks, Starter("en")).AudioIndex);
        Assert.Equal(2, Decide(tracks, Starter("en") with { TieBreaks = new[] { TieBreak.Lossless, TieBreak.MoreChannels } }).AudioIndex);
    }

    [Fact]
    public void EmptyTieBreakList_StillDeterministicByIndex()
    {
        var tracks = new[] { Audio(4, "eng", channels: 8), Audio(2, "eng", channels: 2) };

        Assert.Equal(2, Decide(tracks, Starter("en") with { TieBreaks = System.Array.Empty<TieBreak>() }).AudioIndex);
    }
}
