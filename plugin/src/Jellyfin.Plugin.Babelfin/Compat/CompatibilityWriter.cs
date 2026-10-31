using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Compat;

/// <summary>One remembered selection Babelfin wrote into Jellyfin's <c>UserItemData</c> (a row of <c>compat.json</c>).</summary>
public sealed class CompatEntry
{
    /// <summary>Gets or sets the person.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the item.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the audio index Jellyfin remembered before Babelfin's first write.</summary>
    public int? PreviousAudio { get; set; }

    /// <summary>Gets or sets the subtitle index Jellyfin remembered before Babelfin's first write.</summary>
    public int? PreviousSubtitle { get; set; }

    /// <summary>Gets or sets the audio index Babelfin wrote last.</summary>
    public int? WrittenAudio { get; set; }

    /// <summary>Gets or sets the subtitle index Babelfin wrote last (-1 = off).</summary>
    public int? WrittenSubtitle { get; set; }

    /// <summary>Gets or sets when Babelfin first wrote (UTC).</summary>
    public DateTime FirstWrittenAt { get; set; }

    /// <summary>Gets or sets when Babelfin last wrote (UTC).</summary>
    public DateTime WrittenAt { get; set; }

    /// <summary>Gets or sets whether the last target was journaled but not yet confirmed saved.</summary>
    public bool PendingWrite { get; set; }

    /// <summary>Gets or sets the audio value before the prepared write.</summary>
    public int? PendingPreviousAudio { get; set; }

    /// <summary>Gets or sets the subtitle value before the prepared write.</summary>
    public int? PendingPreviousSubtitle { get; set; }

    /// <summary>Gets or sets whether the prior audio still belonged to Babelfin.</summary>
    public bool PendingAudioWasOurs { get; set; }

    /// <summary>Gets or sets whether the prior subtitle still belonged to Babelfin.</summary>
    public bool PendingSubtitleWasOurs { get; set; }

    /// <summary>A copy.</summary>
    /// <returns>The copy.</returns>
    public CompatEntry Clone() => (CompatEntry)MemberwiseClone();
}

/// <summary>The <c>compat.json</c> document: the ledger of compatibility-mode writes.</summary>
public sealed class CompatLedger
{
    /// <summary>The current schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets or sets the schema version.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Gets or sets the entries.</summary>
    public List<CompatEntry> Entries { get; set; } = new();

    /// <summary>Finds the entry for a person and item.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The entry, or null.</returns>
    public CompatEntry? Find(Guid userId, Guid itemId) => Entries.FirstOrDefault(e => e.UserId == userId && e.ItemId == itemId);
}

/// <summary>What a compatibility write should do.</summary>
/// <param name="Write">Whether Jellyfin's remembered selection must be written.</param>
/// <param name="Audio">The audio index to store.</param>
/// <param name="Subtitle">The subtitle index to store.</param>
/// <param name="Entry">The ledger entry after the write (null when nothing is recorded).</param>
/// <param name="LedgerChanged">Whether the ledger must be saved.</param>
/// <param name="NewEntry">Whether the entry is new (the first write for this person and item).</param>
public sealed record CompatWritePlan(bool Write, int? Audio, int? Subtitle, CompatEntry? Entry, bool LedgerChanged, bool NewEntry);

/// <summary>What undoing one ledger entry should do.</summary>
/// <param name="Write">Whether Jellyfin's remembered selection must be written.</param>
/// <param name="Audio">The audio index to store.</param>
/// <param name="Subtitle">The subtitle index to store.</param>
/// <param name="AudioRestored">Whether the audio index goes back to the previous value.</param>
/// <param name="SubtitleRestored">Whether the subtitle index goes back to the previous value.</param>
public sealed record CompatUndoPlan(bool Write, int? Audio, int? Subtitle, bool AudioRestored, bool SubtitleRestored);

