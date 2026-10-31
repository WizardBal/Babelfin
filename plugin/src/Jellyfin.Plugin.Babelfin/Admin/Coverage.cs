using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Babelfin.Admin;

/// <summary>The fixed flag names of a simulator/coverage row (stable keys for the dashboard's filters and translations).</summary>
public static class CoverageFlags
{
    /// <summary>The chosen subtitle is partial (forced, signs and songs).</summary>
    public const string PartialChosen = "partial-chosen";

    /// <summary>No audio step or no subtitle language matched; a fallback was used (trace <c>audio.fallback</c> or <c>subs.fallback</c>).</summary>
    public const string FallbackUsed = "fallback-used";

    /// <summary>Subtitles were wanted and none was chosen (and none is burned in).</summary>
    public const string NoSubtitles = "no-subtitles";

    /// <summary>The chosen subtitle is an image format (PGS, VobSub…), which many apps must burn in.</summary>
    public const string ImageSubtitles = "image-subtitles";

    /// <summary>The title's original language differs from the library's usual original language (both known).</summary>
    public const string OriginalNotUsual = "original-not-usual";

    /// <summary>The audio came from a matched step of the audio chain (trace <c>audio.step.matched</c>).</summary>
    public const string WantedAudioFound = "wanted-audio-found";

    /// <summary>A wanted subtitle language is burned into the picture (trace <c>subs.burnedin</c>).</summary>
    public const string BurnedIn = "burned-in";

    /// <summary>Gets every flag, in display order.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        PartialChosen, FallbackUsed, NoSubtitles, ImageSubtitles, OriginalNotUsual, WantedAudioFound, BurnedIn,
    };
}

/// <summary>The facts about a library item a row needs (names and numbers only, never a location).</summary>
/// <param name="ItemId">The item id.</param>
/// <param name="Name">The item's own name.</param>
/// <param name="SeriesName">The series name for an episode, else null.</param>
/// <param name="SeasonNumber">The season number for an episode, else null.</param>
/// <param name="EpisodeNumber">The episode number, else null.</param>
public sealed record CoverageItem(Guid ItemId, string Name, string? SeriesName, int? SeasonNumber, int? EpisodeNumber);

/// <summary>What plays for one person, item and media source (one simulator row). Names and indices only: no file names or paths.</summary>
/// <param name="ItemId">The item id ("N" format).</param>
/// <param name="Name">The item's name.</param>
/// <param name="SeriesName">The series name, or null.</param>
/// <param name="SeasonNumber">The season number, or null.</param>
/// <param name="EpisodeNumber">The episode number, or null.</param>
/// <param name="SourceId">The media source id.</param>
/// <param name="Outcome">"Applied", "DryRun", "PassedThrough" or "None" (no decision recorded).</param>
/// <param name="AudioIndex">The audio stream index that plays, or null.</param>
/// <param name="AudioName">Its language's English name, or null.</param>
/// <param name="AudioLanguage">Its canonical language code, or null.</param>
/// <param name="SubtitleIndex">The subtitle stream index that shows, or null for none.</param>
/// <param name="SubtitleName">Its language's English name, or null.</param>
/// <param name="SubtitleLanguage">Its canonical language code, or null.</param>
/// <param name="SubtitleKind">"full", "partial" or "sdh", or null.</param>
/// <param name="SubtitleCodec">The subtitle codec, or null.</param>
/// <param name="SubtitlesWanted">Whether the person's settings wanted subtitles for this file.</param>
/// <param name="OriginalLanguage">The title's original language (canonical code), or null when unknown.</param>
/// <param name="Flags">The flags (<see cref="CoverageFlags"/>).</param>
/// <param name="Explanation">The one-paragraph explanation, or null.</param>
public sealed record CoverageRow(
    string ItemId,
    string Name,
    string? SeriesName,
    int? SeasonNumber,
    int? EpisodeNumber,
    string SourceId,
    string Outcome,
    int? AudioIndex,
    string? AudioName,
    string? AudioLanguage,
    int? SubtitleIndex,
    string? SubtitleName,
    string? SubtitleLanguage,
    string? SubtitleKind,
    string? SubtitleCodec,
    bool SubtitlesWanted,
    string? OriginalLanguage,
    IReadOnlyList<string> Flags,
    string? Explanation)
{
    /// <summary>Whether the row carries a flag.</summary>
    /// <param name="flag">One of <see cref="CoverageFlags"/>.</param>
    /// <returns>True when set.</returns>
    public bool Has(string flag) => Flags.Contains(flag, StringComparer.Ordinal);
}

