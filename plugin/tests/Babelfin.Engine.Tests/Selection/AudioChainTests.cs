using System;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>ordered audio chains of any length, with fallback.</summary>
public class AudioChainTests
{
    [Fact]
    public void ThreeStepChain_OnlyStepThreeMatches_PicksStepThree()
    {
        var tracks = new[] { Audio(1, "eng"), Audio(2, "jpn"), Audio(3, "fre") };
        var profile = Starter("en").Chain(Lang("de"), Lang("it"), Lang("fr"));

        var d = Decide(tracks, profile);

        Assert.Equal(3, d.AudioIndex);
        var matched = Assert.Single(d.Trace, l => l.Code == "audio.step.matched");
        Assert.Equal("3", matched.Args!["step"]);
        Assert.Equal(2, d.Trace.Count(l => l.Code == "audio.step.skipped"));
    }

    [Fact]
    public void StepOrder_ChangesTheResult()
    {
        var tracks = new[] { Audio(1, "eng"), Audio(2, "jpn") };

        Assert.Equal(2, Decide(tracks, Starter("en").Chain(Lang("ja"), Lang("en"))).AudioIndex);
        Assert.Equal(1, Decide(tracks, Starter("en").Chain(Lang("en"), Lang("ja"))).AudioIndex);
    }

    [Fact]
    public void EmptyChain_FallsBackByTieBreak()
    {
        var tracks = new[] { Audio(1, "eng", channels: 2), Audio(2, "jpn", channels: 6) };

        var d = Decide(tracks, Starter("en").Chain());

        Assert.Equal(2, d.AudioIndex);
        Assert.True(d.Has("audio.fallback"));
        Assert.False(d.Has("audio.step.matched"));
    }

    [Fact]
    public void TitleContains_VFQ_PicksQuebecTrackOverVFF()
    {
        var tracks = new[] { Audio(1, "fre", "VFF 5.1"), Audio(2, "fre", "VFQ 5.1") };

        var d = Decide(tracks, Starter("fr").Chain(Lang("fr", titleContains: "VFQ")));

        Assert.Equal(2, d.AudioIndex);
    }

    [Fact]
    public void TitleContains_IsCaseInsensitive()
    {
        var tracks = new[] { Audio(1, "fre", "VFF"), Audio(2, "fre", "vfq stereo") };

        Assert.Equal(2, Decide(tracks, Starter("fr").Chain(Lang("fr", titleContains: "VFQ"))).AudioIndex);
    }

    [Fact]
    public void TitleContains_NoTitleMatch_SkipsStepWithReason()
    {
        var tracks = new[] { Audio(1, "fre", "VFF"), Audio(2, "eng") };

        var d = Decide(tracks, Starter("en").Chain(Lang("fr", titleContains: "VFQ"), Lang("en")));

        Assert.Equal(2, d.AudioIndex);
        var skipped = Assert.Single(d.Trace, l => l.Code == "audio.step.skipped");
        Assert.Contains("VFQ", skipped.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MyLanguages_UsesUnderstoodOrder()
    {
        var tracks = new[] { Audio(1, "eng"), Audio(2, "ger") };
        var profile = Fx.Starter(new[] { "de", "en" }, new[] { "de", "en" });

        Assert.Equal(2, Decide(tracks, profile).AudioIndex);
    }

    [Fact]
    public void MyLanguages_SecondLanguageUsedWhenFirstMissing()
    {
        var tracks = new[] { Audio(1, "jpn", isDefault: true), Audio(2, "eng") };
        var profile = Fx.Starter(new[] { "de", "en" }, new[] { "de" });

        Assert.Equal(2, Decide(tracks, profile).AudioIndex);
    }

    [Fact]
    public void FirstTrackStep_PicksLowestIndex()
    {
        var tracks = new[] { Audio(3, "jpn", channels: 8), Audio(2, "eng", channels: 2) };

        Assert.Equal(2, Decide(tracks, Starter("de").Chain(Step(AudioStepKind.FirstTrack))).AudioIndex);
    }

    [Fact]
    public void NoStepMatches_FallbackPicksTieBreakWinner()
    {
        var tracks = new[] { Audio(1, "jpn", channels: 2), Audio(2, "kor", channels: 6) };

        var d = Decide(tracks, Starter("de").Chain(Lang("de"), Lang("fr")));

        Assert.Equal(2, d.AudioIndex);
        Assert.True(d.Has("audio.fallback"));
    }

    [Fact]
    public void UnknownLanguageInStep_IsIgnoredWithTrace()
    {
        var tracks = new[] { Audio(1, "eng"), Audio(2, "jpn") };

        var d = Decide(tracks, Starter("en").Chain(Lang("zzzz-notreal"), Lang("ja")));

        Assert.Equal(2, d.AudioIndex);
        Assert.True(d.Has("profile.language.unknown"));
    }

    [Fact]
    public void UnknownUnderstoodLanguage_IsIgnoredWithTrace()
    {
        var tracks = new[] { Audio(1, "jpn"), Audio(2, "eng") };
        var profile = Fx.Starter(new[] { "notalanguage", "en" }, new[] { "en" });

        var d = Decide(tracks, profile);

        Assert.Equal(2, d.AudioIndex);
        Assert.True(d.Has("profile.language.unknown"));
    }

    [Fact]
    public void NoAudioTracks_AudioIndexIsNull()
    {
        var d = Decide(new[] { Sub(1, "eng") }, Starter("en"));

        Assert.Null(d.AudioIndex);
        Assert.True(d.Has("audio.none"));
    }

    [Fact]
    public void SkippedSteps_SayWhy()
    {
        var tracks = new[] { Audio(1, "jpn") };

        var d = Decide(tracks, Starter("en").Chain(Lang("en"), Step(AudioStepKind.OriginalLanguage), Step(AudioStepKind.FileDefault), Step(AudioStepKind.FirstTrack)));

        Assert.Equal(1, d.AudioIndex);
        var skipped = d.Trace.Where(l => l.Code == "audio.step.skipped").Select(l => l.Text).ToArray();
        Assert.Equal(3, skipped.Length);
        Assert.Contains("no English audio track", skipped[0], StringComparison.Ordinal);
        Assert.Contains("original language is unknown", skipped[1], StringComparison.Ordinal);
        Assert.Contains("flagged default", skipped[2], StringComparison.Ordinal);
    }
}