/// <summary>The ledger rules of compatibility mode, without I/O.</summary>
public static class CompatLedgerLogic
{
    /// <summary>
    /// Plans a write: the first write for a person and item records the values Jellyfin had (kept from then on); a write that changes
    /// nothing is skipped (and, the first time, not recorded at all). A null index leaves that index as it is.
    /// </summary>
    /// <param name="existing">The ledger entry for this person and item, or null.</param>
    /// <param name="userId">The person.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="currentAudio">Jellyfin's current remembered audio index.</param>
    /// <param name="currentSubtitle">Jellyfin's current remembered subtitle index.</param>
    /// <param name="audio">Babelfin's audio choice, or null to leave it.</param>
    /// <param name="subtitle">Babelfin's subtitle choice (-1 = off), or null to leave it.</param>
    /// <param name="now">The time (UTC).</param>
    /// <returns>The plan.</returns>
    public static CompatWritePlan PlanWrite(CompatEntry? existing, Guid userId, Guid itemId, int? currentAudio, int? currentSubtitle, int? audio, int? subtitle, DateTime now)
    {
        var targetAudio = audio ?? currentAudio;
        var targetSubtitle = subtitle ?? currentSubtitle;
        var unchanged = targetAudio == currentAudio && targetSubtitle == currentSubtitle;
        if (existing is null)
        {
            if (unchanged)
            {
                return new CompatWritePlan(false, currentAudio, currentSubtitle, null, false, false);
            }

            var created = new CompatEntry
            {
                UserId = userId,
                ItemId = itemId,
                PreviousAudio = currentAudio,
                PreviousSubtitle = currentSubtitle,
                WrittenAudio = targetAudio,
                WrittenSubtitle = targetSubtitle,
                FirstWrittenAt = now,
                WrittenAt = now,
            };
            return new CompatWritePlan(true, targetAudio, targetSubtitle, created, true, true);
        }

        if (unchanged)
        {
            return new CompatWritePlan(false, currentAudio, currentSubtitle, existing, false, false);
        }

        var updated = existing.Clone();
        if (targetAudio != currentAudio)
        {
            if (existing.PreviousAudio == existing.WrittenAudio)
            {
                updated.PreviousAudio = currentAudio;
            }

            updated.WrittenAudio = targetAudio;
        }

        if (targetSubtitle != currentSubtitle)
        {
            if (existing.PreviousSubtitle == existing.WrittenSubtitle)
            {
                updated.PreviousSubtitle = currentSubtitle;
            }

            updated.WrittenSubtitle = targetSubtitle;
        }

        updated.WrittenAt = now;
        return new CompatWritePlan(true, targetAudio, targetSubtitle, updated, true, false);
    }

    /// <summary>
    /// Plans the undo of one entry: each index goes back to its previous value only while Jellyfin still holds the value Babelfin wrote
    /// (a change the person or another app made since is left alone).
    /// </summary>
    /// <param name="entry">The ledger entry.</param>
    /// <param name="currentAudio">Jellyfin's current remembered audio index.</param>
    /// <param name="currentSubtitle">Jellyfin's current remembered subtitle index.</param>
    /// <returns>The plan.</returns>
    public static CompatUndoPlan PlanUndo(CompatEntry entry, int? currentAudio, int? currentSubtitle)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var audioRestored = entry.PreviousAudio != currentAudio
            && (currentAudio == entry.WrittenAudio || (entry.PendingWrite && entry.PendingAudioWasOurs && currentAudio == entry.PendingPreviousAudio));
        var subtitleRestored = entry.PreviousSubtitle != currentSubtitle
            && (currentSubtitle == entry.WrittenSubtitle || (entry.PendingWrite && entry.PendingSubtitleWasOurs && currentSubtitle == entry.PendingPreviousSubtitle));
        return new CompatUndoPlan(
            audioRestored || subtitleRestored,
            audioRestored ? entry.PreviousAudio : currentAudio,
            subtitleRestored ? entry.PreviousSubtitle : currentSubtitle,
            audioRestored,
            subtitleRestored);
    }
}

