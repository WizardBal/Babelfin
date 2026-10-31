using System;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Resolution;

/// <summary>Matches enabled, single positive genre rows against an item's genres.</summary>
public static class RuleMatcher
{
    /// <summary>Whether the row's single genre equals an item genre, ignoring case and surrounding whitespace.</summary>
    /// <param name="rule">The genre row.</param>
    /// <param name="context">The item.</param>
    /// <returns>True when the enabled genre row applies; false for other stored rule shapes.</returns>
    public static bool MatchesGenre(SmartRule rule, ItemContext context)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(context);
        if (!PersonMigrations.IsGenreRow(rule) || context.Genres is not { Count: > 0 })
        {
            return false;
        }

        var genre = rule.Conditions[0].Values[0].Trim();
        foreach (var itemGenre in context.Genres)
        {
            if (itemGenre is not null && string.Equals(itemGenre.Trim(), genre, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
