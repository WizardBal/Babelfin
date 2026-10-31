using System;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>forced / Signs &amp; Songs versus full subtitles; SDH ordering.</summary>
public class SignsVsFullTests
{
    private static readonly ResolvedProfile JapaneseAudio = OriginalAudio("en");

    private static readonly ItemFacts Japanese = Item(original: "ja");

    [Fact]
    public void ClockworkOrchard_SignsDefaultNotForced_FullChosen()
    {
        var d = Decide(ClockworkOrchard(), JapaneseAudio, Japanese);

        Assert.Equal(2, d.AudioIndex);
        Assert.Equal(4, d.SubtitleIndex);
    }

    [Fact]
    public void ClockworkOrchard_Explain_SaysSignsTrackSkippedBecausePartial()
    {
        var d = Decide(ClockworkOrchard(), JapaneseAudio, Japanese);

        Assert.Contains("Signs/Songs [PebbleForge]", d.Explain(), StringComparison.Ordinal);
        Assert.Contains("(#3) was skipped because it's partial", d.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void MoonlitHarbor_SignsAndSongsDefault_DialogueChosen()
    {
        var d = Decide(MoonlitHarbor(), JapaneseAudio, Japanese);

        Assert.Equal(4, d.SubtitleIndex);
        Assert.True(d.Has("subs.skipped.partial", 3));
    }

    [Fact]
    public void MoonlitHarbor_EnglishAudio_PartialOnly_PicksSigns()
    {
        var d = Decide(MoonlitHarbor(), Starter("en"), Japanese);

        Assert.Equal(1, d.AudioIndex);
        Assert.Equal(3, d.SubtitleIndex);
    }

    [Fact]
    public void StarlightSurvey_FullPgsChosenOverSignsPgs()
    {
        var d = Decide(StarlightSurvey(), JapaneseAudio, Japanese);

        Assert.Equal(3, d.SubtitleIndex);
    }

    [Fact]
    public void StarlightSurvey_EnglishDub_PartialOnly_PicksSignsPgs()
    {
        var d = Decide(StarlightSurvey(), Starter("en"), Japanese);

        Assert.Equal(2, d.AudioIndex);
        Assert.Equal(4, d.SubtitleIndex);
    }

    // The Glass Relay, full want, SDH neutral → the plain full track.
    [Fact]
    public void GlassRelay_FullWant_SdhNeutral_PicksFull()
    {
        var d = Decide(GlassRelay(), Starter("en"));

        Assert.False(d.AudioUnderstood);
        Assert.Equal(3, d.SubtitleIndex);
    }

    [Fact]
    public void GlassRelay_FullWant_SdhPrefer_PicksSdh()
    {
        var d = Decide(GlassRelay(), Starter("en") with { Sdh = Preference.Prefer });

        Assert.Equal(4, d.SubtitleIndex);
    }

    [Fact]
    public void GlassRelay_FullWant_SdhAvoid_FullExists_SkipsSdh()
    {
        var d = Decide(GlassRelay(), Starter("en") with { Sdh = Preference.Avoid, Embedded = EmbeddedPreference.Neutral });

        Assert.Equal(3, d.SubtitleIndex);
    }

    [Fact]
    public void GlassRelay_SdhAvoid_OnlySdhPresent_SdhStillChosen()
    {
        var tracks = GlassRelay().Where(t => t.Index != 3).ToArray();

        var d = Decide(tracks, Starter("en") with { Sdh = Preference.Avoid });

        Assert.Equal(4, d.SubtitleIndex);
    }

    [Fact]
    public void GlassRelay_PartialOnly_PicksForcedTrack()
    {
        var d = Decide(GlassRelay(), Starter("en") with { WhenAudioNotUnderstood = SubtitleWant.PartialOnly });

        Assert.Equal(2, d.SubtitleIndex);
        Assert.True(d.Has("subs.skipped.full", 3));
    }

    // the default flag on a partial track never beats a full track.
    [Fact]
    public void DefaultFlagOnPartial_NeverBeatsFull()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "eng", "Signs", isDefault: true), Sub(3, "eng", "Forced", isDefault: true, isForced: true), Sub(9, "eng") };

        var d = Decide(tracks, Starter("en"));

        Assert.Equal(9, d.SubtitleIndex);
    }

    // the default flag still breaks ties between two full tracks.
    [Fact]
    public void DefaultFlag_BreaksTieBetweenFullTracks()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "eng"), Sub(3, "eng", isDefault: true) };

        Assert.Equal(3, Decide(tracks, Starter("en")).SubtitleIndex);
    }

    // the classifier and the selector agree on kinds for the representative fixtures.
    [Fact]
    public void RealFixtures_AreClassifiedAsExpected()
    {
        var d = Decide(ClockworkOrchard(), JapaneseAudio, Japanese);

        Assert.Equal(SubtitleKind.Partial, d.Tracks.Single(t => t.Track.Index == 3).SubtitleKind);
        Assert.Equal(SubtitleKind.Full, d.Tracks.Single(t => t.Track.Index == 4).SubtitleKind);
    }

    // a partial-only want picks the forced default among several partials by the default flag.
    [Fact]
    public void PartialOnly_DefaultFlagBreaksTieAmongPartials()
    {
        var tracks = new[] { Audio(1, "eng"), Sub(2, "eng", "Signs"), Sub(3, "eng", "Songs", isDefault: true), Sub(4, "eng") };

        Assert.Equal(3, Decide(tracks, Starter("en")).SubtitleIndex);
    }
}