/// <summary>
/// Compatibility mode: writes Babelfin's choice into Jellyfin's remembered selection
/// (<c>UserItemData.AudioStreamIndex</c>/<c>SubtitleStreamIndex</c>) and records in <c>{dataFolder}/compat.json</c> what was there before,
/// so <see cref="UndoAllAsync"/> can put it back.
/// </summary>
/// <remarks>
/// Decisions where the design is silent:
/// <list type="bullet">
/// <item>The ledger is read on first use (not in the constructor); it is written atomically through <see cref="AtomicJsonFile"/> before
/// Jellyfin's value is changed, so a crash never leaves a write without its record. A corrupt ledger is quarantined by
/// <see cref="AtomicJsonFile"/> (renamed, never deleted); a ledger that exists but can't be read stops compatibility writes (nothing is
/// written to Jellyfin that couldn't be undone) until it can be read again.</item>
/// <item>A write that changes nothing is skipped; the first time for a person and item, it isn't recorded either. A null index leaves that
/// index alone. When the configuration is loaded and compatibility mode is off, <see cref="WriteAsync"/> does nothing.</item>
/// <item>Every write and undo is logged as an activity entry: the first write per person and item as <c>jellyfin-setting-changed</c>
/// (not mirrored to Jellyfin's activity log, which would get one line per title played), the undo as one <c>restored</c> summary.</item>
/// <item>Undo drops entries whose person or item no longer exists (nothing to restore), keeps entries whose write to Jellyfin failed (a
/// later undo retries them) and, when cancelled, keeps the entries it didn't reach.</item>
/// <item>Both methods return to the caller at once and do their work on the thread pool; they never throw (failures are logged).</item>
/// </list>
/// </remarks>
public sealed class CompatibilityWriter : ICompatibilityWriter
{
    /// <summary>The ledger's file name inside the data folder.</summary>
    public const string FileName = "compat.json";

    /// <summary>Maximum entries processed before releasing the ledger lock.</summary>
    public const int BatchSize = 32;

    /// <summary>Pruning checks older entries but never removes one still needed for undo.</summary>
    public static readonly TimeSpan PruneAge = TimeSpan.FromDays(30);

    private long _nextPruneTicks;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IActivityLog _activity;
    private readonly ILogger<CompatibilityWriter> _logger;
    private readonly object _gate = new();
    private CompatLedger? _ledger;
    private bool _unreadable;
    private int _writesStopped;

    /// <summary>Initializes a new instance of the <see cref="CompatibilityWriter"/> class.</summary>
    /// <param name="dataFolder">Babelfin's data folder (<c>{DataPath}/babelfin</c>).</param>
    /// <param name="userDataManager">Jellyfin's user data manager (remembered selections).</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="activity">Babelfin's activity log.</param>
    /// <param name="logger">Logger.</param>
    public CompatibilityWriter(
        string dataFolder,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IActivityLog activity,
        ILogger<CompatibilityWriter> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataFolder);
        FilePath = Path.Combine(dataFolder, FileName);
        _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the ledger file (admin diagnostics and tests only; never sent to a non-admin).</summary>
    public string FilePath { get; }

    /// <inheritdoc />
    public int LedgerCount
    {
        get
        {
            lock (_gate)
            {
                return Ledger().Entries.Count;
            }
        }
    }

    /// <inheritdoc />
    public void StopWrites() => Interlocked.Exchange(ref _writesStopped, 1);

