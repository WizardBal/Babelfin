using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Activity;

/// <summary>Recent activity held in memory until the server restarts.</summary>
public sealed class ActivityLog : IActivityLog
{
    /// <summary>The maximum number of entries retained.</summary>
    public const int RingSize = 5000;
    private readonly object _gate = new();
    private readonly Queue<ActivityEntry> _ring = new();
    private readonly HashSet<Guid> _blocked = new();

    /// <inheritdoc />
    public void Add(ActivityEntry entry)
    {
        try
        {
            if (entry is null) return;
            var at = entry.At == default ? DateTime.UtcNow : entry.At;
            at = at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : DateTime.SpecifyKind(at, DateTimeKind.Utc);
            var normalized = entry with { At = at, Kind = string.IsNullOrWhiteSpace(entry.Kind) ? "other" : entry.Kind, Text = entry.Text ?? string.Empty };
            lock (_gate)
            {
                if (normalized.UserId is { } id && _blocked.Contains(id)) return;
                _ring.Enqueue(normalized);
                if (_ring.Count > RingSize) _ring.Dequeue();
            }
        }
        catch (Exception) { }
    }

    /// <inheritdoc />
    public IReadOnlyList<ActivityEntry> Query(int limit, string? kind = null, Guid? userId = null)
    {
        if (limit <= 0) return Array.Empty<ActivityEntry>();
        lock (_gate)
        {
            var entries = _ring.ToArray();
            var result = new List<ActivityEntry>(Math.Min(limit, entries.Length));
            for (var i = entries.Length - 1; i >= 0 && result.Count < limit; i--)
            {
                var entry = entries[i];
                if (!string.IsNullOrEmpty(kind) && !string.Equals(kind, entry.Kind, StringComparison.OrdinalIgnoreCase)) continue;
                if (userId is not null && entry.UserId != userId) continue;
                result.Add(entry);
            }
            return result;
        }
    }

    /// <summary>Removes a person's history and rejects further entries until reactivation.</summary>
    public void RemoveUser(Guid userId)
    {
        lock (_gate)
        {
            _blocked.Add(userId);
            var count = _ring.Count;
            for (var i = 0; i < count; i++)
            {
                var entry = _ring.Dequeue();
                if (entry.UserId != userId) _ring.Enqueue(entry);
            }
        }
    }

    /// <summary>Allows entries for a reactivated person.</summary>
    public void AllowUser(Guid userId) { lock (_gate) _blocked.Remove(userId); }
}
