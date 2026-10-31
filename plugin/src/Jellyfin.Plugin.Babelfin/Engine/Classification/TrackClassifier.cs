using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Engine.Languages;

namespace Jellyfin.Plugin.Babelfin.Engine.Classification;

/// <summary>
/// Classifies tracks. Keywords are matched against the raw <see cref="Track.Title"/>, <see cref="Track.Comment"/>
/// and <see cref="Track.FileName"/> only, never a localized display title.
/// <para>Subtitles, in order:</para>
/// <list type="number">
/// <item>A full word ("full", "dialogue"…) makes the track full. If partial words also occur, the forced flag decides.
/// A full track that is also SDH (flag or word) is <see cref="SubtitleKind.Sdh"/>.</item>
/// <item>Negated forced title words ("non-forced", "not forced") are ignored. An explicit container forced flag remains evidence of partial coverage;
/// explicit full/dialogue words keep their precedence unless a positive partial word also occurs with that flag.</item>
/// <item>The forced flag or a partial word ("signs", "songs", "s&amp;s", "forced"…) makes it <see cref="SubtitleKind.Partial"/>.</item>
/// <item>The hearing-impaired flag or an SDH word ("sdh", "cc", "hi"…) makes it <see cref="SubtitleKind.Sdh"/>.</item>
/// <item>Otherwise it is <see cref="SubtitleKind.Full"/>. The default flag never decides the kind.</item>
/// </list>
/// <para>Audio: an audio-description word makes it <see cref="AudioKind.AudioDescription"/> (this wins over commentary, so "音声解説" is
/// audio description although it contains "解説"); else a commentary word makes it <see cref="AudioKind.Commentary"/>; else Normal.</para>
/// <para>A keyword that is itself a code for the track's own language is ignored. For SDH, a bare Hindi "HI" can be a language
/// abbreviation, but "Hindi (HI)" and a separate .hin.hi. suffix indicate hearing impairment. "Hi-Res" is not an SDH marker.</para>
/// Thread-safe; keyword lists are prepared once per instance.
/// </summary>
public sealed class TrackClassifier
{
    private static readonly HashSet<string> ForcedMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "forced", "erzwungen", "forcé", "forcés", "forcée", "forcées", "forzado", "forzados", "forzada", "forzadas",
        "forzato", "forzati", "forçado", "forçados", "forçada", "forçadas", "geforceerd", "強制", "强制",
    };

    private static readonly PreparedKeyword[] SignsAliases =
    {
        new("s+s"), new("s + s"), new("sns"),
    };

    // A forced flag describes playback metadata, not the number of translated lines.
    // Explicit signs/songs/foreign-parts labels still establish partial coverage.
    internal static bool HasUncertainForcedCoverage(ClassifiedTrack track)
    {
        if (track.Track.Type != TrackType.Subtitle || track.SubtitleKind != SubtitleKind.Partial) return false;
        var forced = track.Track.IsForced;
        foreach (var reason in track.Reasons)
        {
            if (reason == "forced-flag") continue;
            if (reason.StartsWith("keyword:", StringComparison.Ordinal)
                && ForcedMarkers.Contains(reason.Substring("keyword:".Length).Trim()))
            {
                forced = true;
                continue;
            }

            return false;
        }

        return forced;
    }

    private readonly LanguageTable _languages;
    private readonly Prepared[] _partial;
    private readonly Prepared[] _sdh;
    private readonly Prepared[] _full;
    private readonly Prepared[] _commentary;
    private readonly Prepared[] _audioDescription;

    /// <summary>Initializes a new instance of the <see cref="TrackClassifier"/> class.</summary>
    /// <param name="languages">The language table (resolves each track's language with title and file-name hints).</param>
    /// <param name="keywords">The keyword lists.</param>
    public TrackClassifier(LanguageTable languages, KeywordTables keywords)
    {
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(keywords);
        _languages = languages;
        _partial = Prepare(keywords.PartialWords);
        _sdh = Prepare(keywords.SdhWords);
        _full = Prepare(keywords.FullWords);
        _commentary = Prepare(keywords.CommentaryWords);
        _audioDescription = Prepare(keywords.AudioDescriptionWords);
    }

    /// <summary>Classifies one track.</summary>
    /// <param name="track">The track.</param>
    /// <returns>The classified track; <see cref="ClassifiedTrack.Reasons"/> is never empty.</returns>
    public ClassifiedTrack Classify(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var language = _languages.ResolveWithHints(track.Language, track.Title, track.FileName);
        var texts = new[]
        {
            TextMatcher.Normalize(track.Title),
            TextMatcher.Normalize(track.Comment),
            TextMatcher.Normalize(TextMatcher.FileNameOnly(track.FileName)),
        };

        var reasons = new List<string>();
        if (track.Type == TrackType.Audio)
        {
            var ad = Hits(_audioDescription, texts, language);
            if (ad.Count > 0)
            {
                AddAll(reasons, "keyword:", ad);
                return new ClassifiedTrack(track, language, SubtitleKind.Full, AudioKind.AudioDescription, reasons.AsReadOnly());
            }

            var commentary = Hits(_commentary, texts, language);
            if (commentary.Count > 0)
            {
                AddAll(reasons, "keyword:", commentary);
                return new ClassifiedTrack(track, language, SubtitleKind.Full, AudioKind.Commentary, reasons.AsReadOnly());
            }

            reasons.Add("default:normal");
            return new ClassifiedTrack(track, language, SubtitleKind.Full, AudioKind.Normal, reasons.AsReadOnly());
        }

        var full = Hits(_full, texts, language);
        var partial = Hits(_partial, texts, language, ignoreNegatedForced: true);
        var sdh = Hits(_sdh, texts, language, track);
        var isSdh = track.IsHearingImpaired || sdh.Count > 0;
        SubtitleKind kind;
        if (full.Count > 0 && !(partial.Count > 0 && track.IsForced))
        {
            AddAll(reasons, "title-word:", full);
            kind = isSdh ? SubtitleKind.Sdh : SubtitleKind.Full;
            if (isSdh)
            {
                AddSdhReasons(reasons, track, sdh);
            }
        }
        else
        {
            if (track.IsForced || partial.Count > 0)
            {
                if (track.IsForced)
                {
                    reasons.Add("forced-flag");
                }

                AddAll(reasons, "keyword:", partial);
                kind = SubtitleKind.Partial;
            }
            else if (isSdh)
            {
                AddSdhReasons(reasons, track, sdh);
                kind = SubtitleKind.Sdh;
            }
            else
            {
                reasons.Add("default:full");
                kind = SubtitleKind.Full;
            }
        }

        return new ClassifiedTrack(track, language, kind, AudioKind.Normal, reasons.AsReadOnly());
    }

    /// <summary>Classifies several tracks, keeping their order.</summary>
    /// <param name="tracks">The tracks.</param>
    /// <returns>The classified tracks.</returns>
    public IReadOnlyList<ClassifiedTrack> ClassifyAll(IEnumerable<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var result = new List<ClassifiedTrack>();
        foreach (var track in tracks)
        {
            result.Add(Classify(track));
        }

        return result.AsReadOnly();
    }

    private static void AddSdhReasons(List<string> reasons, Track track, List<string> sdh)
    {
        if (track.IsHearingImpaired)
        {
            reasons.Add("hi-flag");
        }

        AddAll(reasons, "keyword:", sdh);
    }

    private static void AddAll(List<string> reasons, string prefix, List<string> words)
    {
        foreach (var w in words)
        {
            reasons.Add(prefix + w);
        }
    }

    private List<string> Hits(Prepared[] list, string[] texts, LanguageTag? language, Track? sdhTrack = null, bool ignoreNegatedForced = false)
    {
        var hits = new List<string>();
        foreach (var p in list)
        {
            var isHi = sdhTrack is not null && p.Keyword.Normalized == "hi";
            if (!isHi && p.AsLanguage is not null
                && language is not null
                && string.Equals(p.AsLanguage, language.Base, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (var i = 0; i < texts.Length; i++)
            {
                if (isHi
                    ? HasHiMarker(texts[i], language, i == 0, i == 2, sdhTrack!.FileName)
                    : ignoreNegatedForced
                        ? HasPartialMarker(texts[i], p.Keyword)
                        : TextMatcher.Contains(texts[i], p.Keyword))
                {
                    hits.Add(p.Keyword.Original);
                    break;
                }
            }
        }

        return hits;
    }

    private static bool HasPartialMarker(string text, PreparedKeyword keyword)
    {
        if (ForcedMarkers.Contains(keyword.Normalized)) return HasPositiveForcedMarker(text, keyword);
        // Match aliases through the existing editable keyword so saved settings get the same
        // coverage without replacing custom keyword lists. Removing S&S disables its aliases too.
        if (keyword.Normalized == "s&s")
            foreach (var alias in SignsAliases)
                if (TextMatcher.Contains(text, alias)) return true;
        return TextMatcher.Contains(text, keyword);
    }

    // Negation applies only to a forced title marker, never to container metadata or
    // independent signs/song markers. Inspect each occurrence so "non-forced; forced"
    // still has a positive marker. Normalization already unifies hyphens and spaces.
    private static bool HasPositiveForcedMarker(string text, PreparedKeyword keyword)
    {
        var word = keyword.Normalized;
        for (var start = 0; start <= text.Length - word.Length;)
        {
            var index = text.IndexOf(word, start, StringComparison.Ordinal);
            if (index < 0) return false;
            var end = index + word.Length;
            start = end;
            if (!keyword.IsCjk && ((index > 0 && TextMatcher.IsWordChar(text[index - 1]))
                || (end < text.Length && TextMatcher.IsWordChar(text[end])))) continue;
            var prefix = text.Substring(0, index).TrimEnd();
            var negated = false;
            foreach (var negative in new[] { "non", "not" })
            {
                if (prefix.EndsWith(negative, StringComparison.Ordinal)
                    && (prefix.Length == negative.Length || !TextMatcher.IsWordChar(prefix[prefix.Length - negative.Length - 1])))
                {
                    negated = true;
                    break;
                }
            }
            if (!negated) return true;
        }
        return false;
    }

    private bool HasHiMarker(string text, LanguageTag? language, bool isTitle, bool isFileName, string? fileName)
    {
        if (language?.Base == "hi")
        {
            if (isFileName)
            {
                // A single .hi. segment can name Hindi. A separate .hi. after a language is an accessibility flag.
                var segments = (TextMatcher.FileNameOnly(fileName) ?? string.Empty).Split('.');
                var hasLanguage = false;
                for (var i = 1; i < segments.Length - 1; i++)
                {
                    var segment = LanguageTable.NormalizeKey(segments[i]);
                    if (segment == "hi" && hasLanguage)
                    {
                        return true;
                    }

                    hasLanguage |= _languages.Resolve(segment) is not null;
                }

                return false;
            }

            if (isTitle && text == "hi")
            {
                return false;
            }
        }

        for (var start = 0; start < text.Length;)
        {
            var index = text.IndexOf("hi", start, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var end = index + 2;
            start = end;
            if ((index > 0 && TextMatcher.IsWordChar(text[index - 1]))
                || (end < text.Length && TextMatcher.IsWordChar(text[end])))
            {
                continue;
            }

            var tail = text.Substring(end).TrimStart();
            if (StartsWithWord(tail, "res") || StartsWithWord(tail, "resolution"))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool StartsWithWord(string text, string word) =>
        text.StartsWith(word, StringComparison.Ordinal)
        && (text.Length == word.Length || !TextMatcher.IsWordChar(text[word.Length]));

    private Prepared[] Prepare(IReadOnlyList<string>? words)
    {
        if (words is null)
        {
            return Array.Empty<Prepared>();
        }

        var list = new List<Prepared>(words.Count);
        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w))
            {
                continue;
            }

            var keyword = new PreparedKeyword(w);
            if (keyword.Normalized.Length == 0)
            {
                continue;
            }

            // A short keyword that is also a language code ("hi") is ignored on a track of that language.
            string? asLanguage = null;
            if (!keyword.IsCjk && keyword.Normalized.Length <= 3)
            {
                asLanguage = _languages.Resolve(keyword.Normalized)?.Base;
            }

            list.Add(new Prepared(keyword, asLanguage));
        }

        return list.ToArray();
    }

    private sealed record Prepared(PreparedKeyword Keyword, string? AsLanguage);
}
