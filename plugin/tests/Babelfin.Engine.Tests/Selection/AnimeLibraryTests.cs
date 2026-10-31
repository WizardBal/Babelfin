using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>per-library rules and anime (Japanese audio, English subtitles) next to dubbed shows.</summary>
public class AnimeLibraryTests
{
    // the everyday starter on the SLV fixture keeps the English dub.
    [Fact]
    public void Slv_Starter_PicksEnglishAudio_Understood()
    {
        var d = Decide(Slv(), Starter("en"));

        Assert.Equal(1, d.AudioIndex);
        Assert.True(d.AudioUnderstood);
    }

    // understood → partial only → the forced track.
    [Fact]
    public void Slv_Starter_PartialOnly_PicksForcedTrack()
    {
        var d = Decide(Slv(), Starter("en"));

        Assert.Equal(3, d.SubtitleIndex);
        Assert.True(d.SubtitlesWanted);
    }

    // understood → off → no subtitles.
    [Fact]
    public void Slv_Starter_UnderstoodOff_NoSubtitles()
    {
        var d = Decide(Slv(), Starter("en") with { WhenAudioUnderstood = SubtitleWant.Off });

        Assert.Equal(1, d.AudioIndex);
        Assert.Null(d.SubtitleIndex);
        Assert.False(d.SubtitlesWanted);
    }

    // the original-audio library rule picks Japanese and the first full English track.
    [Fact]
    public void Slv_OriginalAudioChain_PicksJapaneseAndFullSubs()
    {
        var d = Decide(Slv(), OriginalAudio("en"), Item(original: "ja"));

        Assert.Equal(2, d.AudioIndex);
        Assert.False(d.AudioUnderstood);
        Assert.Equal(4, d.SubtitleIndex);
    }

    // Explicit partial content does not satisfy a full-dialogue request.
    [Fact]
    public void Slv_OriginalAudioChain_SkipsExplicitPartialDefault()
    {
        var tracks = Slv();
        tracks[2] = tracks[2] with { Title = "Signs & Songs" };
        var d = Decide(tracks, OriginalAudio("en"), Item(original: "ja"));

        Assert.NotEqual(3, d.SubtitleIndex);
        Assert.True(d.Has("subs.skipped.partial", 3));
    }

    // original unknown, library usual covers it.
    [Fact]
    public void Slv_OriginalUnknown_LibraryUsualJapanese_PicksJapanese()
    {
        var d = Decide(Slv(), OriginalAudio("en"), Item(original: null, library: "ja"));

        Assert.Equal(2, d.AudioIndex);
        var matched = Assert.Single(d.Trace, l => l.Code == "audio.step.matched");
        Assert.Equal("2", matched.Args!["step"]);
    }

    // nothing known → the file default.
    [Fact]
    public void Slv_OriginalAndLibraryUnknown_FallsToFileDefault()
    {
        var d = Decide(Slv(), OriginalAudio("en"), Item());

        Assert.Equal(1, d.AudioIndex);
        var matched = Assert.Single(d.Trace, l => l.Code == "audio.step.matched");
        Assert.Equal("3", matched.Args!["step"]);
    }

    // the same person gets different results in two libraries.
    [Fact]
    public void SamePerson_TwoLibraries_DifferentAudio()
    {
        var everyday = Decide(Slv(), Starter("en"), Item(original: "ja"));
        var anime = Decide(Slv(), OriginalAudio("en") with { FieldSources = new Dictionary<string, string> { ["AudioChain"] = "your Anime library rule" } }, Item(original: "ja"));

        Assert.Equal(1, everyday.AudioIndex);
        Assert.Equal(2, anime.AudioIndex);
        Assert.Contains("your Anime library rule", anime.Explain(), StringComparison.Ordinal);
    }

    // a household member who understands Japanese gets Japanese with partial subtitles.
    [Fact]
    public void JapaneseSpeaker_OriginalChain_UnderstoodGetsPartial()
    {
        var profile = OriginalAudio("ja", "en") with { Reads = new[] { "en" } };

        var d = Decide(Slv(), profile, Item(original: "ja"));

        Assert.Equal(2, d.AudioIndex);
        Assert.True(d.AudioUnderstood);
        Assert.Equal(3, d.SubtitleIndex);
    }

    // Generic forced metadata can satisfy full subtitles when no identified dialogue track exists.
    [Fact]
    public void AmberCourier_OriginalChain_UsesGenericForcedTrack()
    {
        var d = Decide(AmberCourier(), OriginalAudio("en"), Item(original: "ja"));

        Assert.Equal(2, d.AudioIndex);
        Assert.Equal(3, d.SubtitleIndex);
        Assert.Contains("metadata does not identify dialogue coverage", d.Explain(), StringComparison.Ordinal);
    }

    // CopperDune with Japanese audio → the "Dialogue" track.
    [Fact]
    public void CopperDune_OriginalChain_PicksDialogue()
    {
        var d = Decide(CopperDune(), OriginalAudio("en"), Item(original: "ja"));

        Assert.Equal(2, d.AudioIndex);
        Assert.Equal(3, d.SubtitleIndex);
    }

    // Starlight Survey's default is already Japanese; the original chain picks it and the full PGS track.
    [Fact]
    public void StarlightSurvey_OriginalChain_JapaneseAndFullPgs()
    {
        var d = Decide(StarlightSurvey(), OriginalAudio("en"), Item(original: "ja"));

        Assert.Equal(1, d.AudioIndex);
        Assert.Equal(3, d.SubtitleIndex);
    }
}
