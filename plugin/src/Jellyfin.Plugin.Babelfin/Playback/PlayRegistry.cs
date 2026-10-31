using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Playback;

/// <summary>
/// A track change made during play and not remembered yet: it is remembered once it has stayed selected for
/// <see cref="PlaybackMonitor.SettlePeriod"/>, and dropped when another track is selected before then.
/// </summary>
/// <param name="Index">The track the change selected (subtitles: negative = off).</param>
/// <param name="Previous">The track before the first of this stream's unsettled changes (subtitles: null = unknown, negative = off).
/// A return to it drops the change.</param>
/// <param name="Since">When <paramref name="Index"/> was selected (UTC).</param>
/// <param name="Order">The order in which the change was observed; a newer remembered change is never overwritten by it.</param>
public sealed record PendingTrackChange(int? Index, int? Previous, DateTime Since, ManualChangeOrder Order);

/// <summary>The in-play track changes of one play that stayed selected for <see cref="PlaybackMonitor.SettlePeriod"/>: what is remembered.</summary>
/// <param name="UserId">The person.</param>
/// <param name="ItemId">The item played.</param>
/// <param name="MediaSourceId">The media source the indices refer to.</param>
/// <param name="Client">The app.</param>
/// <param name="Audio">The settled audio change, or null.</param>
/// <param name="Subtitle">The settled subtitle change, or null.</param>
public sealed record SettledChange(Guid UserId, Guid? ItemId, string? MediaSourceId, string? Client, PendingTrackChange? Audio, PendingTrackChange? Subtitle);

/// <summary>
/// One play as the registry tracks it. A mutable record: the registry hands out copies, and changes go through
/// <see cref="PlayRegistry.Update(string, Action{PlayRecord})"/> under its lock.
/// </summary>
public sealed class PlayRecord
{
    // Copied with the record; renewed on reset so an in-flight lookup cannot populate a newer play.
    internal Guid PlayId { get; private set; } = Guid.NewGuid();

    // The expectation last evaluated by the monitor and whether that target was actually reported.
    // PlaybackInfo can replace expectations at any time; the monitor notices without a filter callback.
    internal int? ReportExpectedAudio { get; set; }

    internal int? ReportExpectedSubtitle { get; set; }

    internal bool ExpectedAudioAcknowledged { get; set; }

    internal bool ExpectedSubtitleAcknowledged { get; set; }

    // A trusted startup report can differ from Babelfin's choice. Its later first application
    // is an acknowledgement, even though the app's initial baseline was trusted.
    internal int? ReportChoiceAudio { get; set; }

    internal int? ReportChoiceSubtitle { get; set; }

    internal bool ChoiceAudioAcknowledged { get; set; }

    internal bool ChoiceSubtitleAcknowledged { get; set; }

    // A real manual pick retires pending startup acknowledgements for that stream only.
    internal long ManualAudioRevision { get; set; }

    internal long ManualSubtitleRevision { get; set; }

    internal ManualChangeOrder? ManualAudioOrder { get; set; }

    internal ManualChangeOrder? ManualSubtitleOrder { get; set; }

    // Identifies a still-unstarted play prepared by PlaybackInfo, rather than a progress or stop event.
    internal ManualChangeOrder? PreparationOrder { get; set; }

    internal bool ManualAudioChanged { get; set; }

    internal bool ManualSubtitleChanged { get; set; }

    internal DateTime? FirstIndexedAt { get; set; }

    internal DateTime? LastAudioChangedAt { get; set; }

    internal DateTime? LastSubtitleChangedAt { get; set; }

    internal int? LastObservedAudio { get; set; }

    internal DateTime? AudioCommandSentAt { get; set; }

    internal DateTime? SubtitleCommandSentAt { get; set; }

    // In-play changes waiting to settle before they are remembered; the play's expectations already follow them.
    internal PendingTrackChange? PendingAudio { get; set; }

    internal PendingTrackChange? PendingSubtitle { get; set; }

    /// <summary>Gets or sets the Jellyfin session id, or null for an entry keyed by device, client and item (a PlaybackInfo before any session).</summary>
    public string? SessionId { get; set; }

    /// <summary>Gets or sets the person.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the app name.</summary>
    public string? Client { get; set; }

