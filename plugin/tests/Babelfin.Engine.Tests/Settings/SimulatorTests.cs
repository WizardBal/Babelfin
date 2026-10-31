using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Admin;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>the simulator's rows, flags and coverage counts from recorded decisions, and the CSV export.</summary>
public class SimulatorTests
{
    private static readonly LanguageTable L = LanguageTable.Default;

    private static readonly CoverageItem Episode = new(Guid.Parse("0123456789abcdef0123456789abcdef"), "The Beginning", "Some Show", 1, 2);

    private static readonly MediaSourceInfo Source = new() { Id = "src1", MediaStreams = new List<MediaStream>() };

    private static ClassifiedTrack Audio(int index, string language) =>
        new(new Track(index, TrackType.Audio, language, null, null, null, "aac", 2, false, false, false, false, false), L.Resolve(language), SubtitleKind.Full, AudioKind.Normal, Array.Empty<string>());

    private static ClassifiedTrack Sub(int index, string language, SubtitleKind kind = SubtitleKind.Full, SubtitleFormat format = SubtitleFormat.Text, string codec = "subrip", string? fileName = null) =>
        new(new Track(index, TrackType.Subtitle, language, null, null, fileName, codec, null, false, false, false, false, fileName is not null, format), L.Resolve(language), kind, AudioKind.Normal, Array.Empty<string>());

