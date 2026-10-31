using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Babelfin.Playback;

/// <summary>
/// Tells whether two track choices of one media source "disagree on language or kind" (replace on autoplay):
/// the streams are mapped with <see cref="StreamMapper"/> and classified with the engine's classifier.
/// Explicit language, variety, script, region and kind conflicts disagree; generic language axes remain compatible.
/// </summary>
/// <remarks>
/// Behavior: a null audio index on either side means "not specified" and never disagrees;
/// a subtitle index that is null or negative means "off" (off and off agree, off and a track disagree); an index that names
/// no audio or subtitle stream of the source disagrees with any real track (Babelfin's own choice is then the safer one);
/// two unlabelled tracks share a language (as in <see cref="TrackDescriptor.Score"/>).
/// </remarks>
public static class TrackComparer
{
    /// <summary>Whether two choices disagree on language or kind, for audio or for subtitles.</summary>
    /// <param name="source">The media source both choices index into.</param>
    /// <param name="audioA">The first choice's audio index.</param>
    /// <param name="subtitleA">The first choice's subtitle index (null or -1 = off).</param>
    /// <param name="audioB">The second choice's audio index.</param>
    /// <param name="subtitleB">The second choice's subtitle index (null or -1 = off).</param>
    /// <param name="classifier">The classifier (null = the engine's current one).</param>
    /// <returns>True when they disagree.</returns>
    public static bool Disagree(MediaSourceInfo source, int? audioA, int? subtitleA, int? audioB, int? subtitleB, TrackClassifier? classifier = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        classifier ??= EngineServices.Current.Classifier;
        var tracks = new Dictionary<int, Track>();
        foreach (var track in StreamMapper.Map(source.MediaStreams, source.Path))
        {
            tracks.TryAdd(track.Index, track);
        }

        return AudioDisagrees(tracks, classifier, audioA, audioB) || SubtitlesDisagree(tracks, classifier, subtitleA, subtitleB);
    }

    /// <summary>Describes one audio or subtitle stream of a source.</summary>
    /// <param name="source">The media source.</param>
    /// <param name="index">The stream index.</param>
    /// <param name="type">The expected stream type.</param>
    /// <param name="classifier">The classifier (null = the engine's current one).</param>
    /// <returns>The descriptor, or null when the index names no such stream.</returns>
    public static TrackDescriptor? Describe(MediaSourceInfo source, int? index, TrackType type, TrackClassifier? classifier = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (index is not { } wanted || wanted < 0)
        {
            return null;
        }

        classifier ??= EngineServices.Current.Classifier;
        foreach (var track in StreamMapper.Map(source.MediaStreams, source.Path))
        {
            if (track.Index == wanted && track.Type == type)
            {
                return TrackDescriptor.From(classifier.Classify(track));
            }
        }

        return null;
    }

    private static bool AudioDisagrees(Dictionary<int, Track> tracks, TrackClassifier classifier, int? a, int? b)
    {
        if (a is null || b is null || a == b)
        {
            return false;
        }

        var da = DescribeIn(tracks, classifier, a.Value, TrackType.Audio);
        var db = DescribeIn(tracks, classifier, b.Value, TrackType.Audio);
        return !Same(da, db);
    }

    private static bool SubtitlesDisagree(Dictionary<int, Track> tracks, TrackClassifier classifier, int? a, int? b)
    {
        var offA = a is null || a < 0;
        var offB = b is null || b < 0;
        if (offA && offB)
        {
            return false;
        }

        if (offA != offB)
        {
            return true;
        }

        if (a == b)
        {
            return false;
        }

        var da = DescribeIn(tracks, classifier, a!.Value, TrackType.Subtitle);
        var db = DescribeIn(tracks, classifier, b!.Value, TrackType.Subtitle);
        return !Same(da, db);
    }

    private static ClassifiedTrack? DescribeIn(Dictionary<int, Track> tracks, TrackClassifier classifier, int index, TrackType type)
        => tracks.TryGetValue(index, out var track) && track.Type == type ? classifier.Classify(track) : null;

    private static bool Same(ClassifiedTrack? a, ClassifiedTrack? b)
    {
        if (a is null || b is null)
        {
            // An index that names no stream of this kind never agrees with anything.
            return false;
        }

        if (!string.Equals(TrackDescriptor.KindOf(a), TrackDescriptor.KindOf(b), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var first = a.Language;
        var second = b.Language;
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        // Compare the resolved axes directly, preserving custom-table tags and generic-to-specific compatibility.
        return string.Equals(first.Base, second.Base, StringComparison.OrdinalIgnoreCase)
            && CompatibleAxis(first.Variety, second.Variety)
            && CompatibleAxis(first.Script, second.Script)
            && CompatibleAxis(first.Region, second.Region);
    }

    private static bool CompatibleAxis(string? first, string? second) =>
        first is null || second is null || string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
