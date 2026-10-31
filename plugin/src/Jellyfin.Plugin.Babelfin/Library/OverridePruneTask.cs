using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Library;

/// <summary>Visible, daily report of missing item references. Settings and recovery files are never changed. Never runs on startup or while the library database appears empty or is being scanned.</summary>
public sealed class OverridePruneTask : IScheduledTask
{
    private readonly IPersonStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ILogger<OverridePruneTask> _logger;
    private readonly IServiceProvider? _services;

    /// <summary>Creates the maintenance task without reading any library or account storage.</summary>
    public OverridePruneTask(IPersonStore store, ILibraryManager library, IUserManager users, ILogger<OverridePruneTask> logger, IServiceProvider? services = null)
    {
        _store = store; _library = library; _users = users; _logger = logger; _services = services;
    }

    /// <inheritdoc />
    public string Name => "Babelfin: report missing-item overrides";
    /// <inheritdoc />
    public string Key => "BabelfinPruneDeletedOverrides";
    /// <inheritdoc />
    public string Description => "Reports overrides and burned-in flags for items missing from the library, without removing or changing them. Skips empty or unavailable libraries and running scans. Existing recovery files are retained.";
    /// <inheritdoc />
    public string Category => "Babelfin";
    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromDays(1).Ticks },
    };

    // Resolve task-manager state only at execution time; task discovery must not introduce a startup cycle.
    private bool LibraryReady() => OverridePruner.Readiness(_library, _services) is null;

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!LibraryReady())
            {
                _logger.LogInformation("[Babelfin] Missing-item report skipped: a library scan is active or the library is not ready/nonempty; no settings were changed");
                return Task.CompletedTask;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[Babelfin] Missing-item report skipped: the library readiness query failed; no settings were changed");
            return Task.CompletedTask;
        }
        var failed = new List<string>();
        var ids = PersonStoreInventory.KnownIds(_store);
        var stopped = false;
        for (var i = 0; i < ids.Count && !stopped; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = ids[i]; var name = id.ToString("N");
            try
            {
                var user = _users.GetUserById(id);
                if (user is null) continue;
                name = user.Username;
                Guid? after = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!LibraryReady())
                    {
                        _logger.LogInformation("[Babelfin] Missing-item report stopped after {Completed} of {Total} accounts: library is not ready", i, ids.Count);
                        stopped = true;
                        break;
                    }
                    var report = OverridePruner.Prune(_store, _library, id, after, cancellationToken, services: _services);
                    if (report.AccountSkipped)
                    {
                        _logger.LogInformation("[Babelfin] Missing-item report skipped {User}: {Reason}", name, report.SkippedReason);
                        break;
                    }
                    if (!report.Ready)
                    {
                        _logger.LogInformation("[Babelfin] Missing-item report skipped: {Reason}", report.SkippedReason);
                        _logger.LogInformation("[Babelfin] Missing-item report stopped after {Completed} of {Total} accounts: {Reason}", i, ids.Count, report.SkippedReason);
                        stopped = true;
                        break;
                    }
                    if (report.MissingOverrides + report.MissingBurnedIn > 0)
                        _logger.LogInformation("[Babelfin] Report only: {Overrides} overrides and {Flags} flags for {User} refer to missing items; settings and recovery files were preserved", report.MissingOverrides, report.MissingBurnedIn, name);
                    after = report.NextAfter;
                }
                while (after is not null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (PersonStorageConflictException ex)
            {
                _logger.LogInformation("[Babelfin] Missing-item report skipped {User}: {Reason}", name, ex.Message);
            }
            catch (Exception ex)
            {
                failed.Add(name);
                _logger.LogWarning(ex, "[Babelfin] Missing-item report skipped remaining pages of {User}; settings are preserved, recovery archives are retained, and other people continue", name);
            }
            progress?.Report(100.0 * (i + 1) / Math.Max(1, ids.Count));
        }
        if (failed.Count > 0) throw new IOException("Missing-item report completed with skipped accounts: " + string.Join(", ", failed) + ". Check the server log for their individual reasons.");
        progress?.Report(100);
        return Task.CompletedTask;
    }
}
