using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class DeletedItemMaintenanceTests
{
    private static ILibraryManager Library(Guid present, bool empty = false, Action? probe = null) => CallProxy.Create<ILibraryManager>((method, args) =>
    {
        if (method.Name == "GetVirtualFolders") return new List<VirtualFolderInfo> { new() { ItemId = Guid.NewGuid().ToString("N") } };
        if (method.Name == "GetItemById") return (Guid)args[0]! == present ? new Movie { Id = present } : null;
        if (method.Name != "GetItemList") return null;
        probe?.Invoke();
        var query = (InternalItemsQuery)args[0]!;
        Assert.Null(query.User);
        if (query.ItemIds.Length == 0) { Assert.Equal(1, query.Limit); return empty ? Array.Empty<BaseItem>() : new BaseItem[] { new Movie { Id = present } }; }
        Assert.InRange(query.ItemIds.Length, 1, 500);
        return query.ItemIds.Contains(present) ? new BaseItem[] { new Movie { Id = present } } : Array.Empty<BaseItem>();
    }).Proxy;

    [Fact]
    public void DailyTaskIsDiscoverableAndHasNoStartupTrigger()
    {
        using var host = new LocalStorageTestHost();
        var task = new OverridePruneTask(host.Store, Library(Guid.NewGuid()), ApiSafetyHost.Users(), NullLogger<OverridePruneTask>.Instance);
        Assert.Contains(typeof(OverridePruneTask), typeof(OverridePruneTask).Assembly.GetExportedTypes().Where(type => typeof(IScheduledTask).IsAssignableFrom(type)));
        var trigger = Assert.Single(task.GetDefaultTriggers());
        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromDays(1).Ticks, trigger.IntervalTicks);
        Assert.Equal("Babelfin", task.Category);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BadAccountIsIsolatedWhileHiddenExistentIsKeptAndHealthyRecoveryCanBeImported(bool locked)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store;
        var bad = ApiSafetyHost.Person("Unavailable", Guid.NewGuid()); var good = ApiSafetyHost.Person("Healthy", Guid.NewGuid());
        var hidden = Guid.NewGuid(); var deleted = Guid.NewGuid();
        store.GetOrCreate(good.Id, () => new PersonSettings { UserName = good.Username, Overrides = new List<ItemOverride>
        {
            new() { ItemId = hidden.ToString("N"), Name = "Hidden existing" }, new() { ItemId = deleted.ToString("N"), Name = "Deleted" },
        } });
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(bad.Id))!);
        var original = "{\"schemaVersion\":999,\"everyday\":{\"reads\":{\"future\":true}}}";
        File.WriteAllText(store.FilePath(bad.Id), original);
        using var held = locked ? new FileStream(store.FilePath(bad.Id), FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var legacyFolder = Path.Combine(host.Folder, "pruned-overrides"); Directory.CreateDirectory(legacyFolder);
        var legacyRecovery = Path.Combine(legacyFolder, "legacy-recovery.json");
        var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(new ExportDocument { Users = new() { store.TryGet(good.Id)!.Clone() } }, BabelfinJson.Options);
        File.WriteAllBytes(legacyRecovery, legacyBytes);
        var task = new OverridePruneTask(store, Library(hidden), ApiSafetyHost.Users(bad, good), NullLogger<OverridePruneTask>.Instance);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Equal(2, store.TryGet(good.Id)!.Overrides.Count);
        Assert.Contains(store.TryGet(good.Id)!.Overrides, entry => entry.ItemId == hidden.ToString("N"));
        Assert.Equal(legacyBytes, File.ReadAllBytes(legacyRecovery));
        // A deliberate fixture edit still can be recovered from a pre-existing archive after read-only reporting.
        store.Update(good.Id, current => { var copy = current!.Clone(); copy.Overrides.RemoveAll(entry => entry.ItemId == deleted.ToString("N")); return copy; });
        held?.Dispose(); Assert.Equal(original, File.ReadAllText(store.FilePath(bad.Id)));
        var recoveryPath = Assert.Single(Directory.GetFiles(Path.Combine(host.Folder, "pruned-overrides"), "*.json"));
        var recovery = JsonSerializer.Deserialize<ExportDocument>(File.ReadAllText(recoveryPath), BabelfinJson.Options)!;
        Assert.Equal(good.Id, Assert.Single(recovery.Users).UserId);
        var users = new[] { new ImportTargetUser(good.Id, good.Username) };
        var preview = ImportPlanner.Run(recovery, new PluginConfiguration(), users, store, LanguageTable.Default, _ => { }, true);
        Assert.False(preview.Applied);
        var imported = ImportPlanner.Run(recovery, new PluginConfiguration(), users, store, LanguageTable.Default, _ => { }, false);
        Assert.True(imported.Applied);
        Assert.Equal(2, store.TryGet(good.Id)!.Overrides.Count);
        Assert.Contains(store.TryGet(good.Id)!.Overrides, entry => entry.ItemId == deleted.ToString("N"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrTemporarilyUnavailableDatabaseNeverTriggersPrune(bool failQuery)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings { Overrides = new List<ItemOverride> { new() { ItemId = Guid.NewGuid().ToString("N") } } });
        var before = File.ReadAllBytes(store.FilePath(user.Id));
        var task = new OverridePruneTask(store, Library(Guid.NewGuid(), true, failQuery ? () => throw new IOException("Database unavailable") : null), ApiSafetyHost.Users(user), NullLogger<OverridePruneTask>.Instance);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Equal(before, File.ReadAllBytes(store.FilePath(user.Id)));
        Assert.False(Directory.Exists(Path.Combine(host.Folder, "pruned-overrides")));
    }

    private sealed class Services(ITaskManager tasks) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(ITaskManager) ? tasks : null;
    }

    [Theory]
    [InlineData(TaskState.Running)]
    [InlineData(TaskState.Cancelling)]
    public async Task ActiveLibraryScanSkipsWithoutAnyLibraryOrAccountQueries(TaskState state)
    {
        using var host = new LocalStorageTestHost();
        var scan = CallProxy.Create<IScheduledTask>((method, _) => method.Name == "get_Key" ? "RefreshLibrary" : null).Proxy;
        var worker = CallProxy.Create<IScheduledTaskWorker>((method, _) => method.Name switch { "get_ScheduledTask" => scan, "get_State" => state, _ => null }).Proxy;
        var tasks = CallProxy.Create<ITaskManager>((method, _) => method.Name == "get_ScheduledTasks" ? new[] { worker } : null).Proxy;
        var library = CallProxy.Create<ILibraryManager>((_, _) => throw new InvalidOperationException("Must not query during a scan")).Proxy;
        var task = new OverridePruneTask(host.Store, library, ApiSafetyHost.Users(), NullLogger<OverridePruneTask>.Instance, new Services(tasks));
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
    }

    [Fact]
    public async Task CancellationDuringReportPagesPreservesEveryReference()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings { Overrides = Enumerable.Range(0, 502).Select(_ => new ItemOverride { ItemId = Guid.NewGuid().ToString("N") }).ToList() });
        using var stop = new CancellationTokenSource();
        var seenPage = false;
        var baseLibrary = Library(Guid.NewGuid());
        var reportingLibrary = CallProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == "GetItemList" && ((InternalItemsQuery)args[0]!).ItemIds.Length > 0) { seenPage = true; stop.Cancel(); }
            return method.Invoke(baseLibrary, args);
        }).Proxy;
        var task = new OverridePruneTask(store, reportingLibrary, ApiSafetyHost.Users(user), NullLogger<OverridePruneTask>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.ExecuteAsync(new Progress<double>(), stop.Token));
        Assert.True(seenPage);
        Assert.Equal(502, store.TryGet(user.Id)!.Overrides.Count);
        Assert.False(Directory.Exists(Path.Combine(host.Folder, "pruned-overrides")));
    }

    [Fact]
    public void ReportDoesNotEnterADocumentUpdate()
    {
        using var host = new LocalStorageTestHost(); var inner = host.Store; var user = ApiSafetyHost.Person();
        inner.GetOrCreate(user.Id, () => new PersonSettings { Overrides = new() { new() { ItemId = Guid.NewGuid().ToString("N") } } });
        var updating = false;
        var store = new ConcurrentUpdateHookStore(inner) { BeforeUpdate = () => updating = true };
        var library = DeletedItemReportTests.ReadyLibrary(Guid.NewGuid(), _ => { Assert.False(updating); return Array.Empty<BaseItem>(); });
        var report = OverridePruner.Prune(store, library, user.Id, null);
        Assert.Equal(1, report.MissingOverrides); Assert.Equal(0, report.RemovedOverrides); Assert.False(updating);
    }

}
