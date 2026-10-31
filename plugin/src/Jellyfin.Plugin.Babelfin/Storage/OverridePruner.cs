using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Babelfin.Storage;

// The compatibility-named prune routes are report-only: temporary library disappearance must never erase personal settings.
internal static class OverridePruner
{
    internal const int BatchSize = 500;

    internal static string? Readiness(ILibraryManager library, IServiceProvider? services = null)
    {
        try
        {
            if (ScanRunning(services)) return "A library scan is active. Retry the report after it finishes; no settings were changed.";
            if (library.GetVirtualFolders()?.Any(folder => Guid.TryParse(folder.ItemId, out var id) && id != Guid.Empty) != true)
                return "The library is not available. No settings were changed.";
            var probe = library.GetItemList(new InternalItemsQuery
            {
                Recursive = true, Limit = 1,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.Season, BaseItemKind.Video, BaseItemKind.BoxSet },
                DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false },
            });
            if (probe is not { Count: > 0 }) return "The library is empty or not ready. No settings were changed.";
            return ScanRunning(services) ? "A library scan is active. No settings were changed." : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "The library availability check failed. Retry later; no settings were changed.";
        }
    }

    private static bool ScanRunning(IServiceProvider? services)
        => (services ?? Plugin.Services)?.GetService(typeof(ITaskManager)) is ITaskManager tasks
            && tasks.ScheduledTasks?.Any(task => task.ScheduledTask.Key == "RefreshLibrary" && task.State is TaskState.Running or TaskState.Cancelling) == true;

    internal static PruneOverridesReport Prune(IPersonStore store, ILibraryManager library, Guid userId, Guid? after, CancellationToken cancellationToken = default, Func<bool>? canPrune = null, IServiceProvider? services = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Readiness(library, services) is { } reason) return Skipped(reason);
        try { PersonStorageAccess.Require(store, userId, writing: false); }
        catch (PersonStorageConflictException blocked)
        {
            return new PruneOverridesReport(0, 0, null, null) { Ready = false, AccountSkipped = true, SkippedReason = blocked.Message };
        }
        var observed = store.TryGet(userId)?.Clone();
        if (observed is null) return new PruneOverridesReport(0, 0, null, null);
        if (observed.SchemaVersion > PersonSettings.CurrentSchemaVersion)
            return new PruneOverridesReport(0, 0, null, null) { Ready = false, AccountSkipped = true, SkippedReason = "These settings use a newer format. Upgrade Babelfin before inspecting this account." };
        var ids = observed.Overrides.Select(o => o.ItemId).Concat(observed.BurnedIn.Select(b => b.ItemId))
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct()
            .OrderBy(id => id.ToString("N"), StringComparer.Ordinal)
            .Where(id => after is null || string.CompareOrdinal(id.ToString("N"), after.Value.ToString("N")) > 0)
            .Take(BatchSize + 1).ToArray();
        var page = ids.Take(BatchSize).ToArray();
        var next = ids.Length > BatchSize ? page[^1] : (Guid?)null;
        if (page.Length == 0) return new PruneOverridesReport(0, 0, null, null);
        var confirmed = new HashSet<Guid>();
        try
        {
            // No person, parental filters or visibility predicate: hidden items still exist.
            var items = library.GetItemList(new InternalItemsQuery
            {
                ItemIds = page,
                DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false },
            }) ?? throw new IOException("The library existence check did not complete.");
            foreach (var id in page.Except(items.Select(item => item.Id)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (library.GetItemById(id) is null) confirmed.Add(id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Skipped("The library existence check failed. Retry later; no settings were changed.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (canPrune?.Invoke() == false) return Skipped("A library scan started. No settings were changed.");
        if (Readiness(library, services) is { } changed) return Skipped(changed);
        bool Missing(string value) => Guid.TryParse(value, out var id) && confirmed.Contains(id);
        return new PruneOverridesReport(0, 0, null, next)
        {
            MissingOverrides = observed.Overrides.Count(entry => Missing(entry.ItemId)),
            MissingBurnedIn = observed.BurnedIn.Count(entry => Missing(entry.ItemId)),
            MissingItemIds = page.Where(confirmed.Contains).ToArray(),
        };
    }

    private static PruneOverridesReport Skipped(string reason) => new(0, 0, null, null) { Ready = false, SkippedReason = reason };
}
