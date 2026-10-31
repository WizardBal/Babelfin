using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Babelfin.Decisions;

/// <summary>
/// Turns Jellyfin's <see cref="MediaStream"/> list into the engine's <see cref="Track"/> list. Audio and subtitle streams only;
/// the raw <see cref="MediaStream.Title"/> and <see cref="MediaStream.Comment"/> are used, never the localized DisplayTitle.
/// Only an external subtitle's suffix after the video's base name becomes <see cref="Track.FileName"/> for classification.
/// Without a matching video name, no file-name hints are used. The external path is retained separately for cache invalidation.
/// Paths are never shown: the engine's trace and explanation don't print them, and the API never serializes <see cref="Track"/>.
/// </summary>
public static class StreamMapper
{
    private static readonly string[] ImageCodecs = { "pgssub", "hdmv_pgs_subtitle", "dvdsub", "dvbsub", "dvb_subtitle", "xsub", "vobsub" };

    /// <summary>Maps a media source's streams.</summary>
    /// <param name="streams">The streams (null = none).</param>
    /// <param name="mediaPath">The video path used to isolate external subtitle suffixes (null = no filename hints).</param>
    /// <returns>The engine tracks, in the source's order.</returns>
    public static IReadOnlyList<Track> Map(IEnumerable<MediaStream>? streams, string? mediaPath = null)
    {
        if (streams is null)
        {
            return Array.Empty<Track>();
        }

        var tracks = new List<Track>();
        foreach (var stream in streams)
        {
            if (stream is not null && (stream.Type == MediaStreamType.Audio || stream.Type == MediaStreamType.Subtitle))
            {
                tracks.Add(Map(stream, mediaPath));
            }
        }

        return tracks;
    }

    /// <summary>Maps one audio or subtitle stream.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="mediaPath">The video path used to isolate external subtitle suffixes (null = no filename hints).</param>
    /// <returns>The engine track.</returns>
    public static Track Map(MediaStream stream, string? mediaPath = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var isAudio = stream.Type == MediaStreamType.Audio;
        return new Track(
            stream.Index,
            isAudio ? TrackType.Audio : TrackType.Subtitle,
            stream.Language,
            stream.Title,
            stream.Comment,
            stream.IsExternal && !isAudio ? ExternalSubtitleSuffix(stream.Path, mediaPath) : null,
            stream.Codec,
            stream.Channels,
            stream.IsDefault,
            stream.IsForced,
            stream.IsHearingImpaired,
            stream.IsOriginal,
            stream.IsExternal,
            isAudio ? SubtitleFormat.Unknown : FormatOf(stream),
            isAudio && IsLosslessProfile(stream.Profile),
            ExternalPath: stream.IsExternal ? stream.Path : null);
    }

    /// <summary>The subtitle format: image for PGS, VobSub and the known bitmap codecs; else text when Jellyfin says it's a text stream; else unknown.</summary>
    /// <param name="stream">A subtitle stream.</param>
    /// <returns>The format.</returns>
    public static SubtitleFormat FormatOf(MediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Image first: Jellyfin 12's IsTextSubtitleStream is true for every codec outside its own short bitmap list (dvb_subtitle and xsub included).
        if (stream.IsPgsSubtitleStream || stream.IsVobSubSubtitleStream)
        {
            return SubtitleFormat.Image;
        }

        var codec = stream.Codec?.Trim();
        if (!string.IsNullOrEmpty(codec))
        {
            foreach (var image in ImageCodecs)
            {
                if (string.Equals(image, codec, StringComparison.OrdinalIgnoreCase))
                {
                    return SubtitleFormat.Image;
                }
            }
        }

        return stream.IsTextSubtitleStream ? SubtitleFormat.Text : SubtitleFormat.Unknown;
    }

    // Match the complete video stem and a dot boundary, never just a title prefix ("Movie" versus "Movie 2").
    private static string? ExternalSubtitleSuffix(string? subtitlePath, string? mediaPath)
    {
        var video = TextMatcher.FileNameOnly(mediaPath);
        var subtitle = TextMatcher.FileNameOnly(subtitlePath);
        if (string.IsNullOrEmpty(video) || string.IsNullOrEmpty(subtitle))
        {
            return null;
        }

        var extension = video.LastIndexOf('.');
        var stem = extension > 0 ? video.Substring(0, extension) : video;
        if (subtitle.Length <= stem.Length || subtitle[stem.Length] != '.'
            || !subtitle.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Keep the leading dot and extension: language hint parsing reads only the intervening suffix segments.
        return subtitle.Substring(stem.Length);
    }

    // Jellyfin reports DTS-HD Master Audio as codec "dts" with profile "DTS-HD MA"; the engine's codec list covers the rest.
    private static bool IsLosslessProfile(string? profile) =>
        profile is not null && profile.Trim().Equals("DTS-HD MA", StringComparison.OrdinalIgnoreCase);
}
