using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Activity;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class BackupInteroperabilityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TemplateLanguageLeafPreservesConcurrentHiddenStateAndNullOrEmpty(bool nullTemplate, bool empty)
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, NewUserTemplate = new PersonSettings
        {
            Everyday = new SettingsLayer { Understood = new[] { "en" }, AvoidCommentary = false, Format = Jellyfin.Plugin.Babelfin.Engine.Selection.FormatPreference.TextOnly },
            HandleNextEpisode = true, Locks = new SettingsLayer { PrefersSdh = true },
        } });
        var body = new PluginConfiguration { NewUserTemplate = nullTemplate ? null : new PersonSettings { Everyday = new SettingsLayer { Understood = empty ? Array.Empty<string>() : new[] { "de" } } } };
        var response = ApiSafetyHost.Admin(host.Store).PutSettings(body, "newUserTemplate.everyday.understood");
        Assert.IsType<OkObjectResult>(response.Result);
        var result = plugin.Configuration.NewUserTemplate!;
        Assert.NotNull(result);
        Assert.False(result.Everyday.AvoidCommentary);
        Assert.Equal(Jellyfin.Plugin.Babelfin.Engine.Selection.FormatPreference.TextOnly, result.Everyday.Format);
        Assert.True(result.HandleNextEpisode); Assert.True(result.Locks.PrefersSdh);
        if (nullTemplate) Assert.Null(result.Everyday.Understood);
        else if (empty) Assert.Empty(result.Everyday.Understood!);
        else Assert.Equal(new[] { "de" }, result.Everyday.Understood);
    }

    [Fact]
    public void TemplateNullStaysNullAndOnlyTrulyBlankTemplateCollapses()
    {
        using var host = new LocalStorageTestHost(); var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var admin = ApiSafetyHost.Admin(host.Store);
        Assert.IsType<OkObjectResult>(admin.PutSettings(new PluginConfiguration(), "newUserTemplate.everyday.understood").Result);
        Assert.Null(plugin.Configuration.NewUserTemplate);
        Assert.IsType<OkObjectResult>(admin.PutSettings(new PluginConfiguration { NewUserTemplate = new PersonSettings { Everyday = new SettingsLayer { Understood = Array.Empty<string>() } } }, "newUserTemplate.everyday.understood").Result);
        Assert.Empty(plugin.Configuration.NewUserTemplate!.Everyday.Understood!);
        Assert.IsType<OkObjectResult>(admin.PutSettings(new PluginConfiguration(), "newUserTemplate.everyday.understood").Result);
        Assert.Null(plugin.Configuration.NewUserTemplate);
    }

    [Fact]
    public async Task QuarantineOmissionSurvivesRepeatedExportsRestartAndStrictResetUntilRepaired()
    {
        using var host = new LocalStorageTestHost(); var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, DryRun = true });
        var store = host.Store; var bad = ApiSafetyHost.Person("Malformed", Guid.NewGuid()); var good = ApiSafetyHost.Person("Healthy", Guid.NewGuid());
        store.GetOrCreate(good.Id, () => new PersonSettings { Enabled = false });
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(bad.Id))!); const string broken = "{invalid";
        File.WriteAllText(store.FilePath(bad.Id), broken, new UTF8Encoding(false));
        var goodBefore = File.ReadAllBytes(store.FilePath(good.Id));
        for (var i = 0; i < 3; i++)
        {
            var admin = ApiSafetyHost.Admin(host.Store, null, bad, good); // Restart the store each time: omission must be durable.
            var file = Assert.IsType<FileContentResult>(admin.Export(true));
            var export = JsonSerializer.Deserialize<ExportDocument>(file.FileContents, BabelfinJson.Options)!;
            Assert.False(export.Complete); Assert.Equal(bad.Id, Assert.Single(export.SkippedUsers).UserId);
            var strict = await StorageApiSafetyTests.InvokeApi(admin, async () => (await admin.ResetAll()).Result);
            Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(strict).StatusCode);
            Assert.Equal(goodBefore, File.ReadAllBytes(store.FilePath(good.Id))); Assert.True(plugin.Configuration.DryRun);
        }
        var partial = await ApiSafetyHost.Admin(host.Store, null, bad, good).ResetAll(true);
        var report = Assert.IsType<ResetAllReport>(Assert.IsAssignableFrom<ObjectResult>(partial.Result).Value);
        Assert.Equal(1, report.People); Assert.Contains(bad.Username, report.FailedNames);
        var backup = JsonSerializer.Deserialize<ExportDocument>(File.ReadAllBytes(Path.Combine(plugin.BackupsFolder, report.BackupFile)), BabelfinJson.Options)!;
        Assert.False(backup.Complete); Assert.Equal(bad.Id, Assert.Single(backup.SkippedUsers).UserId);
        var preserved = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(bad.Id))!, bad.Id.ToString("N") + ".json.corrupt-*"));
        Assert.Equal(broken, File.ReadAllText(preserved, Encoding.UTF8));
        File.WriteAllText(store.FilePath(bad.Id), JsonSerializer.Serialize(new PersonSettings { UserId = bad.Id, Everyday = new SettingsLayer { Reads = new[] { "fr" } } }, BabelfinJson.Options), new UTF8Encoding(false));
        var repaired = ApiSafetyHost.Admin(host.Store, null, bad, good);
        var finalExport = JsonSerializer.Deserialize<ExportDocument>(Assert.IsType<FileContentResult>(repaired.Export(true)).FileContents, BabelfinJson.Options)!;
        Assert.True(finalExport.Complete); Assert.Empty(finalExport.SkippedUsers); Assert.Equal(2, finalExport.Users.Count);
        var reset = await repaired.ResetAll();
        var resetReport = Assert.IsType<ResetAllReport>(Assert.IsType<OkObjectResult>(reset.Result).Value);
        Assert.Equal(2, resetReport.People); Assert.Empty(resetReport.FailedNames);
        Assert.Equal(broken, File.ReadAllText(preserved, Encoding.UTF8));
    }

    [Fact]
    public async Task ReadableFutureExportCannotClaimFullFidelityAndValidRepairClearsConflict()
    {
        using var host = new LocalStorageTestHost(); host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var store = host.Store; var user = ApiSafetyHost.Person();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(user.Id))!);
        const string raw = "{\"schemaVersion\":999,\"futureSetting\":{\"unrecognized\":true},\"everyday\":{\"reads\":[\"fr\"]}}";
        File.WriteAllText(store.FilePath(user.Id), raw, new UTF8Encoding(false));
        var admin = ApiSafetyHost.Admin(store, null, user);
        var strict = await StorageApiSafetyTests.InvokeApi(admin, () => Task.FromResult<IActionResult?>(admin.Export()));
        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(strict).StatusCode);
        var partial = JsonSerializer.Deserialize<ExportDocument>(Assert.IsType<FileContentResult>(admin.Export(true)).FileContents, BabelfinJson.Options)!;
        Assert.False(partial.Complete); Assert.Empty(partial.Users); Assert.Contains("Upgrade", Assert.Single(partial.SkippedUsers).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(raw, File.ReadAllText(store.FilePath(user.Id), Encoding.UTF8));
        File.WriteAllText(store.FilePath(user.Id), JsonSerializer.Serialize(new PersonSettings { UserId = user.Id }, BabelfinJson.Options), new UTF8Encoding(false));
        Assert.True(JsonSerializer.Deserialize<ExportDocument>(Assert.IsType<FileContentResult>(admin.Export(true)).FileContents, BabelfinJson.Options)!.Complete);
    }

    [Fact]
    public async Task EmptyImportIsNotReportedAsApplied()
    {
        using var host = new LocalStorageTestHost(); host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var result = await ApiSafetyHost.Admin(host.Store).Import(new ExportDocument(), false);
        Assert.IsType<BadRequestObjectResult>(result.Result);
        var noMatch = ImportPlanner.Run(new ExportDocument { Users = new List<PersonSettings> { new() { UserId = Guid.NewGuid() } } }, new PluginConfiguration(), Array.Empty<ImportTargetUser>(), host.Store, Jellyfin.Plugin.Babelfin.Engine.Languages.LanguageTable.Default, _ => throw new InvalidOperationException(), false);
        Assert.False(noMatch.Applied);
    }

    [Fact]
    public void NullSeriesLanguageMatchesNativeFolderBehaviorWithoutRepeatedLookups()
    {
        var parent = new MediaBrowser.Controller.Entities.Folder { Id = Guid.NewGuid(), OriginalLanguage = "fr" };
        var series = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), ParentId = parent.Id };
        var episode = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), SeriesId = series.Id };
        var calls = 0;
        EventHandler<MediaBrowser.Controller.Library.ItemChangeEventArgs>? updated = null;
        var library = CallProxy.Create<MediaBrowser.Controller.Library.ILibraryManager>((method, args) =>
        {
            if (method.Name == "add_ItemUpdated") updated += (EventHandler<MediaBrowser.Controller.Library.ItemChangeEventArgs>)args[0]!;
            if (method.Name == "remove_ItemUpdated") updated -= (EventHandler<MediaBrowser.Controller.Library.ItemChangeEventArgs>)args[0]!;
            if (method.Name == "GetItemById") { calls++; return (Guid)args[0]! == series.Id ? series : parent; }
            if (method.Name == "GetCollectionFolders") return new List<MediaBrowser.Controller.Entities.Folder>();
            return null;
        }).Proxy;
        var previous = MediaBrowser.Controller.Entities.BaseItem.LibraryManager;
        try
        {
            MediaBrowser.Controller.Entities.BaseItem.LibraryManager = library;
            using var builder = new Jellyfin.Plugin.Babelfin.Library.ItemContextBuilder(library, CallProxy.Create<MediaBrowser.Controller.Collections.ICollectionManager>().Proxy, new FakeLibraryFacts());
            var native = episode.GetInheritedOriginalLanguage();
            calls = 0;
            for (var i = 0; i < 20; i++) Assert.Equal(native, builder.Build(episode, ApiSafetyHost.Person()).OriginalLanguage);
            output.WriteLine($"Jellyfin native inherited language: {native ?? "<null>"}; 20 builder calls used {calls} library item lookups with null series language and a folder parent.");
            Assert.InRange(calls, 1, 3);
            parent = new MediaBrowser.Controller.Entities.Folder { Id = parent.Id, OriginalLanguage = "de" };
            updated?.Invoke(null, new MediaBrowser.Controller.Library.ItemChangeEventArgs { Item = parent });
            Assert.Equal(episode.GetInheritedOriginalLanguage(), builder.Build(episode, ApiSafetyHost.Person()).OriginalLanguage);
        }
        finally { MediaBrowser.Controller.Entities.BaseItem.LibraryManager = previous; }
    }

    [Fact]
    public async Task CancellationDuringReportPagesRetainsDurableQuarantineAndEveryReference()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store;
        var bad = ApiSafetyHost.Person("Malformed", Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var good = ApiSafetyHost.Person("Healthy", Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        store.GetOrCreate(good.Id, () => new PersonSettings { Overrides = Enumerable.Range(0, 502).Select(_ => new ItemOverride { ItemId = Guid.NewGuid().ToString("N") }).ToList() });
        const string raw = "{invalid"; File.WriteAllText(store.FilePath(bad.Id), raw, new UTF8Encoding(false));
        Assert.NotNull(store.Conflict(bad.Id, true));
        using var stop = new System.Threading.CancellationTokenSource();
        var original = File.ReadAllBytes(store.FilePath(good.Id));
        var anchor = Guid.NewGuid();
        var library = CallProxy.Create<MediaBrowser.Controller.Library.ILibraryManager>((method, args) =>
        {
            if (method.Name == "GetVirtualFolders") return new List<MediaBrowser.Model.Entities.VirtualFolderInfo> { new() { ItemId = Guid.NewGuid().ToString("N") } };
            if (method.Name == "GetItemList")
            {
                if (((MediaBrowser.Controller.Entities.InternalItemsQuery)args[0]!).ItemIds.Length == 0)
                    return new MediaBrowser.Controller.Entities.BaseItem[] { new MediaBrowser.Controller.Entities.Movies.Movie { Id = anchor } };
                stop.Cancel(); return Array.Empty<MediaBrowser.Controller.Entities.BaseItem>();
            }
            return null;
        }).Proxy;
        var task = new Jellyfin.Plugin.Babelfin.Library.OverridePruneTask(store, library, ApiSafetyHost.Users(bad, good), NullLogger<Jellyfin.Plugin.Babelfin.Library.OverridePruneTask>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.ExecuteAsync(new Progress<double>(), stop.Token));
        Assert.Equal(502, store.TryGet(good.Id)!.Overrides.Count);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(good.Id)));
        Assert.False(Directory.Exists(Path.Combine(host.Folder, "pruned-overrides")));
        Assert.Contains(bad.Id, host.Store.KnownUserIds());
        Assert.Equal(raw, File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(bad.Id))!, bad.Id.ToString("N") + ".json.corrupt-*")), Encoding.UTF8));
    }

    [Fact]
    public void ResolvedQuarantineIsDurableAndDoesNotReactivateOldArchivesAfterIntentionalRemoval()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
        File.WriteAllText(store.FilePath(id), "{invalid", new UTF8Encoding(false));
        Assert.NotNull(store.Conflict(id, true)); Assert.Contains(id, host.Store.KnownUserIds());
        File.WriteAllText(store.FilePath(id), JsonSerializer.Serialize(new PersonSettings { UserId = id }, BabelfinJson.Options), new UTF8Encoding(false));
        Assert.Null(store.Conflict(id, true));
        var source = store.FilePath(id); File.Move(source, source + ".explicit-fixture-removal");
        Assert.DoesNotContain(id, host.Store.KnownUserIds());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(source)!, id.ToString("N") + ".json.corrupt-*"));
        // A new corruption must not be mistaken for the older acknowledged archive.
        File.WriteAllText(source, "{another-invalid", new UTF8Encoding(false));
        Assert.NotNull(host.Store.Conflict(id, true)); Assert.Contains(id, host.Store.KnownUserIds());
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(source)!, id.ToString("N") + ".json.corrupt-*").Length);
    }

    [Fact]
    public void NewQuarantineWithOlderTimestampCannotBeHiddenByPriorAcknowledgement()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
        File.WriteAllText(store.FilePath(id), "{invalid", new UTF8Encoding(false)); Assert.NotNull(store.Conflict(id, true));
        File.WriteAllText(store.FilePath(id), JsonSerializer.Serialize(new PersonSettings { UserId = id }, BabelfinJson.Options), new UTF8Encoding(false));
        Assert.Null(store.Conflict(id, true)); File.Move(store.FilePath(id), store.FilePath(id) + ".valid-repair");
        File.WriteAllText(store.FilePath(id) + ".corrupt-000000000000000001", "{new-corruption-after-clock-change", new UTF8Encoding(false));
        Assert.Contains(id, host.Store.KnownUserIds());
        Assert.NotNull(host.Store.Conflict(id, true));
    }

    [Fact]
    public async Task OfflineDeletionAndSameIdRecreationIncludeQuarantineInventoryAndRejectOldEpoch()
    {
        using var host = new LocalStorageTestHost(); host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var store = host.Store; var user = ApiSafetyHost.Person();
        var observed = store.GetOrCreate(user.Id, () => new PersonSettings());
        const string original = "{invalid"; File.WriteAllText(store.FilePath(user.Id), original, new UTF8Encoding(false));
        Assert.NotNull(store.Conflict(user.Id, true)); Assert.Contains(user.Id, store.KnownUserIds());
        var absent = CallProxy.Create<MediaBrowser.Controller.Library.IUserManager>((method, _) => method.Name == "GetUsers" ? new[] { ApiSafetyHost.Person("Active", Guid.NewGuid()) } : null).Proxy;
        var log = new ActivityLog();
        await new Jellyfin.Plugin.Babelfin.Storage.UserDataLifecycle(store, log, absent, NullLogger<Jellyfin.Plugin.Babelfin.Storage.UserDataLifecycle>.Instance).StartAsync(System.Threading.CancellationToken.None);
        Assert.DoesNotContain(user.Id, store.KnownUserIds());
        Assert.True(File.Exists(store.FilePath(user.Id) + ".deleted"));
        await new Jellyfin.Plugin.Babelfin.Seeding.NewUserTemplateConsumer(store, null, NullLogger<Jellyfin.Plugin.Babelfin.Seeding.NewUserTemplateConsumer>.Instance, ApiSafetyHost.Users(user))
            .OnEvent(new Jellyfin.Data.Events.Users.UserCreatedEventArgs(user));
        var fresh = store.TryGet(user.Id)!; Assert.NotNull(fresh); Assert.NotEqual(observed.StorageEpoch, fresh.StorageEpoch);
        var currentBytes = File.ReadAllBytes(store.FilePath(user.Id));
        Assert.Throws<PersonStorageConflictException>(() => PersonStorageAccess.RequireSnapshot(observed, fresh));
        Assert.False(store.Update(user.Id, _ => observed.Clone()));
        Assert.Equal(currentBytes, File.ReadAllBytes(store.FilePath(user.Id)));
        Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(user.Id))!, user.Id.ToString("N") + ".json.corrupt-*")), Encoding.UTF8));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FutureSchemaReplacementTakesPriorityOverHistoricalQuarantine(bool undecodable)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var id = Guid.NewGuid(); var path = store.FilePath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var broken = Encoding.UTF8.GetBytes("{invalid"); File.WriteAllBytes(path, broken);
        Assert.NotNull(store.Conflict(id, writing: true));
        Assert.False(File.Exists(path));
        var archive = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*"));
        Assert.Equal(broken, File.ReadAllBytes(archive));

        var future = Encoding.UTF8.GetBytes(undecodable ? "{\"schemaVersion\":999,\"everyday\":\"future-layout\"}" : "{\"schemaVersion\":999,\"futureSetting\":true}");
        File.WriteAllBytes(path, future);
        Assert.Contains("Upgrade Babelfin", store.Conflict(id, writing: true)!, StringComparison.Ordinal);
        if (undecodable) Assert.Contains("Upgrade Babelfin", store.Conflict(id, writing: false)!, StringComparison.Ordinal);
        else Assert.Null(store.Conflict(id, writing: false));
        Assert.Equal(future, File.ReadAllBytes(path));
        Assert.Equal(broken, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*"))));

        var repaired = JsonSerializer.SerializeToUtf8Bytes(new PersonSettings { UserId = id }, BabelfinJson.Options);
        File.WriteAllBytes(path, repaired);
        Assert.Null(store.Conflict(id, writing: true));
        Assert.Equal(repaired, File.ReadAllBytes(path));
        Assert.Equal(broken, File.ReadAllBytes(archive));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InPlaceCorruptionRepairIsReadWithoutRestartAfterRenameLock(bool sameLength)
    {
        using var host = new LocalStorageTestHost();
        host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var store = host.Store; var user = ApiSafetyHost.Person(); var path = store.FilePath(user.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var repaired = JsonSerializer.SerializeToUtf8Bytes(new PersonSettings { UserId = user.Id, Everyday = new SettingsLayer { Reads = new[] { "fr" } } }, BabelfinJson.Options);
        var broken = Encoding.UTF8.GetBytes("{invalid".PadRight(sameLength ? repaired.Length : 8));
        File.WriteAllBytes(path, broken); var originalWrite = File.GetLastWriteTimeUtc(path);
        var admin = ApiSafetyHost.Admin(store, null, user);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var response = await StorageApiSafetyTests.InvokeApi(admin, () => Task.FromResult<IActionResult?>(admin.Export()));
            var conflict = Assert.IsAssignableFrom<ObjectResult>(response);
            Assert.Equal(409, conflict.StatusCode);
            var message = Assert.IsType<ApiProblem>(conflict.Value).Message;
            Assert.Contains("original file or a recovery archive", message, StringComparison.Ordinal);
            Assert.Contains("Restore a valid settings file", message, StringComparison.Ordinal);
            Assert.DoesNotContain("preserved in a recovery archive", message, StringComparison.Ordinal);
            AssertCorruptOriginalPreserved(path, broken);
        }
        File.WriteAllBytes(path, repaired);
        if (sameLength) File.SetLastWriteTimeUtc(path, originalWrite.AddSeconds(2));
        Assert.Null(store.Conflict(user.Id, writing: true));
        Assert.Equal(new[] { "fr" }, store.TryGet(user.Id)!.Everyday.Reads);
        var exported = JsonSerializer.Deserialize<ExportDocument>(Assert.IsType<FileContentResult>(admin.Export()).FileContents, BabelfinJson.Options)!;
        Assert.True(exported.Complete);
        Assert.Equal(new[] { "fr" }, Assert.Single(exported.Users).Everyday.Reads);
        Assert.Equal(repaired, File.ReadAllBytes(path));
        AssertCorruptOriginalPreserved(path, broken, sourceReplaced: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InPlaceCorruptionReplacementRetainsFutureSchemaGuard(bool undecodable)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var id = Guid.NewGuid(); var path = store.FilePath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var broken = Encoding.UTF8.GetBytes("{invalid"); File.WriteAllBytes(path, broken);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotNull(store.Conflict(id, true));
            AssertCorruptOriginalPreserved(path, broken);
        }
        var future = undecodable ? "{\"schemaVersion\":999,\"everyday\":\"future-layout\"}" : "{\"schemaVersion\":999,\"futureSetting\":true}";
        File.WriteAllText(path, future, new UTF8Encoding(false));
        Assert.Contains("Upgrade Babelfin", store.Conflict(id, true)!, StringComparison.Ordinal);
        Assert.Equal(future, File.ReadAllText(path, Encoding.UTF8));
        AssertCorruptOriginalPreserved(path, broken, sourceReplaced: true);
    }

    [Fact]
    public void InPlaceCorruptionReplacementRetainsUnreadableGuardThenRecovers()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var id = Guid.NewGuid(); var path = store.FilePath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var broken = Encoding.UTF8.GetBytes("{invalid"); File.WriteAllBytes(path, broken);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotNull(store.Conflict(id, true));
            AssertCorruptOriginalPreserved(path, broken);
        }
        var repaired = JsonSerializer.SerializeToUtf8Bytes(new PersonSettings { UserId = id, Everyday = new SettingsLayer { Reads = new[] { "fr" } } }, BabelfinJson.Options);
        File.WriteAllBytes(path, repaired);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Contains("temporarily unavailable", store.Conflict(id, true)!, StringComparison.Ordinal);
        Assert.Null(store.Conflict(id, true));
        Assert.Equal(new[] { "fr" }, store.TryGet(id)!.Everyday.Reads);
        Assert.Equal(repaired, File.ReadAllBytes(path));
        AssertCorruptOriginalPreserved(path, broken, sourceReplaced: true);
    }

    private static void AssertCorruptOriginalPreserved(string path, byte[] original, bool sourceReplaced = false)
    {
        var archives = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*");
        if (OperatingSystem.IsWindows())
        {
            // FileShare.Read omits delete sharing, so Windows must retain the source at its original path.
            Assert.Empty(archives);
            if (!sourceReplaced) Assert.Equal(original, File.ReadAllBytes(path));
        }
        else
        {
            // POSIX permits renaming an open file; quarantine must preserve its exact bytes instead.
            Assert.Equal(original, File.ReadAllBytes(Assert.Single(archives)));
            if (!sourceReplaced) Assert.False(File.Exists(path));
        }
    }

}