    /// <summary>Gets or sets the device id.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Gets or sets the device name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Gets or sets the item playing (or last played).</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets the item's name (display only).</summary>
    public string? ItemName { get; set; }

    /// <summary>Gets or sets the media source.</summary>
    public string? MediaSourceId { get; set; }

    /// <summary>Gets or sets a value indicating whether expected tracks are known for this play.</summary>
    public bool HasExpected { get; set; }

    /// <summary>Gets or sets whether the monitor attempted its fallback choice for this play and media source, including an empty or failed lookup.</summary>
    public bool ChoiceLookupAttempted { get; set; }

    /// <summary>Gets or sets the audio index Babelfin trusted or set.</summary>
    public int? ExpectedAudio { get; set; }

    /// <summary>Gets or sets the expected subtitle index (-1 = off).</summary>
    public int? ExpectedSubtitle { get; set; }

    /// <summary>Gets or sets Babelfin's own choice for this play (audio), when known.</summary>
    public int? ChoiceAudio { get; set; }

    /// <summary>Gets or sets Babelfin's own choice for this play (subtitles, -1 = off), when known.</summary>
    public int? ChoiceSubtitle { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is playing (after a start/progress report and before a stop).</summary>
    public bool Playing { get; set; }

    /// <summary>Gets or sets when the play started (the first start or progress report, UTC).</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Gets or sets the last progress report (UTC).</summary>
    public DateTime? LastProgressAt { get; set; }

    /// <summary>Gets or sets a value indicating whether a start or progress report with indices was seen during this play.</summary>
    public bool HasReport { get; set; }

    /// <summary>Gets or sets the last reported audio index.</summary>
    public int? LastReportedAudio { get; set; }

    /// <summary>Gets or sets the last reported subtitle index.</summary>
    public int? LastReportedSubtitle { get; set; }

    /// <summary>Gets or sets a value indicating whether the app's own choice was trusted (such a play is never corrected).</summary>
    public bool Trusted { get; set; }

    /// <summary>Gets or sets a value indicating whether a manual change was recorded during this play.</summary>
    public bool ManualChanged { get; set; }

    /// <summary>Gets or sets when the most recent track-switch command was sent, for diagnostics. Each stream has its own command lifetime.</summary>
    public DateTime? CorrectionSentAt { get; set; }

    /// <summary>Gets or sets the audio index Babelfin asked the app to switch to.</summary>
    public int? CommandedAudio { get; set; }

    /// <summary>Gets or sets the subtitle index Babelfin asked the app to switch to.</summary>
    public int? CommandedSubtitle { get; set; }

    /// <summary>Gets or sets the item the same session played last, once it stopped.</summary>
    public Guid? LastStoppedItemId { get; set; }

    /// <summary>Gets or sets when that item stopped (UTC).</summary>
    public DateTime? LastStoppedAt { get; set; }

    /// <summary>Gets or sets the last time anything touched the entry (UTC; the sweep and the bound use it).</summary>
    public DateTime Touched { get; set; }

    internal bool IsAudioCommandEcho(int? audio, DateTime now)
        => audio.HasValue && audio == CommandedAudio && LiveCommand(AudioCommandSentAt, now);

    internal bool IsSubtitleCommandEcho(int? subtitle, DateTime now)
        => subtitle.HasValue && CommandedSubtitle.HasValue && Math.Max(-1, subtitle.Value) == Math.Max(-1, CommandedSubtitle.Value)
            && LiveCommand(SubtitleCommandSentAt, now);

    internal void ClearAudioCommand()
    {
        CommandedAudio = null;
        AudioCommandSentAt = null;
    }

    internal void ClearSubtitleCommand()
    {
        CommandedSubtitle = null;
        SubtitleCommandSentAt = null;
    }

    internal void ExpireCommands(DateTime now)
    {
        if (AudioCommandSentAt is { } audioAt && now - audioAt > TimeSpan.FromSeconds(30)) ClearAudioCommand();
        if (SubtitleCommandSentAt is { } subtitleAt && now - subtitleAt > TimeSpan.FromSeconds(30)) ClearSubtitleCommand();
    }

    internal void ObserveAudio(int? audio, DateTime now)
    {
        if (!audio.HasValue) return;
        if (LastObservedAudio.HasValue && audio != LastObservedAudio)
        {
            LastAudioChangedAt = now;
            LastSubtitleChangedAt = null;
        }
        LastObservedAudio = audio;
    }

    internal bool IsSubtitleReset(int? subtitle, DateTime now)
        => subtitle is < 0 && LastAudioChangedAt is { } audioAt
            && now >= audioAt && now - audioAt <= TimeSpan.FromSeconds(10)
            && !(LastSubtitleChangedAt is { } subtitleAt && subtitleAt >= audioAt);

    /// <summary>Holds an in-play audio change until it settles. A newer change replaces the held one and keeps its starting track;
    /// a return to that track drops it.</summary>
    /// <returns>True when a change is held; false when the change returned to the starting track.</returns>
    internal bool HoldAudioChange(int? audio, int? previous, DateTime now, ManualChangeOrder order)
    {
        var from = PendingAudio is { } held ? held.Previous : previous;
        PendingAudio = from.HasValue && audio == from ? null : new PendingTrackChange(audio, from, now, order);
        return PendingAudio is not null;
    }

    /// <summary>Holds an in-play subtitle change until it settles, as <see cref="HoldAudioChange"/> does for audio (every negative index is "off").</summary>
    /// <returns>True when a change is held; false when the change returned to the starting track.</returns>
    internal bool HoldSubtitleChange(int? subtitle, int? previous, DateTime now, ManualChangeOrder order)
    {
        var from = PendingSubtitle is { } held ? held.Previous : previous;
        PendingSubtitle = from.HasValue && Off(subtitle) == Off(from) ? null : new PendingTrackChange(subtitle, from, now, order);
        return PendingSubtitle is not null;
    }

    /// <summary>
    /// Takes the held changes that have settled: still the last reported track and selected for at least
    /// <see cref="PlaybackMonitor.SettlePeriod"/>. A held change whose track is no longer the last reported one is dropped.
    /// </summary>
    /// <param name="now">The time of the report or of the play's end (UTC).</param>
    /// <param name="audio">Whether a held audio change can settle now: a report named the audio track, or the play ended.</param>
    /// <param name="subtitle">Whether a held subtitle change can settle now.</param>
    /// <param name="dropped">Whether a held change was dropped.</param>
    /// <returns>The settled changes, or null when none settled.</returns>
    internal SettledChange? TakeSettled(DateTime now, bool audio, bool subtitle, out bool dropped)
    {
        dropped = false;
        PendingTrackChange? settledAudio = null, settledSubtitle = null;
        if (PendingAudio is { } heldAudio)
        {
            if (LastReportedAudio != heldAudio.Index)
            {
                PendingAudio = null;
                dropped = true;
            }
            else if (audio && now - heldAudio.Since >= PlaybackMonitor.SettlePeriod)
            {
                settledAudio = heldAudio;
                PendingAudio = null;
            }
        }

        if (PendingSubtitle is { } heldSubtitle)
        {
            if (!LastReportedSubtitle.HasValue || Off(LastReportedSubtitle) != Off(heldSubtitle.Index))
            {
                PendingSubtitle = null;
                dropped = true;
            }
            else if (subtitle && now - heldSubtitle.Since >= PlaybackMonitor.SettlePeriod)
            {
                settledSubtitle = heldSubtitle;
                PendingSubtitle = null;
            }
        }

        return settledAudio is null && settledSubtitle is null
            ? null
            : new SettledChange(UserId, ItemId, MediaSourceId, Client, settledAudio, settledSubtitle);
    }

    private static int Off(int? subtitle) => subtitle is { } s && s >= 0 ? s : -1;

    private static bool LiveCommand(DateTime? sentAt, DateTime now)
        => sentAt is { } sent && now >= sent && now - sent <= TimeSpan.FromSeconds(30);

    /// <summary>Clears the fields of the current play, keeping the session's identity and the last stop.</summary>
    public void ResetPlay()
    {
        HasExpected = false;
        ChoiceLookupAttempted = false;
        PlayId = Guid.NewGuid();
        ReportExpectedAudio = null;
        ReportExpectedSubtitle = null;
        ExpectedAudioAcknowledged = false;
        ExpectedSubtitleAcknowledged = false;
        ReportChoiceAudio = null;
        ReportChoiceSubtitle = null;
        ChoiceAudioAcknowledged = false;
        ChoiceSubtitleAcknowledged = false;
        MediaSourceId = null;
        ExpectedAudio = null;
        ExpectedSubtitle = null;
        ChoiceAudio = null;
        ChoiceSubtitle = null;
        Playing = false;
        StartedAt = null;
        LastProgressAt = null;
        HasReport = false;
        LastReportedAudio = null;
        LastReportedSubtitle = null;
        Trusted = false;
        ManualChanged = false;
        ManualAudioChanged = false;
        ManualSubtitleChanged = false;
        ManualAudioRevision = 0;
        ManualSubtitleRevision = 0;
        ManualAudioOrder = null;
        ManualSubtitleOrder = null;
        PreparationOrder = null;
        FirstIndexedAt = null;
        LastAudioChangedAt = null;
        LastSubtitleChangedAt = null;
        LastObservedAudio = null;
        PendingAudio = null;
        PendingSubtitle = null;
        CorrectionSentAt = null;
        ClearAudioCommand();
        ClearSubtitleCommand();
    }

    /// <summary>A copy.</summary>
    /// <returns>The copy.</returns>
    public PlayRecord Clone() => (PlayRecord)MemberwiseClone();

    /// <summary>The dashboard's view of the entry.</summary>
    /// <returns>The entry.</returns>
    public PlayEntry ToEntry() => new(
        SessionId ?? string.Empty,
        UserId,
        Client,
        DeviceName,
        ItemId,
        ItemName,
        MediaSourceId,
        ExpectedAudio,
        ExpectedSubtitle,
        LastReportedAudio,
        LastReportedSubtitle,
        StartedAt,
        LastProgressAt,
        Trusted,
        ManualChanged,
        CorrectionSentAt);
}

/// <summary>
/// The in-memory play registry: one entry per Jellyfin session, plus "pending" entries keyed by
/// <c>deviceId|client|itemId</c> for a PlaybackInfo that arrives before Jellyfin has a session (adopted by the session's first
/// start or progress report). Thread-safe (one lock; every change is a short in-memory update), bounded to
/// <see cref="MaxEntries"/> entries (the least recently touched are dropped), and swept of entries untouched for
/// <see cref="MaxAge"/> (at most every <see cref="SweepInterval"/>, on writes).
/// </summary>
/// <remarks>
/// Entries are swept by last activity, not by start time, so a long film is never
/// forgotten mid-play; the sweep runs opportunistically on writes (no timer, nothing to dispose); a pending entry counts
/// toward the bound like any other; the dashboard snapshot lists pending entries with an empty session id.
/// </remarks>
public sealed class PlayRegistry : IPlayRegistry
{
    /// <summary>The most entries kept (sessions and pending entries together).</summary>
    public const int MaxEntries = 500;

