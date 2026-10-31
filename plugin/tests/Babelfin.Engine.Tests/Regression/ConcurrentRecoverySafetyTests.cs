using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Activity;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

// A real PersonStore with a deterministic interleaving between the API's read and its locked update.
internal sealed class ConcurrentUpdateHookStore(PersonStore inner) : IPersonStore, IPersonStoreSafety
{
    internal Action? BeforeUpdate;
    public event Action<Guid>? Reactivating { add => inner.Reactivating += value; remove => inner.Reactivating -= value; }
    public void Activate(Guid id) => inner.Activate(id);
    public event EventHandler<PersonStoreChangedEventArgs>? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
    public string DataFolder => inner.DataFolder;
    public PersonSettings? TryGet(Guid id) => inner.TryGet(id);
    public PersonSettings GetOrCreate(Guid id, Func<PersonSettings> create) => inner.GetOrCreate(id, create);
    public IReadOnlyList<Guid> UserIds() => inner.UserIds();
    public bool Delete(Guid id) => inner.Delete(id);
    public string? Conflict(Guid id, bool writing) => inner.Conflict(id, writing);
    public bool Update(Guid id, Func<PersonSettings?, PersonSettings?> change)
    {
        var pending = BeforeUpdate; BeforeUpdate = null; pending?.Invoke();
        return inner.Update(id, change);
    }
}

