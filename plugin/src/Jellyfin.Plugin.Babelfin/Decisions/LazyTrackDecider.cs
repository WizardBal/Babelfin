using System;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Babelfin.Decisions;

/// <summary>Resolves only on a decision, never in the media source manager factory. Failed resolution retries with bounded back-off.</summary>
public sealed class LazyTrackDecider : ITrackDecider
{
    private readonly IServiceProvider _services;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private volatile ITrackDecider? _inner;
    private DateTime _retryAt;
    private int _failures;

    /// <summary>Initializes a new instance without resolving any dependencies.</summary>
    public LazyTrackDecider(IServiceProvider services) : this(services, () => DateTime.UtcNow) { }

    /// <summary>Initializes a new instance with a testable UTC clock.</summary>
    public LazyTrackDecider(IServiceProvider services, Func<DateTime> clock)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Gets whether resolution has succeeded.</summary>
    public bool IsResolved => _inner is not null;

    /// <inheritdoc />
    public TrackDecision? Decide(BaseItem item, MediaSourceInfo source, User user)
    {
        var inner = _inner;
        if (inner is null)
        {
            lock (_gate)
            {
                inner = _inner;
                if (inner is null)
                {
                    if (_clock() < _retryAt)
                    {
                        return null;
                    }

                    try
                    {
                        inner = _services.GetRequiredService<ITrackDecider>();
                        _inner = inner;
                    }
                    catch
                    {
                        _failures = Math.Min(_failures + 1, 7);
                        _retryAt = _clock().AddSeconds(Math.Min(60, 1 << (_failures - 1)));
                        throw;
                    }
                }
            }
        }

        return inner.Decide(item, source, user);
    }
}