/// <summary>The coverage report's counts for one library and person.</summary>
/// <param name="Files">Rows counted (one per media source).</param>
/// <param name="Decided">Rows with a Babelfin decision (applied or dry run).</param>
/// <param name="NotDecided">Rows where Babelfin had nothing to say (off for the server or the person) or no decision was recorded.</param>
/// <param name="WantedAudioFound">Rows whose audio came from a matched step.</param>
/// <param name="FallbackUsed">Rows where an audio or subtitle fallback was used.</param>
/// <param name="FullSubtitles">Rows that show a full (or SDH) subtitle.</param>
/// <param name="PartialOnly">Rows that show a partial subtitle.</param>
/// <param name="NoSubtitles">Rows that wanted subtitles and show none.</param>
/// <param name="SubtitlesOff">Decided rows whose settings wanted no subtitles.</param>
/// <param name="BurnedIn">Rows with a burned-in wanted language.</param>
/// <param name="ImageSubtitles">Rows that show an image subtitle.</param>
/// <param name="OriginalNotUsual">Rows whose original language differs from the library's usual one.</param>
public sealed record CoverageCounts(
    int Files,
    int Decided,
    int NotDecided,
    int WantedAudioFound,
    int FallbackUsed,
    int FullSubtitles,
    int PartialOnly,
    int NoSubtitles,
    int SubtitlesOff,
    int BurnedIn,
    int ImageSubtitles,
    int OriginalNotUsual);

