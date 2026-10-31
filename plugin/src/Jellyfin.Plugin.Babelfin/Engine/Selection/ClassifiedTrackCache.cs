using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Babelfin.Engine.Classification;

namespace Jellyfin.Plugin.Babelfin.Engine.Selection;

/// <summary>Bounded classification cache. An engine instance owns its recognition settings and this cache.</summary>
internal sealed class ClassifiedTrackCache
{
    private const int MaxEntries = 512;
    private const long LifetimeMilliseconds = 600000;
    private readonly ConcurrentDictionary<Key, Entry> _entries = new();
    private readonly object _missGate = new();
    private long _insertionOrder;

    internal IReadOnlyList<ClassifiedTrack> Get(IReadOnlyList<Track> tracks, string? sourceId, TrackClassifier classifier)
    {
        var hash = new HashCode();
        for (var i = 0; i < tracks.Count; i++)
        {
            hash.Add(tracks[i]);
        }

        var key = new Key(sourceId ?? string.Empty, tracks.Count, hash.ToHashCode());
        var now = Environment.TickCount64;
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > now && entry.Input.SequenceEqual(tracks))
        {
            return entry.Classified;
        }

        var snapshot = tracks.ToArray();
        var result = Array.AsReadOnly(classifier.ClassifyAll(snapshot).OrderBy(t => t.Track.Index).ToArray());
        lock (_missGate)
        {
            if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
            {
                // Serialize insertion and eviction only; cached reads remain lock-free.
                // A clock tick can cover many inserts, so equal expiries retain their insertion order.
                foreach (var old in _entries.OrderBy(p => p.Value.ExpiresAt).ThenBy(p => p.Value.InsertionOrder).Take(Math.Max(1, MaxEntries / 8)))
                    _entries.TryRemove(old.Key, out _);
            }
            _entries[key] = new Entry(snapshot, result, now + LifetimeMilliseconds, ++_insertionOrder);
        }
        return result;
    }

    private readonly record struct Key(string SourceId, int Count, int Hash);
    private sealed record Entry(Track[] Input, IReadOnlyList<ClassifiedTrack> Classified, long ExpiresAt, long InsertionOrder);
}

/// <summary>Detailed trace generation is deferred until a diagnostic endpoint or log asks for it.</summary>
internal sealed class DeferredTrace : IReadOnlyList<TraceLine>
{
    private readonly Lazy<IReadOnlyList<TraceLine>> _value;
    internal DeferredTrace(Func<IReadOnlyList<TraceLine>> build)
        => _value = new Lazy<IReadOnlyList<TraceLine>>(build, LazyThreadSafetyMode.ExecutionAndPublication);
    public int Count => _value.Value.Count;
    public TraceLine this[int index] => _value.Value[index];
    public IEnumerator<TraceLine> GetEnumerator() => _value.Value.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
