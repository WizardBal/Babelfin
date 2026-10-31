// Subtitle policy adapted from the Jellyfin Project contributors' MediaStreamSelector:
// Emby.Server.Implementations/Library/MediaStreamSelector.cs, GetDefaultSubtitleStreamIndex.
// Source: https://github.com/jellyfin/jellyfin/blob/6c073e19ddf604b2369c638716164fdab4c952dc/Emby.Server.Implementations/Library/MediaStreamSelector.cs
// Upstream licensing/provenance and the Babelfin modification notice are preserved in NOTICE.
// Babelfin modifications (2026): classified-track input and an optional normalized preferred-language list.
// Policy verification: tools/native-policy-check/NativePolicyCheck.csproj.
// Runtime setup and repeatable parity checks: tools/native-policy-check/README.md.

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Classification;

namespace Jellyfin.Plugin.Babelfin.Engine.Selection;

/// <summary>
/// Jellyfin's subtitle-mode policy, evaluated again when Babelfin changes the audio.
/// Mirrors MediaStreamSelector.GetDefaultSubtitleStreamIndex; classification never changes its forced/default rules.
/// </summary>
public static class VideoSubtitleSelector
{
    /// <summary>Selects a subtitle using Jellyfin's own playback mode and preferred language.</summary>
    public static int? Choose(IReadOnlyList<ClassifiedTrack> tracks, string mode, string? preferredLanguage, string? audioLanguage, IReadOnlyList<string>? normalizedPreferredLanguages = null)
    {
        if (string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        IReadOnlyList<string> preferredLanguages = normalizedPreferredLanguages
            ?? (string.IsNullOrEmpty(preferredLanguage) ? Array.Empty<string>() : new[] { preferredLanguage });
        bool Matches(string? language) => preferredLanguages.Count == 0 || preferredLanguages.Any(preferred => SameLanguage(preferred, language));
        var subtitles = tracks.Select(c => c.Track).Where(t => t.Type == TrackType.Subtitle)
            .OrderByDescending(t => t.IsExternal)
            .ThenByDescending(t => t.IsDefault)
            .ThenByDescending(t => !t.IsForced && Matches(t.Language))
            .ThenByDescending(t => t.IsForced && Matches(t.Language))
            .ThenByDescending(t => t.IsForced && Undefined(t.Language))
            .ThenByDescending(t => t.IsForced).ToArray();
        Track? Forced() => subtitles.Where(t => t.IsForced && (Matches(t.Language) || Undefined(t.Language)))
            .OrderByDescending(t => Matches(t.Language)).ThenByDescending(t => Undefined(t.Language)).FirstOrDefault();

        Track? chosen;
        if (string.Equals(mode, "Smart", StringComparison.OrdinalIgnoreCase))
        {
            chosen = preferredLanguages.Count > 0 && preferredLanguages.Any(preferred => SameLanguage(preferred, audioLanguage))
                ? Forced() : subtitles.FirstOrDefault(t => Matches(t.Language));
        }
        else if (string.Equals(mode, "Always", StringComparison.OrdinalIgnoreCase))
        {
            chosen = subtitles.FirstOrDefault(t => !t.IsForced && Matches(t.Language)) ?? Forced();
        }
        else if (string.Equals(mode, "OnlyForced", StringComparison.OrdinalIgnoreCase))
        {
            chosen = Forced();
        }
        else
        {
            chosen = subtitles.FirstOrDefault(t => t.IsExternal || t.IsDefault || t.IsForced);
        }

        return chosen?.Index;
    }

    private static bool SameLanguage(string? wanted, string? actual)
    {
        if (string.IsNullOrEmpty(wanted) || string.IsNullOrEmpty(actual))
        {
            return false;
        }

        // Native MediaStreamSelector compares the supplied codes; Babelfin aliases
        // must not change Jellyfin's Smart / OnlyForced policy (for example en vs eng).
        return string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Undefined(string? language) => string.IsNullOrEmpty(language)
        || language.Equals("und", StringComparison.OrdinalIgnoreCase)
        || language.Equals("unknown", StringComparison.OrdinalIgnoreCase)
        || language.Equals("undetermined", StringComparison.OrdinalIgnoreCase)
        || language.Equals("mul", StringComparison.OrdinalIgnoreCase)
        || language.Equals("zxx", StringComparison.OrdinalIgnoreCase);
}