/// <summary>
/// The pure part of the simulator and the coverage report: one row (with its flags) from a recorded decision and its media source,
/// the counts over rows, and the CSV export. Reads only languages, kinds, formats and codecs from the engine's track list (it holds external file names).
/// </summary>
public static class Coverage
{
    /// <summary>Builds one row.</summary>
    /// <param name="item">The item's names and numbers.</param>
    /// <param name="source">The media source (its id; its streams are read only when Babelfin made no decision).</param>
    /// <param name="record">The recorded decision for the person, item and source, or null.</param>
    /// <param name="originalLanguage">The title's original language (any spelling the table resolves), or null when unknown.</param>
    /// <param name="libraryUsualLanguage">The library's usual original language, or null when unknown.</param>
    /// <param name="languages">The language table (null = the built-in table).</param>
    /// <returns>The row.</returns>
    public static CoverageRow BuildRow(CoverageItem item, MediaSourceInfo source, DecisionRecord? record, string? originalLanguage, string? libraryUsualLanguage, LanguageTable? languages = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(source);
        var table = languages ?? LanguageTable.Default;
        var original = table.Resolve(originalLanguage);
        var flags = new List<string>();
        if (original is not null && table.Resolve(libraryUsualLanguage) is { } usual && !SameSpokenLanguage(original, usual))
        {
            flags.Add(CoverageFlags.OriginalNotUsual);
        }

        var engine = record?.Decision?.Engine;
        var outcome = record?.Outcome.ToString() ?? "None";
        if (engine is null || record is null || record.Outcome == DecisionOutcome.PassedThrough)
        {
            return PassedThroughRow(item, source, record, outcome, original, flags, table);
        }

        var audio = engine.AudioIndex is int ai ? engine.Tracks.FirstOrDefault(t => t.Track.Type == TrackType.Audio && t.Track.Index == ai) : null;
        var subtitle = engine.SubtitleIndex is int si && si >= 0 ? engine.Tracks.FirstOrDefault(t => t.Track.Type == TrackType.Subtitle && t.Track.Index == si) : null;
        var audioLanguage = audio?.Language ?? AssumedAudioLanguage(engine, table);
        var trace = engine.Trace ?? Array.Empty<TraceLine>();
        bool Traced(string code) => trace.Any(l => string.Equals(l.Code, code, StringComparison.Ordinal));

        var burnedIn = Traced(TraceCodes.SubsBurnedIn);
        if (Traced(TraceCodes.AudioStepMatched))
        {
            flags.Add(CoverageFlags.WantedAudioFound);
        }

        if (Traced(TraceCodes.AudioFallback) || Traced(TraceCodes.SubsFallback))
        {
            flags.Add(CoverageFlags.FallbackUsed);
        }

        if (subtitle is not null && subtitle.SubtitleKind == SubtitleKind.Partial)
        {
            flags.Add(CoverageFlags.PartialChosen);
        }

        if (subtitle is not null && subtitle.Track.Format == SubtitleFormat.Image)
        {
            flags.Add(CoverageFlags.ImageSubtitles);
        }

        if (engine.SubtitlesWanted && subtitle is null && !burnedIn)
        {
            flags.Add(CoverageFlags.NoSubtitles);
        }

        if (burnedIn)
        {
            flags.Add(CoverageFlags.BurnedIn);
        }

        return new CoverageRow(
            item.ItemId.ToString("N", CultureInfo.InvariantCulture),
            item.Name,
            item.SeriesName,
            item.SeasonNumber,
            item.EpisodeNumber,
            source.Id ?? string.Empty,
            outcome,
            engine.AudioIndex,
            NameOf(table, audioLanguage),
            audioLanguage?.Code,
            subtitle?.Track.Index,
            NameOf(table, subtitle?.Language),
            subtitle?.Language?.Code,
            subtitle is null ? null : KindWord(subtitle.SubtitleKind),
            subtitle?.Track.Codec,
            engine.SubtitlesWanted,
            original?.Code,
            Ordered(flags),
            record.Decision?.Explanation);
    }

    /// <summary>The title's original language as the engine resolved it (trace <c>audio.original.known</c>), or null.</summary>
    /// <param name="decision">The engine decision, or null.</param>
    /// <returns>The canonical code, or null.</returns>
    public static string? OriginalLanguageOf(Decision? decision)
    {
        var line = decision?.Trace?.FirstOrDefault(l => string.Equals(l.Code, TraceCodes.OriginalKnown, StringComparison.Ordinal));
        return line?.Args is not null && line.Args.TryGetValue("language", out var code) && !string.IsNullOrWhiteSpace(code) ? code : null;
    }

    /// <summary>Counts rows for the coverage report.</summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The counts.</returns>
    public static CoverageCounts Count(IEnumerable<CoverageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int files = 0, decided = 0, wanted = 0, fallback = 0, full = 0, partial = 0, none = 0, off = 0, burned = 0, image = 0, notUsual = 0;
        foreach (var row in rows)
        {
            files++;
            var isDecided = row.Outcome is nameof(DecisionOutcome.Applied) or nameof(DecisionOutcome.DryRun);
            if (isDecided)
            {
                decided++;
            }

            wanted += row.Has(CoverageFlags.WantedAudioFound) ? 1 : 0;
            fallback += row.Has(CoverageFlags.FallbackUsed) ? 1 : 0;
            burned += row.Has(CoverageFlags.BurnedIn) ? 1 : 0;
            image += row.Has(CoverageFlags.ImageSubtitles) ? 1 : 0;
            notUsual += row.Has(CoverageFlags.OriginalNotUsual) ? 1 : 0;
            if (row.SubtitleIndex is not null)
            {
                if (string.Equals(row.SubtitleKind, "partial", StringComparison.Ordinal))
                {
                    partial++;
                }
                else
                {
                    full++;
                }
            }
            else if (row.Has(CoverageFlags.NoSubtitles))
            {
                none++;
            }
            else if (isDecided && !row.SubtitlesWanted)
            {
                off++;
            }
        }

        return new CoverageCounts(files, decided, files - decided, wanted, fallback, full, partial, none, off, burned, image, notUsual);
    }

