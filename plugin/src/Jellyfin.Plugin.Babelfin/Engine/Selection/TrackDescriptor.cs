using System;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;

namespace Jellyfin.Plugin.Babelfin.Engine.Selection;

/// <summary>
/// What a manual track change remembers: descriptors, not indices, so the choice survives rescans and re-rips.
/// </summary>
/// <param name="Language">The language base ("ja", "zh", "pt"), or null for an unlabelled track.</param>
/// <param name="Variety">The canonical code when the tag is more specific than its base ("yue", "zh-Hant", "pt-BR"), else null.</param>
/// <param name="Kind">"full" / "partial" / "sdh" for subtitles; "normal" / "commentary" / "ad" for audio.</param>
/// <param name="Title">The raw track title.</param>
/// <param name="Codec">The codec.</param>
/// <param name="Channels">The channel count (audio).</param>
public sealed record TrackDescriptor(string? Language, string? Variety, string? Kind, string? Title, string? Codec, int? Channels)
{
    /// <summary>The minimum <see cref="Score"/> for a candidate to count as the same track.</summary>
    public const int MatchThreshold = 5;

    /// <summary>Describes a classified track.</summary>
    /// <param name="track">The track.</param>
    /// <returns>Its descriptor.</returns>
    public static TrackDescriptor From(ClassifiedTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var tag = track.Language;
        return new TrackDescriptor(
            tag?.Base,
            tag is null || tag.IsGeneric ? null : tag.Code,
            KindOf(track),
            track.Track.Title,
            track.Track.Codec,
            track.Track.Channels);
    }

    /// <summary>
    /// Scores how closely a candidate matches this descriptor: language base +3 (two unlabelled tracks also count as the same language),
    /// variety/script exact +1 (only when the base matched; two generic tags are equal), kind +2, title equal case-insensitive +2
    /// (non-empty titles only), codec +1 and channels +1 (only when both are known). A candidate needs at least
    /// <see cref="MatchThreshold"/> to count as the same track.
    /// A remembered language must match on variety, script and region as well as base; an incompatible or unknown language scores 0.
    /// Partial subtitles never match full or SDH subtitles, even when their title and codec agree.
    /// A descriptor with no language (an unlabelled track was remembered) is scored on the other fields as usual.
    /// </summary>
    /// <param name="candidate">The candidate track.</param>
    /// <param name="languages">The active language table, including custom aliases (null = built-in table).</param>
    /// <returns>The score, 0 to 10.</returns>
    public int Score(ClassifiedTrack candidate, LanguageTable? languages = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var other = From(candidate);
        languages ??= LanguageTable.Default;
        LanguageTag? wanted = null;
        if (Language is not null)
        {
            wanted = languages.Resolve(Variety ?? Language);
            if (wanted is null || candidate.Language is null || languages.Match(wanted, candidate.Language) == MatchQuality.None)
            {
                return 0;
            }
        }

        // A remembered soundtrack must not become commentary or audio description just because other tags match.
        if (candidate.Track.Type == TrackType.Audio
            && !string.Equals(Kind ?? "normal", other.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        // Full and SDH cover the same dialogue; neither can stand in for a partial track, or the reverse.
        if (candidate.Track.Type == TrackType.Subtitle && Kind is not null
            && (string.Equals(Kind, "partial", StringComparison.OrdinalIgnoreCase) != (candidate.SubtitleKind == SubtitleKind.Partial)))
        {
            return 0;
        }

        var score = 0;
        if (wanted is not null || (Language is null && other.Language is null))
        {
            score += 3;
            if (string.Equals(wanted?.Code, candidate.Language?.Code, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        if (string.Equals(Kind, other.Kind, StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(Title) && string.Equals(Title.Trim(), other.Title?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(Codec) && string.Equals(Codec, other.Codec, StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        if (Channels is not null && Channels == other.Channels)
        {
            score += 1;
        }

        return score;
    }

    /// <summary>The kind word for a track: "full"/"partial"/"sdh" for subtitles, "normal"/"commentary"/"ad" for audio.</summary>
    /// <param name="track">The track.</param>
    /// <returns>The kind word.</returns>
    internal static string KindOf(ClassifiedTrack track) => track.Track.Type == TrackType.Subtitle
        ? track.SubtitleKind switch
        {
            SubtitleKind.Partial => "partial",
            SubtitleKind.Sdh => "sdh",
            _ => "full",
        }
        : track.AudioKind switch
        {
            AudioKind.Commentary => "commentary",
            AudioKind.AudioDescription => "ad",
            _ => "normal",
        };
}