[Collection("Preservation safety globals")]
public sealed class ConcurrentRecoverySafetyTests
{
    [Fact]
    public async Task PartialSaveStillRejectsRecreatedAccountEpochWith409()
    {
        using var host = new LocalStorageTestHost();
        var inner = host.Store; var user = ApiSafetyHost.Person();
        inner.GetOrCreate(user.Id, () => new PersonSettings { Everyday = new SettingsLayer { Reads = new[] { "en" } } });
        var store = new ConcurrentUpdateHookStore(inner);
        byte[]? fresh = null;
        store.BeforeUpdate = () =>
        {
            inner.Delete(user.Id); inner.Activate(user.Id);
            inner.GetOrCreate(user.Id, () => new PersonSettings { Everyday = new SettingsLayer { Reads = new[] { "ja" } } });
            fresh = File.ReadAllBytes(inner.FilePath(user.Id));
        };
        var me = ApiSafetyHost.Me(store, user);
        var response = await StorageApiSafetyTests.InvokeApi(me, async () => (await me.PutSettings(new PersonSettings { Everyday = new SettingsLayer { Reads = null } }, true, "everyday.reads")).Result);
        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(response).StatusCode);
        Assert.Equal(fresh, File.ReadAllBytes(inner.FilePath(user.Id)));
    }

    [Fact]
    public async Task ExplicitEmptyListIsAppliedAndHiddenMalformedBodyIsIgnored()
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings { UserId = user.Id, Everyday = new SettingsLayer { Reads = new[] { "en" }, AvoidCommentary = false } });
        var body = new PersonSettings { Everyday = new SettingsLayer { Reads = Array.Empty<string>(), Understood = new[] { "not-a-language" } } };
        var result = await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, body, true, "everyday.reads");
        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(store.TryGet(user.Id)!.Everyday.Reads!);
        Assert.False(store.TryGet(user.Id)!.Everyday.AvoidCommentary);
        Assert.Null(store.TryGet(user.Id)!.Everyday.Understood);
    }

    [Fact]
    public async Task ConcurrentAdminFieldSavesKeepBothEditsAndHiddenValues()
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, ServedWindowMinutes = 45, Everyday = new SettingsLayer { AvoidCommentary = false } });
        var admin = ApiSafetyHost.Admin(host.Store);
        await Task.WhenAll(
            Task.Run(() => Assert.IsType<OkObjectResult>(admin.PutSettings(new PluginConfiguration { Everyday = new SettingsLayer { Reads = new[] { "fr" } } }, "everyday.reads").Result)),
            Task.Run(() => Assert.IsType<OkObjectResult>(admin.PutSettings(new PluginConfiguration { DryRun = true }, "dryRun").Result)));
        Assert.True(plugin.Configuration.DryRun);
        Assert.Equal(new[] { "fr" }, plugin.Configuration.Everyday.Reads);
        Assert.False(plugin.Configuration.Everyday.AvoidCommentary);
        Assert.Equal(45, plugin.Configuration.ServedWindowMinutes);
    }

    [Fact]
    public void ReadOnlyReportDoesNotRequireAWritableRecoveryDirectory()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings { Overrides = new() { new() { ItemId = Guid.NewGuid().ToString("N") } } });
        var original = File.ReadAllBytes(store.FilePath(user.Id));
        var blocked = Path.Combine(host.Folder, "pruned-overrides"); File.WriteAllText(blocked, "Blocks backup directory creation");
        var report = OverridePruner.Prune(store, DeletedItemReportTests.ReadyLibrary(Guid.NewGuid()), user.Id, null);
        Assert.True(report.Ready); Assert.True(report.ReportOnly);
        Assert.Equal(1, report.MissingOverrides); Assert.Equal(0, report.RemovedOverrides); Assert.Null(report.BackupFile);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(user.Id)));
        Assert.Single(store.TryGet(user.Id)!.Overrides);
        Assert.Equal("Blocks backup directory creation", File.ReadAllText(blocked));
    }

    [Fact]
    public void ReportPreservesConcurrentOverrideEditsAndRestoredItems()
    {
        using var host = new LocalStorageTestHost(); var inner = host.Store; var user = ApiSafetyHost.Person();
        var changed = Guid.NewGuid(); var restored = Guid.NewGuid();
        inner.GetOrCreate(user.Id, () => new PersonSettings { Overrides = new() { new() { ItemId = changed.ToString("N") }, new() { ItemId = restored.ToString("N") } } });
        var library = CallProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == "GetVirtualFolders") return new List<MediaBrowser.Model.Entities.VirtualFolderInfo> { new() { ItemId = Guid.NewGuid().ToString("N") } };
            if (method.Name == "GetItemList")
            {
                if (((InternalItemsQuery)args[0]!).ItemIds.Length == 0) return new BaseItem[] { new Movie { Id = restored } };
                inner.Update(user.Id, current => { var copy = current!.Clone(); copy.FindOverride(changed)!.Settings.AvoidCommentary = false; return copy; });
                return Array.Empty<BaseItem>();
            }
            return method.Name == "GetItemById" && (Guid)args[0]! == restored ? new Movie { Id = restored } : null;
        }).Proxy;
        var report = OverridePruner.Prune(inner, library, user.Id, null);
        Assert.Equal(0, report.RemovedOverrides); Assert.Null(report.BackupFile);
        Assert.Equal(1, report.MissingOverrides); Assert.Equal(changed, Assert.Single(report.MissingItemIds));
        Assert.Equal(2, inner.TryGet(user.Id)!.Overrides.Count);
        Assert.False(inner.TryGet(user.Id)!.FindOverride(changed)!.Settings.AvoidCommentary);
        Assert.False(Directory.Exists(Path.Combine(host.Folder, "pruned-overrides")));
    }

    [Fact]
    public void ReportContinuationBoundsEveryRequestAndPreservesBothReferenceKinds()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var user = ApiSafetyHost.Person();
        var ids = Enumerable.Range(1, 502).Select(i => Guid.Parse(i.ToString("x8") + "-0000-0000-0000-000000000000")).ToArray();
        store.GetOrCreate(user.Id, () => new PersonSettings { Overrides = ids.Select(id => new ItemOverride { ItemId = id.ToString("N") }).ToList(), BurnedIn = ids.Select(id => new BurnedInFlag { ItemId = id.ToString("N") }).ToList() });
        var original = File.ReadAllBytes(store.FilePath(user.Id)); var batches = new List<Guid[]>();
        var library = DeletedItemReportTests.ReadyLibrary(Guid.NewGuid(), query => { batches.Add(query.ItemIds); return Array.Empty<BaseItem>(); });
        var first = OverridePruner.Prune(store, library, user.Id, null);
        Assert.Equal(500, first.MissingOverrides); Assert.Equal(500, first.MissingBurnedIn); Assert.NotNull(first.NextAfter);
        var last = OverridePruner.Prune(store, library, user.Id, first.NextAfter);
        Assert.Equal(2, last.MissingOverrides); Assert.Equal(2, last.MissingBurnedIn); Assert.Null(last.NextAfter);
        Assert.Equal(0, first.RemovedOverrides + first.RemovedBurnedIn + last.RemovedOverrides + last.RemovedBurnedIn);
        Assert.Equal(new[] { 500, 2 }, batches.Select(b => b.Length));
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(user.Id)));
        Assert.Equal(502, store.TryGet(user.Id)!.Overrides.Count); Assert.Equal(502, store.TryGet(user.Id)!.BurnedIn.Count);
        Assert.False(Directory.Exists(Path.Combine(host.Folder, "pruned-overrides")));
    }

    [Fact]
    public async Task ResetSkipsAccountChangedAfterItsBackupInsteadOfDestroyingUnbackedEdit()
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var inner = host.Store; var user = ApiSafetyHost.Person();
        inner.GetOrCreate(user.Id, () => new PersonSettings());
        var store = new ConcurrentUpdateHookStore(inner) { BeforeUpdate = () => inner.Update(user.Id, current =>
        {
            var copy = current!.Clone(); copy.Everyday.Reads = new[] { "fr" }; return copy;
        }) };
        var response = await ApiSafetyHost.Admin(store, null, user).ResetAll(true);
        var report = Assert.IsType<ResetAllReport>(Assert.IsAssignableFrom<ObjectResult>(response.Result).Value);
        Assert.Equal(0, report.People); Assert.Contains(user.Username, report.FailedNames);
        Assert.Equal(new[] { "fr" }, inner.TryGet(user.Id)!.Everyday.Reads);
        Assert.True(report.AdminReset);
        Assert.True(File.Exists(Path.Combine(plugin.BackupsFolder, report.BackupFile)));
    }

    [Fact]
    public async Task ResetKeepsServerSettingsChangedAfterTheirBackup()
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var inner = host.Store; var user = ApiSafetyHost.Person();
        inner.GetOrCreate(user.Id, () => new PersonSettings());
        var store = new ConcurrentUpdateHookStore(inner) { BeforeUpdate = () => plugin.UpdateConfiguration(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, DryRun = true }) };
        var response = await ApiSafetyHost.Admin(store, null, user).ResetAll(true);
        var report = Assert.IsType<ResetAllReport>(Assert.IsAssignableFrom<ObjectResult>(response.Result).Value);
        Assert.True(plugin.Configuration.DryRun);
        Assert.False(report.AdminReset);
        Assert.Contains("Babelfin server settings", report.FailedNames);
        Assert.NotEqual("All settings were reset.", report.Message);
    }

}