    private static CoverageRow PassedThroughRow(CoverageItem item, MediaSourceInfo source, DecisionRecord? record, string outcome, LanguageTag? original, List<string> flags, LanguageTable table)
    {
        // Jellyfin's own choice stands: describe it from the source's streams.
        var audioIndex = record is null ? source.DefaultAudioStreamIndex : record.JellyfinAudioIndex;
        var subtitleIndex = record is null ? source.DefaultSubtitleStreamIndex : record.JellyfinSubtitleIndex;
        var audioStream = Stream(source, audioIndex, MediaStreamType.Audio);
        var subtitleStream = subtitleIndex is >= 0 ? Stream(source, subtitleIndex, MediaStreamType.Subtitle) : null;
        var audioTag = table.Resolve(audioStream?.Language);
        var subtitleTag = table.Resolve(subtitleStream?.Language);
        if (subtitleStream is not null && StreamMapper.FormatOf(subtitleStream) == SubtitleFormat.Image)
        {
            flags.Add(CoverageFlags.ImageSubtitles);
        }

        string? kind = null;
        if (subtitleStream is not null)
        {
            kind = subtitleStream.IsForced ? "partial" : subtitleStream.IsHearingImpaired ? "sdh" : "full";
        }

        if (kind == "partial")
        {
            flags.Add(CoverageFlags.PartialChosen);
        }

        return new CoverageRow(
            item.ItemId.ToString("N", CultureInfo.InvariantCulture),
            item.Name,
            item.SeriesName,
            item.SeasonNumber,
            item.EpisodeNumber,
            source.Id ?? string.Empty,
            outcome,
            audioStream?.Index ?? audioIndex,
            NameOf(table, audioTag),
            audioTag?.Code,
            subtitleStream?.Index,
            NameOf(table, subtitleTag),
            subtitleTag?.Code,
            kind,
            subtitleStream?.Codec,
            false,
            original?.Code,
            Ordered(flags),
            record?.Decision?.Explanation);
    }

    private static IReadOnlyList<string> Ordered(List<string> flags) => CoverageFlags.All.Where(flags.Contains).ToArray();

    private static string KindWord(SubtitleKind kind) => kind switch
    {
        SubtitleKind.Partial => "partial",
        SubtitleKind.Sdh => "sdh",
        _ => "full",
    };

    private static string? NameOf(LanguageTable table, LanguageTag? tag) => tag is null ? null : table.EnglishName(tag) ?? tag.Code;

    private static MediaStream? Stream(MediaSourceInfo source, int? index, MediaStreamType type)
        => index is null || source.MediaStreams is null ? null : source.MediaStreams.FirstOrDefault(s => s is not null && s.Index == index && s.Type == type);

    // An unlabelled audio track the engine assumed to be a language carries that language only in the trace.
    private static LanguageTag? AssumedAudioLanguage(Decision engine, LanguageTable table)
    {
        var line = engine.Trace?.FirstOrDefault(l => string.Equals(l.Code, TraceCodes.AudioUnlabelledAssumed, StringComparison.Ordinal) && l.TrackIndex == engine.AudioIndex);
        return line?.Args is not null && line.Args.TryGetValue("language", out var code) ? table.Resolve(code) : null;
    }

    // Spoken languages compare by base, and by variety when both name one (Cantonese is not Mandarin; plain "zh" matches either).
    private static bool SameSpokenLanguage(LanguageTag a, LanguageTag b)
        => string.Equals(a.Base, b.Base, StringComparison.OrdinalIgnoreCase)
           && (a.Variety is null || b.Variety is null || string.Equals(a.Variety, b.Variety, StringComparison.OrdinalIgnoreCase));
}
