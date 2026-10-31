using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Library;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[CollectionDefinition("StreamBuilder selection globals", DisableParallelization = true)]
public sealed class StreamBuilderSelectionCollection { }

/// <summary>
/// Runs what <see cref="BabelfinMediaSourceManager"/> writes through Jellyfin's real <see cref="StreamBuilder"/>, the way
/// Jellyfin's MediaInfoHelper does for a PlaybackInfo request. The file matches the reported one: audio 1 English (default-flagged),
/// audio 2 Japanese, subtitles 3 PGS "Full Sub", 4 ASS "Signs &amp; Songs" (default-flagged) and 5 ASS "Dialogue", all English
/// and equally scored. Babelfin chooses Japanese audio and the dialogue subtitles. The Jellyfin Android app's native player asks
/// for the next episode without indices, so StreamBuilder's own re-selection is what decides unless the source prevents it.
/// </summary>
[Collection("StreamBuilder selection globals")]
public sealed class StreamBuilderSelectionTests : IDisposable
{
    private const int JellyfinScore = 10000;
    private static readonly FieldInfo PluginInstance = typeof(Plugin).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly Plugin? _previous = Plugin.Instance;

    public StreamBuilderSelectionTests()
    {
        // No configuration: another test's dry run must not stop the wrapper from writing.
        PluginInstance.SetValue(null, null);
    }

    public void Dispose() => PluginInstance.SetValue(null, _previous);

    public static TheoryData<AudioIndexSource, string> JellyfinStates()
    {
        var data = new TheoryData<AudioIndexSource, string>();
        foreach (var flags in new[] { AudioIndexSource.Default, AudioIndexSource.Default | AudioIndexSource.Language, AudioIndexSource.None })
        {
            foreach (var profile in new[] { TranscodeOnly, DirectPlay })
            {
                data.Add(flags, profile);
            }
        }

        return data;
    }

    private const string TranscodeOnly = "transcode-only";
    private const string DirectPlay = "direct-play";

    [Theory]
    [MemberData(nameof(JellyfinStates))]
    public async Task ChosenAudioAndSubtitleSurviveStreamBuilderWithoutRequestIndices(AudioIndexSource jellyfinFlags, string profile)
    {
        var source = await Applied(new TrackDecision(2, 5, Array.Empty<string>()), jellyfinFlags);

        Assert.Equal(2, source.DefaultAudioStreamIndex);
        Assert.Equal(AudioIndexSource.User, source.DefaultAudioIndexSource);
        Assert.Equal(5, source.DefaultSubtitleStreamIndex);
        Assert.Equal(JellyfinScore + 1, Stream(source, 5).Score);
        Assert.Equal(JellyfinScore, Stream(source, 3).Score);
        Assert.Equal(JellyfinScore, Stream(source, 4).Score);

        var info = Build(source, profile);
        Assert.Equal(profile == DirectPlay ? PlayMethod.DirectPlay : PlayMethod.Transcode, info.PlayMethod);
        Assert.Equal(2, info.AudioStreamIndex);
        Assert.Equal(5, info.SubtitleStreamIndex);
    }

    [Theory]
    [MemberData(nameof(JellyfinStates))]
    public void IndicesAloneAreOverriddenByStreamBuilder(AudioIndexSource jellyfinFlags, string profile)
    {
        // Control: the same indices written without the flag and the scores, as the wrapper wrote them before.
        var source = JellyfinSource(jellyfinFlags);
        source.DefaultAudioStreamIndex = 2;
        source.DefaultSubtitleStreamIndex = 5;

        var info = Build(source, profile);
        Assert.Equal(1, info.AudioStreamIndex);

        // With External ass delivery, the first equally scored track an External profile matches wins: Signs & Songs.
        Assert.Equal(profile == DirectPlay ? 4 : 5, info.SubtitleStreamIndex);
    }

    [Theory]
    [InlineData(TranscodeOnly)]
    [InlineData(DirectPlay)]
    public async Task SubtitlesOffSurviveStreamBuilderWithoutRequestIndices(string profile)
    {
        var source = await Applied(new TrackDecision(2, -1, Array.Empty<string>()), AudioIndexSource.Default);

        Assert.Equal(-1, source.DefaultSubtitleStreamIndex);
        Assert.Equal(JellyfinScore + 1, Stream(source, 3).Score);
        Assert.Equal(JellyfinScore, Stream(source, 4).Score);
        Assert.Equal(JellyfinScore, Stream(source, 5).Score);

        var info = Build(source, profile);
        Assert.Equal(2, info.AudioStreamIndex);
        Assert.Equal(-1, info.SubtitleStreamIndex);
    }

