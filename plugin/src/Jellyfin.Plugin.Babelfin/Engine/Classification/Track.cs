using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Engine.Languages;

namespace Jellyfin.Plugin.Babelfin.Engine.Classification;

/// <summary>The kind of stream.</summary>
public enum TrackType
{
    /// <summary>An audio stream.</summary>
    Audio,

    /// <summary>A subtitle stream (embedded or external).</summary>
    Subtitle,
}

/// <summary>How a subtitle is stored.</summary>
public enum SubtitleFormat
{
    /// <summary>Text (SRT, ASS, WebVTT…): any app can show it without burning in.</summary>
    Text,

    /// <summary>Image (PGS, VobSub, DVB): many apps must burn it in.</summary>
    Image,

    /// <summary>Not known.</summary>
    Unknown,
}

/// <summary>What a subtitle track covers.</summary>
public enum SubtitleKind
{
    /// <summary>Every line of dialogue.</summary>
    Full,

    /// <summary>Only some lines: forced, Signs &amp; Songs, foreign parts.</summary>
    Partial,

    /// <summary>Full, with sound descriptions for people who are deaf or hard of hearing (SDH / closed captions).</summary>
    Sdh,
}

/// <summary>What an audio track carries.</summary>
public enum AudioKind
{
    /// <summary>The programme's soundtrack.</summary>
    Normal,

    /// <summary>A commentary track.</summary>
    Commentary,

    /// <summary>An audio-description track for people who are blind or visually impaired.</summary>
    AudioDescription,
}

/// <summary>
/// One audio or subtitle stream as the engine sees it (adapters build it from Jellyfin's MediaStream; the engine never sees Jellyfin types).
/// </summary>
/// <param name="Index">The stream index in the media source.</param>
/// <param name="Type">Audio or subtitle.</param>
/// <param name="Language">The raw language tag ("jpn", "chi", "pt-BR", "und", <c>null</c>).</param>
/// <param name="Title">The raw title from the file (never the localized DisplayTitle).</param>
/// <param name="Comment">The raw comment tag, when present.</param>
/// <param name="FileName">The external file name or path; <c>null</c> for embedded streams.</param>
/// <param name="Codec">The codec ("aac", "subrip", "hdmv_pgs_subtitle").</param>
/// <param name="Channels">Audio channel count.</param>
/// <param name="IsDefault">The file's default flag.</param>
/// <param name="IsForced">The file's forced flag.</param>
/// <param name="IsHearingImpaired">The hearing-impaired flag.</param>
/// <param name="IsOriginal">The original-language flag.</param>
/// <param name="IsExternal">Whether the stream is an external file.</param>
/// <param name="Format">Text or image subtitle (passed through unchanged).</param>
/// <param name="IsLossless">Whether the audio is lossless (passed through unchanged).</param>
public sealed record Track(
    int Index,
    TrackType Type,
    string? Language,
    string? Title,
    string? Comment,
    string? FileName,
    string? Codec,
    int? Channels,
    bool IsDefault,
    bool IsForced,
    bool IsHearingImpaired,
    bool IsOriginal,
    bool IsExternal,
    SubtitleFormat Format = SubtitleFormat.Unknown,
    bool IsLossless = false,
    string? ExternalPath = null);

/// <summary>A track with its resolved language and kind.</summary>
/// <param name="Track">The track, unchanged.</param>
/// <param name="Language">The resolved language, or <c>null</c> when unknown.</param>
/// <param name="SubtitleKind">The subtitle kind (always <see cref="SubtitleKind.Full"/> for audio tracks).</param>
/// <param name="AudioKind">The audio kind (always <see cref="AudioKind.Normal"/> for subtitle tracks).</param>
/// <param name="Reasons">Short codes explaining the kind: "forced-flag", "hi-flag", "keyword:signs", "keyword:commentary", "title-word:full", "default:full", "default:normal".</param>
public sealed record ClassifiedTrack(Track Track, LanguageTag? Language, SubtitleKind SubtitleKind, AudioKind AudioKind, IReadOnlyList<string> Reasons);
