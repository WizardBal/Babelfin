using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Engine.Classification;

namespace Jellyfin.Plugin.Babelfin.Engine.Selection;

/// <summary>One step in an audio chain.</summary>
/// <param name="Kind">The step kind.</param>
/// <param name="Language">For <see cref="AudioStepKind.Language"/>: a language string resolved through the language table ("ja", "yue", "pt-BR", "zh" = any Chinese).</param>
/// <param name="TitleContains">Optional plain text the track's raw title must contain (case-insensitive, no regex). Applies to the Language, OriginalLanguage and LibraryUsualOriginalLanguage steps.</param>
public sealed record AudioStep(AudioStepKind Kind, string? Language = null, string? TitleContains = null);

/// <summary>Facts about the item being played that the selection needs.</summary>
/// <param name="OriginalLanguage">The raw metadata original language ("ja", "jpn", or an OMDb name like "English"); resolved through the language table.</param>
/// <param name="LibraryUsualOriginalLanguage">The library's usual original language (canonical code) or null.</param>
/// <param name="BurnedInLanguages">Languages flagged as burned into the picture; they count as already shown.</param>
/// <param name="IsEpisode">Whether the item is an episode.</param>
public sealed record ItemFacts(
    string? OriginalLanguage,
    string? LibraryUsualOriginalLanguage,
    IReadOnlyList<string> BurnedInLanguages,
    bool IsEpisode = false,
    int? JellyfinAudioIndex = null,
    int? JellyfinSubtitleIndex = null,
    string? JellyfinSubtitleMode = null,
    string? JellyfinSubtitleLanguage = null,
    IReadOnlyList<string>? JellyfinSubtitleLanguages = null)
{
    /// <summary>Gets an item with no known facts.</summary>
    public static ItemFacts None { get; } = new(null, null, Array.Empty<string>());
}

/// <summary>What the requesting app can do, from its device profile when present.</summary>
/// <param name="SupportsTextSubtitles">Whether text subtitles can be shown without burning in; when false, text tracks rank last.</param>
/// <param name="SupportsImageSubtitles">Whether image subtitles can be shown without burning in; when false, image tracks rank last.</param>
/// <param name="CanDirectPlayAudio">Whether an audio track can be direct-played; null = unknown (the <see cref="TieBreak.DirectPlayable"/> tie-break then has no effect).</param>
public sealed record DeviceCapabilities(bool SupportsTextSubtitles = true, bool SupportsImageSubtitles = true, Func<Track, bool>? CanDirectPlayAudio = null);

/// <summary>A remembered manual track change, stored as descriptors rather than indices.</summary>
/// <param name="Audio">The remembered audio track, or null when audio wasn't changed.</param>
/// <param name="SubtitlesOff">Whether the person turned subtitles off by hand.</param>
/// <param name="Subtitle">The remembered subtitle track, or null.</param>
public sealed record ManualOverride(TrackDescriptor? Audio, bool SubtitlesOff, TrackDescriptor? Subtitle);

/// <summary>One line of a decision's explanation trace.</summary>
/// <param name="Code">A stable key for translation, one of <see cref="TraceCodes.All"/>.</param>
/// <param name="Text">The English sentence.</param>
/// <param name="TrackIndex">The stream index this line is about, if any.</param>
/// <param name="Args">Named values used in <paramref name="Text"/> (for translation and for <see cref="Decision.Explain"/>).</param>
public sealed record TraceLine(string Code, string Text, int? TrackIndex = null, IReadOnlyDictionary<string, string>? Args = null);
