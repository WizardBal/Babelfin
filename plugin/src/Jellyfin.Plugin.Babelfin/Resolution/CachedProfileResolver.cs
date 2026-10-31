using System;
using System.Collections.Concurrent;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Seeding;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Resolution;

/// <summary>
/// The compiled-profile cache: resolves a person's settings for a Jellyfin item through <see cref="ProfileResolver"/> and keeps
/// the result per (person, item, app) so a repeat lookup is one dictionary read. At most <see cref="MaxEntries"/> entries (all dropped when
/// full); each expires after ten minutes as a safety net. Dropped on any settings save, configuration change, library-facts refresh, and
/// on item updates and removals. A person with no file is seeded from their Jellyfin settings on first use.
/// </summary>
public sealed class CachedProfileResolver : IProfileResolver, IDisposable
{
    /// <summary>The most entries kept before the cache is emptied.</summary>
    public const int MaxEntries = 5000;

    private static readonly long TimeToLiveMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    private readonly IPersonStore _store;
    private readonly IItemContextBuilder _contextBuilder;
    private readonly ILibraryFacts _facts;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager? _serverConfiguration;
    private readonly ILogger<CachedProfileResolver> _logger;
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> _cache = new();
    private readonly Plugin? _plugin;
    private long _generation;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="CachedProfileResolver"/> class and subscribes to every invalidation source.</summary>
    /// <param name="store">The per-person settings store.</param>
    /// <param name="contextBuilder">Builds item contexts.</param>
    /// <param name="facts">The library facts.</param>
    /// <param name="libraryManager">Jellyfin's library manager (item updates and removals).</param>
    /// <param name="serverConfiguration">The server configuration (its metadata language seeds people with no language); null in test hosts.</param>
    /// <param name="logger">The logger.</param>
    public CachedProfileResolver(
        IPersonStore store,
        IItemContextBuilder contextBuilder,
        ILibraryFacts facts,
        ILibraryManager libraryManager,
        IServerConfigurationManager? serverConfiguration,
        ILogger<CachedProfileResolver> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contextBuilder = contextBuilder ?? throw new ArgumentNullException(nameof(contextBuilder));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _serverConfiguration = serverConfiguration;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _store.Changed += OnStoreChanged;
        _facts.Changed += OnFactsChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemChanged;
        _plugin = Plugin.Instance;
        if (_plugin is not null)
        {
            _plugin.ConfigurationChanged += OnConfigurationChanged;
        }
    }

    /// <summary>Gets the number of cached profiles (admin diagnostics).</summary>
    public int Count => _cache.Count;

    /// <inheritdoc />
    public ResolvedProfileResult Resolve(User user, BaseItem item, string? client = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);
        var key = new CacheKey(user.Id, item.Id, client ?? string.Empty);
        var now = Environment.TickCount64;
        if (_cache.TryGetValue(key, out var hit) && hit.ExpiresAt > now)
        {
            return hit.Result;
        }

        var generation = Interlocked.Read(ref _generation);
        var context = _contextBuilder.Build(item, user, client);
        var result = Resolve(user, context);
        if (_cache.Count >= MaxEntries)
        {
            _cache.Clear();
        }

        _cache[key] = new CacheEntry(result, now + TimeToLiveMs);

        // Settings changed while this was computed: don't keep a result built from the old ones.
        if (Interlocked.Read(ref _generation) != generation)
        {
            _cache.TryRemove(key, out _);
        }

        return result;
    }

    /// <inheritdoc />
    public ResolvedProfileResult Resolve(User user, ItemContext context)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(context);
        var languages = EngineServices.Current.Languages;
        var person = GetPerson(user);
        var admin = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return ProfileResolver.Resolve(person, admin, context, languages);
    }

    /// <inheritdoc />
    public void Invalidate(Guid? userId)
    {
        Interlocked.Increment(ref _generation);
        if (userId is not { } id)
        {
            _cache.Clear();
            return;
        }

        foreach (var key in _cache.Keys)
        {
            if (key.UserId == id)
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Changed -= OnStoreChanged;
        _facts.Changed -= OnFactsChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemChanged;
        if (_plugin is not null)
        {
            _plugin.ConfigurationChanged -= OnConfigurationChanged;
        }
    }

    private PersonSettings GetPerson(User user)
    {
        var seeded = false;
        var person = _store.GetOrCreate(user.Id, () =>
        {
            seeded = true;
            var serverLanguage = _serverConfiguration?.Configuration?.PreferredMetadataLanguage;
            return PersonSeeder.Seed(SeedInputs.FromUser(user, serverLanguage), EngineServices.Current.Languages);
        });
        if (seeded)
        {
            _logger.LogInformation(
                "[Babelfin] Seeded settings for {User} from their Jellyfin preferences ({Assumed} assumed language(s))",
                user.Username,
                person.Seed?.Assumed.Count ?? 0);
        }

        return person;
    }

    private void OnStoreChanged(object? sender, PersonStoreChangedEventArgs e) => Invalidate(e?.UserId);

    private void OnFactsChanged(object? sender, EventArgs e) => Invalidate(null);

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e) => Invalidate(null);

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        var item = e?.Item;
        if (item is null)
        {
            return;
        }

        // Advance even when the cache is empty: an in-flight resolution may still hold old metadata.
        Interlocked.Increment(ref _generation);
        if (_cache.IsEmpty) return;

        // Collection and library changes can affect titles in several series.
        if (item is BoxSet or CollectionFolder)
        {
            Invalidate(null);
            return;
        }

        var seriesId = item switch { Series => (Guid?)item.Id, Season season => season.SeriesId, _ => null };
        var id = item.Id;
        foreach (var key in _cache.Keys)
        {
            if (key.ItemId == id || (seriesId.HasValue && _cache.TryGetValue(key, out var entry) && entry.Result.Context.SeriesId == seriesId))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    private readonly record struct CacheKey(Guid UserId, Guid ItemId, string Client);

    private sealed record CacheEntry(ResolvedProfileResult Result, long ExpiresAt);
}