    /// <summary>Entries untouched for this long are swept.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>The sweep runs at most this often.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly Dictionary<string, PlayRecord> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayRecord> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _clock;
    private DateTime _lastSweep;

    /// <summary>Initializes a new instance of the <see cref="PlayRegistry"/> class with the system clock.</summary>
    public PlayRegistry()
        : this(() => DateTime.UtcNow)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PlayRegistry"/> class.</summary>
    /// <param name="clock">The clock (UTC); tests pass their own.</param>
    public PlayRegistry(Func<DateTime> clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _lastSweep = _clock();
    }

    /// <summary>Gets the current time (UTC) as the registry sees it.</summary>
    public DateTime Now => _clock();

    /// <summary>Gets the number of entries (sessions and pending).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Count + _pending.Count;
            }
        }
    }

    /// <summary>The key of a pending entry.</summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="client">The client name.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The key.</returns>
    public static string PendingKey(string? deviceId, string? client, Guid itemId)
        => (deviceId ?? string.Empty).Trim() + "|" + (client ?? string.Empty).Trim() + "|" + itemId.ToString("N");

    /// <inheritdoc />
    public IReadOnlyList<PlayEntry> Snapshot()
    {
        lock (_lock)
        {
            return _sessions.Values.Concat(_pending.Values)
                .OrderByDescending(r => r.Touched)
                .Select(r => r.ToEntry())
                .ToList();
        }
    }

    /// <summary>Gets a copy of a session's entry.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The copy, or null.</returns>
    public PlayRecord? Get(string sessionId)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var r) ? r.Clone() : null;
        }
    }

    /// <summary>Gets a copy of a pending entry.</summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="client">The client name.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The copy, or null.</returns>
    public PlayRecord? GetPending(string? deviceId, string? client, Guid itemId)
    {
        lock (_lock)
        {
            return _pending.TryGetValue(PendingKey(deviceId, client, itemId), out var r) ? r.Clone() : null;
        }
    }

    /// <summary>Changes a session's entry, creating it when absent.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="change">The change (runs under the lock: in-memory work only).</param>
    /// <returns>A copy of the entry after the change.</returns>
    public PlayRecord Update(string sessionId, Action<PlayRecord> change)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            var now = _clock();
            if (!_sessions.TryGetValue(sessionId, out var record))
            {
                MakeRoom(now);
                record = new PlayRecord { SessionId = sessionId, Touched = now };
                _sessions[sessionId] = record;
            }

            change(record);
            record.SessionId = sessionId;
            record.Touched = now;
            return record.Clone();
        }
    }

    // Validate the request's identities and commit its in-memory changes together. Resolver,
    // recorder and compatibility writes run outside this lock; request argument mutation is local.
    internal bool TryUpdatePlaybackInfo(string? sessionId, string? deviceId, string? client, Guid itemId, Guid userId,
        PlayRecord? sessionSnapshot, PlayRecord? pendingSnapshot, bool pending, string? sourceId, ManualChangeOrder order, Action<PlayRecord> change)
    {
        lock (_lock)
        {
            _sessions.TryGetValue(sessionId ?? string.Empty, out var session);
            // A request can precede SessionManager visibility while progress has already made
            // this device active. Do not leave a stale pending copy for later adoption.
            if (string.IsNullOrEmpty(sessionId) && _sessions.Values.Any(r => r.Playing && r.UserId == userId
                && string.Equals(r.DeviceId, deviceId, StringComparison.Ordinal)
                && (string.IsNullOrEmpty(client) || string.Equals(r.Client, client, StringComparison.OrdinalIgnoreCase)))) return false;
            var key = PendingKey(deviceId, client, itemId);
            _pending.TryGetValue(key, out var prepared);
            bool SameUnstartedPreparation(PlayRecord? current, PlayRecord? snapshot) =>
                (snapshot is null || !snapshot.Playing) && current is { Playing: false, HasExpected: true, PreparationOrder: { } previous }
                && current.UserId == userId && current.ItemId == itemId
                && string.Equals(current.DeviceId, deviceId, StringComparison.Ordinal)
                && string.Equals(current.Client, client, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.MediaSourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                && previous.Epoch == order.Epoch && order.Sequence > previous.Sequence;
            if (!string.IsNullOrEmpty(sessionId) && session?.PlayId != sessionSnapshot?.PlayId
                && (pending || !SameUnstartedPreparation(session, sessionSnapshot))) return false;
            if ((pending || session is null) && prepared?.PlayId != pendingSnapshot?.PlayId
                && (!pending || !SameUnstartedPreparation(prepared, pendingSnapshot))) return false;

            var now = _clock();
            var record = pending ? prepared : session;
            if (record is null)
            {
                MakeRoom(now);
                record = new PlayRecord { Touched = now };
            }
            var earlier = record.PreparationOrder;
            var fresh = earlier is null && !record.HasExpected;
            change(record);
            if (!record.Playing && (fresh || earlier is not null || record.PreparationOrder is not null))
            {
                var basis = record.PreparationOrder ?? earlier;
                record.PreparationOrder = basis is { } prior && prior.Epoch == order.Epoch && order.Sequence <= prior.Sequence ? prior : order;
            }
            record.Touched = now;
            if (pending)
            {
                record.SessionId = null;
                record.ItemId = itemId;
                _pending[key] = record;
            }
            else
            {
                record.SessionId = sessionId;
                _sessions[sessionId!] = record;
                // This request now supplies the session's prepared expectations. An older pending
                // copy must not be adopted over them by the next start/progress report.
                if (prepared?.UserId == record.UserId) _pending.Remove(key);
            }
            return true;
        }
    }

    /// <summary>Changes a session's entry only when it exists.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="change">The change (runs under the lock).</param>
    /// <returns>A copy of the entry after the change, or null when there is none.</returns>
    public PlayRecord? UpdateExisting(string sessionId, Action<PlayRecord> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var record))
            {
                return null;
            }

            change(record);
            record.Touched = _clock();
            return record.Clone();
        }
    }

    /// <summary>Changes a pending entry (a PlaybackInfo before the session exists), creating it when absent.</summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="client">The client name.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="change">The change (runs under the lock).</param>
    /// <returns>A copy of the entry after the change.</returns>
    public PlayRecord UpdatePending(string? deviceId, string? client, Guid itemId, Action<PlayRecord> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            var now = _clock();
            var key = PendingKey(deviceId, client, itemId);
            if (!_pending.TryGetValue(key, out var record))
            {
                MakeRoom(now);
                record = new PlayRecord { DeviceId = deviceId, Client = client, ItemId = itemId, Touched = now };
                _pending[key] = record;
            }

            change(record);
            record.SessionId = null;
            record.ItemId = itemId;
            record.Touched = now;
            return record.Clone();
        }
    }

    /// <summary>
    /// Gives a session the pending entry made for its device, client and item, if any: the play fields move to the session's
    /// entry (created when absent); the session's last stop is kept.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="deviceId">The session's device id.</param>
    /// <param name="client">The session's client name.</param>
    /// <param name="itemId">The item it plays.</param>
    /// <param name="userId">The session's authenticated person; legacy callers use the existing registry identity.</param>
    /// <returns>A copy of the session's entry after adoption, or null when nothing was pending.</returns>
    public PlayRecord? Adopt(string sessionId, string? deviceId, string? client, Guid itemId, Guid? userId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_lock)
        {
            var key = PendingKey(deviceId, client, itemId);
            if (!_pending.TryGetValue(key, out var pending)
                || (userId.HasValue && pending.UserId != userId.Value)
                || (!userId.HasValue && _sessions.TryGetValue(sessionId, out var session) && session.UserId != Guid.Empty && session.UserId != pending.UserId))
            {
                return null;
            }

            _pending.Remove(key);
            var now = _clock();
            if (_sessions.TryGetValue(sessionId, out var existing) && existing.UserId == pending.UserId)
            {
                pending.LastStoppedItemId = existing.LastStoppedItemId;
                pending.LastStoppedAt = existing.LastStoppedAt;
                pending.DeviceName ??= existing.DeviceName;
            }

            pending.SessionId = sessionId;
            pending.Touched = now;
            _sessions[sessionId] = pending;
            return pending.Clone();
        }
    }

    /// <summary>Drops a session's entry (the session ended).</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>True when an entry existed.</returns>
    public bool Remove(string sessionId)
    {
        lock (_lock)
        {
            return !string.IsNullOrEmpty(sessionId) && _sessions.Remove(sessionId);
        }
    }

    /// <summary>Drops entries untouched for <see cref="MaxAge"/>.</summary>
    /// <returns>The number dropped.</returns>
    public int Sweep()
    {
        lock (_lock)
        {
            return SweepLocked(_clock());
        }
    }

    private void MakeRoom(DateTime now)
    {
        if (now - _lastSweep >= SweepInterval)
        {
            SweepLocked(now);
        }

        while (_sessions.Count + _pending.Count >= MaxEntries)
        {
            var oldestSession = _sessions.Count == 0 ? null : _sessions.MinBy(p => p.Value.Touched).Key;
            var oldestPending = _pending.Count == 0 ? null : _pending.MinBy(p => p.Value.Touched).Key;
            if (oldestPending is not null
                && (oldestSession is null || _pending[oldestPending].Touched <= _sessions[oldestSession].Touched))
            {
                _pending.Remove(oldestPending);
            }
            else if (oldestSession is not null)
            {
                _sessions.Remove(oldestSession);
            }
            else
            {
                break;
            }
        }
    }

    private int SweepLocked(DateTime now)
    {
        _lastSweep = now;
        var cutoff = now - MaxAge;
        var dropped = 0;
        foreach (var key in _sessions.Where(p => p.Value.Touched < cutoff).Select(p => p.Key).ToList())
        {
            _sessions.Remove(key);
            dropped++;
        }

        foreach (var key in _pending.Where(p => p.Value.Touched < cutoff).Select(p => p.Key).ToList())
        {
            _pending.Remove(key);
            dropped++;
        }

        return dropped;
    }
}
