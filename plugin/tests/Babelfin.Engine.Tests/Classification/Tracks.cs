using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;

namespace Babelfin.Engine.Tests.Classification;

/// <summary>Builders for test tracks and the default classifier.</summary>
internal static class Tracks
{
    internal static readonly TrackClassifier Classifier = new(LanguageTable.Default, KeywordTables.Default);

    internal static Track Sub(
        int index,
        string? language,
        string? title,
        bool isDefault = false,
        bool forced = false,
        bool hearingImpaired = false,
        string? file = null,
        string? comment = null,
        SubtitleFormat format = SubtitleFormat.Unknown) =>
        new(index, TrackType.Subtitle, language, title, comment, file, null, null, isDefault, forced, hearingImpaired, false, file is not null, format);

    internal static Track Audio(int index, string? language, string? title, bool isDefault = false, int? channels = 2, string? comment = null, bool lossless = false) =>
        new(index, TrackType.Audio, language, title, comment, null, "aac", channels, isDefault, false, false, false, false, SubtitleFormat.Unknown, lossless);
}
