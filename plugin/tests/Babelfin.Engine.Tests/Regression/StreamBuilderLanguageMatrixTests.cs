using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Selection;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Babelfin.Engine.Tests.Regression.StreamBuilderSelectionTests;
using static Babelfin.Engine.Tests.Selection.Fx;
using User = Jellyfin.Database.Implementations.Entities.User;

namespace Babelfin.Engine.Tests.Regression;

/// <summary>
/// The StreamBuilder round trip of <see cref="StreamBuilderSelectionTests"/> across languages, varieties, file orders and settings.
/// Babelfin's real <see cref="EngineTrackDecider"/> (only the profile resolver is faked, so the scenario's <see cref="ResolvedProfile"/>
/// is the person's settings) decides from Jellyfin's state, <see cref="BabelfinMediaSourceManager"/> writes the choice, and Jellyfin's
/// real StreamBuilder is asked without request indices, as the Android app's native player does. Nothing in the flag or the scores
/// depends on a language; these scenarios show that across languages. Each scenario also states what StreamBuilder plays when only
/// the indices are written, so every one of them guards the flag, the scores, or both.
/// </summary>
[Collection("StreamBuilder selection globals")]
public sealed class StreamBuilderLanguageMatrixTests : IDisposable
{
    /// <summary>Jellyfin's score for subtitles in the person's Jellyfin subtitle language; same-language tracks tie.</summary>
    private const int Top = 10000;

    /// <summary>Jellyfin's score for subtitles in another language.</summary>
    private const int Low = 1111;

    private static readonly FieldInfo PluginInstance = typeof(Plugin).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly AudioIndexSource[] JellyfinFlags =
    {
        AudioIndexSource.Default,
        AudioIndexSource.Default | AudioIndexSource.Language,
        AudioIndexSource.Language,
        AudioIndexSource.None,
    };

    private static readonly string[] Profiles = { TranscodeOnly, DirectPlay };

