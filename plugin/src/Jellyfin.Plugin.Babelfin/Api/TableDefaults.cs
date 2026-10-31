using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>What one row of the "Audio and subtitles" table resolves to when the person leaves it on Default.</summary>
/// <param name="AudioChain">The audio order that applies.</param>
/// <param name="WhenAudioUnderstood">Subtitles when the audio is in a language the person understands.</param>
/// <param name="WhenAudioNotUnderstood">Subtitles when it isn't.</param>
/// <param name="SubtitleLanguages">The subtitle languages (empty: the languages the person reads).</param>
/// <param name="Understood">The languages the person understands.</param>
/// <param name="Reads">The languages the person reads.</param>
/// <param name="AudioFromLibrary">True when the admin's default for this library sets the audio, so the person's "All libraries" choice doesn't reach it.</param>
/// <param name="SubtitlesFromLibrary">The same for subtitles.</param>
public sealed record RowDefaultDto(
    IReadOnlyList<AudioStep> AudioChain,
    SubtitleWant WhenAudioUnderstood,
    SubtitleWant WhenAudioNotUnderstood,
    IReadOnlyList<string> SubtitleLanguages,
    IReadOnlyList<string> Understood,
    IReadOnlyList<string> Reads,
    bool AudioFromLibrary,
    bool SubtitlesFromLibrary);

/// <summary>The Default values of the "Audio and subtitles" table: the "All libraries" row and one row per library.</summary>
/// <param name="AllLibraries">The "All libraries" row (without the person's own everyday audio and subtitle choices).</param>
/// <param name="Libraries">Per library id ("N" format): the row without the person's own choice for that library.</param>
/// <param name="PerLibrary">The person's table mode as it applies (their choice, or the automatic one).</param>
public sealed record TableDefaultsDto(RowDefaultDto AllLibraries, IReadOnlyDictionary<string, RowDefaultDto> Libraries, bool PerLibrary = false);

/// <summary>Works out the table's Default values for a person.</summary>
public static class TableDefaults
{
    private static readonly string[] AudioFields = { SettingsFields.AudioChain, SettingsFields.ForeignTitles };

    private static readonly string[] SubtitleFields = { SettingsFields.WhenAudioUnderstood, SettingsFields.WhenAudioNotUnderstood, SettingsFields.SubtitleLanguages };

    /// <summary>Builds the Default values for every row.</summary>
    /// <param name="person">The person's stored document.</param>
    /// <param name="admin">The admin's configuration.</param>
    /// <param name="libraries">The libraries the person can see.</param>
    /// <param name="facts">The library facts (detection), or null.</param>
    /// <param name="languages">The language table.</param>
    /// <returns>The defaults.</returns>
    public static TableDefaultsDto Build(PersonSettings person, PluginConfiguration admin, IEnumerable<LibraryInfoDto> libraries, ILibraryFacts? facts, LanguageTable languages)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(languages);

        // The person's own table choices are left out; everything else (locks, rules, the admin's defaults, detection) still applies.
        var stripped = person.Clone();
        Strip(stripped.Everyday);
        foreach (var library in stripped.Libraries)
        {
            Strip(library.Settings);
        }

        var all = Row(ProfileResolver.Resolve(stripped, admin, ItemContext.None, languages).Profile, false, false);
        var rows = new Dictionary<string, RowDefaultDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            if (library is null || !Guid.TryParse(library.Id, out var id) || id == Guid.Empty)
            {
                continue;
            }

            var context = ItemContext.None with { LibraryIds = new[] { id }, LibraryName = library.Name, Library = facts?.Get(id) };
            var profile = ProfileResolver.Resolve(stripped, admin, context, languages).Profile;
            var theirs = TableModes.AdminPerLibrary(admin) ? admin.FindLibrary(id)?.Settings : null;
            rows[id.ToString("N")] = Row(profile, Sets(theirs, AudioFields), Sets(theirs, SubtitleFields));
        }

        return new TableDefaultsDto(all, rows, TableModes.PersonPerLibrary(person));
    }

    private static void Strip(SettingsLayer? layer)
    {
        if (layer is null)
        {
            return;
        }

        foreach (var field in AudioFields.Concat(SubtitleFields))
        {
            layer.Clear(field);
        }
    }

    private static bool Sets(SettingsLayer? layer, string[] fields) => layer is not null && fields.Any(layer.IsSet);

    private static RowDefaultDto Row(ResolvedProfile profile, bool audioFromLibrary, bool subtitlesFromLibrary) => new(
        profile.AudioChain,
        profile.WhenAudioUnderstood,
        profile.WhenAudioNotUnderstood,
        profile.SubtitleLanguages,
        profile.Understood,
        profile.Reads,
        audioFromLibrary,
        subtitlesFromLibrary);
}
