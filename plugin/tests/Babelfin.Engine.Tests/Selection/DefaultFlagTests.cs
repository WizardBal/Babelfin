using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>the file's default flag is a step the person places, and a late tie-break.</summary>
public class DefaultFlagTests
{
    [Fact]
    public void FileDefaultStep_PicksDefaultTrack()
    {
        var tracks = new[] { Audio(1, "eng"), Audio(2, "jpn", isDefault: true) };

        Assert.Equal(2, Decide(tracks, Starter("en").Chain(Step(AudioStepKind.FileDefault))).AudioIndex);
    }

    // Jellyfin's "play default audio track" reproduced by a chain of [FileDefault] only.
    [Fact]
    public void PlayDefaultStyle_ChainOfFileDefaultOnly_IgnoresLanguage()
    {
        var d = Decide(StarlightSurvey(), Starter("en").Chain(Step(AudioStepKind.FileDefault)));

        Assert.Equal(1, d.AudioIndex);
        Assert.False(d.AudioUnderstood);
    }

    // my languages beat the file's default when placed first.
    [Fact]
    public void MyLanguagesFirst_BeatsFileDefault()
    {
        Assert.Equal(2, Decide(StarlightSurvey(), Starter("en")).AudioIndex);
    }

    // the default flag is only a tie-break among equals; more channels comes first.
    [Fact]
    public void DefaultFlag_IsOnlyTieBreakAmongEquals()
    {
        var tracks = new[] { Audio(1, "eng", isDefault: true, channels: 2), Audio(2, "eng", channels: 6) };

        Assert.Equal(2, Decide(tracks, Starter("en")).AudioIndex);
    }

    [Fact]
    public void DefaultFlag_DecidesBetweenOtherwiseEqualTracks()
    {
        var tracks = new[] { Audio(1, "eng", channels: 2), Audio(2, "eng", isDefault: true, channels: 2) };

        Assert.Equal(2, Decide(tracks, Starter("en")).AudioIndex);
    }

    [Fact]
    public void FileDefaultStep_NoDefault_SkippedWithTrace()
    {
        var tracks = new[] { Audio(1, "jpn"), Audio(2, "kor") };

        var d = Decide(tracks, Starter("en").Chain(Step(AudioStepKind.FileDefault), Step(AudioStepKind.FirstTrack)));

        Assert.Equal(1, d.AudioIndex);
        Assert.True(d.Has("audio.step.skipped"));
    }

    // two default-flagged tracks → tie-breaks decide between them.
    [Fact]
    public void FileDefaultStep_TwoDefaults_TieBreaksDecide()
    {
        var tracks = new[] { Audio(1, "eng", isDefault: true, channels: 2), Audio(2, "jpn", isDefault: true, channels: 6), Audio(3, "fre", channels: 8) };

        Assert.Equal(2, Decide(tracks, Starter("de").Chain(Step(AudioStepKind.FileDefault))).AudioIndex);
    }
}