    /// <inheritdoc />
    public async Task WriteAsync(User user, BaseItem item, int? audioIndex, int? subtitleIndex, CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (user is null || item is null || cancellationToken.IsCancellationRequested || Volatile.Read(ref _writesStopped) != 0)
        {
            return;
        }

        if (Plugin.Instance?.Configuration is { CompatibilityMode: false })
        {
            return;
        }

        try
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var nextPrune = Interlocked.Read(ref _nextPruneTicks);
            if (nowTicks >= nextPrune && Interlocked.CompareExchange(ref _nextPruneTicks, nowTicks + TimeSpan.FromHours(1).Ticks, nextPrune) == nextPrune)
            {
                await PruneAsync(cancellationToken).ConfigureAwait(false);
            }

            CompatWritePlan plan;
            lock (_gate)
            {
                // StopWrites never waits on this lock. A write already admitted here finishes, then undo
                // takes the same lock before its snapshot; queued writers recheck the closed gate here.
                if (Volatile.Read(ref _writesStopped) != 0 || Plugin.Instance?.Configuration is { CompatibilityMode: false }) return;
                var ledger = Ledger();
                if (_unreadable)
                {
                    _logger.LogWarning("[Babelfin] Compatibility mode skipped a write: {File} can't be read, so the write couldn't be undone", FileName);
                    return;
                }

                var data = _userDataManager.GetUserData(user, item);
                if (data is null)
                {
                    return;
                }

                plan = CompatLedgerLogic.PlanWrite(ledger.Find(user.Id, item.Id), user.Id, item.Id, data.AudioStreamIndex, data.SubtitleStreamIndex, audioIndex, subtitleIndex, DateTime.UtcNow);
                if (!plan.Write)
                {
                    return;
                }

                var previousAudio = data.AudioStreamIndex;
                var previousSubtitle = data.SubtitleStreamIndex;
                var prepared = plan.Entry!;
                var existing = ledger.Find(user.Id, item.Id);
                var priorUndo = existing is null ? null : CompatLedgerLogic.PlanUndo(existing, previousAudio, previousSubtitle);
                prepared.PendingWrite = true;
                prepared.PendingPreviousAudio = previousAudio;
                prepared.PendingPreviousSubtitle = previousSubtitle;
                prepared.PendingAudioWasOurs = priorUndo?.AudioRestored == true;
                prepared.PendingSubtitleWasOurs = priorUndo?.SubtitleRestored == true;
                SaveLedger(new CompatLedger { Entries = ledger.Entries.Where(e => !(e.UserId == user.Id && e.ItemId == item.Id)).Append(prepared).ToList() });

                try
                {
                    data.AudioStreamIndex = plan.Audio;
                    data.SubtitleStreamIndex = plan.Subtitle;
                    _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserData, cancellationToken);
                }
                catch
                {
                    data.AudioStreamIndex = previousAudio;
                    data.SubtitleStreamIndex = previousSubtitle;
                    throw;
                }

                var confirmed = prepared.Clone();
                confirmed.PendingWrite = false;
                SaveLedger(new CompatLedger { Entries = _ledger!.Entries.Where(e => !ReferenceEquals(e, prepared)).Append(confirmed).ToList() });
            }

