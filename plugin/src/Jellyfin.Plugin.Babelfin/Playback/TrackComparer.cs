using System;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Babelfin.Playback;

/// <summary>
/// Describes the tracks of one media source as a remembered change stores them: the streams are mapped with
/// <see cref="StreamMapper"/> and classified with the engine's classifier.
/// </summary>
public static class TrackComparer
{
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
}
