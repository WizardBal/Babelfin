using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MediaBrowser.Model.Dto;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Babelfin.Decisions;

namespace Jellyfin.Plugin.Babelfin.Library;

/// <summary>
/// What a recorded decision led to.
/// </summary>
public enum DecisionOutcome
{
    /// <summary>The decider had nothing to say; Jellyfin's choice stands.</summary>
    PassedThrough,

    /// <summary>Dry run: the choice was logged, not applied.</summary>
    DryRun,

    /// <summary>The choice was written into the media source.</summary>
    Applied,
}

/// <summary>
/// The last decision made for one person, item and media source.
/// </summary>
/// <param name="At">When it was made (UTC).</param>
/// <param name="JellyfinAudioIndex">Jellyfin's own audio index.</param>
/// <param name="JellyfinSubtitleIndex">Jellyfin's own subtitle index.</param>
/// <param name="Decision">Babelfin's decision, or null when it had nothing to say.</param>
/// <param name="Outcome">What happened to it.</param>
/// <param name="PreviousServedAt">For a served record, when the same person, item, source, device and app were last served before this response within the 30-minute served window; otherwise null.</param>
public sealed record DecisionRecord(DateTime At, int? JellyfinAudioIndex, int? JellyfinSubtitleIndex, TrackDecision? Decision, DecisionOutcome Outcome, DateTime? PreviousServedAt = null);

