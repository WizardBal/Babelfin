using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class DeletedItemReportTests
{
    internal static ILibraryManager ReadyLibrary(Guid present, Func<InternalItemsQuery, IReadOnlyList<BaseItem>>? query = null) => CallProxy.Create<ILibraryManager>((method, args) => method.Name switch
    {
        "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { ItemId = Guid.NewGuid().ToString("N") } },
        "GetItemList" => ((InternalItemsQuery)args[0]!).ItemIds.Length == 0 ? new BaseItem[] { new Movie { Id = present } }
            : query?.Invoke((InternalItemsQuery)args[0]!) ?? Array.Empty<BaseItem>(),
        "GetItemById" => (Guid)args[0]! == present ? new Movie { Id = present } : null,
        _ => null,
    }).Proxy;

    [Fact]
    public void MissingReferencesAreReportedWithoutAnySettingsOrRecoveryWrites()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var user = ApiSafetyHost.Person();
        var missing = Guid.NewGuid(); var hidden = Guid.NewGuid();
        store.GetOrCreate(user.Id, () => new PersonSettings
        {
            Overrides = new() { new() { ItemId = missing.ToString("N") }, new() { ItemId = hidden.ToString("N") } },
            BurnedIn = new() { new() { ItemId = missing.ToString("N"), Languages = new() { "fr" } } },
        });
        var original = File.ReadAllBytes(store.FilePath(user.Id));
        var recovery = Path.Combine(host.Folder, "pruned-overrides"); Directory.CreateDirectory(recovery);
        var legacyFile = Path.Combine(recovery, "recovery.json"); File.WriteAllBytes(legacyFile, original);
        var changes = 0; store.Changed += (_, _) => changes++;
        var library = ReadyLibrary(hidden, query =>
        {
            Assert.Null(query.User);
            return new BaseItem[] { new Movie { Id = hidden } };
        });
        var report = OverridePruner.Prune(store, library, user.Id, null);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(user.Id)));
        Assert.Equal(0, changes);
        Assert.Equal(0, report.RemovedOverrides); Assert.Equal(0, report.RemovedBurnedIn); Assert.Null(report.BackupFile);
        var json = JsonSerializer.SerializeToElement(report, BabelfinJson.Options);
        Assert.True(json.GetProperty("reportOnly").GetBoolean());
        Assert.Equal(1, json.GetProperty("missingOverrides").GetInt32());
        Assert.Equal(1, json.GetProperty("missingBurnedIn").GetInt32());
        Assert.Equal(missing, json.GetProperty("missingItemIds")[0].GetGuid());
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(recovery))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualEndpointsSkipAnEmptyOrUnavailableLibrary(bool unavailable)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings { Overrides = new() { new() { ItemId = Guid.NewGuid().ToString("N") } } });
        var original = File.ReadAllBytes(store.FilePath(user.Id));
        var library = CallProxy.Create<ILibraryManager>((method, _) =>
        {
            if (method.Name == "GetVirtualFolders") return new List<VirtualFolderInfo> { new() { ItemId = Guid.NewGuid().ToString("N") } };
            if (method.Name == "GetItemList") return unavailable ? throw new IOException("Library unavailable") : Array.Empty<BaseItem>();
            return null;
        }).Proxy;
        var me = new MeController(library, ApiSafetyHost.Users(user), CallProxy.Create<ICollectionManager>().Proxy, store, CallProxy.Create<IProfileResolver>().Proxy, new FakeLibraryFacts(), new FakeContextBuilder())
            { ControllerContext = ApiSafetyHost.Me(store, user).ControllerContext };
        var response = Assert.IsType<OkObjectResult>(me.PruneOverrides());
        var json = JsonSerializer.SerializeToElement(response.Value, BabelfinJson.Options);
        Assert.False(json.GetProperty("ready").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("skippedReason").GetString()));
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(user.Id)));
    }

    [Fact]
    public async Task DailyReportNeverMutatesMissingReferences()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings { Overrides = new() { new() { ItemId = Guid.NewGuid().ToString("N") } } });
        var original = File.ReadAllBytes(store.FilePath(user.Id));
        var task = new OverridePruneTask(store, ReadyLibrary(Guid.NewGuid()), ApiSafetyHost.Users(user), NullLogger<OverridePruneTask>.Instance);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(user.Id)));
        Assert.False(Directory.Exists(Path.Combine(host.Folder, "pruned-overrides")));
        Assert.Contains("report", task.Name, StringComparison.OrdinalIgnoreCase);
    }
    private sealed class Services(ITaskManager tasks) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(ITaskManager) ? tasks : null;
    }

    [Theory]
    [InlineData(false, TaskState.Running)]
    [InlineData(false, TaskState.Cancelling)]
    [InlineData(true, TaskState.Running)]
    [InlineData(true, TaskState.Cancelling)]
    public void BothManualReportsSkipActiveScansBeforeAnyLibraryQuery(bool admin, TaskState state)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings { Overrides = new() { new() { ItemId = Guid.NewGuid().ToString("N") } } });
        var original = File.ReadAllBytes(store.FilePath(user.Id));
        var task = CallProxy.Create<IScheduledTask>((method, _) => method.Name == "get_Key" ? "RefreshLibrary" : null).Proxy;
        var worker = CallProxy.Create<IScheduledTaskWorker>((method, _) => method.Name switch { "get_ScheduledTask" => task, "get_State" => state, _ => null }).Proxy;
        var tasks = CallProxy.Create<ITaskManager>((method, _) => method.Name == "get_ScheduledTasks" ? new[] { worker } : null).Proxy;
        var calls = 0;
        var library = CallProxy.Create<ILibraryManager>((_, _) => { calls++; throw new IOException("Must not query during a scan"); }).Proxy;
        var context = ApiSafetyHost.Me(store, user).ControllerContext;
        context.HttpContext.RequestServices = new Services(tasks);
        ActionResult response;
        if (admin)
        {
            var controller = new AdminController(library, ApiSafetyHost.Users(user), store, new FakeLibraryFacts(), CallProxy.Create<IProfileResolver>().Proxy) { ControllerContext = context };
            response = controller.PruneUserOverrides(user.Id);
        }
        else
        {
            var controller = new MeController(library, ApiSafetyHost.Users(user), CallProxy.Create<ICollectionManager>().Proxy, store, CallProxy.Create<IProfileResolver>().Proxy, new FakeLibraryFacts(), new FakeContextBuilder()) { ControllerContext = context };
            response = controller.PruneOverrides();
        }
        var report = Assert.IsType<PruneOverridesReport>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.False(report.Ready); Assert.True(report.ReportOnly); Assert.Contains("scan", report.SkippedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(report.MissingItemIds); Assert.Equal(0, calls);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(user.Id)));
    }

}
