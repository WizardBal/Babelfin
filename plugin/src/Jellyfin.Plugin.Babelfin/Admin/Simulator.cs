using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Library;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.Babelfin.Admin;

/// <summary>One simulator job: what plays for one person on every episode and movie under a library, series, season, collection or title.</summary>
public sealed class SimulationJob : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private int _progress;
    private int _total;
    private int _errors;
    private volatile bool _done;
    private volatile bool _cancelled;
    private volatile string? _error;
    private volatile IReadOnlyList<CoverageRow> _rows = Array.Empty<CoverageRow>();
    private long _finishedTicks;

    internal SimulationJob(Guid userId, string? userName, Guid rootId, string? rootName, DateTime startedAt)
    {
        Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        UserId = userId;
        UserName = userName;
        RootId = rootId;
        RootName = rootName;
        StartedAt = startedAt;
    }

    /// <summary>Gets the job id.</summary>
    public string Id { get; }

    /// <summary>Gets the person simulated.</summary>
    public Guid UserId { get; }

    /// <summary>Gets the person's name.</summary>
    public string? UserName { get; }

    /// <summary>Gets the library, series, season, collection or title walked.</summary>
    public Guid RootId { get; }

    /// <summary>Gets its name.</summary>
    public string? RootName { get; }

    /// <summary>Gets when the job started (UTC).</summary>
    public DateTime StartedAt { get; }

    /// <summary>Gets when the job ended (UTC), or null while it runs.</summary>
    public DateTime? FinishedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _finishedTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>Gets the progress, 0–100.</summary>
    public int Progress => Volatile.Read(ref _progress);

    /// <summary>Gets the number of episodes and movies found.</summary>
    public int Total => Volatile.Read(ref _total);

    /// <summary>Gets the number of items that couldn't be evaluated (details in the server log).</summary>
    public int Errors => Volatile.Read(ref _errors);

    /// <summary>Gets a value indicating whether the job has ended (finished, cancelled or failed).</summary>
    public bool Done => _done;

    /// <summary>Gets a value indicating whether the job was cancelled.</summary>
    public bool Cancelled => _cancelled;

    /// <summary>Gets the error message when the job failed, else null.</summary>
    public string? Error => _error;

    /// <summary>Gets the rows (empty until the job is done; the partial rows of a cancelled job are kept).</summary>
    public IReadOnlyList<CoverageRow> Rows => _rows;

    internal CancellationToken Token => _cancel.Token;

    /// <summary>Asks the job to stop; it stops before the next item.</summary>
    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed: the job has ended
        }
    }

    /// <inheritdoc />
    public void Dispose() => _cancel.Dispose();

    internal void SetTotal(int total) => Volatile.Write(ref _total, total);

    internal void SetProgress(int progress) => Volatile.Write(ref _progress, Math.Clamp(progress, 0, 100));

    internal void CountError() => Interlocked.Increment(ref _errors);

    internal void Finish(IReadOnlyList<CoverageRow> rows, bool cancelled, string? error)
    {
        _rows = rows;
        _cancelled = cancelled;
        _error = error;
        if (!cancelled && error is null)
        {
            SetProgress(100);
        }

        Interlocked.Exchange(ref _finishedTicks, DateTime.UtcNow.Ticks);
        _done = true;
    }
}

/// <summary>
/// Runs the simulator: walks every episode and movie under an item as the chosen person, asks Jellyfin for the media
/// sources (Babelfin's wrapper decides) and reads the recorded decision. One job at a time on the thread pool; results are kept 30 minutes;
/// jobs are cancellable. The coverage report runs the same walk inline (also on the thread pool).
/// Registered as a singleton; the constructor only stores its arguments (it may be created while Jellyfin's services are still being built).
/// </summary>
public sealed class Simulator : IDisposable
{
    /// <summary>How long a finished job's results are kept.</summary>
    public static readonly TimeSpan KeepResults = TimeSpan.FromMinutes(30);

    /// <summary>The most episodes and movies one walk evaluates (a safety bound).</summary>
    public const int MaxItems = 100_000;

