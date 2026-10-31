namespace Jellyfin.Plugin.Babelfin.Engine.Selection;

/// <summary>The kind of one step in an audio chain. The first step with a usable track wins.</summary>
public enum AudioStepKind
{
    /// <summary>A specific language or variety (<see cref="AudioStep.Language"/>), optionally limited by <see cref="AudioStep.TitleContains"/>.</summary>
    Language,

    /// <summary>The title's original language (source set by <see cref="ResolvedProfile.OriginalSource"/>).</summary>
    OriginalLanguage,

    /// <summary>The library's usual original language (<see cref="ItemFacts.LibraryUsualOriginalLanguage"/>).</summary>
    LibraryUsualOriginalLanguage,

    /// <summary>The person's understood languages, in order.</summary>
    MyLanguages,

    /// <summary>The track the file flags as default.</summary>
    FileDefault,

    /// <summary>The lowest-index usable track.</summary>
    FirstTrack,

    /// <summary>Leave the audio to the video: Babelfin doesn't set it, so Jellyfin's own choice stands (the track the file marks as
    /// default, or the person's Jellyfin playback settings). Later steps are never reached.</summary>
    VideoDefault,
}

/// <summary>Which subtitles a person wants in a situation.</summary>
public enum SubtitleWant
{
    /// <summary>No subtitles.</summary>
    Off,

    /// <summary>Partial subtitles only (forced, Signs &amp; Songs).</summary>
    PartialOnly,

    /// <summary>Full subtitles (full or SDH tracks).</summary>
    Full,

    /// <summary>Leave the subtitles to the video: Babelfin doesn't set them, so Jellyfin's own choice stands.</summary>
    VideoDefault,
}

/// <summary>A three-way preference.</summary>
public enum Preference
{
    /// <summary>Rank matching tracks first.</summary>
    Prefer,

    /// <summary>No preference.</summary>
    Neutral,

    /// <summary>Use matching tracks only when nothing else exists.</summary>
    Avoid,
}

/// <summary>Text versus image subtitle preference.</summary>
public enum FormatPreference
{
    /// <summary>Text tracks rank before image tracks (default).</summary>
    PreferText,

    /// <summary>No preference.</summary>
    Neutral,

    /// <summary>Image tracks are never chosen.</summary>
    TextOnly,
}

/// <summary>Embedded versus external subtitle preference.</summary>
public enum EmbeddedPreference
{
    /// <summary>No preference (default).</summary>
    Neutral,

    /// <summary>Embedded tracks rank first.</summary>
    PreferEmbedded,

    /// <summary>External files rank first.</summary>
    PreferExternal,
}

/// <summary>What to show when no wanted full subtitle track exists.</summary>
public enum WhenNothingMatches
{
    /// <summary>Show nothing (default).</summary>
    ShowNothing,

    /// <summary>A partial track in a language the person reads.</summary>
    PartialInMyLanguage,

    /// <summary>A full track in any other language the person reads, else one they understand.</summary>
    FullInAnyBackupLanguage,
}

/// <summary>What an audio track with no language (after title hints) is assumed to be.</summary>
public enum UnlabelledAudioRule
{
    /// <summary>It stays unknown.</summary>
    Unknown,

    /// <summary>The title's original language, when known.</summary>
    OriginalLanguage,

    /// <summary>The library's usual original language, when known.</summary>
    LibraryUsualOriginalLanguage,

    /// <summary><see cref="ResolvedProfile.UnlabelledAudioLanguage"/>.</summary>
    SpecificLanguage,

    /// <summary>The original language, but only when it is the file's only audio track (starter default).</summary>
    OriginalWhenOnlyTrack,
}

/// <summary>Where the title's original language comes from.</summary>
public enum OriginalLanguageSource
{
    /// <summary>Item metadata first, then a track flagged original (default).</summary>
    MetadataThenFlag,

    /// <summary>A track flagged original first, then item metadata.</summary>
    FlagThenMetadata,
}

/// <summary>Same-step audio tie-breaks, applied in the profile's order.</summary>
public enum TieBreak
{
    /// <summary>A track the app can direct-play first (<see cref="DeviceCapabilities.CanDirectPlayAudio"/>).</summary>
    DirectPlayable,

    /// <summary>More channels first.</summary>
    MoreChannels,

    /// <summary>Lossless first (flag, or codec flac, alac, truehd, mlp, dts-hd ma, pcm*).</summary>
    Lossless,

    /// <summary>A track flagged original first.</summary>
    IsOriginalFlag,

    /// <summary>A track flagged default first.</summary>
    DefaultFlag,

    /// <summary>The lowest index first.</summary>
    LowestIndex,
}