    private static TraceLine Line(string code, int? index = null, params (string Key, string Value)[] args) =>
        new(code, code, index, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));

    private static DecisionRecord Record(Decision engine, DecisionOutcome outcome = DecisionOutcome.Applied) =>
        new(DateTime.UtcNow, 1, -1, new TrackDecision(engine.AudioIndex, engine.SubtitleIndex ?? -1, engine.Trace.Select(t => t.Text).ToArray(), 1, -1, "Audio: Japanese (#1).", engine), outcome);

    private static Decision Decision(int? audio, int? subtitle, bool wanted, IReadOnlyList<ClassifiedTrack> tracks, params TraceLine[] trace) =>
        new(audio, subtitle, wanted, false, tracks, trace);

    [Fact]
    public void MatchedAudio_FullSubtitle_FlagsWantedAudioOnly()
    {
        var engine = Decision(1, 3, true, new[] { Audio(1, "jpn"), Sub(3, "eng") }, Line(TraceCodes.AudioStepMatched, 1), Line(TraceCodes.SubsMatched, 3));

        var row = Coverage.BuildRow(Episode, Source, Record(engine), "ja", "ja");

        Assert.Equal(new[] { CoverageFlags.WantedAudioFound }, row.Flags);
        Assert.Equal("0123456789abcdef0123456789abcdef", row.ItemId);
        Assert.Equal("Some Show", row.SeriesName);
        Assert.Equal("src1", row.SourceId);
        Assert.Equal("Applied", row.Outcome);
        Assert.Equal(1, row.AudioIndex);
        Assert.Equal("ja", row.AudioLanguage);
        Assert.Equal("Japanese", row.AudioName);
        Assert.Equal(3, row.SubtitleIndex);
        Assert.Equal("en", row.SubtitleLanguage);
        Assert.Equal("full", row.SubtitleKind);
        Assert.Equal("Audio: Japanese (#1).", row.Explanation);
    }

    [Fact]
    public void PartialChosen_ImageSubtitles_AndFallback()
    {
        var engine = Decision(
            1,
            4,
            true,
            new[] { Audio(1, "jpn"), Sub(4, "eng", SubtitleKind.Partial, SubtitleFormat.Image, "hdmv_pgs_subtitle") },
            Line(TraceCodes.AudioFallback, 1),
            Line(TraceCodes.SubsNothing),
            Line(TraceCodes.SubsFallback, 4));

        var row = Coverage.BuildRow(Episode, Source, Record(engine), null, null);

        Assert.True(row.Has(CoverageFlags.PartialChosen));
        Assert.True(row.Has(CoverageFlags.ImageSubtitles));
        Assert.True(row.Has(CoverageFlags.FallbackUsed));
        Assert.False(row.Has(CoverageFlags.WantedAudioFound));
        Assert.False(row.Has(CoverageFlags.NoSubtitles));
        Assert.Equal("partial", row.SubtitleKind);
        Assert.Equal("hdmv_pgs_subtitle", row.SubtitleCodec);
    }

    [Fact]
    public void SubtitleFallbackAlone_CountsAsFallbackUsed()
    {
        var engine = Decision(1, 3, true, new[] { Audio(1, "jpn"), Sub(3, "fra") }, Line(TraceCodes.AudioStepMatched, 1), Line(TraceCodes.SubsFallback, 3));

        var row = Coverage.BuildRow(Episode, Source, Record(engine), null, null);

        Assert.Equal(new[] { CoverageFlags.FallbackUsed, CoverageFlags.WantedAudioFound }, row.Flags);
    }

    [Fact]
    public void WantedButNone_IsNoSubtitles()
    {
        var engine = Decision(1, null, true, new[] { Audio(1, "jpn") }, Line(TraceCodes.AudioStepMatched, 1), Line(TraceCodes.SubsNothing));

        var row = Coverage.BuildRow(Episode, Source, Record(engine), null, null);

        Assert.True(row.Has(CoverageFlags.NoSubtitles));
        Assert.Null(row.SubtitleIndex);
        Assert.Null(row.SubtitleKind);
    }

    [Fact]
    public void BurnedIn_IsNotNoSubtitles()
    {
        var engine = Decision(1, null, true, new[] { Audio(1, "jpn") }, Line(TraceCodes.AudioStepMatched, 1), Line(TraceCodes.SubsBurnedIn));

        var row = Coverage.BuildRow(Episode, Source, Record(engine), null, null);

        Assert.True(row.Has(CoverageFlags.BurnedIn));
        Assert.False(row.Has(CoverageFlags.NoSubtitles));
    }

    [Fact]
    public void SubtitlesOff_NoSubtitleFlags()
    {
        var engine = Decision(1, null, false, new[] { Audio(1, "eng"), Sub(2, "eng") }, Line(TraceCodes.AudioStepMatched, 1), Line(TraceCodes.SubsOff));

        var row = Coverage.BuildRow(Episode, Source, Record(engine), null, null);

        Assert.Equal(new[] { CoverageFlags.WantedAudioFound }, row.Flags);
        Assert.False(row.SubtitlesWanted);
    }

    [Theory]
    [InlineData("en", "ja", true)]
    [InlineData("English", "ja", true)]
    [InlineData("jpn", "ja", false)]
    [InlineData("zh", "yue", false)]
    [InlineData("cmn", "yue", true)]
    [InlineData(null, "ja", false)]
    [InlineData("en", null, false)]
    [InlineData("xx-not-a-language", "ja", false)]
    public void OriginalNotUsual_OnlyWhenBothKnownAndDifferent(string? original, string? usual, bool expected)
    {
        var engine = Decision(1, null, false, new[] { Audio(1, "jpn") }, Line(TraceCodes.AudioStepMatched, 1));

        var row = Coverage.BuildRow(Episode, Source, Record(engine), original, usual);

        Assert.Equal(expected, row.Has(CoverageFlags.OriginalNotUsual));
    }

    [Fact]
    public void OriginalLanguage_ComesFromTheTrace()
    {
        var engine = Decision(1, null, false, new[] { Audio(1, "jpn") }, Line(TraceCodes.OriginalKnown, null, ("language", "ja"), ("name", "Japanese"), ("from", "metadata")));

        Assert.Equal("ja", Coverage.OriginalLanguageOf(engine));
        Assert.Null(Coverage.OriginalLanguageOf(Decision(1, null, false, Array.Empty<ClassifiedTrack>())));
        Assert.Null(Coverage.OriginalLanguageOf(null));
    }

    [Fact]
    public void AssumedUnlabelledAudio_TakesTheTraceLanguage()
    {
        var unlabelled = new ClassifiedTrack(new Track(1, TrackType.Audio, null, null, null, null, "aac", 2, true, false, false, false, false), null, SubtitleKind.Full, AudioKind.Normal, Array.Empty<string>());
        var engine = Decision(1, null, false, new[] { unlabelled }, Line(TraceCodes.AudioUnlabelledAssumed, 1, ("index", "1"), ("language", "ja"), ("name", "Japanese")), Line(TraceCodes.AudioStepMatched, 1));

        var row = Coverage.BuildRow(Episode, Source, Record(engine), null, null);

        Assert.Equal("ja", row.AudioLanguage);
    }

    [Fact]
    public void PassedThrough_DescribesJellyfinsChoice_WithoutFlags()
    {
        var source = new MediaSourceInfo
        {
            Id = "src2",
            MediaStreams = new List<MediaStream>
            {
                new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng", Codec = "aac" },
                new() { Index = 2, Type = MediaStreamType.Subtitle, Language = "eng", Codec = "subrip", IsForced = true },
            },
        };
        var record = new DecisionRecord(DateTime.UtcNow, 1, 2, null, DecisionOutcome.PassedThrough);

        var row = Coverage.BuildRow(Episode, source, record, null, null);

        Assert.Equal("PassedThrough", row.Outcome);
        Assert.Equal(1, row.AudioIndex);
        Assert.Equal("en", row.AudioLanguage);
        Assert.Equal(2, row.SubtitleIndex);
        Assert.Equal("partial", row.SubtitleKind);
        Assert.Equal(new[] { CoverageFlags.PartialChosen }, row.Flags);
    }

    [Fact]
    public void NoRecord_IsNone()
    {
        var row = Coverage.BuildRow(Episode, Source, null, null, null);

        Assert.Equal("None", row.Outcome);
        Assert.Empty(row.Flags);
    }

    [Fact]
    public void EngineDecision_EndToEnd_SignsSkippedForFull()
    {
        // A real selection: Japanese audio the person doesn't understand, a Signs & Songs track and a full English track.
        var classifier = new TrackClassifier(L, KeywordTables.Default);
        var selector = new TrackSelector(L, classifier);
        var tracks = new[]
        {
            new Track(1, TrackType.Audio, "jpn", null, null, null, "aac", 2, true, false, false, false, false),
            new Track(2, TrackType.Subtitle, "eng", "Signs & Songs", null, null, "ass", null, true, false, false, false, false, SubtitleFormat.Text),
            new Track(3, TrackType.Subtitle, "eng", "Full", null, null, "ass", null, false, false, false, false, false, SubtitleFormat.Text),
        };
        var engine = selector.Decide(tracks, new ItemFacts("ja", "ja", Array.Empty<string>(), true), Babelfin.Engine.Tests.Selection.Fx.Starter(new[] { "en" }, new[] { "en" }), null, null);

        var row = Coverage.BuildRow(Episode, Source, Record(engine), Coverage.OriginalLanguageOf(engine), "ja");

        Assert.Equal(3, row.SubtitleIndex);
        Assert.Equal("full", row.SubtitleKind);
        Assert.False(row.Has(CoverageFlags.PartialChosen));
        Assert.False(row.Has(CoverageFlags.OriginalNotUsual));
        Assert.Equal("ja", row.OriginalLanguage);
    }

    [Fact]
    public void Counts_PerCategory()
    {
        var rows = new[]
        {
            Coverage.BuildRow(Episode, Source, Record(Decision(1, 3, true, new[] { Audio(1, "jpn"), Sub(3, "eng") }, Line(TraceCodes.AudioStepMatched, 1))), "en", "ja"),
            Coverage.BuildRow(Episode, Source, Record(Decision(1, 4, true, new[] { Audio(1, "jpn"), Sub(4, "eng", SubtitleKind.Partial, SubtitleFormat.Image) }, Line(TraceCodes.AudioFallback, 1), Line(TraceCodes.SubsFallback, 4))), null, null),
            Coverage.BuildRow(Episode, Source, Record(Decision(1, null, true, new[] { Audio(1, "jpn") }, Line(TraceCodes.AudioStepMatched, 1))), null, null),
            Coverage.BuildRow(Episode, Source, Record(Decision(1, null, true, new[] { Audio(1, "jpn") }, Line(TraceCodes.SubsBurnedIn))), null, null),
            Coverage.BuildRow(Episode, Source, Record(Decision(1, null, false, new[] { Audio(1, "eng") }, Line(TraceCodes.SubsOff)), DecisionOutcome.DryRun), null, null),
            Coverage.BuildRow(Episode, Source, Record(Decision(1, 3, true, new[] { Audio(1, "jpn"), Sub(3, "eng", SubtitleKind.Sdh) })), null, null),
            Coverage.BuildRow(Episode, Source, null, null, null),
        };

        var c = Coverage.Count(rows);

        Assert.Equal(7, c.Files);
        Assert.Equal(6, c.Decided);
        Assert.Equal(1, c.NotDecided);
        Assert.Equal(2, c.WantedAudioFound);
        Assert.Equal(1, c.FallbackUsed);
        Assert.Equal(2, c.FullSubtitles);
        Assert.Equal(1, c.PartialOnly);
        Assert.Equal(1, c.NoSubtitles);
        Assert.Equal(1, c.SubtitlesOff);
        Assert.Equal(1, c.BurnedIn);
        Assert.Equal(1, c.ImageSubtitles);
        Assert.Equal(1, c.OriginalNotUsual);
    }

    // The "Check a title" rows never carry an external subtitle's file name (the engine's track list holds it).
    [Fact]
    public void Row_HasNoFileNames()
    {
        var item = new CoverageItem(Guid.NewGuid(), "Hello, \"World\"", "=Show", 1, 1);
        var engine = Decision(1, 3, true, new[] { Audio(1, "jpn"), Sub(3, "eng", fileName: "/media/secret/Show.en.srt") }, Line(TraceCodes.AudioStepMatched, 1));

        var row = Coverage.BuildRow(item, Source, Record(engine), null, null);

        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(row), StringComparison.Ordinal);
        Assert.Equal("Hello, \"World\"", row.Name);
    }
}
