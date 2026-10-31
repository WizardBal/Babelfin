using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Babelfin.Configuration;

public partial class AppMode
{
    /// <summary>Gets or sets whether differing pre-play picks are remembered. Null uses the shipped behavior for this app.</summary>
    public bool? RememberPrePlayChoices { get; set; }

    /// <summary>Gets or sets whether this row was discovered and may be evicted. Admin-edited rows must set this to false.</summary>
    public bool SeenOnly { get; set; }
}

public partial class PluginConfiguration
{
    /// <summary>Whether this app fills its pre-play picker from server defaults (unless the admin overrides it).</summary>
    public bool RemembersPrePlayChoices(string? client)
        => FindAppMode(client)?.RememberPrePlayChoices ?? ShippedRememberPrePlayChoices(client);

    /// <summary>The shipped jellyfin-web family; other apps need an explicit opt-in.</summary>
    public static bool ShippedRememberPrePlayChoices(string? client)
        => client?.Trim().ToUpperInvariant() is "JELLYFIN WEB" or "JELLYFIN MEDIA PLAYER" or "JELLYFIN DESKTOP" or "JELLYFIN ANDROID";

    /// <summary>Updates discovery metadata without changing admin rows or exceeding the validator's limits. Caller serializes and saves.</summary>
    public bool NoteAppSeen(string? client, DateTime now)
    {
        if (ConfigVersion > CurrentVersion)
        {
            return false;
        }

        client = client?.Trim();
        if (string.IsNullOrEmpty(client) || client.Length > 100)
        {
            return false;
        }

        var row = FindAppMode(client);
        if (row is not null)
        {
            row.Seen = true;
            row.LastSeen = now;
            return true;
        }

        var rows = new List<AppMode>(AppModes ?? new List<AppMode>());
        // Reserve room for explicit rows; discovered rows have their own, smaller cap.
        while (rows.Count >= 100 || rows.Count(a => a.SeenOnly) >= 50)
        {
            var oldest = rows.Where(a => a.SeenOnly).OrderBy(a => a.LastSeen ?? DateTime.MinValue).FirstOrDefault();
            if (oldest is null)
            {
                return false;
            }

            rows.Remove(oldest);
        }

        rows.Add(new AppMode { Client = client, Mode = UnknownAppMode, Seen = true, SeenOnly = true, LastSeen = now });
        AppModes = rows;
        return true;
    }
}