    [Fact]
    public void SubtitlesOffAloneAreOverriddenByStreamBuilder()
    {
        // Control: off written without breaking the tie.
        var source = JellyfinSource(AudioIndexSource.Default);
        source.DefaultSubtitleStreamIndex = -1;

        Assert.Equal(4, Build(source, DirectPlay).SubtitleStreamIndex);
    }

    [Theory]
    [InlineData(TranscodeOnly)]
    [InlineData(DirectPlay)]
    public async Task AudioLeftToTheVideoKeepsJellyfinsReselection(string profile)
    {
        // Jellyfin chose Japanese from a language preference with "play default audio track" on. Babelfin leaves audio alone,
        // so StreamBuilder's own re-selection (default-flagged streams, here English) still applies.
        var jellyfinFlags = AudioIndexSource.Default | AudioIndexSource.Language;
        var source = await Applied(new TrackDecision(null, 5, Array.Empty<string>()), jellyfinFlags, jellyfinAudio: 2);

        Assert.Equal(2, source.DefaultAudioStreamIndex);
        Assert.Equal(jellyfinFlags, source.DefaultAudioIndexSource);

        var info = Build(source, profile);
        Assert.Equal(1, info.AudioStreamIndex);
        Assert.Equal(5, info.SubtitleStreamIndex);
    }

    [Theory]
    [InlineData(TranscodeOnly)]
    [InlineData(DirectPlay)]
    public async Task SubtitlesLeftToTheVideoKeepJellyfinsScores(string profile)
    {
        var source = await Applied(new TrackDecision(2, null, Array.Empty<string>()), AudioIndexSource.Default);

        Assert.Equal(4, source.DefaultSubtitleStreamIndex);
        Assert.All(new[] { 3, 4, 5 }, index => Assert.Equal(JellyfinScore, Stream(source, index).Score));

        var info = Build(source, profile);
        Assert.Equal(2, info.AudioStreamIndex);
        Assert.Equal(4, info.SubtitleStreamIndex);
    }

    [Theory]
    [InlineData(TranscodeOnly)]
    [InlineData(DirectPlay)]
    public async Task RequestIndicesStillWinOverTheFlagAndTheScores(string profile)
    {
        // An app that names its tracks (jellyfin-web always does) gets exactly those.
        var source = await Applied(new TrackDecision(2, 5, Array.Empty<string>()), AudioIndexSource.Default);

        var info = Build(source, profile, audio: 1, subtitle: 4);
        Assert.Equal(1, info.AudioStreamIndex);
        Assert.Equal(4, info.SubtitleStreamIndex);

        var off = Build(source, profile, audio: 2, subtitle: -1);
        Assert.Equal(2, off.AudioStreamIndex);
        Assert.Equal(-1, off.SubtitleStreamIndex);
    }

    [Fact]
    public async Task ApplyingTwiceKeepsOneScorePointAndTheFlag()
    {
        var source = await Applied(new TrackDecision(2, 5, Array.Empty<string>()), AudioIndexSource.Default);
        var manager = Manager(new TrackDecision(2, 5, Array.Empty<string>()), source);

        manager.SetDefaultAudioAndSubtitleStreamIndices(new Episode(), source, NewUser());

        Assert.Equal(AudioIndexSource.User, source.DefaultAudioIndexSource);
        Assert.Equal(JellyfinScore + 1, Stream(source, 5).Score);
        Assert.Equal(JellyfinScore, Stream(source, 4).Score);
    }

    [Fact]
    public async Task UnscoredSubtitlesGiveTheChosenStreamTheOnlyScore()
    {
        var source = JellyfinSource(AudioIndexSource.Default);
        foreach (var stream in source.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle))
        {
            stream.Score = null;
        }

        await Manager(new TrackDecision(2, 5, Array.Empty<string>()), source).GetPlaybackMediaSources(new Episode(), NewUser(), true, true, CancellationToken.None);