    private const int MaxJobsKept = 20;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly DecoratorStats _stats;
    private readonly ILogger<Simulator> _logger;
    private readonly ConcurrentDictionary<string, SimulationJob> _jobs = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private SimulationJob? _running;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="Simulator"/> class. Stores its arguments and nothing else.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="mediaSourceManager">Media source manager (Babelfin's wrapper when wrapped).</param>
    /// <param name="stats">The recorded decisions.</param>
    /// <param name="logger">Logger.</param>
    public Simulator(ILibraryManager libraryManager, IUserManager userManager, IMediaSourceManager mediaSourceManager, DecoratorStats stats, ILogger<Simulator> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _stats = stats;
        _logger = logger;
    }

    /// <summary>Starts a job unless one is running.</summary>
    /// <param name="user">The person to simulate.</param>
    /// <param name="root">The library, series, season, collection or title (already checked as visible to the person).</param>
    /// <param name="libraryUsualLanguage">The library's usual original language, or null.</param>
    /// <param name="job">The new job, or the running one when this returns false.</param>
    /// <returns>True when a new job started.</returns>
    public bool TryStart(JellyfinUser user, BaseItem root, string? libraryUsualLanguage, out SimulationJob job)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(root);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Sweep();
            if (_running is { Done: false } running)
            {
                job = running;
                return false;
            }

            var created = new SimulationJob(user.Id, user.Username, root.Id, root.Name, DateTime.UtcNow);
            _jobs[created.Id] = created;
            _running = created;
            job = created;
            var userId = user.Id;
            var rootId = root.Id;
            _ = Task.Run(() => RunJob(created, userId, rootId, libraryUsualLanguage));
            return true;
        }
    }

    /// <summary>Gets a job that is running or finished within <see cref="KeepResults"/>.</summary>
    /// <param name="jobId">The job id.</param>
    /// <returns>The job, or null.</returns>
    public SimulationJob? Get(string jobId)
    {
        lock (_gate)
        {
            Sweep();
        }

        return !string.IsNullOrEmpty(jobId) && _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    /// <summary>Cancels a running job (a finished job is left as it is).</summary>
    /// <param name="jobId">The job id.</param>
    /// <returns>True when the job exists.</returns>
    public bool Cancel(string jobId)
    {
        var job = Get(jobId);
        if (job is null)
        {
            return false;
        }

        if (!job.Done)
        {
            job.Cancel();
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var job in _jobs.Values)
            {
                job.Cancel();
            }
        }

    }

    private void RunJob(SimulationJob job, Guid userId, Guid rootId, string? libraryUsualLanguage)
    {
        var rows = new List<CoverageRow>();
        try
        {
            // Look the person and the item up again on the worker: nothing from the request's scope is used here.
            var user = _userManager.GetUserById(userId);
            var root = user is null ? null : _libraryManager.GetItemById<BaseItem>(rootId, user);
            if (user is null || root is null)
            {
                job.Finish(rows, false, "The person or the item can't be found any more.");
                return;
            }

            Walk(user, root, libraryUsualLanguage, job, rows, job.Token);
            job.Finish(rows, false, null);
            _logger.LogInformation("[Babelfin] Simulator: {Count} files for {User} under {Root} ({Errors} errors)", rows.Count, user.Username, root.Name, job.Errors);
        }
        catch (OperationCanceledException)
        {
            job.Finish(rows, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] The simulator stopped");
            job.Finish(rows, false, "The simulation stopped because of an error; see the server log.");
        }
    }

    private void Walk(JellyfinUser user, BaseItem root, string? libraryUsualLanguage, SimulationJob? job, List<CoverageRow> rows, CancellationToken cancellationToken)
    {
        var items = Collect(user, root, cancellationToken);
        job?.SetTotal(items.Count);
        job?.SetProgress(5);
        var languages = EngineServices.Current.Languages;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            try
            {
                var sources = _mediaSourceManager.GetStaticMediaSources(item, true, user);
                if (sources is not null)
                {
                    var facts = Describe(item);
                    foreach (var source in sources)
                    {
                        if (source is null)
                        {
                            continue;
                        }

                        var record = _stats.GetDecision(user.Id, item.Id, source.Id);
                        var original = Coverage.OriginalLanguageOf(record?.Decision?.Engine) ?? RawOriginalLanguage(item);
                        rows.Add(Coverage.BuildRow(facts, source, record, original, libraryUsualLanguage, languages));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                job?.CountError();
                _logger.LogDebug(ex, "[Babelfin] Simulator: {Item} couldn't be evaluated", item.Name);
            }

            job?.SetProgress(5 + (int)(95L * (i + 1) / Math.Max(1, items.Count)));
        }
    }

    private List<BaseItem> Collect(JellyfinUser user, BaseItem root, CancellationToken cancellationToken)
    {
        var found = new List<BaseItem>();
        var seen = new HashSet<Guid>();

        void Add(BaseItem? item)
        {
            if (item is (Episode or Movie) && !item.IsVirtualItem && found.Count < MaxItems && seen.Add(item.Id))
            {
                found.Add(item);
            }
        }

        void Visit(BaseItem node, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is Episode or Movie)
            {
                Add(node);
                return;
            }

            if (node is BoxSet box)
            {
                // A collection's members are linked, not children: walk each one (a series in a collection is walked in full).
                if (depth < 3)
                {
                    foreach (var child in box.GetLinkedChildren(user))
                    {
                        Visit(child, depth + 1);
                    }
                }

                return;
            }

            var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ParentId = node.Id,
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie },
                IsVirtualItem = false,
                DtoOptions = new DtoOptions(false),
            });
            if (items is not null)
            {
                foreach (var item in items)
                {
                    Add(item);
                }
            }
        }

        Visit(root, 0);
        return found
            .OrderBy(i => (i as Episode)?.SeriesName ?? i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(i => i.IndexNumber ?? int.MaxValue)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static CoverageItem Describe(BaseItem item)
    {
        if (item is Episode episode)
        {
            return new CoverageItem(item.Id, item.Name ?? string.Empty, episode.SeriesName, episode.ParentIndexNumber, episode.IndexNumber);
        }

        return new CoverageItem(item.Id, item.Name ?? string.Empty, null, null, null);
    }

    private static string? RawOriginalLanguage(BaseItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.OriginalLanguage))
        {
            return item.OriginalLanguage;
        }

        return item is Episode episode ? episode.Series?.OriginalLanguage : null;
    }

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _jobs)
        {
            if (pair.Value.FinishedAt is { } finished && now - finished > KeepResults)
            {
                if (_jobs.TryRemove(pair.Key, out var removed))
                {
                    removed.Dispose();
                }
            }
        }

        if (_jobs.Count > MaxJobsKept)
        {
            foreach (var old in _jobs.Values.Where(j => j.Done).OrderBy(j => j.StartedAt).Take(_jobs.Count - MaxJobsKept).ToList())
            {
                if (_jobs.TryRemove(old.Id, out var removed))
                {
                    removed.Dispose();
                }
            }
        }
    }
}
