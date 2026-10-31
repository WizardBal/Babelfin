using System.Collections.Generic;

namespace Jellyfin.Plugin.Babelfin.Engine.Selection;

/// <summary>
/// A person's fully resolved settings for one title, with all scopes already merged.
/// Language strings are resolved through the language table at decision time; unknown strings are ignored with a trace line.
/// </summary>
/// <param name="Understood">Spoken languages, ordered.</param>
/// <param name="Reads">Written languages, ordered (the default subtitle languages).</param>
/// <param name="UsesAudioDescription">Prefer audio-description tracks over normal ones of the same language.</param>
/// <param name="PrefersSdh">Prefer SDH, and show full subtitles even when the audio is understood: both "off" and "partial only" become "full".</param>
/// <param name="AudioChain">Ordered audio steps.</param>
/// <param name="AvoidCommentary">Skip commentary tracks unless nothing else exists (default true).</param>
/// <param name="TieBreaks">Same-step audio tie-breaks, in order.</param>
/// <param name="UnlabelledAudio">What unlabelled audio is assumed to be.</param>
/// <param name="UnlabelledAudioLanguage">The language for <see cref="UnlabelledAudioRule.SpecificLanguage"/>.</param>
/// <param name="OriginalSource">Where the original language comes from.</param>
/// <param name="WhenAudioUnderstood">Subtitles when the chosen audio is understood.</param>
/// <param name="WhenAudioNotUnderstood">Subtitles when it isn't.</param>
/// <param name="UnknownAudioCountsAsUnderstood">Whether audio in an unknown language counts as understood (default false).</param>
/// <param name="SubtitleLanguages">Ordered subtitle languages; empty = <paramref name="Reads"/>.</param>
/// <param name="Sdh">SDH preference; <paramref name="PrefersSdh"/> forces <see cref="Preference.Prefer"/>.</param>
/// <param name="Format">Text versus image preference.</param>
/// <param name="Embedded">Embedded versus external preference.</param>
/// <param name="IfNothingMatches">What to show when no wanted full track exists.</param>
/// <param name="FieldSources">Optional provenance per field name ("AudioChain" → "your Anime library rule"), used in the trace and <see cref="Decision.Explain"/>.</param>
public sealed record ResolvedProfile(
    IReadOnlyList<string> Understood,
    IReadOnlyList<string> Reads,
    bool UsesAudioDescription,
    bool PrefersSdh,
    IReadOnlyList<AudioStep> AudioChain,
    bool AvoidCommentary,
    IReadOnlyList<TieBreak> TieBreaks,
    UnlabelledAudioRule UnlabelledAudio,
    string? UnlabelledAudioLanguage,
    OriginalLanguageSource OriginalSource,
    SubtitleWant WhenAudioUnderstood,
    SubtitleWant WhenAudioNotUnderstood,
    bool UnknownAudioCountsAsUnderstood,
    IReadOnlyList<string> SubtitleLanguages,
    Preference Sdh,
    FormatPreference Format,
    EmbeddedPreference Embedded,
    WhenNothingMatches IfNothingMatches,
    IReadOnlyDictionary<string, string>? FieldSources = null)
{
    /// <summary>Gets the default tie-break order: direct-playable, more channels, lossless, original flag, default flag, lowest index.</summary>
    public static IReadOnlyList<TieBreak> DefaultTieBreaks { get; } = new[]
    {
        TieBreak.DirectPlayable,
        TieBreak.MoreChannels,
        TieBreak.Lossless,
        TieBreak.IsOriginalFlag,
        TieBreak.DefaultFlag,
        TieBreak.LowestIndex,
    };

}