/// <summary>
/// Counters and recent decisions of the <see cref="BabelfinMediaSourceManager"/>. Thread-safe.
/// </summary>
public sealed class DecoratorStats
{
    /// <summary>The most decisions kept for Explain.</summary>
    public const int DecisionCacheCap = 2000;

    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId, string SourceId), DecisionRecord> _decisions = new();

    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId, string SourceId, string DeviceId, string Client), DecisionRecord> _served = new();
    private readonly ConditionalWeakTable<MediaSourceInfo, SourceDecision> _sources = new();
    private readonly Func<DateTime> _clock;
    private readonly object _servedGate = new();
    private DateTime _lastServedSweep;

    /// <summary>Initializes counters with the system UTC clock.</summary>
    public DecoratorStats() : this(() => DateTime.UtcNow) { }

    /// <summary>Initializes counters with a testable UTC clock.</summary>
    public DecoratorStats(Func<DateTime> clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private sealed record SourceDecision(Guid UserId, Guid ItemId);

    private long _staticCalls;
    private long _playbackCalls;
    private long _setDefaultsCalls;
    private long _applied;
    private long _dryRun;
    private long _passedThrough;
    private long _errors;
    private volatile bool _wrapped;
    private volatile string _innerTypeName = string.Empty;

    /// <summary>Gets a value indicating whether Jellyfin's media source manager was wrapped.</summary>
    public bool Wrapped => _wrapped;

    /// <summary>Gets the type name of the wrapped implementation.</summary>
    public string InnerTypeName => _innerTypeName;

    /// <summary>Gets the number of GetStaticMediaSources calls with a user.</summary>
    public long StaticCalls => Interlocked.Read(ref _staticCalls);

    /// <summary>Gets the number of GetPlaybackMediaSources calls with a user.</summary>
    public long PlaybackCalls => Interlocked.Read(ref _playbackCalls);

    /// <summary>Gets the number of SetDefaultAudioAndSubtitleStreamIndices calls.</summary>
    public long SetDefaultsCalls => Interlocked.Read(ref _setDefaultsCalls);

    /// <summary>Gets the number of sources Babelfin changed.</summary>
    public long Applied => Interlocked.Read(ref _applied);

    /// <summary>Gets the number of dry-run decisions.</summary>
    public long DryRun => Interlocked.Read(ref _dryRun);

    /// <summary>Gets the number of sources left as Jellyfin made them.</summary>
    public long PassedThrough => Interlocked.Read(ref _passedThrough);

    /// <summary>Gets the number of errors caught.</summary>
    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>Gets the number of decisions held for Explain.</summary>
    public int DecisionCount => _decisions.Count;

    /// <summary>Records that the media source manager was wrapped.</summary>
    /// <param name="innerTypeName">The wrapped implementation's type name.</param>
    public void MarkWrapped(string innerTypeName)
    {
        _innerTypeName = innerTypeName ?? string.Empty;
        _wrapped = true;
    }

    /// <summary>Counts a GetStaticMediaSources call.</summary>
    public void CountStatic() => Interlocked.Increment(ref _staticCalls);

    /// <summary>Counts a GetPlaybackMediaSources call.</summary>
    public void CountPlayback() => Interlocked.Increment(ref _playbackCalls);

    /// <summary>Counts a SetDefaultAudioAndSubtitleStreamIndices call.</summary>
    public void CountSetDefaults() => Interlocked.Increment(ref _setDefaultsCalls);

    /// <summary>Counts an error.</summary>
    public void CountError() => Interlocked.Increment(ref _errors);

    /// <summary>
    /// Counts and stores a decision.
    /// </summary>
    /// <param name="userId">The person.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="sourceId">The media source id.</param>
    /// <param name="record">The decision.</param>
    public void Record(Guid userId, Guid itemId, string? sourceId, DecisionRecord record, MediaSourceInfo? source = null)
    {
        switch (record.Outcome)
        {
            case DecisionOutcome.Applied:
                Interlocked.Increment(ref _applied);
                break;
            case DecisionOutcome.DryRun:
                Interlocked.Increment(ref _dryRun);
                break;
            default:
                Interlocked.Increment(ref _passedThrough);
                break;
        }

        if (_decisions.Count >= DecisionCacheCap)
        {
            // Bounded: drop an arbitrary batch of the oldest entries.
            foreach (var key in _decisions.OrderBy(p => p.Value.At).Take(DecisionCacheCap / 10).Select(p => p.Key).ToList())
            {
                _decisions.TryRemove(key, out _);
            }
        }

        _decisions[(userId, itemId, Normalize(sourceId))] = record;
        if (source is not null)
        {
            _sources.AddOrUpdate(source, new SourceDecision(userId, itemId));
        }
    }

    /// <summary>
    /// Gets the last decision for a person, item and media source.
    /// </summary>
    /// <param name="userId">The person.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="sourceId">The media source id.</param>
    /// <returns>The decision, or null when none is recorded.</returns>
    public DecisionRecord? GetDecision(Guid userId, Guid itemId, string? sourceId)
        => _decisions.TryGetValue((userId, itemId, Normalize(sourceId)), out var record) ? record : null;

    /// <summary>Records only sources included in an actual successful response. Internal/simulator lookups alone never call this.</summary>
    public void RecordResponse(Guid userId, object? value, string? deviceId = null, string? client = null)
    {
        if (value is null || userId == Guid.Empty || string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(client))
        {
            return;
        }

        if (value is MediaSourceInfo source)
        {
            if (_sources.TryGetValue(source, out var provenance) && provenance.UserId == userId)
            {
                var now = _clock();
                lock (_servedGate)
                {
                    if (now - _lastServedSweep >= TimeSpan.FromMinutes(1))
                    {
                        foreach (var expired in _served.Where(p => p.Value.At <= now.AddMinutes(-30)).Select(p => p.Key).ToList())
                        {
                            _served.TryRemove(expired, out _);
                        }

                        _lastServedSweep = now;
                    }

                    var key = (userId, provenance.ItemId, Normalize(source.Id), deviceId.Trim(), NormalizeClient(client));
                    if (!_served.ContainsKey(key) && _served.Count >= DecisionCacheCap)
                    {
                        foreach (var oldest in _served.OrderBy(p => p.Value.At).Take(DecisionCacheCap / 10).Select(p => p.Key).ToList())
                        {
                            _served.TryRemove(oldest, out _);
                        }
                    }

                    DateTime? previous = _served.TryGetValue(key, out var earlier) && earlier.At > now.AddMinutes(-30) ? earlier.At : null;

                    // These are the defaults actually returned, after the controller's changes as well as ours.
                    _served[key] = new DecisionRecord(now, source.DefaultAudioStreamIndex, source.DefaultSubtitleStreamIndex, null, DecisionOutcome.PassedThrough, previous);
                }
            }

            return;
        }

        if (value is IEnumerable sequence && value is not string)
        {
            foreach (var entry in sequence)
            {
                RecordResponse(userId, entry, deviceId, client);
            }

            return;
        }

        // Jellyfin's item/list and PlaybackInfo response envelopes. Do not walk arbitrary object graphs or simulator reports.
        foreach (var name in new[] { "MediaSources", "Items" })
        {
            var property = value.GetType().GetProperty(name);
            if (property?.GetIndexParameters().Length == 0 && property.GetValue(value) is IEnumerable children)
            {
                RecordResponse(userId, children, deviceId, client);
            }
        }
    }

    /// <summary>Gets what was actually served to this person on this device and app, with a hard 30-minute expiry.
    /// Missing identity never falls back to another device's picker.</summary>
    public DecisionRecord? GetServed(Guid userId, Guid itemId, string? sourceId, string? deviceId = null, string? client = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(client)) return null;
        var key = (userId, itemId, Normalize(sourceId), deviceId.Trim(), NormalizeClient(client));
        lock (_servedGate)
        {
            if (_served.TryGetValue(key, out var record) && record.At > _clock().AddMinutes(-30))
            {
                return record;
            }

            _served.TryRemove(key, out _);
            return null;
        }
    }

    // Device IDs are opaque, case-sensitive identifiers; app names follow the config's case-insensitive comparison.
    private static string NormalizeClient(string? client) => (client ?? string.Empty).Trim().ToUpperInvariant();

    private static string Normalize(string? sourceId) => (sourceId ?? string.Empty).ToUpperInvariant();

}
