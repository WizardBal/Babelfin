using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Babelfin.Settings;

/// <summary>
/// The settings only the admin sets (admin page, Track recognition): they describe the files rather than a person's preferences, so the
/// resolver ignores them in every layer a person owns (their everyday layer, library entries, title, season, show and collection overrides
/// and smart rules). The admin's layers and the admin's locks for a person still set them. Values a person's document already holds stay
/// stored as they are; they are not used.
/// </summary>
public static class AdminOnlyFields
{
    /// <summary>Gets the admin-only fields: what an audio track with no language is assumed to be (<see cref="SettingsFields.UnlabelledAudio"/>,
    /// which carries <see cref="SettingsLayer.UnlabelledAudioLanguage"/> with it).</summary>
    public static IReadOnlyList<string> All { get; } = new[] { SettingsFields.UnlabelledAudio };

    /// <summary>Whether a layer sets any admin-only field.</summary>
    /// <param name="layer">The layer, or null.</param>
    /// <returns>True when at least one admin-only field is set.</returns>
    public static bool AnySet(SettingsLayer? layer) => layer is not null && All.Any(layer.IsSet);

    /// <summary>A copy of a person's layer without the admin-only fields (the layer itself when it sets none of them).</summary>
    /// <param name="layer">The layer, or null.</param>
    /// <returns>The copy, the layer, or null.</returns>
    public static SettingsLayer? Without(SettingsLayer? layer)
    {
        if (!AnySet(layer))
        {
            return layer;
        }

        var copy = layer!.Clone();
        foreach (var field in All)
        {
            copy.Clear(field);
        }

        return copy;
    }
}
