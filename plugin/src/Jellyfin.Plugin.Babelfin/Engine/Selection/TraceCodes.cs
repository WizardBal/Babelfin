using System.Collections.Generic;

namespace Jellyfin.Plugin.Babelfin.Engine.Selection;

/// <summary>The fixed set of <see cref="TraceLine.Code"/> values (stable keys for translation).</summary>
public static class TraceCodes
{
    /// <summary>A language string in the profile or item facts isn't recognised and was ignored.</summary>
    public const string ProfileLanguageUnknown = "profile.language.unknown";

    /// <summary>The title's original language and where it came from.</summary>
    public const string OriginalKnown = "audio.original.known";

    /// <summary>The file has no audio tracks.</summary>
    public const string AudioNone = "audio.none";

    /// <summary>An unlabelled audio track was assumed to be a language.</summary>
    public const string AudioUnlabelledAssumed = "audio.unlabelled.assumed";

    /// <summary>An unlabelled audio track stays unknown.</summary>
    public const string AudioUnlabelledUnknown = "audio.unlabelled.unknown";

    /// <summary>Audio chosen from a remembered manual change.</summary>
    public const string AudioManual = "audio.manual";

    /// <summary>A remembered audio change matched no track; the chain decides.</summary>
    public const string AudioManualNoMatch = "audio.manual.nomatch";

    /// <summary>An audio step matched.</summary>
    public const string AudioStepMatched = "audio.step.matched";

    /// <summary>An audio step was skipped, with why.</summary>
    public const string AudioStepSkipped = "audio.step.skipped";

    /// <summary>No audio step matched; the best remaining track was used.</summary>
    public const string AudioFallback = "audio.fallback";

    /// <summary>The audio is left to the video (Jellyfin's own choice stands).</summary>
    public const string AudioVideoDefault = "audio.videodefault";

    /// <summary>The chosen audio is understood.</summary>
    public const string AudioUnderstood = "audio.understood";

    /// <summary>The chosen audio is not understood.</summary>
    public const string AudioNotUnderstood = "audio.notunderstood";

    /// <summary>The chosen audio's language is unknown.</summary>
    public const string AudioUnknown = "audio.unknown";

    /// <summary>Which subtitles are wanted, and why.</summary>
    public const string SubsWant = "subs.want";

    /// <summary>Subtitles are off.</summary>
    public const string SubsOff = "subs.off";

    /// <summary>The subtitles are left to the video (Jellyfin's own choice stands).</summary>
    public const string SubsVideoDefault = "subs.videodefault";

    /// <summary>A wanted subtitle language is burned in.</summary>
    public const string SubsBurnedIn = "subs.burnedin";

    /// <summary>Subtitle chosen from a remembered manual change.</summary>
    public const string SubsManual = "subs.manual";

    /// <summary>Subtitles remembered as turned off by hand.</summary>
    public const string SubsManualOff = "subs.manual.off";

    /// <summary>A remembered subtitle change matched no track.</summary>
    public const string SubsManualNoMatch = "subs.manual.nomatch";

    /// <summary>A subtitle track with no language was ignored.</summary>
    public const string SubsUnlabelledIgnored = "subs.unlabelled.ignored";

    /// <summary>A subtitle track matched.</summary>
    public const string SubsMatched = "subs.matched";

    /// <summary>A track in a wanted language was skipped because it's partial.</summary>
    public const string SubsSkippedPartial = "subs.skipped.partial";

    /// <summary>A track in a wanted language was skipped because only partial subtitles were wanted.</summary>
    public const string SubsSkippedFull = "subs.skipped.full";

    /// <summary>An SDH track was skipped because SDH is avoided and a full track exists.</summary>
    public const string SubsSkippedSdh = "subs.skipped.sdh";

    /// <summary>An image track was skipped because only text subtitles are allowed.</summary>
    public const string SubsSkippedImage = "subs.skipped.image";

    /// <summary>A wanted language had no usable track.</summary>
    public const string SubsSkippedLanguage = "subs.skipped.language";

    /// <summary>No wanted subtitle track exists.</summary>
    public const string SubsNothing = "subs.nothing";

    /// <summary>A fallback subtitle track was shown.</summary>
    public const string SubsFallback = "subs.fallback";

    /// <summary>Gets every code.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        ProfileLanguageUnknown, OriginalKnown, AudioNone, AudioUnlabelledAssumed, AudioUnlabelledUnknown, AudioManual, AudioManualNoMatch,
        AudioStepMatched, AudioStepSkipped, AudioFallback, AudioVideoDefault, AudioUnderstood, AudioNotUnderstood, AudioUnknown,
        SubsWant, SubsOff, SubsVideoDefault, SubsBurnedIn, SubsManual, SubsManualOff, SubsManualNoMatch, SubsUnlabelledIgnored, SubsMatched,
        SubsSkippedPartial, SubsSkippedFull, SubsSkippedSdh, SubsSkippedImage, SubsSkippedLanguage, SubsNothing, SubsFallback,
    };
}
