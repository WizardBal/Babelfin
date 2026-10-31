using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Configuration;

namespace Jellyfin.Plugin.Babelfin.Settings;

/// <summary>
/// The "Audio and subtitles" table's two modes: the same choice for every library (the Everyday layer's table fields),
/// or a choice per library (the library entries). Only one of the two counts; the other stays stored, so switching back restores it.
/// </summary>
public static class TableModes
{
    /// <summary>Gets the five fields the table edits.</summary>
    public static IReadOnlyList<string> Fields => PluginConfiguration.TrackDefaultFields;

    /// <summary>Whether a layer sets any of the table's fields.</summary>
    /// <param name="layer">The layer, or null.</param>
    /// <returns>True when at least one table field is set.</returns>
    public static bool HasTableFields(SettingsLayer? layer) => layer is not null && Fields.Any(layer.IsSet);

    /// <summary>The person's mode: their stored choice, else per library when any library entry sets a table field.</summary>
    /// <param name="person">The person's document.</param>
    /// <returns>True for "choose for each library".</returns>
    public static bool PersonPerLibrary(PersonSettings person) =>
        person.PerLibrary ?? (person.Libraries?.Any(l => HasTableFields(l?.Settings)) ?? false);

    /// <summary>The admin's mode: the stored choice, else per library when any library entry sets a table field.</summary>
    /// <param name="admin">The admin settings.</param>
    /// <returns>True for "choose for each library".</returns>
    public static bool AdminPerLibrary(PluginConfiguration admin) =>
        admin.PerLibraryDefaults ?? (admin.Libraries?.Any(l => HasTableFields(l?.Settings)) ?? false);

    /// <summary>A copy of a layer with only the table's fields (null when it sets none).</summary>
    /// <param name="layer">The layer, or null.</param>
    /// <returns>The copy, or null.</returns>
    public static SettingsLayer? TableOnly(SettingsLayer? layer)
    {
        if (!HasTableFields(layer))
        {
            return null;
        }

        var copy = new SettingsLayer();
        foreach (var field in Fields)
        {
            copy.CopyField(layer!, field);
        }

        return copy;
    }

    /// <summary>A copy of a layer without the table's fields (the layer itself when it sets none of them).</summary>
    /// <param name="layer">The layer, or null.</param>
    /// <returns>The copy, the layer, or null.</returns>
    public static SettingsLayer? WithoutTableFields(SettingsLayer? layer)
    {
        if (!HasTableFields(layer))
        {
            return layer;
        }

        var copy = layer!.Clone();
        foreach (var field in Fields)
        {
            copy.Clear(field);
        }

        return copy;
    }
}