        Assert.Equal(1, Stream(source, 5).Score);
        Assert.Null(Stream(source, 3).Score);
        Assert.Null(Stream(source, 4).Score);
        Assert.Equal(5, Build(source, DirectPlay).SubtitleStreamIndex);
    }

    [Fact]
    public void ScoresAreUntouchedWhenStreamBuilderAlreadyResolvesTheChoice()
    {
        var source = JellyfinSource(AudioIndexSource.Default);
        Stream(source, 5).Score = JellyfinScore + 7;
        List<(MediaStream Stream, int? Score)>? changed = null;
        BabelfinMediaSourceManager.SettleSubtitleScores(source, 5, ref changed);
        Assert.Null(changed);

        // Off with a unique highest score: StreamBuilder already falls back to -1.
        BabelfinMediaSourceManager.SettleSubtitleScores(source, -1, ref changed);
        Assert.Null(changed);
        Assert.Equal(JellyfinScore + 7, Stream(source, 5).Score);
    }

    [Fact]
    public void RestoringScoresReturnsJellyfinsValues()
    {
        var source = JellyfinSource(AudioIndexSource.Default);
        Stream(source, 5).Score = null;
        List<(MediaStream Stream, int? Score)>? changed = null;

        BabelfinMediaSourceManager.SettleSubtitleScores(source, 5, ref changed);
        Assert.Equal(JellyfinScore + 1, Stream(source, 5).Score);
        BabelfinMediaSourceManager.RestoreScores(changed);
        Assert.Null(Stream(source, 5).Score);

        changed = null;
        BabelfinMediaSourceManager.SettleSubtitleScores(source, -1, ref changed);
        Assert.Equal(JellyfinScore + 1, Stream(source, 3).Score);
        BabelfinMediaSourceManager.RestoreScores(changed);
        Assert.All(new[] { 3, 4 }, index => Assert.Equal(JellyfinScore, Stream(source, index).Score));
    }

    [Fact]
    public async Task AnErrorLeavesTheSourceAsJellyfinMadeIt()
    {
        var jellyfinFlags = AudioIndexSource.Default | AudioIndexSource.Language;
        var source = JellyfinSource(jellyfinFlags);
        source.MediaStreams = new ThrowingStreams(source.MediaStreams);
        var stats = new DecoratorStats();
        var user = NewUser();
        var item = new Episode { Id = Guid.NewGuid() };

        await Manager(new TrackDecision(2, 5, Array.Empty<string>()), source, stats).GetPlaybackMediaSources(item, user, true, true, CancellationToken.None);

        Assert.Equal(1, source.DefaultAudioStreamIndex);
        Assert.Equal(4, source.DefaultSubtitleStreamIndex);
        Assert.Equal(jellyfinFlags, source.DefaultAudioIndexSource);
        Assert.Equal(1, stats.Errors);
        Assert.Equal(DecisionOutcome.PassedThrough, stats.GetDecision(user.Id, item.Id, source.Id)!.Outcome);
    }

    private static async Task<MediaSourceInfo> Applied(TrackDecision decision, AudioIndexSource jellyfinFlags, int jellyfinAudio = 1)
    {
        var source = JellyfinSource(jellyfinFlags);
        source.DefaultAudioStreamIndex = jellyfinAudio;
        var sources = await Manager(decision, source).GetPlaybackMediaSources(new Episode(), NewUser(), true, true, CancellationToken.None);
        return Assert.Single(sources);
    }

    private static BabelfinMediaSourceManager Manager(TrackDecision decision, MediaSourceInfo source, DecoratorStats? stats = null)
    {
        var inner = CallProxy.Create<IMediaSourceManager>((method, _) => method.Name == nameof(IMediaSourceManager.GetPlaybackMediaSources)
            ? Task.FromResult<IReadOnlyList<MediaSourceInfo>>(new[] { source })
            : null).Proxy;
        var decider = CallProxy.Create<ITrackDecider>((_, _) => decision).Proxy;
        return new BabelfinMediaSourceManager(inner, decider, stats ?? new DecoratorStats(), NullLogger.Instance);
    }

    /// <summary>Jellyfin's state after its own SetDefaultAudioAndSubtitleStreamIndices: English audio, equally scored English subtitles.</summary>
    private static MediaSourceInfo JellyfinSource(AudioIndexSource flags) => new()
    {
        Id = "source",
        Path = "/media/anime/Show/Season 01/Show - S01E02.mkv",
        Protocol = MediaProtocol.File,
        Container = "mkv",
        RunTimeTicks = TimeSpan.FromMinutes(24).Ticks,
        Bitrate = 8_000_000,
        SupportsDirectPlay = true,
        SupportsDirectStream = true,
        SupportsTranscoding = true,
        VideoType = VideoType.VideoFile,
        DefaultAudioStreamIndex = 1,
        DefaultSubtitleStreamIndex = 4,
        DefaultAudioIndexSource = flags,
        MediaStreams = new List<MediaStream>
        {
            new() { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 6_000_000, IsDefault = true, Profile = "High", Level = 41, BitDepth = 8, PixelFormat = "yuv420p", AverageFrameRate = 23.976f, RealFrameRate = 23.976f },
            new() { Index = 1, Type = MediaStreamType.Audio, Codec = "ac3", Language = "eng", Channels = 6, ChannelLayout = "5.1", BitRate = 640_000, SampleRate = 48000, IsDefault = true },
            new() { Index = 2, Type = MediaStreamType.Audio, Codec = "ac3", Language = "jpn", Channels = 6, ChannelLayout = "5.1", BitRate = 640_000, SampleRate = 48000 },
            new() { Index = 3, Type = MediaStreamType.Subtitle, Codec = "PGSSUB", Language = "eng", Title = "Full Sub", Score = JellyfinScore },
            new() { Index = 4, Type = MediaStreamType.Subtitle, Codec = "ass", Language = "eng", Title = "Signs & Songs", IsDefault = true, Score = JellyfinScore },
            new() { Index = 5, Type = MediaStreamType.Subtitle, Codec = "ass", Language = "eng", Title = "Dialogue", Score = JellyfinScore },
        },
    };

    /// <summary>Calls Jellyfin's StreamBuilder as MediaInfoHelper.SetDeviceSpecificData does; null indices mean the request named none.</summary>
    private static StreamInfo Build(MediaSourceInfo source, string profile, int? audio = null, int? subtitle = null)
    {
        var transcoder = CallProxy.Create<ITranscoderSupport>((method, _) => method.ReturnType == typeof(bool) ? true : null).Proxy;
        var builder = new StreamBuilder(transcoder, NullLogger.Instance);
        var info = builder.GetOptimalVideoStream(new MediaOptions
        {
            ItemId = Guid.NewGuid(),
            MediaSourceId = source.Id,
            MediaSources = new[] { source },
            Profile = profile == DirectPlay ? DirectPlayProfile() : TranscodeOnlyProfile(),
            DeviceId = "device",
            MaxBitrate = 200_000_000,
            AllowVideoStreamCopy = true,
            AllowAudioStreamCopy = true,
            EnableDirectPlay = true,
            EnableDirectStream = true,
            AudioStreamIndex = audio,
            SubtitleStreamIndex = subtitle,
        });
        return Assert.IsType<StreamInfo>(info);
    }

    /// <summary>No direct play; HLS transcoding that can copy ac3 and burns subtitles in (the Android app's native player case).</summary>
    private static DeviceProfile TranscodeOnlyProfile() => new()
    {
        Name = TranscodeOnly,
        DirectPlayProfiles = Array.Empty<DirectPlayProfile>(),
        TranscodingProfiles = new[]
        {
            new TranscodingProfile { Container = "ts", Type = DlnaProfileType.Video, VideoCodec = "h264", AudioCodec = "aac,ac3,eac3,mp3", Protocol = MediaStreamProtocol.hls, Context = EncodingContext.Streaming, MaxAudioChannels = "6" },
        },
        SubtitleProfiles = new[]
        {
            new SubtitleProfile { Format = "pgssub", Method = SubtitleDeliveryMethod.Encode },
            new SubtitleProfile { Format = "ass", Method = SubtitleDeliveryMethod.Encode },
        },
        CodecProfiles = Array.Empty<CodecProfile>(),
        ContainerProfiles = Array.Empty<ContainerProfile>(),
    };

    /// <summary>Direct play of mkv with ac3, and ass/srt delivered as external files.</summary>
    private static DeviceProfile DirectPlayProfile() => new()
    {
        Name = DirectPlay,
        DirectPlayProfiles = new[]
        {
            new DirectPlayProfile { Container = "mkv", Type = DlnaProfileType.Video, VideoCodec = "h264,hevc", AudioCodec = "aac,ac3,eac3" },
        },
        TranscodingProfiles = new[]
        {
            new TranscodingProfile { Container = "ts", Type = DlnaProfileType.Video, VideoCodec = "h264", AudioCodec = "aac,ac3", Protocol = MediaStreamProtocol.hls, Context = EncodingContext.Streaming },
        },
        SubtitleProfiles = new[]
        {
            new SubtitleProfile { Format = "ass", Method = SubtitleDeliveryMethod.External },
            new SubtitleProfile { Format = "srt", Method = SubtitleDeliveryMethod.External },
            new SubtitleProfile { Format = "pgssub", Method = SubtitleDeliveryMethod.Encode },
        },
        CodecProfiles = Array.Empty<CodecProfile>(),
        ContainerProfiles = Array.Empty<ContainerProfile>(),
    };

    private static MediaStream Stream(MediaSourceInfo source, int index) => source.MediaStreams.Single(s => s.Index == index);

    private static User NewUser() => new("Test", "Default", "Default") { Id = Guid.NewGuid() };

    /// <summary>Streams whose enumeration fails, after the audio has been written.</summary>
    private sealed class ThrowingStreams : IReadOnlyList<MediaStream>
    {
        private readonly IReadOnlyList<MediaStream> _inner;

        public ThrowingStreams(IReadOnlyList<MediaStream> inner) => _inner = inner;

        public int Count => _inner.Count;

        public MediaStream this[int index] => _inner[index];

        public IEnumerator<MediaStream> GetEnumerator() => throw new InvalidOperationException("Test stream failure");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