    private static readonly Dictionary<string, Scenario> Scenarios = new(StringComparer.Ordinal)
    {
        // ---------- audio: the chosen track is not the default-flagged one ----------
        ["de (language step) over default fr, subs off"] = new(
            Starter(new[] { "de" }, new[] { "de" }).Chain(Lang("de")) with { WhenAudioUnderstood = SubtitleWant.Off },
            null,
            () => new[]
            {
                A(1, "fre", "Français 5.1", isDefault: true),
                A(2, "ger", "Deutsch DTS", codec: "dts"),
                S(3, "fre", "ass", "Français", Low, isDefault: true),
                S(4, "ger", "ass", "Deutsch", Top),
                S(5, "ger", "srt", "Deutsch SDH", Top, hearingImpaired: true),
            },
            Audio: (2, "de"),
            Subtitle: (-1, null, null),
            IndicesOnly: (1, 4)),

        ["es (my languages) over default en, Carteles"] = new(
            Starter(new[] { "es", "en" }, new[] { "es" }),
            null,
            () => new[]
            {
                A(1, "eng", "English 5.1", isDefault: true, codec: "eac3"),
                A(2, "spa", "Español"),
                S(3, "spa", "ass", "Carteles", Top, isDefault: true, isForced: true),
                S(4, "spa", "ass", "Completos", Top),
                S(5, "eng", "srt", "English", Low),
            },
            Audio: (2, "es"),
            Subtitle: (3, "es", SubtitleKind.Partial),
            IndicesOnly: (1, 3)),

        ["en dub (my languages) over default ja original"] = new(
            Starter("en"),
            "ja",
            () => new[]
            {
                A(1, "jpn", "日本語 2.0", isDefault: true, codec: "aac", channels: 2),
                A(2, "eng", "English Dub 5.1"),
                S(3, "eng", "ass", "Signs & Songs", Top, isDefault: true),
                S(4, "eng", "ass", "Dialogue", Top),
            },
            Audio: (2, "en"),
            Subtitle: (3, "en", SubtitleKind.Partial),
            IndicesOnly: (1, 3)),

        ["pt-PT over default pt-BR, pt-PT full subs"] = new(
            Starter(new[] { "pt-PT" }, new[] { "pt-PT" }).Chain(Lang("pt-PT")) with { WhenAudioUnderstood = SubtitleWant.Full },
            null,
            () => new[]
            {
                A(1, "por", "Português (Brasil)", isDefault: true),
                A(2, "por", "Português (Portugal)"),
                S(3, "por", "srt", "Português (Brasil)", Top, isDefault: true),
                S(4, "por", "srt", "Português (Portugal)", Top),
            },
            Audio: (2, "pt-PT"),
            Subtitle: (4, "pt-PT", SubtitleKind.Full),
            IndicesOnly: (1, 3)),

        ["es-ES Castellano over default es-419, subs off"] = new(
            Starter(new[] { "es-ES" }, new[] { "es-ES" }).Chain(Lang("es-ES")) with { WhenAudioUnderstood = SubtitleWant.Off },
            null,
            () => new[]
            {
                A(1, "spa", "Latino", isDefault: true),
                A(2, "spa", "Castellano"),
                S(3, "spa", "srt", "Latino", Top, isDefault: true),
                S(4, "spa", "srt", "Castellano", Top),
            },
            Audio: (2, "es-ES"),
            Subtitle: (-1, null, null),
            IndicesOnly: (1, 3)),

        ["yue over default Mandarin, zh-Hant subs"] = new(
            Starter(new[] { "yue" }, new[] { "zh-Hant" }).Chain(Lang("yue")) with { WhenAudioUnderstood = SubtitleWant.Full },
            null,
            () => new[]
            {
                A(1, "chi", "Mandarin", isDefault: true),
                A(2, "chi", "Cantonese"),
                S(3, "chi", "srt", "简体", Top, isDefault: true),
                S(4, "chi", "srt", "繁體", Top),
            },
            Audio: (2, "yue"),
            Subtitle: (4, "zh-Hant", SubtitleKind.Full),
            IndicesOnly: (1, 3)),

        ["it in the middle of three, Forzati"] = new(
            Starter("it").Chain(Lang("it")),
            null,
            () => new[]
            {
                A(1, "eng", "English", isDefault: true),
                A(2, "ita", "Italiano"),
                A(3, "fre", "Français"),
                S(4, "ita", "ass", "Forzati", Top, isDefault: true, isForced: true),
                S(5, "ita", "ass", "Italiano", Top),
            },
            Audio: (2, "it"),
            Subtitle: (4, "it", SubtitleKind.Partial),
            IndicesOnly: (1, 4)),

        ["de 5.1 last of four, after de stereo, Schilder"] = new(
            Starter(new[] { "de", "en" }, new[] { "de" }),
            null,
            () => new[]
            {
                A(1, "jpn", "日本語", isDefault: true),
                A(2, "eng", "English"),
                A(3, "ger", "Deutsch Stereo", codec: "aac", channels: 2),
                A(4, "ger", "Deutsch 5.1"),
                S(5, "ger", "ass", "Schilder & Songs", Top, isDefault: true, isForced: true),
                S(6, "ger", "ass", "Vollständig", Top),
            },
            Audio: (4, "de"),
            Subtitle: (5, "de", SubtitleKind.Partial),
            IndicesOnly: (1, 5)),

        ["my languages nl (missing) then fr, Forcés"] = new(
            Starter("nl", "fr"),
            null,
            () => new[]
            {
                A(1, "eng", "English", isDefault: true),
                A(2, "fre", "Français"),
                S(3, "fre", "ass", "Forcés", Top, isDefault: true, isForced: true),
                S(4, "fre", "ass", "Complets", Top),
            },
            Audio: (2, "fr"),
            Subtitle: (3, "fr", SubtitleKind.Partial),
            IndicesOnly: (1, 3)),

        ["original fr (metadata), en full over forced+SDH"] = new(
            OriginalAudio("en"),
            "French",
            () => new[]
            {
                A(1, "eng", "English", isDefault: true),
                A(2, "fre", "Français"),
                S(3, "eng", "srt", "Forced", Top, isDefault: true, isForced: true),
                S(4, "eng", "srt", "English", Top),
                S(5, "eng", "srt", "English SDH", Top, hearingImpaired: true),
            },
            Audio: (2, "fr"),
            Subtitle: (4, "en", SubtitleKind.Full),
            IndicesOnly: (1, 3)),

        ["ru over default en, forced by flag only"] = new(
            Starter("ru"),
            null,
            () => new[]
            {
                A(1, "eng", "English", isDefault: true),
                A(2, "rus", "Русский"),
                S(3, "rus", "srt", "Надписи", Top, isForced: true),
                S(4, "rus", "srt", "Русский", Top, isDefault: true),
            },
            Audio: (2, "ru"),
            Subtitle: (3, "ru", SubtitleKind.Partial),
            IndicesOnly: (1, 3)),

        // ---------- audio: the chosen track is the default-flagged one ----------
        ["de chosen and default-flagged, subs off"] = new(
            Starter(new[] { "de" }, new[] { "de" }).Chain(Lang("de")) with { WhenAudioUnderstood = SubtitleWant.Off },
            null,
            () => new[]
            {
                A(1, "eng", "English"),
                A(2, "ger", "Deutsch", isDefault: true),
                A(3, "jpn", "日本語"),
                S(4, "ger", "ass", "Schilder & Songs", Top, isDefault: true, isForced: true),
                S(5, "ger", "ass", "Deutsch", Top),
            },
            Audio: (2, "de"),
            Subtitle: (-1, null, null),
            IndicesOnly: (2, 4)),

        ["ja chosen and default-flagged, en SDH preferred"] = new(
            OriginalAudio("en") with { Sdh = Preference.Prefer },
            "ja",
            () => new[]
            {
                A(1, "jpn", "日本語", isDefault: true),
                A(2, "eng", "English Dub"),
                S(3, "eng", "ass", "Signs & Songs", Top, isDefault: true),
                S(4, "eng", "srt", "English", Top),
                S(5, "eng", "srt", "English SDH", Top, hearingImpaired: true),
            },
            Audio: (1, "ja"),
            Subtitle: (5, "en", SubtitleKind.Sdh),
            IndicesOnly: (1, 3)),

        ["en chosen and default-flagged, always-on SDH"] = new(
            Starter("en") with { PrefersSdh = true },
            null,
            () => new[]
            {
                A(1, "eng", "English", isDefault: true),
                A(2, "jpn", "日本語"),
                S(3, "eng", "ass", "Signs & Songs", Top, isDefault: true),
                S(4, "eng", "srt", "English", Top),
                S(5, "eng", "srt", "English SDH", Top, hearingImpaired: true),
            },
            Audio: (1, "en"),
            Subtitle: (5, "en", SubtitleKind.Sdh),
            IndicesOnly: (1, 3)),

        // ---------- subtitles: full over a default-flagged signs/forced track listed first ----------
        ["de Vollständig over default Schilder & Songs"] = new(
            OriginalAudio("de"),
            "ja",
            () => new[]
            {
                A(1, "ger", "Deutsch", isDefault: true),
                A(2, "jpn", "日本語"),
                S(3, "ger", "ass", "Schilder & Songs", Top, isDefault: true, isForced: true),
                S(4, "ger", "ass", "Vollständig", Top),
                S(5, "eng", "ass", "English", Low),
            },
            Audio: (2, "ja"),
            Subtitle: (4, "de", SubtitleKind.Full),
            IndicesOnly: (1, 3)),

        ["es Completos over default Carteles"] = new(
            OriginalAudio("es"),
            "ja",
            () => new[]
            {
                A(1, "spa", "Español Latino", isDefault: true),
                A(2, "jpn", "日本語"),
                S(3, "spa", "ass", "Carteles", Top, isDefault: true, isForced: true),
                S(4, "spa", "ass", "Completos", Top),
            },
            Audio: (2, "ja"),
            Subtitle: (4, "es", SubtitleKind.Full),
            IndicesOnly: (1, 3)),

        ["fr Complets over default Forcés"] = new(
            OriginalAudio("fr"),
            "ko",
            () => new[]
            {
                A(1, "fre", "VF", isDefault: true),
                A(2, "kor", "한국어"),
                S(3, "fre", "ass", "Forcés", Top, isDefault: true, isForced: true),
                S(4, "fre", "ass", "Complets", Top),
            },
            Audio: (2, "ko"),
            Subtitle: (4, "fr", SubtitleKind.Full),
            IndicesOnly: (1, 3)),

        ["subs off: it understood, over default en"] = new(
            Starter(new[] { "it", "en" }, new[] { "it" }) with { WhenAudioUnderstood = SubtitleWant.Off },
            null,
            () => new[]
            {
                A(1, "eng", "English", isDefault: true),
                A(2, "ita", "Italiano"),
                S(3, "ita", "srt", "Forzati", Top, isDefault: true, isForced: true),
                S(4, "ita", "srt", "Italiano", Top),
            },
            Audio: (2, "it"),
            Subtitle: (-1, null, null),
            IndicesOnly: (1, 3)),

        ["second subtitle language: no nl, de full"] = new(
            Starter(new[] { "nl" }, new[] { "nl", "de" }).Chain(Step(AudioStepKind.OriginalLanguage), Step(AudioStepKind.FileDefault)),
            "ja",
            () => new[]
            {
                A(1, "jpn", "日本語", isDefault: true),
                A(2, "eng", "English"),
                S(3, "eng", "ass", "English", Top, isDefault: true),
                S(4, "ger", "ass", "Schilder & Songs", Top, isForced: true),
                S(5, "ger", "ass", "Vollständig", Top),
            },
            Audio: (1, "ja"),
            Subtitle: (5, "de", SubtitleKind.Full),
            IndicesOnly: (1, 3)),

        // ---------- subtitles: text over image by the PreferText default ----------
        ["es text over default image, Carteles between"] = new(
            OriginalAudio("es"),
            "ja",
            () => new[]
            {
                A(1, "jpn", "日本語", isDefault: true),
                A(2, "spa", "Español"),
                S(3, "spa", "PGSSUB", "Español", Top, isDefault: true),
                S(4, "spa", "ass", "Carteles", Top, isForced: true),
                S(5, "spa", "srt", "Español", Top),
            },
            Audio: (1, "ja"),
            Subtitle: (5, "es", SubtitleKind.Full),
            IndicesOnly: (1, 4)),

        ["zh-Hant text over image, ja over Mandarin"] = new(
            Starter(new[] { "en" }, new[] { "zh-Hant", "en" }).Chain(Step(AudioStepKind.OriginalLanguage), Step(AudioStepKind.FileDefault)),
            "ja",
            () => new[]
            {
                A(1, "chi", "Mandarin", isDefault: true),
                A(2, "jpn", "日本語"),
                S(3, "chi", "PGSSUB", "繁體", Top, isDefault: true),
                S(4, "chi", "srt", "繁體", Top),
                S(5, "chi", "srt", "简体", Top),
            },
            Audio: (2, "ja"),
            Subtitle: (4, "zh-Hant", SubtitleKind.Full),
            IndicesOnly: (1, 4)),
    };

