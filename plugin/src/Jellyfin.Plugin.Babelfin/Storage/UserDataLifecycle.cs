using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Babelfin.Activity;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Storage;

/// <summary>Archives deleted user settings and removes their in-memory activity.</summary>
public sealed class UserDataLifecycle : IHostedService, IEventConsumer<UserDeletedEventArgs>
{
    private readonly IPersonStore _store;
    private readonly ActivityLog _activity;
    private readonly IUserManager _users;
    private readonly ILogger<UserDataLifecycle> _logger;

    /// <summary>Creates the deletion consumer and startup reconciliation service without accessing storage.</summary>
    public UserDataLifecycle(IPersonStore store, ActivityLog activity, IUserManager users, ILogger<UserDataLifecycle> logger)
    {
        _store = store;
        _activity = activity;
        _users = users;
        _logger = logger;
        if (_store is IPersonStoreSafety safety) safety.Reactivating += _activity.AllowUser;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A failed user listing must never be interpreted as an empty server.
            var active = _users.GetUsers().Select(u => u.Id).ToHashSet();
            if (active.Count == 0 && PersonStoreInventory.KnownIds(_store).Count > 0)
            {
                _logger.LogWarning("[Babelfin] Startup account listing was empty while stored settings exist; reconciliation was skipped and original data retained");
                return Task.CompletedTask;
            }
            foreach (var id in active)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (UserLifecycleTransitions.For(_store))
                {
                    // GetUsers is only a candidate snapshot. A deletion event may have completed since it was read.
                    if (_users.GetUserById(id) is null) continue;
                    if (_store is IPersonStoreSafety safety) safety.Activate(id);
                }
            }
            var deleted = PersonStoreInventory.KnownIds(_store).Where(id => id != Guid.Empty && !active.Contains(id)).ToHashSet();
            Archive(deleted, revalidate: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't reconcile deleted users; original data is retained and cleanup will retry on the next startup");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        if (eventArgs?.Argument is { Id: var id } && id != Guid.Empty) Archive(new HashSet<Guid> { id });
        return Task.CompletedTask;
    }

    private void Archive(IReadOnlySet<Guid> deleted, bool revalidate = false)
    {
        if (deleted.Count == 0) return;
        lock (UserLifecycleTransitions.For(_store))
        {
            foreach (var id in deleted)
            {
                // A newly created account must not be deleted because it was absent from an earlier startup listing.
                if (revalidate && _users.GetUserById(id) is not null) continue;
                _activity.RemoveUser(id);
                try
                {
                    _store.Delete(id);
                    _logger.LogInformation("[Babelfin] Deleted user {UserId} blocked from active settings; recovery archives require manual retention review", id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Babelfin] Couldn't archive settings of deleted user {UserId}; original data is retained and cleanup will retry on the next startup", id);
                }
            }

        }
    }
}

/// <summary>Serializes whole account transitions across settings and activity, including the positive creation consumer.</summary>
internal static class UserLifecycleTransitions
{
    private static readonly ConditionalWeakTable<IPersonStore, object> Gates = new();
    internal static object For(IPersonStore store) => Gates.GetValue(store, _ => new object());
}
