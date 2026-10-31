using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Resolution;

/// <summary>
/// The starter rules and Babelfin's defaults, plus the original-audio library test.
/// Everything here is written in relative terms (the original language, my languages); no language or library name is fixed in code:
/// the word and genre lists come from the admin's <see cref="StarterSettings"/>.
/// </summary>
public static class StarterRules
{
    /// <summary>The provenance label of the starter chain applied to an original-audio library.</summary>
    public const string LibraryLabel = "the smart starter rule for original-audio libraries";

    /// <summary>The provenance label of the starter chain applied to a title with an original-audio genre in a mixed library.</summary>
    public const string GenreLabel = "the smart starter rule for original-audio genres";

    /// <summary>The provenance label of the last layer.</summary>
    public const string DefaultsLabel = "Babelfin's defaults";

    private static readonly SettingsLayer OriginalAudioChainLayer = new()
    {
        AudioChain = new[]
        {
            new AudioStepSetting(AudioStepKind.OriginalLanguage),
            new AudioStepSetting(AudioStepKind.LibraryUsualOriginalLanguage),
            new AudioStepSetting(AudioStepKind.FileDefault),
        },
    };

    private static readonly SettingsLayer DubDefaults = BuildDefaults(ForeignTitlesRule.DubInMyLanguage);

    private static readonly SettingsLayer OriginalDefaults = BuildDefaults(ForeignTitlesRule.OriginalWithSubtitles);

    /// <summary>
    /// Gets the starter layer for original-audio content: <c>AudioChain</c> = the original language → the library's usual original language → the file's default.
    /// A shared instance: callers must not change it.
    /// </summary>
    public static SettingsLayer OriginalAudioLayer => OriginalAudioChainLayer;

    /// <summary>
    /// Gets "Babelfin's defaults" (layer 9): every field except <see cref="SettingsFields.AudioChain"/> is set, so every field resolves.
    /// A shared instance: callers must not change it.
    /// </summary>
    /// <param name="foreignTitles">The starter's "titles in a language I don't understand" choice (<see cref="StarterSettings.ForeignTitles"/>).</param>
    /// <returns>The defaults layer.</returns>
    public static SettingsLayer Defaults(ForeignTitlesRule foreignTitles) =>
        foreignTitles == ForeignTitlesRule.OriginalWithSubtitles ? OriginalDefaults : DubDefaults;

    /// <summary>
    /// Builds the audio chain a <see cref="ForeignTitlesRule"/> stands for when no layer sets the chain itself:
    /// a dub in my language → my languages, the original, the file's default; the original with subtitles → the original, my languages, the file's default.
    /// </summary>
    /// <param name="rule">The resolved rule.</param>
    /// <returns>The engine chain (a new array).</returns>
    public static AudioStep[] ChainFor(ForeignTitlesRule rule) => rule == ForeignTitlesRule.OriginalWithSubtitles
        ? new[] { new AudioStep(AudioStepKind.OriginalLanguage), new AudioStep(AudioStepKind.MyLanguages), new AudioStep(AudioStepKind.FileDefault) }
        : new[] { new AudioStep(AudioStepKind.MyLanguages), new AudioStep(AudioStepKind.OriginalLanguage), new AudioStep(AudioStepKind.FileDefault) };

    /// <summary>
    /// Whether a library name matches the admin's original-audio word list: Latin-script words as whole words, CJK words as substrings,
    /// case-insensitive after the engine's text normalisation (the same rule the library facts use).
    /// </summary>
    /// <param name="libraryName">The library name.</param>
    /// <param name="words">The word list (<see cref="StarterSettings.OriginalAudioLibraryWords"/>).</param>
    /// <returns>True on a match.</returns>
    public static bool LibraryNameMatches(string? libraryName, IEnumerable<string>? words)
    {
        if (string.IsNullOrWhiteSpace(libraryName) || words is null)
        {
            return false;
        }

        var text = TextMatcher.Normalize(libraryName);
        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word))
            {
                continue;
            }

            if (TextMatcher.Contains(text, new PreparedKeyword(word.Trim())))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any of the item's genres is in the admin's original-audio genre list (trimmed, case-insensitive).</summary>
    /// <param name="genres">The item's genres (and its series').</param>
    /// <param name="originalAudioGenres">The list (<see cref="StarterSettings.OriginalAudioGenres"/>).</param>
    /// <returns>True when one matches.</returns>
    public static bool HasOriginalAudioGenre(IReadOnlyList<string>? genres, IEnumerable<string>? originalAudioGenres)
    {
        if (genres is null || genres.Count == 0 || originalAudioGenres is null)
        {
            return false;
        }

        foreach (var wanted in originalAudioGenres)
        {
            if (string.IsNullOrWhiteSpace(wanted))
            {
                continue;
            }

            var w = wanted.Trim();
            foreach (var genre in genres)
            {
                if (genre is not null && string.Equals(genre.Trim(), w, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the item's first library counts as an original-audio library: the admin's <see cref="OriginalAudioChoice.Yes"/> or
    /// <see cref="OriginalAudioChoice.No"/> wins; on <see cref="OriginalAudioChoice.Auto"/> (or no admin entry) the computed library facts decide, and
    /// while no facts are computed yet the library name is matched against the word list.
    /// </summary>
    /// <param name="admin">The admin's configuration.</param>
    /// <param name="context">The item context.</param>
    /// <returns>True for an original-audio library.</returns>
    public static bool IsOriginalAudioLibrary(PluginConfiguration admin, ItemContext context)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(context);
        var choice = context.LibraryId is { } id && admin.Libraries is not null ? admin.FindLibrary(id)?.OriginalAudio ?? OriginalAudioChoice.Auto : OriginalAudioChoice.Auto;
        switch (choice)
        {
            case OriginalAudioChoice.Yes:
                return true;
            case OriginalAudioChoice.No:
                return false;
            default:
                break;
        }

        if (context.Library is { ComputedAt: not null } facts)
        {
            return facts.DetectedOriginalAudio;
        }

        var name = !string.IsNullOrWhiteSpace(context.LibraryName) ? context.LibraryName : context.Library?.Name;
        return LibraryNameMatches(name, admin.Starter?.OriginalAudioLibraryWords);
    }

    private static SettingsLayer BuildDefaults(ForeignTitlesRule foreignTitles) => new()
    {
        Understood = Array.Empty<string>(),
        Reads = Array.Empty<string>(),
        UsesAudioDescription = false,
        PrefersSdh = false,
        ForeignTitles = foreignTitles,
        AvoidCommentary = true,
        TieBreaks = ToArray(ResolvedProfile.DefaultTieBreaks),
        UnlabelledAudio = UnlabelledAudioRule.OriginalWhenOnlyTrack,
        OriginalSource = OriginalLanguageSource.MetadataThenFlag,
        WhenAudioUnderstood = SubtitleWant.VideoDefault,
        WhenAudioNotUnderstood = SubtitleWant.VideoDefault,
        UnknownAudioCountsAsUnderstood = false,
        SubtitleLanguages = Array.Empty<string>(),
        Sdh = Preference.Neutral,
        Format = FormatPreference.PreferText,
        Embedded = EmbeddedPreference.Neutral,
        IfNothingMatches = WhenNothingMatches.ShowNothing,
        Memory = MemoryScope.RestOfShow,
    };

    private static TieBreak[] ToArray(IReadOnlyList<TieBreak> list)
    {
        var array = new TieBreak[list.Count];
        for (var i = 0; i < array.Length; i++)
        {
            array[i] = list[i];
        }

        return array;
    }
}
