using System;
using System.Text.Json;

namespace Jellyfin.Plugin.Babelfin.Settings;

/// <summary>Explicit override saves supersede memory only for the audio or subtitle settings that changed.</summary>
internal static class OverrideEdits
{
    /// <summary>Copies memory, clearing only the track choices superseded by changed explicit settings.</summary>
    internal static ManualMemory? AfterSettingsChange(SettingsLayer? previous, SettingsLayer next, ManualMemory? memory)
    {
        if (memory is null)
        {
            return null;
        }

        previous ??= new SettingsLayer();
        var copy = memory.Clone();
        if (!Same(previous.AudioChain, next.AudioChain) || previous.ForeignTitles != next.ForeignTitles)
        {
            copy.Audio = null;
        }

        if (previous.WhenAudioUnderstood != next.WhenAudioUnderstood
            || previous.WhenAudioNotUnderstood != next.WhenAudioNotUnderstood
            || !Same(previous.SubtitleLanguages, next.SubtitleLanguages))
        {
            copy.Subtitle = null;
            copy.SubtitlesOff = false;
        }

        return copy.Audio is null && copy.Subtitle is null && !copy.SubtitlesOff ? null : copy;
    }

    private static bool Same<T>(T before, T after)
        => string.Equals(JsonSerializer.Serialize(before, BabelfinJson.Options), JsonSerializer.Serialize(after, BabelfinJson.Options), StringComparison.Ordinal);
}
