using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class PartialBackupSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialExportAndResetNameSkippedAccountAndKeepItsOriginalBytes(bool locked)
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var store = host.Store;
        var bad = ApiSafetyHost.Person("Unavailable", Guid.NewGuid());
        var good = ApiSafetyHost.Person("Healthy", Guid.NewGuid());
        store.GetOrCreate(good.Id, () => new PersonSettings { UserId = good.Id, Enabled = false });
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(bad.Id))!);
        var raw = "{\"schemaVersion\":999,\"everyday\":{\"reads\":{\"future\":true}}}";
        File.WriteAllText(store.FilePath(bad.Id), raw);
        using var fileLock = locked ? new FileStream(store.FilePath(bad.Id), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var admin = ApiSafetyHost.Admin(store, null, bad, good);
        var exported = Assert.IsType<FileContentResult>(admin.Export(true));
        using var json = JsonDocument.Parse(exported.FileContents);
        Assert.False(json.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal("Unavailable", json.RootElement.GetProperty("skippedUsers")[0].GetProperty("userName").GetString());
        Assert.Single(json.RootElement.GetProperty("users").EnumerateArray());
        var method = typeof(AdminController).GetMethod("ResetAll")!;
        var reset = await (Task<ActionResult<ResetAllReport>>)method.Invoke(admin, method.GetParameters().Length == 0 ? null : new object?[] { true })!;
        var report = Assert.IsType<ResetAllReport>(Assert.IsAssignableFrom<ObjectResult>(reset.Result).Value);
        Assert.Equal(1, report.People);
        Assert.Contains("Unavailable", report.FailedNames);
        Assert.NotEqual("All settings were reset.", report.Message);
        Assert.True(store.TryGet(good.Id)!.Enabled);
        Assert.True(File.Exists(Path.Combine(plugin.BackupsFolder, report.BackupFile)));
        fileLock?.Dispose();
        Assert.Equal(raw, File.ReadAllText(store.FilePath(bad.Id)));
    }

    [Fact]
    public void ReportIsPerPersonBatchedAndKeepsHiddenExistingAndMissingReferences()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var user = ApiSafetyHost.Person(); var other = ApiSafetyHost.Person("Other", Guid.NewGuid());
        var hidden = Guid.NewGuid(); var missing = Guid.NewGuid();
        PersonSettings Doc(Guid id) => new() { UserId = id, Overrides = new List<ItemOverride>
        {
            new() { ItemId = hidden.ToString("N"), Name = "Hidden existent" },
            new() { ItemId = missing.ToString("N"), Name = "Deleted title", Manual = new ManualMemory { SubtitlesOff = true } },
        } };
        store.GetOrCreate(user.Id, () => Doc(user.Id)); store.GetOrCreate(other.Id, () => Doc(other.Id));
        var queries = 0;
        var legacyFolder = Path.Combine(store.DataFolder, "pruned-overrides"); Directory.CreateDirectory(legacyFolder);
        var oldBackup = Path.Combine(legacyFolder, "legacy-recovery.json");
        var original = JsonSerializer.SerializeToUtf8Bytes(new ExportDocument { Users = new() { store.TryGet(user.Id)!.Clone() } }, BabelfinJson.Options);
        File.WriteAllBytes(oldBackup, original);
        var library = CallProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == "GetVirtualFolders") return new List<MediaBrowser.Model.Entities.VirtualFolderInfo> { new() { ItemId = Guid.NewGuid().ToString("N") } };
            if (method.Name == "GetItemList")
            {
                var query = (InternalItemsQuery)args[0]!;
                if (query.ItemIds.Length == 0) return new BaseItem[] { new Movie { Id = hidden } };
                queries++;
                Assert.Null(query.User); // Hidden is not deleted. The existence probe must be authoritative, not person-filtered.
                Assert.InRange(query.ItemIds.Length, 1, 500);
                return new BaseItem[] { new Movie { Id = hidden } };
            }
            if (method.Name == "GetItemById") return (Guid)args[0]! == hidden ? new Movie { Id = hidden } : null;
            return null;
        }).Proxy;
        var admin = new AdminController(library, ApiSafetyHost.Users(user, other), store, new FakeLibraryFacts(), CallProxy.Create<IProfileResolver>().Proxy);
        var prune = typeof(AdminController).GetMethod("PruneUserOverrides");
        Assert.NotNull(prune);
        var result = Assert.IsType<OkObjectResult>(prune!.Invoke(admin, new object?[] { user.Id, null }));
        Assert.NotNull(result.Value);
        var report = Assert.IsType<PruneOverridesReport>(result.Value);
        Assert.True(report.ReportOnly); Assert.True(report.Ready);
        Assert.Equal(1, report.MissingOverrides); Assert.Equal(missing, Assert.Single(report.MissingItemIds));
        Assert.Equal(0, report.RemovedOverrides); Assert.Null(report.BackupFile);
        Assert.Equal(2, store.TryGet(user.Id)!.Overrides.Count);
        Assert.Contains(store.TryGet(user.Id)!.Overrides, entry => entry.ItemId == hidden.ToString("N"));
        Assert.Equal(original, File.ReadAllBytes(oldBackup));
        Assert.Equal(2, store.TryGet(other.Id)!.Overrides.Count);
        Assert.Equal(1, queries);
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(store.DataFolder, "pruned-overrides"), "*.json"));
        var recovery = JsonSerializer.Deserialize<ExportDocument>(File.ReadAllText(backup), BabelfinJson.Options)!;
        Assert.Equal(user.Id, Assert.Single(recovery.Users).UserId);
        Assert.Equal(2, recovery.Users[0].Overrides.Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(store.DataFolder, "pruned-overrides"), "*" + other.Id.ToString("N") + "*"));
    }
}