            _logger.LogDebug("[Babelfin] Compatibility mode remembered audio {Audio} / subtitles {Subtitle} for {User} on {Item}", plan.Audio, plan.Subtitle, user.Username, item.Name);
            if (plan.NewEntry)
            {
                _activity.Add(new ActivityEntry(
                    DateTime.UtcNow,
                    ActivityKinds.JellyfinSettingChanged,
                    user.Id,
                    item.Id,
                    $"Babelfin set Jellyfin's remembered tracks for {item.Name} (compatibility mode)",
                    new Dictionary<string, string>
                    {
                        ["audio"] = Format(plan.Audio),
                        ["subtitle"] = Format(plan.Subtitle),
                        ["mirror"] = "false",
                    }));
            }
        }
        catch (OperationCanceledException)
        {
            // Nothing more to do.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Compatibility mode couldn't write the remembered tracks for {Item}", item.Name);
        }
    }

    /// <inheritdoc />
    public async Task UndoAllAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            int restored = 0, leftAlone = 0, gone = 0, failed = 0, notReached = 0;
            var result = await ProcessLedgerAsync(undo: true, cancellationToken).ConfigureAwait(false);
            restored = result.Restored;
            leftAlone = result.LeftAlone;
            gone = result.Gone;
            failed = result.Failed;
            notReached = result.NotReached;

            _logger.LogInformation(
                "[Babelfin] Compatibility mode undone: {Restored} remembered selection(s) restored, {LeftAlone} changed since and left alone, {Gone} gone, {Failed} failed, {NotReached} not reached",
                restored,
                leftAlone,
                gone,
                failed,
                notReached);
            _activity.Add(new ActivityEntry(
                DateTime.UtcNow,
                ActivityKinds.Restored,
                null,
                null,
                $"Babelfin restored {restored} remembered track selection(s) it had written in compatibility mode",
                new Dictionary<string, string>
                {
                    ["restored"] = Format(restored),
                    ["leftAlone"] = Format(leftAlone),
                    ["gone"] = Format(gone),
                    ["failed"] = Format(failed),
                    ["notReached"] = Format(notReached),
                }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't undo compatibility mode");
        }
    }

    /// <summary>Prunes only stale entries whose person/item is gone or whose tracks no longer need restoration.</summary>
    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ProcessLedgerAsync(undo: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Babelfin] Couldn't prune the compatibility ledger; recovery records are retained");
        }
    }

    private async Task<(int Restored, int LeftAlone, int Gone, int Failed, int NotReached)> ProcessLedgerAsync(bool undo, CancellationToken cancellationToken)
    {
        List<CompatEntry> snapshot;
        lock (_gate)
        {
            var ledger = Ledger();
            if (_unreadable)
            {
                _logger.LogWarning("[Babelfin] Compatibility ledger can't be read; recovery records are retained");
                return default;
            }

            snapshot = ledger.Entries.Where(e => undo || e.WrittenAt < DateTime.UtcNow - PruneAge).ToList();
        }

        int restored = 0, leftAlone = 0, gone = 0, failed = 0, reached = 0;
        foreach (var batch in snapshot.Chunk(BatchSize))
        {
            // Writers and diagnostics can proceed between batches. Fresh writes are merged, never replaced by the old snapshot.
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            lock (_gate)
            {
                var ledger = Ledger();
                var remove = new HashSet<CompatEntry>();
                foreach (var entry in batch)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    reached++;
                    if (!ReferenceEquals(ledger.Find(entry.UserId, entry.ItemId), entry))
                    {
                        continue;
                    }

                    try
                    {
                        var user = _userManager.GetUserById(entry.UserId);
                        var item = user is null ? null : _libraryManager.GetItemById(entry.ItemId);
                        if (user is null || item is null)
                        {
                            remove.Add(entry);
                            gone++;
                            continue;
                        }

                        var data = _userDataManager.GetUserData(user, item);
                        if (data is null)
                        {
                            // Missing user data can be a transient read failure; preserve the only undo record.
                            failed++;
                            continue;
                        }

                        var plan = CompatLedgerLogic.PlanUndo(entry, data.AudioStreamIndex, data.SubtitleStreamIndex);
                        if (!plan.Write)
                        {
                            remove.Add(entry);
                            leftAlone++;
                            continue;
                        }

                        if (!undo)
                        {
                            continue;
                        }

                        var oldAudio = data.AudioStreamIndex;
                        var oldSubtitle = data.SubtitleStreamIndex;
                        try
                        {
                            data.AudioStreamIndex = plan.Audio;
                            data.SubtitleStreamIndex = plan.Subtitle;
                            _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserData, CancellationToken.None);
                        }
                        catch
                        {
                            data.AudioStreamIndex = oldAudio;
                            data.SubtitleStreamIndex = oldSubtitle;
                            throw;
                        }

                        remove.Add(entry);
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "[Babelfin] Couldn't inspect or restore item {Item}; its recovery record is kept", entry.ItemId);
                    }
                }

                if (remove.Count > 0)
                {
                    SaveLedger(new CompatLedger { Entries = ledger.Entries.Where(e => !remove.Contains(e)).ToList() });
                }
            }
        }

        return (restored, leftAlone, gone, failed, snapshot.Count - reached);
    }

    private void SaveLedger(CompatLedger next)
    {
        AtomicJsonFile.Write(FilePath, next, BabelfinJson.Options);
        _ledger = next;
    }

    private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "none";

    private CompatLedger Ledger()
    {
        if (_ledger is not null && !_unreadable)
        {
            return _ledger;
        }

        var outcome = AtomicJsonFile.Read<CompatLedger>(FilePath, BabelfinJson.Options, _logger, out var read);
        _unreadable = outcome == JsonReadOutcome.Unreadable;
        var ledger = read ?? new CompatLedger();
        ledger.Entries ??= new List<CompatEntry>();
        ledger.Entries.RemoveAll(e => e is null);
        _ledger = ledger;
        return ledger;
    }
}
