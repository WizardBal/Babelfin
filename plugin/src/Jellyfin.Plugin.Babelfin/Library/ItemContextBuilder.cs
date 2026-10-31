using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Library;

/// <summary>
/// Builds the <see cref="ItemContext"/> the resolver and rules need from a Jellyfin item. Path-free by construction.
/// </summary>
/// <remarks>
/// Behavior:
/// <list type="bullet">
/// <item>The per-person lookups (collections, which may touch the database, and the item's libraries) are cached together per
/// (person, item) for 5 minutes in a cache of at most <see cref="MaxCacheEntries"/> entries that is cleared when full. Collections
/// are per person because Jellyfin filters them by what the person may see. Series metadata (including missing series) has its own
/// bounded five-minute cache, invalidated on library changes. Series collections have a separate bounded per-(person, series) cache so sibling episodes share that query. Native library-parent fallback is also shared by parent id so sibling episodes do not repeat the same ancestry lookups.</item>
/// <item>A collection lookup that throws gives no collections (logged once a minute at most, not cached), so a collection problem
/// never blocks playback. The optional logger parameter keeps the agreed three-argument constructor working.</item>
/// <item>An extra's owner is followed at most three levels deep; an owner that can't be found leaves the extra as an item of its own.</item>
/// <item>For a series, <see cref="ItemContext.SeriesName"/> is its own name; for a season, the season and its series ids are both set.</item>
/// <item>Values in genres, tags, studios and countries are trimmed, empty ones dropped, duplicates removed case-insensitively
/// (the item's own come first).</item>
/// </list>
/// </remarks>
public sealed class ItemContextBuilder : IItemContextBuilder, IDisposable
{
    /// <summary>The most (person, item) lookups kept.</summary>
    public const int MaxCacheEntries = 2000;

    private const int MaxOwnerDepth = 3;
    private static readonly long CacheLifetimeMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
    private static readonly long ErrorLogIntervalMs = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILibraryFacts _facts;
    private readonly ILogger<ItemContextBuilder>? _logger;
    private readonly ConcurrentDictionary<(Guid User, Guid Item), Lookups> _cache = new();
    private readonly ConcurrentDictionary<Guid, (BaseItem? Item, long ExpiresAt)> _series = new();
    private readonly ConcurrentDictionary<(Guid User, Guid Series), (BoxSet[] Collections, long ExpiresAt)> _seriesCollections = new();
    private readonly ConcurrentDictionary<Guid, (BaseItem? Item, long ExpiresAt)> _topParents = new();
    private readonly object _seriesGate = new();
    private long _seriesGeneration;
    private bool _disposed;
    private long _lastErrorLog = long.MinValue / 2;

    /// <summary>Initializes a new instance of the <see cref="ItemContextBuilder"/> class.</summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="collectionManager">Jellyfin's collection manager.</param>
    /// <param name="facts">The library facts.</param>
    /// <param name="logger">Optional logger.</param>
    public ItemContextBuilder(ILibraryManager libraryManager, ICollectionManager collectionManager, ILibraryFacts facts, ILogger<ItemContextBuilder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(collectionManager);
        ArgumentNullException.ThrowIfNull(facts);
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _facts = facts;
        _logger = logger;
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemChanged;
    }

    /// <summary>Gets the number of cached lookups (diagnostics).</summary>
    public int CacheSize => _cache.Count;

    /// <inheritdoc />
    public ItemContext Build(BaseItem item, User user, string? client = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(user);
        return Build(item, user, client, 0);
    }

