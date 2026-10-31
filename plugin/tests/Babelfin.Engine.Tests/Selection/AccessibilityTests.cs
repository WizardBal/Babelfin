using System;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>commentary, audio description and SDH.</summary>
public class AccessibilityTests
{
    [Fact]
    public void CommentaryFlaggedDefault_IsSkipped()
    {
        var tracks = new[] { Audio(1, "eng", "Director's Commentary", isDefault: true), Audio(2, "eng") };

        Assert.Equal(2, Decide(tracks, Starter("en")).AudioIndex);
    }

    [Fact]
    public void CommentaryFlaggedDefault_SkippedEvenByFileDefaultStep()
    {
        var tracks = new[] { Audio(1, "eng", "Commentary", isDefault: true), Audio(2, "eng") };

        var d = Decide(tracks, Starter("en").Chain(Step(AudioStepKind.FileDefault)));

        Assert.Equal(2, d.AudioIndex);
        Assert.True(d.Has("audio.fallback"));
    }

    [Fact]
    public void Commentary_ChosenOnlyWhenItIsTheOnlyAudio()
    {
        var d = Decide(new[] { Audio(1, "eng", "Commentary") }, Starter("en"));

        Assert.Equal(1, d.AudioIndex);
        Assert.True(d.Has("audio.fallback"));
        Assert.Contains("commentary", d.Trace.First(l => l.Code == "audio.step.skipped").Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AvoidCommentaryOff_CommentaryCanWin()
    {
        var tracks = new[] { Audio(1, "eng", "Commentary", isDefault: true), Audio(2, "eng") };

        var d = Decide(tracks, Starter("en").Chain(Step(AudioStepKind.FileDefault)) with { AvoidCommentary = false });

        Assert.Equal(1, d.AudioIndex);
    }

    [Fact]
    public void AudioDescription_SkippedByDefault()
    {
        var tracks = new[] { Audio(1, "eng", "Audio Description", isDefault: true, channels: 6), Audio(2, "eng", channels: 2) };

        Assert.Equal(2, Decide(tracks, Starter("en")).AudioIndex);
    }

    [Fact]
    public void AudioDescription_PreferredWhenPersonUsesIt()
    {
        var tracks = new[] { Audio(1, "eng", channels: 6), Audio(2, "eng", "Descriptive Audio", channels: 2) };

        var d = Decide(tracks, Starter("en") with { UsesAudioDescription = true });

        Assert.Equal(2, d.AudioIndex);
    }

    // AD preference stays within the language; an AD track in another language doesn't jump the chain.
    [Fact]
    public void AudioDescription_PreferredOnlyWithinLanguage()
    {
        var tracks = new[] { Audio(1, "eng"), Audio(2, "ger", "Audiodeskription") };

        var d = Decide(tracks, Starter("en") with { UsesAudioDescription = true });

        Assert.Equal(1, d.AudioIndex);
    }

    [Fact]
    public void PrefersSdh_ShowsSubtitlesEvenWhenAudioUnderstood()
    {
        var tracks = new[] { Audio(1, "eng"), Sub(2, "eng"), Sub(3, "eng", hi: true) };

        var d = Decide(tracks, Starter("en") with { PrefersSdh = true });

        Assert.True(d.AudioUnderstood);
        Assert.True(d.SubtitlesWanted);
        Assert.Equal(3, d.SubtitleIndex);
    }

    [Fact]
    public void PrefersSdh_UpgradesOffToFull()
    {
        var tracks = new[] { Audio(1, "eng"), Sub(2, "eng") };

        var d = Decide(tracks, Starter("en") with { PrefersSdh = true, WhenAudioUnderstood = SubtitleWant.Off });

        Assert.Equal(2, d.SubtitleIndex);
    }

    [Fact]
    public void PrefersSdh_ForcesSdhPreferenceOverAvoid()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "eng"), Sub(3, "eng", "English SDH") };

        var d = Decide(tracks, Starter("en") with { PrefersSdh = true, Sdh = Preference.Avoid });

        Assert.Equal(3, d.SubtitleIndex);
    }

    // without the accessibility choice, understood audio keeps partial-only.
    [Fact]
    public void WithoutPrefersSdh_UnderstoodAudio_NoFullSubs()
    {
        var tracks = new[] { Audio(1, "eng"), Sub(2, "eng"), Sub(3, "eng", hi: true) };

        var d = Decide(tracks, Starter("en"));

        Assert.Null(d.SubtitleIndex);
    }
}
