using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Regression;

// Global EngineServices / Plugin restoration tests must not overlap other test classes.
[CollectionDefinition("Selection cache globals", DisableParallelization = true)]
public sealed class SelectionCacheGlobalCollection { }

[Collection("Selection cache globals")]
public class ClassificationCacheTests
{
    private static TrackSelector NewSelector() => new EngineServices(null).Selector;
    private static Decision Choose(TrackSelector selector, IReadOnlyList<Track> tracks, string source = "same-source", bool trace = false)
        => selector.Decide(tracks, Item("ja"), OriginalAudio("en"), includeTrace: trace, sourceId: source);

    public static IEnumerable<object[]> InputFields() => typeof(Track).GetProperties().Where(p => p.SetMethod is not null).Select(p => new object[] { p.Name });

    private static Track Change(Track track, string field) => field switch
    {
        nameof(Track.Index) => track with { Index = 7 },
        nameof(Track.Type) => track with { Type = TrackType.Audio },
        nameof(Track.Language) => track with { Language = "jpn" },
        nameof(Track.Title) => track with { Title = "Signs & Songs" },
        nameof(Track.Comment) => track with { Comment = "SDH" },
        nameof(Track.FileName) => track with { FileName = ".eng.forced.srt" },
        nameof(Track.Codec) => track with { Codec = "ass" },
        nameof(Track.Channels) => track with { Channels = 6 },
        nameof(Track.IsDefault) => track with { IsDefault = true },
        nameof(Track.IsForced) => track with { IsForced = true },
        nameof(Track.IsHearingImpaired) => track with { IsHearingImpaired = true },
        nameof(Track.IsOriginal) => track with { IsOriginal = true },
        nameof(Track.IsExternal) => track with { IsExternal = true },
        nameof(Track.Format) => track with { Format = SubtitleFormat.Image },
        nameof(Track.IsLossless) => track with { IsLossless = true },
        nameof(Track.ExternalPath) => track with { ExternalPath = @"C:\media\episode.en.srt" },
        _ => throw new InvalidOperationException("Add a semantic cache mutation for new Track field " + field),
    };

    [Theory]
    [MemberData(nameof(InputFields))]
    public void EveryClassificationInputAndFlagInvalidatesTheCachedSnapshot(string field)
    {
        var selector = NewSelector();
        var original = Sub(3, "eng", codec: "subrip", format: SubtitleFormat.Text);
        var tracks = new[] { Audio(1, "jpn"), original };
        var before = Choose(selector, tracks);
        var mutated = Change(original, field);
        tracks[1] = mutated;
        var after = Choose(selector, tracks);
        Assert.NotSame(before.Tracks, after.Tracks);
        Assert.Equal(original, before.Tracks.Single(t => t.Track.Index == 3).Track);
        Assert.Contains(after.Tracks, t => t.Track == mutated);
        Assert.Same(after.Tracks, Choose(selector, tracks.Select(t => t with { }).ToArray()).Tracks);
    }

    [Fact]
    public void ExternalSubtitleAdditionInvalidatesWithoutChangingVideoOrSourceIdentity()
    {
        var selector = NewSelector();
        var original = new[] { Audio(1, "jpn") };
        var before = Choose(selector, original);
        var external = Sub(4, "eng", external: true, fileName: ".en.srt") with { ExternalPath = @"C:\media\episode.en.srt" };
        var after = Choose(selector, original.Append(external).ToArray());
        Assert.Null(before.SubtitleIndex);
        Assert.Equal(4, after.SubtitleIndex);
        Assert.NotSame(before.Tracks, after.Tracks);
    }

