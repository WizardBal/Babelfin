using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Classification;

namespace Jellyfin.Plugin.Babelfin.Engine.Selection;

/// <summary>The result of one selection: audio and subtitles decided together, with the trace that explains them.</summary>
/// <param name="AudioIndex">The chosen audio stream index; null only when the source has no audio at all.</param>
/// <param name="SubtitleIndex">The chosen subtitle stream index; null = no subtitles.</param>
/// <param name="SubtitlesWanted">Whether the profile wanted subtitles (even if none matched).</param>
/// <param name="AudioUnderstood">Whether the chosen audio counts as understood.</param>
/// <param name="Tracks">Every track, classified, ordered by index.</param>
/// <param name="Trace">The explanation trace, in decision order.</param>
public sealed record Decision(
    int? AudioIndex,
    int? SubtitleIndex,
    bool SubtitlesWanted,
    bool AudioUnderstood,
    IReadOnlyList<ClassifiedTrack> Tracks,
    IReadOnlyList<TraceLine> Trace,
    bool AudioLeftToVideo = false,
    bool SubtitlesLeftToVideo = false)
{
    private static readonly string[] AudioDeciders = { TraceCodes.AudioStepMatched, TraceCodes.AudioManual, TraceCodes.AudioFallback, TraceCodes.AudioVideoDefault, TraceCodes.AudioNone };

    private static readonly string[] SubtitleDeciders =
    {
        TraceCodes.SubsManualOff, TraceCodes.SubsBurnedIn, TraceCodes.SubsManual, TraceCodes.SubsVideoDefault, TraceCodes.SubsOff,
        TraceCodes.SubsMatched, TraceCodes.SubsFallback, TraceCodes.SubsNothing, // order = priority
    };

    /// <summary>
    /// One short paragraph explaining the choice, for example
    /// "Audio: Japanese (#2): your Anime library rule, step 1. Subtitles: English full (#5): the Signs &amp; Songs track (#4) was skipped because it's partial."
    /// Each sentence stays short; it never contains file paths (the engine never sees them).
    /// </summary>
    /// <returns>The explanation.</returns>
    public string Explain() => ExplainAudio() + " " + ExplainSubtitles();

    private static string Arg(TraceLine line, string key) =>
        line.Args is not null && line.Args.TryGetValue(key, out var value) ? value : string.Empty;

    private string ExplainAudio()
    {
        var line = Trace.LastOrDefault(l => AudioDeciders.Contains(l.Code));
        if (line is null)
        {
            return "Audio: no decision.";
        }

        var head = $"Audio: {Arg(line, "name")} (#{Arg(line, "index")})";
        return line.Code switch
        {
            TraceCodes.AudioStepMatched => $"{head}: {Arg(line, "source")}, step {Arg(line, "step")}.",
            TraceCodes.AudioManual => $"{head}: remembered from your track change.",
            TraceCodes.AudioFallback => $"{head}: no audio step matched, so the best remaining track.",
            TraceCodes.AudioVideoDefault => Arg(line, "filtered") == "true" ? $"{head}: another track in the same language replaces the skipped video default."
                : line.TrackIndex is null ? "Audio: the video's default." : $"{head}: the video's default.",
            _ => "Audio: none in this file.",
        };
    }

    private string ExplainSubtitles()
    {
        // Priority order: a fallback line follows "subs.nothing" in the trace but decides the outcome.
        var line = SubtitleDeciders.Select(code => Trace.FirstOrDefault(l => l.Code == code)).FirstOrDefault(l => l is not null);
        var want = Trace.FirstOrDefault(l => l.Code == TraceCodes.SubsWant);
        var audio = want is null ? string.Empty : Arg(want, "audio");
        var understood = want is null ? string.Empty : Arg(want, "understood");
        var wanted = want is null ? string.Empty : Arg(want, "want");
        if (line is null)
        {
            return "Subtitles: none.";
        }

        var head = $"Subtitles: {Arg(line, "name")} {Arg(line, "kind")} (#{Arg(line, "index")})";
        switch (line.Code)
        {
            case TraceCodes.SubsManualOff:
                return "Subtitles: off (you turned them off).";
            case TraceCodes.SubsBurnedIn:
                return $"Subtitles: none ({Arg(line, "name")} is burned in).";
            case TraceCodes.SubsManual:
                return $"{head}: remembered from your track change.";
            case TraceCodes.SubsVideoDefault:
                return Arg(line, "recomputed") == "true"
                    ? line.TrackIndex is null ? "Subtitles: off (Jellyfin's policy for the chosen audio)." : $"{head}: Jellyfin's policy for the chosen audio."
                    : line.TrackIndex is null ? "Subtitles: the video's default." : $"{head}: the video's default.";
            case TraceCodes.SubsOff:
                return understood == "true" && audio.Length > 0 ? $"Subtitles: off ({audio} is in My languages)." : "Subtitles: off (your settings).";
            case TraceCodes.SubsFallback:
                return $"{head}: nothing in {Arg(line, "tried")}, so a fallback.";
            case TraceCodes.SubsNothing:
                var tried = Arg(line, "tried");
                var none = tried.Length == 0 ? "Subtitles: none (no subtitle languages set)" : $"Subtitles: none found in {tried}";
                return none + ".";
            default:
                break;
        }

        // subs.matched
        string reason;
        var skipped = Trace.FirstOrDefault(l => l.Code == TraceCodes.SubsSkippedPartial);
        if (Arg(line, "coverage") == "unknown")
        {
            reason = "a forced track in the requested language; its metadata does not identify dialogue coverage";
        }
        else if (Arg(line, "position") is var position && position.Length > 0 && position != "1")
        {
            reason = $"nothing usable in {Arg(line, "firstName")}, so your next language";
        }
        else if (wanted == "full" && skipped is not null)
        {
            reason = $"the {Arg(skipped, "label")} track (#{Arg(skipped, "index")}) was skipped because it's partial";
        }
        else if (wanted == "partial")
        {
            reason = audio.Length > 0 ? $"signs and songs only, because {audio} is in My languages" : "partial only";
        }
        else if (understood == "true")
        {
            reason = "Always show subtitles is on";
        }
        else if (understood == "unknown" || audio.Length == 0)
        {
            reason = "the audio language is unknown";
        }
        else
        {
            reason = $"{audio} is outside My languages";
        }

        return $"{head}: {reason}.";
    }
}
