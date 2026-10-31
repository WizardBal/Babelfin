using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>Jellyfin's streams become engine tracks (audio and subtitles only, raw fields, external file name only).</summary>
public class StreamMapperTests
{
    private static MediaStream Audio(int index, string? language, bool isDefault = false) => new()
    {
        Index = index,
        Type = MediaStreamType.Audio,
        Language = language,
        Codec = "aac",
        Channels = 6,
        IsDefault = isDefault,
        Title = "Surround",
        Comment = "Original mix",
        Path = "/media/anime/Show/episode.mkv",
    };

    private static MediaStream Sub(int index, string codec, bool external = false, string? path = null) => new()
    {
        Index = index,
        Type = MediaStreamType.Subtitle,
        Language = "eng",
        Codec = codec,
        IsExternal = external,
        Path = path,
    };

    [Fact]
    public void OnlyAudioAndSubtitleStreams_InOrder()
    {
        var streams = new[]
        {
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264" },
            Audio(1, "jpn"),
            Sub(2, "subrip"),
            new MediaStream { Index = 3, Type = MediaStreamType.EmbeddedImage, Codec = "png" },
            new MediaStream { Index = 4, Type = MediaStreamType.Data },
        };

        var tracks = StreamMapper.Map(streams);

        Assert.Equal(new[] { 1, 2 }, System.Linq.Enumerable.Select(tracks, t => t.Index));
        Assert.Equal(TrackType.Audio, tracks[0].Type);
        Assert.Equal(TrackType.Subtitle, tracks[1].Type);
    }

    [Fact]
    public void Null_IsEmpty()
    {
        Assert.Empty(StreamMapper.Map((MediaStream[]?)null));
    }

    [Fact]
    public void Flags_AndRawFields_AreCopied()
    {
        var stream = Audio(1, "jpn", isDefault: true);
        stream.IsForced = true;
        stream.IsHearingImpaired = true;
        stream.IsOriginal = true;

        var t = StreamMapper.Map(stream);

        Assert.Equal(1, t.Index);
        Assert.Equal("jpn", t.Language);
        Assert.Equal("Surround", t.Title);
        Assert.Equal("Original mix", t.Comment);
        Assert.Equal("aac", t.Codec);
        Assert.Equal(6, t.Channels);
        Assert.True(t.IsDefault);
        Assert.True(t.IsForced);
        Assert.True(t.IsHearingImpaired);
        Assert.True(t.IsOriginal);
        Assert.False(t.IsExternal);
        Assert.Equal(SubtitleFormat.Unknown, t.Format);
    }

    [Fact]
    public void EmbeddedStream_HasNoFileName()
    {
        var t = StreamMapper.Map(Audio(1, "jpn"));

        Assert.Null(t.FileName);
    }

    [Fact]
    public void ExternalStream_KeepsOnlyTheSafeSuffix()
    {
        var t = StreamMapper.Map(Sub(5, "subrip", external: true, path: "/media/anime/Show (2015)/Season 1/Show S01E01.en.forced.srt"), "/media/anime/Show (2015)/Season 1/Show S01E01.mkv");

        Assert.True(t.IsExternal);
        Assert.Equal(".en.forced.srt", t.FileName);
    }

    [Fact]
    public void ExternalStream_WindowsPath_KeepsOnlyTheSafeSuffix()
    {
        var t = StreamMapper.Map(Sub(5, "ass", external: true, path: @"D:\Media\Show\Show.ja.ass"), @"D:\Media\Show\Show.mkv");

        Assert.Equal(".ja.ass", t.FileName);
    }

    [Theory]
    [InlineData("subrip", SubtitleFormat.Text)]
    [InlineData("srt", SubtitleFormat.Text)]
    [InlineData("ass", SubtitleFormat.Text)]
    [InlineData("webvtt", SubtitleFormat.Text)]
    [InlineData("mov_text", SubtitleFormat.Text)]
    [InlineData("pgssub", SubtitleFormat.Image)]
    [InlineData("hdmv_pgs_subtitle", SubtitleFormat.Image)]
    [InlineData("PGSSUB", SubtitleFormat.Image)]
    [InlineData("dvdsub", SubtitleFormat.Image)]
    [InlineData("dvbsub", SubtitleFormat.Image)]
    [InlineData("dvb_subtitle", SubtitleFormat.Image)]
    [InlineData("xsub", SubtitleFormat.Image)]
    [InlineData("vobsub", SubtitleFormat.Image)]
    [InlineData("something_new", SubtitleFormat.Text)]
    public void SubtitleFormat_FromJellyfinFlagsOrCodec(string codec, SubtitleFormat expected)
    {
        Assert.Equal(expected, StreamMapper.Map(Sub(3, codec)).Format);
    }

    [Fact]
    public void SubtitleFormat_NoCodec_IsUnknown()
    {
        var stream = Sub(3, "x");
        stream.Codec = null;

        Assert.Equal(SubtitleFormat.Unknown, StreamMapper.Map(stream).Format);
    }

    [Fact]
    public void DtsHdMa_IsLossless()
    {
        var stream = Audio(1, "eng");
        stream.Codec = "dts";
        stream.Profile = "DTS-HD MA";

        Assert.True(StreamMapper.Map(stream).IsLossless);
        Assert.False(StreamMapper.Map(Audio(2, "eng")).IsLossless);
    }
}