    [Fact]
    public void SameTracksWithNewItemModifiedDateReuseClassificationThroughTheRealDecider()
    {
        var resolved = Ctx.Resolve(Ctx.Person(), null, Ctx.Movie(original: "ja"));
        resolved = resolved with { Profile = OriginalAudio("en") };
        var resolver = CallProxy.Create<IProfileResolver>((m, _) => m.Name == "Resolve" ? resolved : null).Proxy;
        var decider = new EngineTrackDecider(resolver, NullLogger<EngineTrackDecider>.Instance);
        var movie = new Movie { Id = Guid.NewGuid(), DateModified = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) };
        var source = new MediaBrowser.Model.Dto.MediaSourceInfo
        {
            Id = "date-reuse", Path = @"C:\media\episode.mkv",
            MediaStreams = new List<MediaStream>
            {
                new() { Index = 1, Type = MediaStreamType.Audio, Language = "jpn", Codec = "aac" },
                new() { Index = 3, Type = MediaStreamType.Subtitle, Language = "eng", Codec = "subrip" },
            },
        };
        var user = ApiSafetyHost.Person();
        var first = decider.Decide(movie, source, user)!;
        movie.DateModified = movie.DateModified.AddDays(5);
        var second = decider.Decide(movie, source, user)!;
        Assert.Same(first.Engine!.Tracks, second.Engine!.Tracks);
        Assert.Equal(first.SubtitleIndex, second.SubtitleIndex);
        source.MediaStreams = source.MediaStreams.Append(new MediaStream { Index = 4, Type = MediaStreamType.Subtitle, Language = "eng", Codec = "subrip", IsExternal = true, Path = @"C:\media\episode.en.srt" }).ToArray();
        Assert.NotSame(second.Engine.Tracks, decider.Decide(movie, source, user)!.Engine!.Tracks);
    }

    [Fact]
    public void IndependentMediaSourcesNeverShareAnEntry()
    {
        var selector = NewSelector();
        var tracks = Slv();
        Assert.NotSame(Choose(selector, tracks, "first").Tracks, Choose(selector, tracks, "second").Tracks);
        Assert.Same(Choose(selector, tracks, "first").Tracks, Choose(selector, tracks, "first").Tracks);
    }

    [Fact]
    public void KeywordAndAliasSavesAdvanceRecognitionGenerationAndReclassify()
    {
        var oldPlugin = Plugin.Instance;
        var instanceField = typeof(Plugin).GetField("<Instance>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!;
        var directory = Path.Combine(Path.GetTempPath(), "bf-cache-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var paths = CallProxy.Create<IApplicationPaths>((m, _) => m.ReturnType == typeof(string) ? directory : null).Proxy;
            var initial = new PluginConfiguration();
            var serializer = CallProxy.Create<IXmlSerializer>((m, _) => m.Name.StartsWith("Deserialize", StringComparison.Ordinal) ? initial : null).Proxy;
            var plugin = new Plugin(paths, serializer);
            EngineServices.Reset();
            var before = EngineServices.Current;
            var tracks = new[] { Audio(1, "jpn"), Sub(3, "eng", "bfpartial") };
            var original = Choose(before.Selector, tracks);
            Assert.Equal(SubtitleKind.Full, original.Tracks[1].SubtitleKind);
            var config = plugin.Configuration;
            config.Keywords.PartialWords.Add("bfpartial");
            plugin.UpdateConfiguration(config);
            var keywords = EngineServices.Current;
            Assert.True(keywords.RecognitionVersion > before.RecognitionVersion);
            Assert.Equal(SubtitleKind.Partial, Choose(keywords.Selector, tracks).Tracks[1].SubtitleKind);
            Assert.Equal(SubtitleKind.Full, Choose(before.Selector, tracks).Tracks[1].SubtitleKind);
            config.LanguageAliases.Add(new LanguageAlias { Raw = "bflanguage", Code = "ja" });
            plugin.UpdateConfiguration(config);
            var aliases = EngineServices.Current;
            Assert.True(aliases.RecognitionVersion > keywords.RecognitionVersion);
            var aliased = new[] { Audio(1, "bflanguage") };
            Assert.Null(Choose(keywords.Selector, aliased).Tracks[0].Language);
            Assert.Equal("ja", Choose(aliases.Selector, aliased).Tracks[0].Language!.Code);
            config.Everyday.UnlabelledAudio = UnlabelledAudioRule.SpecificLanguage;
            config.Everyday.UnlabelledAudioLanguage = "ja";
            plugin.UpdateConfiguration(config);
            Assert.True(EngineServices.Current.RecognitionVersion > aliases.RecognitionVersion);
            Assert.Same(EngineServices.Current, EngineServices.Current);
        }
        finally
        {
            instanceField.SetValue(null, oldPlugin);
            EngineServices.Reset();
            _ = EngineServices.Current; // Unsubscribe the fake plugin and restore the prior binding.
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VarietyKeywordGenerationsUseIndependentCaches()
    {
        var tracks = new[] { Audio(1, "chi", "bfvariety") };
        var ordinary = NewSelector();
        var table = new LanguageTable(IsoLanguages.All, AliasTable.Default, new VarietyKeywordTable(new[] { new VarietyKeyword("bfvariety", "zh", "yue", null, null) }));
        var changed = new TrackSelector(table, new TrackClassifier(table, KeywordTables.Default));
        Assert.Null(Choose(ordinary, tracks).Tracks[0].Language!.Variety);
        Assert.Equal("yue", Choose(changed, tracks).Tracks[0].Language!.Variety);
        Assert.NotSame(Choose(ordinary, tracks).Tracks, Choose(changed, tracks).Tracks);
    }

    private static IDictionary Entries(TrackSelector selector)
    {
        var cache = typeof(TrackSelector).GetField("_classified", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(selector)!;
        return (IDictionary)typeof(ClassifiedTrackCache).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
    }

    [Fact]
    public void ExpiredEntryReclassifiesWithoutATenMinuteSleep()
    {
        var selector = NewSelector();
        var first = Choose(selector, Slv());
        var entry = Assert.Single(Entries(selector).Values.Cast<object>());
        entry.GetType().GetProperty("ExpiresAt")!.SetValue(entry, Environment.TickCount64 - 1);
        var second = Choose(selector, Slv());
        Assert.NotSame(first.Tracks, second.Tracks);
        Assert.Equal(first.SubtitleIndex, second.SubtitleIndex);
        Assert.Single(Entries(selector).Values.Cast<object>());
    }

    [Fact]
    public void SequentialCacheStaysBoundedAndOldestEntriesAreEvicted()
    {
        var selector = NewSelector();
        var first = Choose(selector, Slv(), "oldest");
        for (var i = 0; i < 1100; i++) Choose(selector, Slv(), "source-" + i);
        Assert.InRange(Entries(selector).Count, 1, 512);
        Assert.NotSame(first.Tracks, Choose(selector, Slv(), "oldest").Tracks);
    }

    [Theory]
    [InlineData("equal-clock-a")]
    [InlineData("equal-clock-b")]
    [InlineData("equal-clock-c")]
    [InlineData("equal-clock-d")]
    public void EqualExpiryEntriesEvictTheEarlierInsertionBeforeTheNewest(string prefix)
    {
        var selector = NewSelector();
        var oldest = Choose(selector, Slv(), prefix + "-oldest");
        Decision newest = oldest;
        for (var i = 0; i < 511; i++) newest = Choose(selector, Slv(), prefix + "-" + i);
        // Model a low-resolution clock: every existing entry has exactly the same valid expiry.
        var equalExpiry = Environment.TickCount64 + 600000;
        foreach (var entry in Entries(selector).Values.Cast<object>())
            entry.GetType().GetProperty("ExpiresAt")!.SetValue(entry, equalExpiry);
        Choose(selector, Slv(), prefix + "-new");
        Assert.NotSame(oldest.Tracks, Choose(selector, Slv(), prefix + "-oldest").Tracks);
        Assert.Same(newest.Tracks, Choose(selector, Slv(), prefix + "-510").Tracks);
        Assert.InRange(Entries(selector).Count, 1, 512);
    }

    [Fact]
    public void ConcurrentColdMissesStayBoundedAndNeverReturnAnotherInputsTracks()
    {
        var selector = NewSelector();
        Parallel.For(0, 2048, new ParallelOptions { MaxDegreeOfParallelism = 32 }, i =>
        {
            var title = (i % 2 == 0 ? "Signs & Songs source " : "Dialogue source ") + i;
            var tracks = new[] { Audio(1, "jpn"), Sub(3, "eng", title, isForced: i % 2 == 0) };
            var result = Choose(selector, tracks, "source-" + i);
            Assert.Equal(tracks[1], result.Tracks[1].Track);
            Assert.Equal(i % 2 == 0 ? (int?)null : 3, result.SubtitleIndex);
        });
        Assert.InRange(Entries(selector).Count, 1, 512);
    }

    [Fact]
    public void ConcurrentHotHitsReuseTheSameImmutableClassification()
    {
        var selector = NewSelector();
        var first = Choose(selector, Slv());
        Parallel.For(0, 512, _ => Assert.Same(first.Tracks, Choose(selector, Slv()).Tracks));
        Assert.Throws<NotSupportedException>(() => ((IList<ClassifiedTrack>)first.Tracks)[0] = first.Tracks[1]);
    }

    [Fact]
    public void CachedDeferredAndEagerDecisionsHaveIdenticalTraceArgsAndExplanation()
    {
        foreach (var pair in SelectionSafetyTests.TraceCorpus().Zip(SelectionSafetyTests.TraceCorpus(includeTrace: false)))
        {
            var eager = pair.First;
            var deferred = pair.Second;
            Assert.Equal((eager.AudioIndex, eager.SubtitleIndex, eager.SubtitlesWanted, eager.AudioUnderstood, eager.AudioLeftToVideo, eager.SubtitlesLeftToVideo),
                (deferred.AudioIndex, deferred.SubtitleIndex, deferred.SubtitlesWanted, deferred.AudioUnderstood, deferred.AudioLeftToVideo, deferred.SubtitlesLeftToVideo));
            Assert.Equal(eager.Trace.Select(TraceKey), deferred.Trace.Select(TraceKey));
            Assert.Equal(eager.Explain(), deferred.Explain());
        }
    }

    private static string TraceKey(TraceLine line) => line.Code + "|" + line.TrackIndex + "|" + line.Text + "|" + string.Join(";", line.Args?.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value) ?? Array.Empty<string>());

    [Fact]
    public void TrackDecisionBuildsItsDiagnosticTextOnlyOnDemandAndOnceAcrossThreads()
    {
        var calls = 0;
        var expected = Choose(NewSelector(), Slv(), trace: true);
        var trace = new DeferredTrace(() => { Interlocked.Increment(ref calls); return expected.Trace; });
        var engine = expected with { Trace = trace };
        var decision = new TrackDecision(engine.AudioIndex, engine.SubtitleIndex, new DeferredTextTrace(trace), Engine: engine);
        Assert.Equal(0, calls);
        Parallel.For(0, 64, _ =>
        {
            Assert.Equal(expected.Explain(), decision.Explanation);
            Assert.Equal(expected.Trace.Select(l => l.Text), decision.Trace);
        });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DeferredTraceRetainsTheClassifiedStreamSnapshot()
    {
        var tracks = Slv();
        var selector = NewSelector();
        var eager = Choose(selector, tracks, trace: true);
        var deferred = Choose(selector, tracks, trace: false);
        tracks[1] = tracks[1] with { Language = "ger" };
        tracks[3] = tracks[3] with { IsForced = true };
        Assert.Equal(eager.Trace.Select(TraceKey), deferred.Trace.Select(TraceKey));
        Assert.Equal(eager.Explain(), deferred.Explain());
    }

    [Fact]
    public void ReadingAnExistingEngineBindingDoesNotTakeTheRebuildLock()
    {
        var expected = EngineServices.Current;
        var sync = typeof(EngineServices).GetField("SyncRoot", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var read = new ManualResetEventSlim();
        EngineServices? observed = null;
        var holder = new Thread(() => { lock (sync) { held.Set(); release.Wait(); } });
        var reader = new Thread(() => { observed = EngineServices.Current; read.Set(); });
        holder.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)), "Rebuild-lock fixture did not start.");
            reader.Start();
            Assert.True(read.Wait(TimeSpan.FromSeconds(5)), "Cached Current read waited on the rebuild lock.");
            Assert.Same(expected, observed);
        }
        finally
        {
            release.Set();
            holder.Join();
            if (reader.ThreadState != ThreadState.Unstarted) reader.Join();
        }
    }

    [Fact]
    public void DeferredTraceRetainsTheProfileThatProducedTheDecision()
    {
        var languages = new List<string> { "en" };
        var profile = Starter("en") with { Understood = languages };
        var tracks = new[] { Audio(1, "eng"), Audio(2, "jpn"), Sub(3, "eng") };
        var selector = NewSelector();
        var eager = selector.Decide(tracks, ItemFacts.None, profile, includeTrace: true);
        var deferred = selector.Decide(tracks, ItemFacts.None, profile, includeTrace: false);
        languages[0] = "ja";
        Assert.Equal(eager.AudioIndex, deferred.AudioIndex);
        Assert.Equal(eager.Trace.Select(TraceKey), deferred.Trace.Select(TraceKey));
        Assert.Equal(eager.Explain(), deferred.Explain());
    }
}