    private readonly Plugin? _previous = Plugin.Instance;

    public StreamBuilderLanguageMatrixTests()
    {
        // No configuration: another test's dry run must not stop the wrapper from writing.
        PluginInstance.SetValue(null, null);
    }

    public void Dispose() => PluginInstance.SetValue(null, _previous);

    public static TheoryData<string, string, AudioIndexSource> Matrix()
    {
        var data = new TheoryData<string, string, AudioIndexSource>();
        foreach (var scenario in Scenarios.Keys)
        {
            foreach (var flags in JellyfinFlags)
            {
                foreach (var profile in Profiles)
                {
                    data.Add(scenario, profile, flags);
                }
            }
        }

        return data;
    }

    /// <summary>Jellyfin's states with "play default audio track" on, which make StreamBuilder fall back to the default-flagged audio.</summary>
    public static TheoryData<string, string, AudioIndexSource> Controls()
    {
        var data = new TheoryData<string, string, AudioIndexSource>();
        foreach (var scenario in Scenarios.Keys)
        {
            foreach (var flags in new[] { AudioIndexSource.Default, AudioIndexSource.Default | AudioIndexSource.Language })
            {
                foreach (var profile in Profiles)
                {
                    data.Add(scenario, profile, flags);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EngineChoiceSurvivesStreamBuilderWithoutRequestIndices(string scenario, string profile, AudioIndexSource jellyfinFlags)
    {
        var s = Scenarios[scenario];
        var (source, decision) = await Applied(s, jellyfinFlags);

        AssertMeaning(s, decision);

        var info = Build(source, profile);
        Assert.Equal(s.Audio.Index, info.AudioStreamIndex);
        Assert.Equal(s.Subtitle.Index, info.SubtitleStreamIndex);

        Assert.Equal(s.Audio.Index, source.DefaultAudioStreamIndex);
        Assert.Equal(AudioIndexSource.User, source.DefaultAudioIndexSource);
        Assert.Equal(s.Subtitle.Index, source.DefaultSubtitleStreamIndex);
    }

    [Theory]
    [MemberData(nameof(Controls))]
    public void IndicesAloneAreOverriddenByStreamBuilder(string scenario, string profile, AudioIndexSource jellyfinFlags)
    {
        // Control: the engine's indices written without the flag and the scores, as the wrapper wrote them before.
        var s = Scenarios[scenario];
        var source = JellyfinSource(s, jellyfinFlags);
        source.DefaultAudioStreamIndex = s.Audio.Index;
        source.DefaultSubtitleStreamIndex = s.Subtitle.Index;

        var info = Build(source, profile);
        Assert.Equal(s.IndicesOnly.Audio, info.AudioStreamIndex);

        // Without External delivery StreamBuilder takes DefaultSubtitleStreamIndex; with it, the first tied track it can deliver.
        Assert.Equal(profile == DirectPlay ? s.IndicesOnly.DirectPlaySubtitle : s.Subtitle.Index, info.SubtitleStreamIndex);
    }

    [Fact]
    public void EveryScenarioGuardsTheFlagOrTheScores()
    {
        Assert.All(Scenarios, pair => Assert.True(
            pair.Value.IndicesOnly.Audio != pair.Value.Audio.Index || pair.Value.IndicesOnly.DirectPlaySubtitle != pair.Value.Subtitle.Index,
            pair.Key));
    }

    /// <summary>The engine's choice is the track the settings ask for: its language (with variety or script), and full, partial or SDH.</summary>
    private static void AssertMeaning(Scenario s, TrackDecision decision)
    {
        var engine = Assert.IsType<Decision>(decision.Engine);
        Fx.Check(engine);
        Assert.Equal(s.Audio.Index, decision.AudioIndex);
        var audio = engine.Tracks.Single(t => t.Track.Index == s.Audio.Index);
        Assert.Equal(TrackType.Audio, audio.Track.Type);
        Assert.Equal(AudioKind.Normal, audio.AudioKind);
        Assert.Equal(s.Audio.Language, audio.Language?.Code);

        Assert.Equal(s.Subtitle.Index, decision.SubtitleIndex);
        if (s.Subtitle.Index < 0)
        {
            Assert.Null(engine.SubtitleIndex);
            Assert.False(engine.SubtitlesWanted);
            return;
        }

        var subtitle = engine.Tracks.Single(t => t.Track.Index == s.Subtitle.Index);
        Assert.Equal(TrackType.Subtitle, subtitle.Track.Type);
        Assert.Equal(s.Subtitle.Language, subtitle.Language?.Code);
        Assert.Equal(s.Subtitle.Kind, subtitle.SubtitleKind);
    }

    private static async Task<(MediaSourceInfo Source, TrackDecision Decision)> Applied(Scenario scenario, AudioIndexSource jellyfinFlags)
    {
        var source = JellyfinSource(scenario, jellyfinFlags);
        var resolved = Ctx.Resolve(Ctx.Person(), null, Ctx.Episode(original: scenario.Original)) with { Profile = scenario.Profile };
        var resolver = CallProxy.Create<IProfileResolver>((method, _) => method.Name == nameof(IProfileResolver.Resolve) ? resolved : null).Proxy;
        var inner = CallProxy.Create<IMediaSourceManager>((method, _) => method.Name == nameof(IMediaSourceManager.GetPlaybackMediaSources)
            ? Task.FromResult<IReadOnlyList<MediaSourceInfo>>(new[] { source })
            : null).Proxy;
        var stats = new DecoratorStats();
        var manager = new BabelfinMediaSourceManager(inner, new EngineTrackDecider(resolver, NullLogger<EngineTrackDecider>.Instance), stats, NullLogger.Instance);
        var user = new User("Test", "Default", "Default") { Id = Guid.NewGuid() };
        var item = new Episode { Id = Guid.NewGuid() };

        var applied = Assert.Single(await manager.GetPlaybackMediaSources(item, user, true, true, CancellationToken.None));

        var record = stats.GetDecision(user.Id, item.Id, applied.Id);
        Assert.NotNull(record);
        Assert.Equal(DecisionOutcome.Applied, record.Outcome);
        return (applied, Assert.IsType<TrackDecision>(record.Decision));
    }

    /// <summary>Jellyfin's state after its own SetDefaultAudioAndSubtitleStreamIndices: the default-flagged audio and subtitle, scored subtitles.</summary>
    private static MediaSourceInfo JellyfinSource(Scenario scenario, AudioIndexSource flags)
    {
        var streams = scenario.Streams();
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        return new MediaSourceInfo
        {
            Id = "source",
            Path = "/media/shows/Show/Season 01/Show - S01E05.mkv",
            Protocol = MediaProtocol.File,
            Container = "mkv",
            RunTimeTicks = TimeSpan.FromMinutes(24).Ticks,
            Bitrate = 8_000_000,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            VideoType = VideoType.VideoFile,
            DefaultAudioStreamIndex = (audio.FirstOrDefault(s => s.IsDefault) ?? audio[0]).Index,
            DefaultSubtitleStreamIndex = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.IsDefault)?.Index ?? -1,
            DefaultAudioIndexSource = flags,
            MediaStreams = new[]
            {
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 6_000_000, IsDefault = true, Profile = "High", Level = 41, BitDepth = 8, PixelFormat = "yuv420p", AverageFrameRate = 23.976f, RealFrameRate = 23.976f },
            }.Concat(streams).ToList(),
        };
    }

    private static MediaStream A(int index, string language, string title, bool isDefault = false, string codec = "ac3", int channels = 6) => new()
    {
        Index = index,
        Type = MediaStreamType.Audio,
        Codec = codec,
        Language = language,
        Title = title,
        Channels = channels,
        ChannelLayout = channels == 6 ? "5.1" : "stereo",
        BitRate = channels == 6 ? 640_000 : 192_000,
        SampleRate = 48000,
        IsDefault = isDefault,
    };

    private static MediaStream S(int index, string language, string codec, string title, int score, bool isDefault = false, bool isForced = false, bool hearingImpaired = false) => new()
    {
        Index = index,
        Type = MediaStreamType.Subtitle,
        Codec = codec,
        Language = language,
        Title = title,
        IsDefault = isDefault,
        IsForced = isForced,
        IsHearingImpaired = hearingImpaired,
        Score = score,
    };

    /// <summary>One file and one person's settings.</summary>
    /// <param name="Profile">The person's resolved settings.</param>
    /// <param name="Original">The title's original language in its metadata, or null.</param>
    /// <param name="Streams">The file's audio and subtitle streams, new on every call (the wrapper changes scores).</param>
    /// <param name="Audio">The engine's audio: the stream and its canonical language.</param>
    /// <param name="Subtitle">The engine's subtitles: the stream (-1 = off), its canonical language and its kind.</param>
    /// <param name="IndicesOnly">What StreamBuilder plays when only the indices are written and "play default audio track" is on: the audio,
    /// and the subtitles for the direct-play profile (the transcode-only profile follows the subtitle index).</param>
    private sealed record Scenario(
        ResolvedProfile Profile,
        string? Original,
        Func<MediaStream[]> Streams,
        (int Index, string Language) Audio,
        (int Index, string? Language, SubtitleKind? Kind) Subtitle,
        (int Audio, int DirectPlaySubtitle) IndicesOnly);
}
