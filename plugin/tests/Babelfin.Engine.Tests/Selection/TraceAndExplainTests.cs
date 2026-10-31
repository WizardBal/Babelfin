using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>the explanation trace, Explain(), multi-version sources, determinism and purity.</summary>
public class TraceAndExplainTests
{
    private static IEnumerable<Decision> Sample()
    {
        yield return Decide(Slv(), Starter("en"));
        yield return Decide(Slv(), OriginalAudio("en"), Item("ja"));
        yield return Decide(ClockworkOrchard(), OriginalAudio("en"), Item("ja"));
        yield return Decide(AmberCourier(), OriginalAudio("en"), Item("ja"));
        yield return Decide(GlassRelay(), Starter("en"));
        yield return Decide(SingleUnlabelledAudio(), Starter("en"));
        yield return Decide(MidnightCircuit(), Starter("en"));
        yield return Decide(Array.Empty<Jellyfin.Plugin.Babelfin.Engine.Classification.Track>(), Starter("en"));
    }

    [Fact]
    public void TraceCodes_All_MatchesKnownList()
    {
        Assert.Equal(KnownCodes.OrderBy(c => c, StringComparer.Ordinal), TraceCodes.All.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryDecision_HasAtLeastTwoTraceLines_WithKnownCodes()
    {
        foreach (var d in Sample())
        {
            Assert.True(d.Trace.Count >= 2);
            Assert.All(d.Trace, l => Assert.Contains(l.Code, TraceCodes.All));
        }
    }

    [Fact]
    public void Explain_DescribesChosenTracksAndSkippedPartialTrack()
    {
        var profile = OriginalAudio("en") with { FieldSources = new Dictionary<string, string> { ["AudioChain"] = "your Anime library rule" } };

        var explain = Decide(ClockworkOrchard(), profile, Item("ja")).Explain();

        Assert.Equal(
            "Audio: Japanese (#2): your Anime library rule, step 1. Subtitles: English full (#4): the Signs/Songs [PebbleForge] track (#3) was skipped because it's partial.",
            explain);
    }

    [Fact]
    public void Explain_DefaultSourceIsEverydaySettings()
    {
        Assert.StartsWith("Audio: English (#1): your everyday settings, step 1.", Decide(Slv(), Starter("en")).Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_NeverContainsNull_AndSentencesStayShort()
    {
        foreach (var d in Sample())
        {
            var explain = d.Explain();
            Assert.DoesNotContain("null", explain, StringComparison.OrdinalIgnoreCase);
            var parts = explain.Split(" Subtitles: ");
            Assert.Equal(2, parts.Length);
            Assert.All(parts, p => Assert.True(p.Length <= 160, p));
        }
    }

    [Fact]
    public void Explain_NoAudio_SaysNone()
    {
        var d = Decide(Array.Empty<Jellyfin.Plugin.Babelfin.Engine.Classification.Track>(), Starter("en"));

        Assert.StartsWith("Audio: none in this file.", d.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_NeverContainsFilePaths()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "eng", external: true, fileName: "/media/anime/Show/Show.S01E01.en.srt") };

        var d = Decide(tracks, OriginalAudio("en"), Item("ja"));

        Assert.Equal(2, d.SubtitleIndex);
        Assert.DoesNotContain("/media", d.Explain(), StringComparison.Ordinal);
        Assert.All(d.Trace, l => Assert.DoesNotContain("/media", l.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Explain_ExplicitPartialCannotSatisfyFullRequest()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "eng", "Signs & Songs", isForced: true) };
        Assert.Contains("Subtitles: none found in English.", Decide(tracks, OriginalAudio("en"), Item("ja")).Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_PartialOnly_SaysWhy()
    {
        var tracks = new[] { Audio(1, "eng"), Sub(3, "eng", "Signs & Songs", isForced: true) };
        Assert.Contains("Subtitles: English partial (#3): signs and songs only, because English is in My languages.", Decide(tracks, Starter("en")).Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_ManualAudio_SaysRemembered()
    {
        var remembered = TrackDescriptor.From(Classify(Slv()[1]));

        var d = Decide(Slv(), Starter("en"), manual: new ManualOverride(remembered, false, null));

        Assert.StartsWith("Audio: Japanese (#2): remembered from your track change.", d.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void FieldSources_AppearInTrace()
    {
        var profile = OriginalAudio("en") with { FieldSources = new Dictionary<string, string> { ["AudioChain"] = "your Anime library rule", ["WhenAudioNotUnderstood"] = "the admin's everyday default" } };

        var d = Decide(Slv(), profile, Item("ja"));

        Assert.Contains("your Anime library rule", d.Trace.Single(l => l.Code == "audio.step.matched").Text, StringComparison.Ordinal);
        Assert.Contains("the admin's everyday default", d.Trace.Single(l => l.Code == "subs.want").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TraceLines_CarryTrackIndex()
    {
        var d = Decide(ClockworkOrchard(), OriginalAudio("en"), Item("ja"));

        Assert.Equal(2, d.Trace.Single(l => l.Code == "audio.step.matched").TrackIndex);
        Assert.Equal(4, d.Trace.Single(l => l.Code == "subs.matched").TrackIndex);
        Assert.Equal(3, d.Trace.Single(l => l.Code == "subs.skipped.partial").TrackIndex);
    }

    [Fact]
    public void Tracks_AreReturnedClassifiedInIndexOrder()
    {
        var d = Decide(Enumerable.Reverse(Slv()).ToArray(), Starter("en"));

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, d.Tracks.Select(t => t.Track.Index));
    }

    // Multi-version: the caller decides per source.
    [Fact]
    public void MultiVersion_TheGlassRelay_SourcesDecidedIndependently()
    {
        var first = Decide(SingleUnlabelledAudio(), Starter("en"), Item("en"));
        var second = Decide(GlassRelay(), Starter("en"), Item("en"));

        Assert.Equal(1, first.AudioIndex);
        Assert.Null(first.SubtitleIndex);
        Assert.Equal(1, second.AudioIndex);
        Assert.Equal(2, second.SubtitleIndex);
    }

    // Multi-version
    [Fact]
    public void MultiVersion_MidnightCircuit_SourcesDecidedIndependently()
    {
        var profile = Fx.Starter(new[] { "de" }, new[] { "en" });
        var first = Decide(new[] { Audio(1, "und") }, profile, Item("en"));
        var second = Decide(MidnightCircuit(), profile, Item("en"));

        Assert.Equal(1, first.AudioIndex);
        Assert.Null(first.SubtitleIndex);
        Assert.Equal(1, second.AudioIndex);
        Assert.Equal(3, second.SubtitleIndex);
    }

    // Determinism
    [Fact]
    public void SameInputs_EqualDecisions()
    {
        var a = Decide(ClockworkOrchard(), OriginalAudio("en"), Item("ja"));
        var b = Decide(ClockworkOrchard(), OriginalAudio("en"), Item("ja"));

        Assert.Equal(a.AudioIndex, b.AudioIndex);
        Assert.Equal(a.SubtitleIndex, b.SubtitleIndex);
        Assert.Equal(a.Trace.Select(l => l.Text), b.Trace.Select(l => l.Text));
        Assert.Equal(a.Explain(), b.Explain());
    }

    // Determinism: input order doesn't matter.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ShuffledInput_SameDecision(int seed)
    {
        var tracks = Slv();
        var rng = new Random(seed);
        var shuffled = tracks.OrderBy(_ => rng.Next()).ToArray();
        var rotated = tracks.Skip(seed).Concat(tracks.Take(seed)).ToArray();

        var a = Decide(tracks, OriginalAudio("en"), Item("ja"));
        var b = Decide(shuffled, OriginalAudio("en"), Item("ja"));
        var c = Decide(rotated, OriginalAudio("en"), Item("ja"));

        Assert.Equal(a.AudioIndex, b.AudioIndex);
        Assert.Equal(a.SubtitleIndex, b.SubtitleIndex);
        Assert.Equal(a.Trace.Select(l => l.Text), b.Trace.Select(l => l.Text));
        Assert.Equal(a.Trace.Select(l => l.Text), c.Trace.Select(l => l.Text));
    }

    // Purity: deciding doesn't change the inputs, and the selector can be shared between threads.
    [Fact]
    public void ParallelDecisions_AreIdentical()
    {
        var tracks = ClockworkOrchard();
        var profile = OriginalAudio("en");
        var expected = Decide(tracks, profile, Item("ja")).Explain();

        var results = Enumerable.Range(0, 64).AsParallel().Select(_ => Selector.Decide(tracks, Item("ja"), profile).Explain()).ToArray();

        Assert.All(results, r => Assert.Equal(expected, r));
        Assert.Equal(4, tracks.Length);
        Assert.Equal(3, tracks[2].Index);
    }

    [Fact]
    public void TestStarter_HasExplicitSelectionDefaults()
    {
        var p = Fx.Starter(new[] { "en" }, new[] { "en" });

        Assert.Equal(new[] { AudioStepKind.MyLanguages, AudioStepKind.OriginalLanguage, AudioStepKind.FileDefault }, p.AudioChain.Select(s => s.Kind));
        Assert.Equal(SubtitleWant.PartialOnly, p.WhenAudioUnderstood);
        Assert.Equal(SubtitleWant.Full, p.WhenAudioNotUnderstood);
        Assert.Equal(FormatPreference.PreferText, p.Format);
        Assert.True(p.AvoidCommentary);
        Assert.Equal(UnlabelledAudioRule.OriginalWhenOnlyTrack, p.UnlabelledAudio);
        Assert.Equal(Preference.Neutral, p.Sdh);
        Assert.False(p.UnknownAudioCountsAsUnderstood);
        Assert.Equal(new[] { TieBreak.DirectPlayable, TieBreak.MoreChannels, TieBreak.Lossless, TieBreak.IsOriginalFlag, TieBreak.DefaultFlag, TieBreak.LowestIndex }, p.TieBreaks);
    }

    [Fact]
    public void Decide_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => Selector.Decide(null!, ItemFacts.None, Starter("en")));
        Assert.Throws<ArgumentNullException>(() => Selector.Decide(Slv(), null!, Starter("en")));
        Assert.Throws<ArgumentNullException>(() => Selector.Decide(Slv(), ItemFacts.None, null!));
    }
}
