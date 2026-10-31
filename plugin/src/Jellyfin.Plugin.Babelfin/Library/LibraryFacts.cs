using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Library;

/// <summary>
/// Library facts computed in the background over <see cref="ILibraryManager"/> and cached in <c>{dataFolder}/library-facts.json</c>
///. <see cref="Get"/> is a dictionary lookup; it never queries the library.
/// </summary>
/// <remarks>
/// Behavior:
/// <list type="bullet">
/// <item>Only additions and removals of movies, series and libraries schedule a refresh (episodes, music and the rest don't change the facts).</item>
/// <item>The 2-minute debounce restarts on every request, but a refresh is never put off more than 10 minutes after the first
/// pending request, so a long library scan can't postpone it forever.</item>
/// <item>A refresh requested while one runs waits for it and then runs again (one at a time), so items added mid-refresh count.</item>
/// <item>When one library fails to compute, its previous facts are kept and the others are still refreshed. Libraries that no longer
/// exist are dropped.</item>
/// <item>The cache file carries its own schema version (1). A cache that can't be written is logged; the facts still apply in memory.</item>
/// <item>An unreadable or corrupt cache file is treated as no facts (a corrupt one is renamed, not deleted) and the first
/// <see cref="Get"/> schedules a refresh.</item>
/// </list>
/// </remarks>
public sealed class LibraryFacts : ILibraryFacts, IDisposable
{
    /// <summary>The debounce delay of <see cref="RequestRefresh"/>.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMinutes(2);

    /// <summary>The longest a requested refresh is put off by further requests.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(10);

    private readonly string _filePath;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryFacts> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _timerGate = new();
    private readonly Timer _timer;
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private long _firstPendingAt;
    private int _lazyRequested;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="LibraryFacts"/> class and loads the cache file.</summary>
    /// <param name="dataFolder">Babelfin's data folder (<c>{DataPath}/babelfin</c>).</param>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="logger">Logger.</param>
    public LibraryFacts(string dataFolder, ILibraryManager libraryManager, ILogger<LibraryFacts> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataFolder);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(logger);
        _filePath = Path.Combine(dataFolder, "library-facts.json");
        _libraryManager = libraryManager;
        _logger = logger;
        _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);

        LoadFile();

        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemChanged;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public DateTime? ComputedAt => _snapshot.ComputedAt;

    /// <inheritdoc />
    public IReadOnlyList<LibraryFact> All => _snapshot.All;

    /// <inheritdoc />
    public LibraryFact? Get(Guid libraryId)
    {
        var snapshot = _snapshot;
        if (snapshot.ComputedAt is null && Interlocked.Exchange(ref _lazyRequested, 1) == 0)
        {
            RequestRefresh();
        }

        return snapshot.ById.TryGetValue(libraryId, out var fact) ? fact : null;
    }

    /// <inheritdoc />
    public void RequestRefresh()
    {
        lock (_timerGate)
        {
            if (_disposed)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (_firstPendingAt == 0)
            {
                _firstPendingAt = now;
            }

            var dueAt = Math.Min(now + (long)Debounce.TotalMilliseconds, _firstPendingAt + (long)MaxDelay.TotalMilliseconds);
            _timer.Change(Math.Max(0, dueAt - now), Timeout.Infinite);
        }
    }

    /// <inheritdoc />
    public async Task RefreshAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var starter = Plugin.Instance?.Configuration.Starter ?? new StarterSettings();
            var languages = EngineServices.Current.Languages;
            var previous = _snapshot;
            var folders = _libraryManager.GetVirtualFolders() ?? new List<MediaBrowser.Model.Entities.VirtualFolderInfo>();
            var facts = new List<LibraryFact>();
            var now = DateTime.UtcNow;
            for (var i = 0; i < folders.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(100.0 * i / folders.Count);
                var folder = folders[i];
                if (folder is null || !Guid.TryParse(folder.ItemId, out var libraryId) || libraryId == Guid.Empty)
                {
                    continue;
                }

                try
                {
                    var titles = Titles(libraryId);
                    facts.Add(LibraryFactsCalculator.Calculate(libraryId, folder.Name, titles, starter, languages, now));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Babelfin] Couldn't compute the facts of library {Library}; its previous facts are kept", folder.Name);
                    if (previous.ById.TryGetValue(libraryId, out var old))
                    {
                        facts.Add(old);
                    }
                }
            }

            _snapshot = new Snapshot(now, facts);
            SaveFile(now, facts);
            progress?.Report(100);
            _logger.LogInformation(
                "[Babelfin] Library facts refreshed: {Count} libraries, original-audio: {OriginalAudio}",
                facts.Count,
                string.Join(", ", facts.Where(f => f.DetectedOriginalAudio).Select(f => f.Name)));
        }
        finally
        {
            _refreshGate.Release();
        }

        RaiseChanged();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_timerGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemChanged;
        _timer.Dispose();
        _refreshGate.Dispose();
    }

    private List<LibraryTitle> Titles(Guid libraryId)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = libraryId,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Movie },
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false),
        });

        var titles = new List<LibraryTitle>(items?.Count ?? 0);
        if (items is null)
        {
            return titles;
        }

        foreach (var item in items)
        {
            if (item is not null)
            {
                titles.Add(new LibraryTitle(item.Genres, item.OriginalLanguage));
            }
        }

        return titles;
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e?.Item is Movie or Series or CollectionFolder)
        {
            RequestRefresh();
        }
    }

    private void OnTimer(object? state)
    {
        lock (_timerGate)
        {
            _firstPendingAt = 0;
            if (_disposed)
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Babelfin] The background refresh of library facts failed");
            }
        });
    }

    private void LoadFile()
    {
        try
        {
            if (AtomicJsonFile.Read<FactsFile>(_filePath, BabelfinJson.Options, _logger, out var file) == JsonReadOutcome.Loaded
                && file is not null && file.ComputedAt is not null)
            {
                _snapshot = new Snapshot(file.ComputedAt, (file.Libraries ?? new List<LibraryFact>()).Where(f => f is not null && f.LibraryId != Guid.Empty).ToList());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Babelfin] Couldn't load the cached library facts; they are computed again");
        }
    }

    private void SaveFile(DateTime computedAt, List<LibraryFact> facts)
    {
        try
        {
            AtomicJsonFile.Write(_filePath, new FactsFile { ComputedAt = computedAt, Libraries = facts }, BabelfinJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[Babelfin] Couldn't save the library facts; they apply until the server restarts");
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] A library-facts-changed handler failed");
        }
    }

    /// <summary>The cache file's shape.</summary>
    private sealed class FactsFile
    {
        public int SchemaVersion { get; set; } = 1;

        public DateTime? ComputedAt { get; set; }

        public List<LibraryFact>? Libraries { get; set; }
    }

    /// <summary>An immutable view of the facts, swapped whole after each refresh.</summary>
    private sealed class Snapshot
    {
        public Snapshot(DateTime? computedAt, IReadOnlyList<LibraryFact> all)
        {
            ComputedAt = computedAt;
            All = all;
            ById = new Dictionary<Guid, LibraryFact>();
            foreach (var fact in all)
            {
                ById[fact.LibraryId] = fact;
            }
        }

        public static Snapshot Empty { get; } = new(null, Array.Empty<LibraryFact>());

        public DateTime? ComputedAt { get; }

        public IReadOnlyList<LibraryFact> All { get; }

        public Dictionary<Guid, LibraryFact> ById { get; }
    }
}
