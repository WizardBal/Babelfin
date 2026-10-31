using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>"Video default" (a track left to the video is not chosen by Babelfin; Jellyfin's own choice stands.</summary>
public class VideoDefaultTests
{
    private static ResolvedProfile Neutral(string understood = "en") => Starter(understood) with
    {
        AudioChain = new[] { new AudioStep(AudioStepKind.VideoDefault) },
        WhenAudioUnderstood = SubtitleWant.VideoDefault,
        WhenAudioNotUnderstood = SubtitleWant.VideoDefault,
    };

    private static ItemFacts Jellyfin(int? audio, int? subtitle) => new(null, null, Array.Empty<string>(), false, audio, subtitle);

    [Fact]
    public void BothLeft_ReportsJellyfinsTracks_AndBothFlags()
    {
        var d = Decide(Slv(), Neutral(), Jellyfin(2, 4));

        Assert.True(d.AudioLeftToVideo);
        Assert.True(d.SubtitlesLeftToVideo);
        Assert.Equal(2, d.AudioIndex);
        Assert.Equal(4, d.SubtitleIndex);
        Assert.Equal("Audio: Japanese (#2): the video's default. Subtitles: English full (#4): the video's default.", d.Explain());
    }

    [Fact]
    public void JellyfinUnknown_TheFlaggedDefaultIsReported()
    {
        var d = Decide(Slv(), Neutral(), ItemFacts.None);

        Assert.Equal(1, d.AudioIndex);
        Assert.Null(d.SubtitleIndex);
        Assert.True(d.SubtitlesLeftToVideo);
    }

    [Fact]
    public void NoFlaggedDefault_TheFirstTrackIsReported()
    {
        var tracks = new[] { Audio(3, "jpn"), Audio(1, "eng"), Sub(4, "eng") };

        var d = Decide(tracks, Neutral(), ItemFacts.None);

        Assert.Equal(1, d.AudioIndex);
        Assert.True(d.AudioLeftToVideo);
    }

    [Fact]
    public void NoAudioAtAll_IsStillLeftToTheVideo()
    {
        var d = Decide(new[] { Sub(1, "eng") }, Neutral(), ItemFacts.None);

        Assert.Null(d.AudioIndex);
        Assert.True(d.AudioLeftToVideo);
        Assert.True(d.Has("audio.none"));
    }

    // A Jellyfin index that names no audio track (a stale or bad value) falls back to the file's flags.
    [Fact]
    public void JellyfinIndexNotInTheFile_FallsBackToTheFlag()
    {
        var d = Decide(Slv(), Neutral(), Jellyfin(99, 77));

        Assert.Equal(1, d.AudioIndex);
        Assert.Null(d.SubtitleIndex);
    }

    // A chosen language first, then the video's default: the language wins when present, the video otherwise; no other guesses.
    [Theory]
    [InlineData("jpn", 2, false)]
    [InlineData("fre", 1, true)]
    public void LanguageThenVideoDefault(string language, int expected, bool left)
    {
        var profile = Neutral() with { AudioChain = new[] { new AudioStep(AudioStepKind.Language, language), new AudioStep(AudioStepKind.VideoDefault) } };

        var d = Decide(Slv(), profile, Jellyfin(1, -1));

        Assert.Equal(expected, d.AudioIndex);
        Assert.Equal(left, d.AudioLeftToVideo);
    }

    // Steps after "video default" are never reached.
    [Fact]
    public void VideoDefault_EndsTheChain()
    {
        var profile = Neutral() with { AudioChain = new[] { new AudioStep(AudioStepKind.VideoDefault), new AudioStep(AudioStepKind.Language, "jpn") } };

        var d = Decide(Slv(), profile, Jellyfin(1, -1));

        Assert.Equal(1, d.AudioIndex);
        Assert.True(d.AudioLeftToVideo);
    }

    // A remembered track change (the person's own action) still beats "video default".
    [Fact]
    public void RememberedChange_BeatsVideoDefault()
    {
        var manual = new ManualOverride(TrackDescriptor.From(Classify(Slv()[1])), false, null);

        var d = Decide(Slv(), Neutral(), Jellyfin(1, -1), manual: manual);

        Assert.Equal(2, d.AudioIndex);
        Assert.False(d.AudioLeftToVideo);
        Assert.True(d.SubtitlesLeftToVideo);
    }

    // Turning subtitles off by hand earlier keeps them off, even when they are otherwise left to the video.
    [Fact]
    public void RememberedOff_BeatsVideoDefault()
    {
        var d = Decide(Slv(), Neutral(), Jellyfin(1, 4), manual: new ManualOverride(null, true, null));

        Assert.Null(d.SubtitleIndex);
        Assert.False(d.SubtitlesLeftToVideo);
        Assert.True(d.Has("subs.manual.off"));
    }

    // Preferring SDH is an explicit accessibility choice: it turns full subtitles on even when they would be left to the video.
    [Fact]
    public void PrefersSdh_StillAppliesWhenTheAudioIsUnderstood()
    {
        var profile = Neutral() with { PrefersSdh = true };

        var d = Decide(Slv(), profile, Jellyfin(1, -1));

        Assert.False(d.SubtitlesLeftToVideo);
        Assert.NotNull(d.SubtitleIndex);
    }

    // Audio left to the video, subtitles chosen per understanding: the understood check uses what Jellyfin plays.
    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    public void SmartSubtitles_FollowTheVideosAudio(int jellyfinAudio, int expectedSubtitle)
    {
        var profile = Neutral() with { WhenAudioUnderstood = SubtitleWant.PartialOnly, WhenAudioNotUnderstood = SubtitleWant.Full };

        var d = Decide(Slv(), profile, Jellyfin(jellyfinAudio, -1));

        Assert.True(d.AudioLeftToVideo);
        Assert.False(d.SubtitlesLeftToVideo);
        Assert.Equal(expectedSubtitle, d.SubtitleIndex);
    }

    [Fact]
    public void NeverThrows_OnOddTracks()
    {
        var tracks = new List<Track>
        {
            Audio(5, null),
            Audio(6, "und"),
            Sub(7, null),
            Sub(8, "xx-bogus"),
        };

        var d = Decide(tracks.ToArray(), Neutral(), Jellyfin(null, null));

        Assert.True(d.AudioLeftToVideo);
        Assert.True(d.SubtitlesLeftToVideo);
    }
}
