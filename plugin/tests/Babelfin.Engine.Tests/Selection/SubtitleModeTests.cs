using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>subtitles turning on unexpectedly; explicit "understood" / "not understood" modes.</summary>
public class SubtitleModeTests
{
    [Fact]
    public void UnderstoodAudio_WhenUnderstoodOff_NoSubtitles()
    {
        var d = Decide(AmberCourier(), Starter("en") with { WhenAudioUnderstood = SubtitleWant.Off });

        Assert.Null(d.SubtitleIndex);
        Assert.False(d.SubtitlesWanted);
        Assert.True(d.Has("subs.off"));
    }

    [Fact]
    public void UnderstoodAudio_PartialOnly_PicksForcedSignsTrack()
    {
        var d = Decide(AmberCourier(), Starter("en"));

        Assert.Equal(3, d.SubtitleIndex);
    }

    [Fact]
    public void UnknownAudioLanguage_CountsAsNotUnderstood_FullSubs()
    {
        var d = Decide(GlassRelay(), Starter("en"));

        Assert.False(d.AudioUnderstood);
        Assert.True(d.Has("audio.unknown"));
        Assert.Equal(3, d.SubtitleIndex);
    }

    [Fact]
    public void UnknownAudioLanguage_FlagFlips_PartialOnly()
    {
        var d = Decide(GlassRelay(), Starter("en") with { UnknownAudioCountsAsUnderstood = true });

        Assert.True(d.AudioUnderstood);
        Assert.Equal(2, d.SubtitleIndex);
    }

    [Fact]
    public void NotUnderstood_WhenNotUnderstoodOff_NoSubtitles()
    {
        var d = Decide(ClockworkOrchard(), OriginalAudio("en") with { WhenAudioNotUnderstood = SubtitleWant.Off }, Item(original: "ja"));

        Assert.Equal(2, d.AudioIndex);
        Assert.Null(d.SubtitleIndex);
        Assert.False(d.SubtitlesWanted);
    }

    [Fact]
    public void Understood_WhenUnderstoodFull_ShowsFull()
    {
        var d = Decide(Slv(), Starter("en") with { WhenAudioUnderstood = SubtitleWant.Full });

        Assert.Equal(1, d.AudioIndex);
        Assert.Equal(4, d.SubtitleIndex);
    }

    // "subs.want" says which mode applied.
    [Fact]
    public void SubsWant_TraceSaysWhichModeApplied()
    {
        var d = Decide(Slv(), Starter("en"));

        var want = Assert.Single(d.Trace, l => l.Code == "subs.want");
        Assert.Equal("partial", want.Args!["want"]);
        Assert.Equal("true", want.Args["understood"]);
    }

    // the Explain sentence for "off" names the understood language.
    [Fact]
    public void Explain_Off_NamesUnderstoodLanguage()
    {
        var d = Decide(AmberCourier(), Starter("en") with { WhenAudioUnderstood = SubtitleWant.Off });

        Assert.Contains("Subtitles: off (English is in My languages).", d.Explain(), System.StringComparison.Ordinal);
    }
}