    private static string[] Merge(IEnumerable<string>? own, IEnumerable<string>? inherited)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var source in new[] { own, inherited })
        {
            if (source is null)
            {
                continue;
            }

            foreach (var raw in source)
            {
                var value = raw?.Trim();
                if (!string.IsNullOrEmpty(value) && seen.Add(value))
                {
                    list.Add(value);
                }
            }
        }

        return list.ToArray();
    }

    private static Guid? NullIfEmpty(Guid id) => id == Guid.Empty ? null : id;

    private ItemContext Build(BaseItem item, User user, string? client, int depth)
    {
        if (item.ExtraType is not null && item.OwnerId != Guid.Empty && item.OwnerId != item.Id && depth < MaxOwnerDepth
            && LibraryQueries.Read(() => _libraryManager.GetItemById(item.OwnerId)) is { } owner)
        {
            // Extras and trailers inherit their parent's rule.
            return Build(owner, user, client, depth + 1) with { ItemId = item.Id };
        }

        var kind = item switch
        {
            Movie => ContentKind.Movie,
            Episode => ContentKind.Episode,
            _ => ContentKind.Other,
        };

        Guid? seasonId = null;
        Guid? seriesId = null;
        string? seriesName = null;
        BaseItem? series = null;
        switch (item)
        {
            case Episode episode:
                seasonId = NullIfEmpty(episode.SeasonId);
                seriesId = NullIfEmpty(episode.SeriesId);
                seriesName = episode.SeriesName;
                series = GetSeries(seriesId);
                break;
            case Season season:
                seasonId = item.Id;
                seriesId = NullIfEmpty(season.SeriesId);
                seriesName = season.SeriesName;
                series = GetSeries(seriesId);
                break;
            case Series:
                seriesId = item.Id;
                seriesName = item.Name;
                break;
        }

        if (series is not null)
        {
            seriesId ??= NullIfEmpty(series.Id);
            seriesName ??= series.Name;
        }

        var lookups = GetLookups(item, user, seriesId);
        var libraryId = lookups.LibraryIds.Length > 0 ? lookups.LibraryIds[0] : (Guid?)null;

        return new ItemContext(
            item.Id,
            item.Name,
            kind,
            seasonId,
            seriesId,
            seriesName,
            lookups.CollectionIds,
            lookups.CollectionNames,
            lookups.LibraryIds,
            lookups.LibraryNames.Length > 0 ? lookups.LibraryNames[0] : null,
            Merge(item.Genres, series?.Genres),
            Merge(item.Tags, series?.Tags),
            Merge(item.Studios, series?.Studios),
            Merge(item.ProductionLocations, series?.ProductionLocations),
            LibraryQueries.Read(() => item is Episode or Season ? (item.OriginalLanguage ?? series?.GetInheritedOriginalLanguage()) : item.GetInheritedOriginalLanguage()),
            client,
            libraryId is null ? null : _facts.Get(libraryId.Value));
    }

    /// <summary>Unsubscribes metadata invalidation and releases the bounded lookup caches.</summary>
    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemChanged;
        lock (_seriesGate) { _disposed = true; _seriesGeneration++; _cache.Clear(); _series.Clear(); _topParents.Clear(); _seriesCollections.Clear(); }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e?.Item is not { } item) return;
        lock (_seriesGate)
        {
            _seriesGeneration++;
            var seriesId = item switch
            {
                Series => (Guid?)item.Id,
                Season season => NullIfEmpty(season.SeriesId),
                _ => null,
            };
            _series.TryRemove(item.Id, out _);
            if (seriesId is { } id) _series.TryRemove(id, out _);
            _topParents.Clear();
            if ((item is Folder && item is not (Series or Season)) || item is BoxSet)
            {
                _cache.Clear();
                _seriesCollections.Clear();
            }
            else
            {
                foreach (var pair in _cache)
                    if (pair.Key.Item == item.Id || (seriesId.HasValue && pair.Value.SeriesId == seriesId))
                        _cache.TryRemove(pair.Key, out _);
                foreach (var key in _seriesCollections.Keys)
                    if (key.Series == item.Id || key.Series == seriesId) _seriesCollections.TryRemove(key, out _);
            }
        }
    }

    private BaseItem? GetSeries(Guid? id)
    {
        if (id is null) return null;
        return CachedMetadata(_series, id.Value, () => LibraryQueries.Read(() => _libraryManager.GetItemById(id.Value)));
    }

    private BaseItem? TopParent(BaseItem item)
    {
        // Preserve native self/top-parent behavior. Siblings with the same parent otherwise have the same ancestry.
        if (item.ParentId == Guid.Empty || item.IsTopParent) return LibraryQueries.Read(item.GetTopParent);
        return CachedMetadata(_topParents, item.ParentId, () => LibraryQueries.Read(item.GetTopParent));
    }

    private BaseItem? CachedMetadata(ConcurrentDictionary<Guid, (BaseItem? Item, long ExpiresAt)> cache, Guid id, Func<BaseItem?> lookup)
    {
        var now = Environment.TickCount64;
        long generation;
        lock (_seriesGate)
        {
            if (cache.TryGetValue(id, out var cached) && cached.ExpiresAt > now) return cached.Item;
            generation = _seriesGeneration;
        }
        var item = lookup();
        // No library I/O under the gate. An event or disposal during this lookup must win over its stale result.
        lock (_seriesGate)
        {
            if (!_disposed && generation == _seriesGeneration)
            {
                if (cache.Count >= MaxCacheEntries) cache.Clear();
                cache[id] = (item, now + CacheLifetimeMs);
            }
        }
        return item;
    }

    private Lookups GetLookups(BaseItem item, User user, Guid? seriesId)
    {
        var key = (user.Id, item.Id);
        var now = Environment.TickCount64;
        long generation;
        lock (_seriesGate)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now) return cached;
            generation = _seriesGeneration;
        }

        var (libraryIds, libraryNames) = Libraries(_libraryManager, item, TopParent);
        var (collectionIds, collectionNames, ok) = Collections(item, user, seriesId);
        var lookups = new Lookups(libraryIds, libraryNames, collectionIds, collectionNames, seriesId, now + CacheLifetimeMs);
        if (ok)
        {
            lock (_seriesGate)
            {
                // An event or disposal during either lookup must prevent its result from being cached.
                if (!_disposed && generation == _seriesGeneration)
                {
                    if (_cache.Count >= MaxCacheEntries) _cache.Clear();
                    _cache[key] = lookups;
                }
            }
        }

        return lookups;
    }

    /// <summary>The libraries (collection folders) an item belongs to, with their names; usually one. The override lists find an item's library the same way.</summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="item">The item.</param>
    /// <returns>The library ids and names, same order (empty when the item is in no library).</returns>
    internal static (Guid[] Ids, string[] Names) Libraries(ILibraryManager libraryManager, BaseItem item) => Libraries(libraryManager, item, null);

    private static (Guid[] Ids, string[] Names) Libraries(ILibraryManager libraryManager, BaseItem item, Func<BaseItem, BaseItem?>? topParent)
    {
        var ids = new List<Guid>();
        var names = new List<string>();
        var folders = LibraryQueries.Read(() => libraryManager.GetCollectionFolders(item)?.ToArray());
        if (folders is not null)
        {
            foreach (var folder in folders)
            {
                if (folder is not null && folder.Id != Guid.Empty && !ids.Contains(folder.Id))
                {
                    ids.Add(folder.Id);
                    names.Add(folder.Name ?? string.Empty);
                }
            }
        }

        if (ids.Count == 0 && (topParent is null ? LibraryQueries.Read(item.GetTopParent) : topParent(item)) is CollectionFolder top && top.Id != Guid.Empty)
        {
            ids.Add(top.Id);
            names.Add(top.Name ?? string.Empty);
        }

        return (ids.ToArray(), names.ToArray());
    }

    private (Guid[] Ids, string[] Names, bool Ok) Collections(BaseItem item, User user, Guid? seriesId)
    {
        var ids = new List<Guid>();
        var names = new List<string>();
        try
        {
            Add(_collectionManager.GetCollectionsContainingItem(user, item.Id));
            if (item is Episode or Season && seriesId is { } series && series != item.Id)
            {
                Add(SeriesCollections(user, series));
            }

            return (ids.ToArray(), names.ToArray(), true);
        }
        catch (Exception ex)
        {
            var now = Environment.TickCount64;
            if (_logger is not null && now - System.Threading.Interlocked.Read(ref _lastErrorLog) >= ErrorLogIntervalMs)
            {
                System.Threading.Interlocked.Exchange(ref _lastErrorLog, now);
                _logger.LogWarning(ex, "[Babelfin] Couldn't look up the collections of {Item}; collection settings are skipped for it (logged at most once a minute)", item.Name);
            }

            return (Array.Empty<Guid>(), Array.Empty<string>(), false);
        }

        void Add(IEnumerable<BoxSet>? sets)
        {
            if (sets is null)
            {
                return;
            }

            foreach (var set in sets)
            {
                if (set is not null && set.Id != Guid.Empty && !ids.Contains(set.Id))
                {
                    ids.Add(set.Id);
                    names.Add(set.Name ?? string.Empty);
                }
            }
        }
    }

    private BoxSet[] SeriesCollections(User user, Guid seriesId)
    {
        var key = (user.Id, seriesId);
        var now = Environment.TickCount64;
        long generation;
        lock (_seriesGate)
        {
            if (_seriesCollections.TryGetValue(key, out var cached) && cached.ExpiresAt > now) return cached.Collections;
            generation = _seriesGeneration;
        }
        var collections = _collectionManager.GetCollectionsContainingItem(user, seriesId)?.ToArray() ?? Array.Empty<BoxSet>();
        lock (_seriesGate)
        {
            if (!_disposed && generation == _seriesGeneration)
            {
                if (_seriesCollections.Count >= MaxCacheEntries) _seriesCollections.Clear();
                _seriesCollections[key] = (collections, now + CacheLifetimeMs);
            }
        }
        return collections;
    }

    private sealed record Lookups(Guid[] LibraryIds, string[] LibraryNames, Guid[] CollectionIds, string[] CollectionNames, Guid? SeriesId, long ExpiresAt);
}
