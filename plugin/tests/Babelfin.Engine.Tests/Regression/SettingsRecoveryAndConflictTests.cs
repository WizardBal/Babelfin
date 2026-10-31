using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Activity;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using Jellyfin.Plugin.Babelfin.Web;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class SettingsRecoveryAndConflictTests
{
    private static string Expected(object value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static AdminController Admin(IPersonStore store, params Jellyfin.Database.Implementations.Entities.User[] people)
    {
        var controller = ApiSafetyHost.Admin(store, null, people);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
    private static string? Property(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value)?.ToString();
    private static ItemOverride Override(Guid id) => new() { ItemId = id.ToString("N"), Settings = new SettingsLayer { Reads = new[] { "fr" } } };

    [Fact]
    public async Task SameFieldSaveReturnsCurrentValueAndCanBeForced()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store; var person = ApiSafetyHost.Person();
        store.GetOrCreate(person.Id, () => new PersonSettings { Everyday = new SettingsLayer { SubtitleLanguages = new[] { "en" } } });
        var controller = Admin(store, person);
        controller.Request.Headers["X-Babelfin-Expected"] = Expected(new Dictionary<string, object?> { ["everyday.subtitleLanguages"] = new[] { "en" } });
        Assert.IsType<OkObjectResult>((await controller.PutUserSettings(person.Id, new PersonSettings { Everyday = new SettingsLayer { SubtitleLanguages = new[] { "de" } } }, true, "everyday.subtitleLanguages")).Result);
        var result = Assert.IsType<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, async () => (await controller.PutUserSettings(person.Id, new PersonSettings { Everyday = new SettingsLayer { SubtitleLanguages = new[] { "fr" } } }, true, "everyday.subtitleLanguages")).Result));
        Assert.Equal(409, result.StatusCode);
        var problem = Assert.IsType<ApiProblem>(result.Value);
        Assert.Equal("field-conflict", Property(problem, "Code"));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(problem));
        Assert.Equal("everyday.subtitleLanguages", Assert.Single(json.RootElement.GetProperty("ConflictingPaths").EnumerateArray()).GetString());
        Assert.Equal("de", json.RootElement.GetProperty("CurrentValues").GetProperty("everyday.subtitleLanguages")[0].GetString());
        Assert.StartsWith("admin:", Property(problem, "LastChangedBy"));
        controller.Request.Headers.Remove("X-Babelfin-Expected");
        Assert.IsType<OkObjectResult>((await controller.PutUserSettings(person.Id, new PersonSettings { Everyday = new SettingsLayer { SubtitleLanguages = new[] { "fr" } } }, true, "everyday.subtitleLanguages")).Result);
        Assert.Equal(new[] { "fr" }, store.TryGet(person.Id)!.Everyday.SubtitleLanguages);
    }

    [Fact]
    public async Task ExpectedValuesAreComparedInsideUpdateAndDifferentFieldsMerge()
    {
        var store = new ApiSafetyStore(); var person = ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings { UserId = person.Id, Enabled = true });
        var controller = Admin(store, person);
        controller.Request.Headers["X-Babelfin-Expected"] = Expected(new { enabled = true });
        store.BeforeUpdate = () => store.Inner.Update(person.Id, current => { var copy = current!.Clone(); copy.Enabled = false; return copy; });
        var conflict = Assert.IsType<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, async () => (await controller.PutUserSettings(person.Id, new PersonSettings { Enabled = true }, true, "enabled")).Result));
        Assert.Equal(409, conflict.StatusCode);
        controller.Request.Headers["X-Babelfin-Expected"] = Expected(new { handleNextEpisode = false });
        Assert.IsType<OkObjectResult>((await controller.PutUserSettings(person.Id, new PersonSettings { HandleNextEpisode = true }, true, "handleNextEpisode")).Result);
        Assert.False(store.TryGet(person.Id)!.Enabled);
        Assert.True(store.TryGet(person.Id)!.HandleNextEpisode);
    }

    [Theory]
    [InlineData("%%%")]
    [InlineData("W10")]
    [InlineData("eyJ1bmtub3duIjp0cnVlfQ")]
    public async Task InvalidExpectedHeaderIsRefusedWithoutSaving(string header)
    {
        var store = new ApiSafetyStore(); var person = ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings { UserId = person.Id });
        var controller = Admin(store, person); controller.Request.Headers["X-Babelfin-Expected"] = header;
        var result = Assert.IsAssignableFrom<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, async () => (await controller.PutUserSettings(person.Id, new PersonSettings { Enabled = false }, true, "enabled")).Result));
        Assert.Equal(400, result.StatusCode); Assert.True(store.TryGet(person.Id)!.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyBodyPreservesAbsentReferenceListsAndClearsExplicitEmptyLists(bool explicitLists)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var person = ApiSafetyHost.Person();
        store.GetOrCreate(person.Id, () => new PersonSettings { Overrides = new List<ItemOverride> { Override(Guid.NewGuid()) }, BurnedIn = new List<BurnedInFlag> { new() { ItemId = Guid.NewGuid().ToString("N"), Languages = new List<string> { "en" } } } });
        var json = explicitLists ? "{\"enabled\":false,\"overrides\":[],\"burnedIn\":[]}" : "{\"enabled\":false}";
        var body = JsonSerializer.Deserialize<PersonSettings>(json, BabelfinJson.Options)!;
        Assert.IsType<OkObjectResult>((await Admin(store, person).PutUserSettings(person.Id, body)).Result);
        Assert.Equal(explicitLists ? 0 : 1, store.TryGet(person.Id)!.Overrides.Count);
        Assert.Equal(explicitLists ? 0 : 1, store.TryGet(person.Id)!.BurnedIn.Count);
    }

    [Fact]
    public void EverySuccessfulStoreWriteRecordsChangeMetadata()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var person = ApiSafetyHost.Person();
        var before = DateTime.UtcNow;
        store.GetOrCreate(person.Id, () => new PersonSettings());
        store.Update(person.Id, current => { var copy = current!.Clone(); copy.Enabled = false; return copy; });
        var saved = store.TryGet(person.Id)!;
        Assert.Equal(person.Id.ToString("N"), Property(saved, "LastChangedBy"));
        Assert.True((DateTime)typeof(PersonSettings).GetProperty("LastChangedAt")!.GetValue(saved)! >= before);
    }

    [Fact]
    public void CopyPreservesHiddenOverridesAndCreatesRestorableBackup()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store;
        var source = ApiSafetyHost.Person("Source", Guid.NewGuid()); var target = ApiSafetyHost.Person("Target", Guid.NewGuid()); var hidden = Guid.NewGuid();
        store.GetOrCreate(source.Id, () => new PersonSettings());
        store.GetOrCreate(target.Id, () => new PersonSettings { Overrides = new List<ItemOverride> { Override(hidden) } });
        var library = CallProxy.Create<ILibraryManager>((method, args) => method.Name == "GetItemById" ? ((Guid)args[0]! == hidden ? null : new Movie { Id = (Guid)args[0]! }) : null).Proxy;
        var controller = new AdminController(library, ApiSafetyHost.Users(source, target), store, new FakeLibraryFacts(), CallProxy.Create<IProfileResolver>().Proxy);
        var result = Assert.IsType<CopySettingsResult>(Assert.IsType<OkObjectResult>(controller.CopyTo(source.Id, target.Id).Result).Value);
        Assert.Single(store.TryGet(target.Id)!.Overrides);
        var file = Property(result, "BackupFile"); Assert.False(string.IsNullOrWhiteSpace(file));
        var backup = JsonSerializer.Deserialize<ExportDocument>(File.ReadAllText(Path.Combine(store.DataFolder, "backups", file!)), BabelfinJson.Options)!;
        Assert.Single(Assert.Single(backup.Users).Overrides);
    }

    [Fact]
    public async Task EmptyStartupAccountListRetainsStoredPeopleAndActivity()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store;
        var people = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in people) store.GetOrCreate(id, () => new PersonSettings());
        var log = new ActivityLog();
        log.Add(new ActivityEntry(DateTime.UtcNow, ActivityKinds.SettingsChanged, people[0], null, "Settings changed"));
        var users = CallProxy.Create<IUserManager>((method, _) => method.Name == "GetUsers" ? Array.Empty<Jellyfin.Database.Implementations.Entities.User>() : null).Proxy;
        await new UserDataLifecycle(store, log, users, NullLogger<UserDataLifecycle>.Instance).StartAsync(CancellationToken.None);
        Assert.All(people, id => Assert.NotNull(store.TryGet(id))); Assert.Single(log.Query(10));
    }

    [Fact]
    public void InvalidStoredReferencesAreBackedUpBeforeRepair()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
        var json = "{\"schemaVersion\":3,\"libraries\":[{\"libraryId\":\"\",\"settings\":{}}],\"overrides\":[{\"itemId\":\"not-a-guid\",\"settings\":{}}]}";
        File.WriteAllText(store.FilePath(id), json);
        var original = File.ReadAllBytes(store.FilePath(id)); var loaded = store.TryGet(id)!;
        Assert.Empty(loaded.Libraries); Assert.Empty(loaded.Overrides);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.repaired-*.bak"));
        Assert.Equal(original, File.ReadAllBytes(backup));
        store.Update(id, current => current!.Clone());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.repaired-*.bak"));
    }

    [Fact]
    public void ImportPreviewSerializesStorageBlock()
    {
        var preview = new ImportPreview();
        typeof(ImportPreview).GetProperty("StorageBlocked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.SetValue(preview, true);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(preview));
        Assert.True(json.RootElement.TryGetProperty("StorageBlocked", out var blocked)); Assert.True(blocked.GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LibraryEndpointFailuresReturnRetryableServiceUnavailable(bool io)
    {
        using var host = new LocalStorageTestHost(); var person = ApiSafetyHost.Person();
        var library = CallProxy.Create<ILibraryManager>((method, _) => method.Name == "GetItemById" ? throw (io ? new IOException("Unavailable") : new InvalidOperationException("Unavailable")) : null).Proxy;
        var controller = new MeController(library, ApiSafetyHost.Users(person), CallProxy.Create<ICollectionManager>().Proxy, host.Store, CallProxy.Create<IProfileResolver>().Proxy, new FakeLibraryFacts(), new FakeContextBuilder()) { ControllerContext = ApiSafetyHost.Me(host.Store, person).ControllerContext };
        foreach (var run in new Func<IActionResult?>[] { () => controller.GetEffective(Ids.Item).Result, () => controller.GetOverrideTarget(Ids.Item).Result })
        {
            var result = Assert.IsAssignableFrom<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, () => Task.FromResult(run())));
            Assert.Equal(503, result.StatusCode); Assert.Equal("library-unavailable", Property(result.Value!, "Code"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkippedAccountReportReturnsNotReadyWithoutRequiringWriteAccess(bool undecodable)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var person = ApiSafetyHost.Person();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(person.Id))!);
        File.WriteAllText(store.FilePath(person.Id), undecodable ? "{\"schemaVersion\":999,\"everyday\":\"unknown\"}" : "{\"schemaVersion\":999}");
        var before = File.ReadAllBytes(store.FilePath(person.Id));
        var controller = new AdminController(ReadyLibrary(), ApiSafetyHost.Users(person), store, new FakeLibraryFacts(), CallProxy.Create<IProfileResolver>().Proxy)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = Assert.IsType<OkObjectResult>(controller.PruneUserOverrides(person.Id));
        Assert.Equal(200, result.StatusCode);
        var report = Assert.IsType<PruneOverridesReport>(result.Value);
        Assert.False(report.Ready);
        Assert.NotEmpty(report.SkippedReason!);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, BabelfinJson.Options));
        Assert.False(json.RootElement.GetProperty("ready").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("accountSkipped", out _));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath(person.Id)));
    }

    private static ILibraryManager ReadyLibrary() => CallProxy.Create<ILibraryManager>((method, _) => method.Name switch
    {
        "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { ItemId = Guid.NewGuid().ToString("N") } },
        "GetItemList" => new List<BaseItem> { new Movie { Id = Guid.NewGuid() } },
        _ => null,
    }).Proxy;

    [Fact]
    public async Task HtmlResponsesRestrictFramingToSameOrigin()
    {
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        await ResponseWriter.WriteAsync(context, new Payload(Encoding.UTF8.GetBytes("<html></html>"), "text/html; charset=utf-8", true), "html", "no-cache");
        Assert.Contains("frame-ancestors 'self'", context.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerFieldConflictChecksExpectedValueUnderConfigurationLock()
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, ServedWindowMinutes = 90 });
        var controller = Admin(host.Store);
        controller.Request.Headers["X-Babelfin-Expected"] = Expected(new { servedWindowMinutes = 90 });
        Assert.IsType<OkObjectResult>(controller.PutSettings(new PluginConfiguration { ServedWindowMinutes = 45 }, "servedWindowMinutes").Result);
        var response = Assert.IsAssignableFrom<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, () => Task.FromResult<IActionResult?>(controller.PutSettings(new PluginConfiguration { ServedWindowMinutes = 30 }, "servedWindowMinutes").Result)));
        Assert.Equal(409, response.StatusCode); Assert.Equal(45, plugin.Configuration.ServedWindowMinutes);
    }

    [Fact]
    public async Task ExpectedAtomicRowsAcceptOmittedDefaultMembers()
    {
        using var host = new LocalStorageTestHost(); var person = ApiSafetyHost.Person(); var store = host.Store;
        var rule = new SmartRule { Name = "Genre", Conditions = new List<RuleCondition> { new() { Kind = ConditionKind.Genre, Values = new List<string> { "Drama" } } } };
        rule.EnsureId();
        var stored = store.GetOrCreate(person.Id, () => new PersonSettings { SmartRules = new List<SmartRule> { rule } });
        var loaded = JsonSerializer.SerializeToNode(stored, BabelfinJson.Options)!;
        var controller = Admin(store, person);
        controller.Request.Headers["X-Babelfin-Expected"] = Expected(new Dictionary<string, object?> { ["smartRules"] = loaded["smartRules"] });
        Assert.IsType<OkObjectResult>((await controller.PutUserSettings(person.Id, stored.Clone(), true, "smartRules")).Result);
    }

    [Fact]
    public async Task StableLibraryLeafConflictReturnsThatLeaf()
    {
        using var host = new LocalStorageTestHost(); var person = ApiSafetyHost.Person(); var store = host.Store; var libraryId = Guid.NewGuid();
        store.GetOrCreate(person.Id, () => new PersonSettings { Libraries = new List<LibraryLayer> { new() { LibraryId = libraryId.ToString("N"), Settings = new SettingsLayer { Reads = new[] { "en" } } } } });
        var controller = Admin(store, person); var path = "libraries." + libraryId.ToString("N") + ".settings.reads";
        controller.Request.Headers["X-Babelfin-Expected"] = Expected(new Dictionary<string, object?> { [path] = new[] { "fr" } });
        var response = Assert.IsAssignableFrom<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, async () => (await controller.PutUserSettings(person.Id, new PersonSettings(), true, path)).Result));
        Assert.Equal(409, response.StatusCode);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public async Task FieldSaveEnforcesStoredBurnedInLimit(int count)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var person = ApiSafetyHost.Person();
        store.GetOrCreate(person.Id, () => new PersonSettings { BurnedIn = Enumerable.Range(0, count).Select(_ => new BurnedInFlag { ItemId = Guid.NewGuid().ToString("N"), Languages = new List<string> { "en" } }).ToList() });
        var controller = Admin(store, person);
        var response = Assert.IsAssignableFrom<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, async () => (await controller.PutUserSettings(person.Id, new PersonSettings { Enabled = false }, true, "enabled")).Result));
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(count, store.TryGet(person.Id)!.BurnedIn.Count);
        if (count == 200) Assert.IsType<BadRequestObjectResult>(ApiSafetyHost.Me(store, person).PostBurnedIn(Guid.NewGuid(), new BurnedInRequest { Languages = new List<string> { "en" } }, null).Result);
    }

    [Fact]
    public async Task MissingItemTaskRetainsFailuresWhenReadinessChanges()
    {
        var store = new ApiSafetyStore();
        var first = ApiSafetyHost.Person("First", Guid.Parse("00000000000000000000000000000001")); var second = ApiSafetyHost.Person("Second", Guid.Parse("00000000000000000000000000000002"));
        store.Inner.Save(new PersonSettings { UserId = first.Id }); store.Inner.Save(new PersonSettings { UserId = second.Id });
        var ready = true;
        var library = CallProxy.Create<ILibraryManager>((method, _) => method.Name switch
        {
            "GetVirtualFolders" => ready ? new List<VirtualFolderInfo> { new() { ItemId = Guid.NewGuid().ToString("N") } } : new List<VirtualFolderInfo>(),
            "GetItemList" => new List<BaseItem> { new Movie { Id = Guid.NewGuid() } }, _ => null,
        }).Proxy;
        var users = CallProxy.Create<IUserManager>((method, args) =>
        {
            if (method.Name != "GetUserById") return null;
            if ((Guid)args[0]! == first.Id) { ready = false; throw new InvalidOperationException("Account query failed"); }
            return second;
        }).Proxy;
        var logger = new StorageTestLogger<OverridePruneTask>();
        var task = new OverridePruneTask(store, library, users, logger);
        var error = await Assert.ThrowsAsync<IOException>(() => task.ExecuteAsync(new Progress<double>(), CancellationToken.None));
        Assert.Contains(first.Id.ToString("N"), error.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("stopped after 1 of 2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingItemTaskContinuesAfterSkippedAccount(bool undecodable)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store;
        var first = ApiSafetyHost.Person("First", Guid.Parse("00000000000000000000000000000001"));
        var second = ApiSafetyHost.Person("Second", Guid.Parse("00000000000000000000000000000002"));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(first.Id))!);
        File.WriteAllText(store.FilePath(first.Id), undecodable ? "{\"schemaVersion\":999,\"everyday\":\"unknown\"}" : "{\"schemaVersion\":999}");
        store.GetOrCreate(second.Id, () => new PersonSettings { Overrides = new() { Override(Guid.NewGuid()) } });
        var before = File.ReadAllBytes(store.FilePath(first.Id));
        var logger = new StorageTestLogger<OverridePruneTask>();
        await new OverridePruneTask(store, ReadyLibrary(), ApiSafetyHost.Users(first, second), logger).ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("skipped First:", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Report only:", StringComparison.Ordinal) && entry.Message.Contains("Second", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("stopped after", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath(first.Id)));
        Assert.Single(store.TryGet(second.Id)!.Overrides);
    }

    [Fact]
    public async Task ResetReportExposesOnlyRelativeBackupLocation()
    {
        using var host = new LocalStorageTestHost();
        var controller = Admin(host.Store);
        var report = await controller.ResetPeopleAsync(Path.Combine(host.Folder, "backups", "reset.json"));
        Assert.Equal("backups/reset.json", report.BackupPath);
    }

    private static MeController Me(IPersonStore store, Jellyfin.Database.Implementations.Entities.User person)
    {
        var library = CallProxy.Create<ILibraryManager>((method, args) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo>(),
            "GetItemById" => new Movie { Id = (Guid)args[0]!, Name = "Movie" },
            _ => null,
        }).Proxy;
        var resolver = CallProxy.Create<IProfileResolver>((method, _) => method.Name == "Resolve"
            ? Jellyfin.Plugin.Babelfin.Resolution.ProfileResolver.Resolve(store.TryGet(person.Id)!, new PluginConfiguration(), ItemContext.None, Jellyfin.Plugin.Babelfin.Engine.Languages.LanguageTable.Default) : null).Proxy;
        return new MeController(library, ApiSafetyHost.Users(person), CallProxy.Create<ICollectionManager>().Proxy, store, resolver, new FakeLibraryFacts(), new FakeContextBuilder())
            { ControllerContext = ApiSafetyHost.Me(store, person).ControllerContext };
    }

    [Fact]
    public async Task PersonSaveRejectsStaleFieldAndRecordsSelfActor()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var person = ApiSafetyHost.Person();
        store.GetOrCreate(person.Id, () => new PersonSettings());
        var controller = Me(store, person);
        controller.Request.Headers["X-Babelfin-Expected"] = Expected(new { enabled = true });
        Assert.IsType<OkObjectResult>((await controller.PutSettings(new PersonSettings { Enabled = false }, true, "enabled")).Result);
        Assert.Equal(person.Id.ToString("N"), Property(store.TryGet(person.Id)!, "LastChangedBy"));
        var response = Assert.IsAssignableFrom<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, async () => (await controller.PutSettings(new PersonSettings { Enabled = true }, true, "enabled")).Result));
        Assert.Equal(409, response.StatusCode); Assert.False(store.TryGet(person.Id)!.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyPersonSavePreservesEachAbsentListIndependently(bool clearOverrides)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var person = ApiSafetyHost.Person();
        store.GetOrCreate(person.Id, () => new PersonSettings { Overrides = new List<ItemOverride> { Override(Guid.NewGuid()) }, BurnedIn = new List<BurnedInFlag> { new() { ItemId = Guid.NewGuid().ToString("N"), Languages = new List<string> { "en" } } } });
        var body = JsonSerializer.Deserialize<PersonSettings>(clearOverrides ? "{\"overrides\":[]}" : "{\"burnedIn\":[]}", BabelfinJson.Options)!;
        Assert.IsType<OkObjectResult>((await Me(store, person).PutSettings(body)).Result);
        Assert.Equal(clearOverrides ? 0 : 1, store.TryGet(person.Id)!.Overrides.Count);
        Assert.Equal(clearOverrides ? 1 : 0, store.TryGet(person.Id)!.BurnedIn.Count);
    }

    [Fact]
    public void FailedCopyBackupLeavesTargetByteIdentical()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store;
        var source = ApiSafetyHost.Person("Source", Guid.NewGuid()); var target = ApiSafetyHost.Person("Target", Guid.NewGuid());
        store.GetOrCreate(source.Id, () => new PersonSettings { Everyday = new SettingsLayer { Reads = new[] { "fr" } } });
        store.GetOrCreate(target.Id, () => new PersonSettings());
        var before = File.ReadAllBytes(store.FilePath(target.Id));
        File.WriteAllText(Path.Combine(store.DataFolder, "backups"), "Backup directory is unavailable");
        Assert.ThrowsAny<IOException>(() => Admin(store, source, target).CopyTo(source.Id, target.Id));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath(target.Id)));
    }

    [Fact]
    public void ReadingRepairedReferencesAfterRestartDoesNotDuplicateRecoveryCopy()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
        File.WriteAllText(store.FilePath(id), "{\"schemaVersion\":3,\"libraries\":[{\"libraryId\":\"invalid\"}]}");
        var original = File.ReadAllBytes(store.FilePath(id));
        Assert.Empty(store.TryGet(id)!.Libraries);
        Assert.Empty(host.Store.TryGet(id)!.Libraries);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.repaired-*.bak"));
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportPersonWritesRunOutsideTheConfigurationLock(bool adminSection)
    {
        using var host = new LocalStorageTestHost(); var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var store = new ApiSafetyStore(); var person = ApiSafetyHost.Person(); store.Inner.Save(new PersonSettings { UserId = person.Id });
        store.BeforeUpdate = () => Assert.False(MonitorIsEntered(plugin));
        var users = CallProxy.Create<IUserManager>((method, _) => method.Name switch { "GetUsers" => new[] { person }, "GetUserById" => person, _ => null }).Proxy;
        var controller = new AdminController(ApiSafetyHost.Library(), users, store, new FakeLibraryFacts(), CallProxy.Create<IProfileResolver>().Proxy);
        var result = Assert.IsType<ImportPreview>(Assert.IsType<OkObjectResult>((await controller.Import(new ExportDocument
            { Admin = adminSection ? new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, Enabled = false } : null, Users = new() { new() { UserId = person.Id, Enabled = false } } }, false)).Result).Value);
        Assert.True(result.Applied); Assert.False(store.TryGet(person.Id)!.Enabled);
    }

    private static bool MonitorIsEntered(object value) => System.Threading.Monitor.IsEntered(value);

    [Theory]
    [InlineData("app.html")]
    public async Task HtmlAssetRoutesRestrictFramingAndPreserveExistingPolicy(string asset)
    {
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'";
        var controller = new AssetsController { ControllerContext = new ControllerContext { HttpContext = context } };
        await controller.Get(asset, null);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Contains("frame-ancestors 'self'", context.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Contains("default-src 'self'", context.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConditionalHtmlResponseRetainsFramingProtection()
    {
        var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Request.Headers.IfNoneMatch = "\"html\"";
        await ResponseWriter.WriteAsync(context, new Payload(Encoding.UTF8.GetBytes("<html></html>"), "text/html", true), "html", "no-cache");
        Assert.Equal(304, context.Response.StatusCode);
        Assert.Contains("frame-ancestors 'self'", context.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmittedLegacyListsPreserveStoredAuditAndUnusableFlagIdsExactly()
    {
        var store = new ApiSafetyStore(); var person = ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings { UserId = person.Id, BurnedIn = new List<BurnedInFlag> { new() { ItemId = "unusable-id", SetBy = "admin", Languages = new List<string> { "en" } } } });
        var before = JsonSerializer.Serialize(store.TryGet(person.Id)!.BurnedIn, BabelfinJson.Options);
        var controller = Admin(store, person);
        var result = Assert.IsAssignableFrom<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, async () => (await controller.PutUserSettings(person.Id, new PersonSettings { Enabled = false })).Result));
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(before, JsonSerializer.Serialize(store.TryGet(person.Id)!.BurnedIn, BabelfinJson.Options));
    }

    [Fact]
    public async Task EquivalentLibraryIdFormatsAreAcceptedInExpectedPaths()
    {
        using var host = new LocalStorageTestHost(); var person = ApiSafetyHost.Person(); var id = Guid.NewGuid(); var store = host.Store;
        store.GetOrCreate(person.Id, () => new PersonSettings { Libraries = new() { new() { LibraryId = id.ToString("N"), Settings = new SettingsLayer { Reads = new[] { "en" } } } } });
        var path = "libraries." + id.ToString("D").ToUpperInvariant() + ".settings.reads";
        var controller = Admin(store, person); controller.Request.Headers["X-Babelfin-Expected"] = Expected(new Dictionary<string, object?> { [path] = new[] { "en" } });
        var result = Assert.IsAssignableFrom<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, async () => (await controller.PutUserSettings(person.Id, new PersonSettings { Libraries = new() { new() { LibraryId = id.ToString("N"), Settings = new SettingsLayer { Reads = new[] { "fr" } } } } }, true, path)).Result));
        Assert.Equal(200, result.StatusCode); Assert.Equal(new[] { "fr" }, store.TryGet(person.Id)!.FindLibrary(id)!.Settings.Reads);
    }

    [Fact]
    public async Task EffectiveProfileStorageFailuresKeepStorageConflictClassification()
    {
        var store = new ApiSafetyStore(); var person = ApiSafetyHost.Person(); store.Inner.Save(new PersonSettings { UserId = person.Id });
        var resolver = CallProxy.Create<IProfileResolver>((method, _) => method.Name == "Resolve" ? throw new IOException("Settings storage unavailable") : null).Proxy;
        var controller = new MeController(ApiSafetyHost.Library(), ApiSafetyHost.Users(person), CallProxy.Create<ICollectionManager>().Proxy, store, resolver, new FakeLibraryFacts(), new FakeContextBuilder()) { ControllerContext = ApiSafetyHost.Me(store, person).ControllerContext };
        var result = Assert.IsAssignableFrom<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, () => Task.FromResult<IActionResult?>(controller.GetEffective(Ids.Item).Result)));
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("storage-unavailable", Property(result.Value!, "Code"));
    }

    [Fact]
    public void FailedMigrationWritePreservesLoadedChangeMetadata()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = new LocalStorageTestHost(); var store = host.Store; var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
        var at = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(store.FilePath(id), "{\"schemaVersion\":1,\"lastChangedAt\":\"2020-01-01T00:00:00Z\",\"lastChangedBy\":\"prior-write\"}");
        var before = File.ReadAllBytes(store.FilePath(id));
        using var held = new FileStream(store.FilePath(id), FileMode.Open, FileAccess.Read, FileShare.Read);
        var loaded = store.TryGet(id)!;
        Assert.Equal("prior-write", Property(loaded, "LastChangedBy"));
        Assert.Equal(at, (DateTime)typeof(PersonSettings).GetProperty("LastChangedAt")!.GetValue(loaded)!);
        Assert.Equal(before, File.ReadAllBytes(store.FilePath(id)));
    }

    [Fact]
    public void NewerSchemaReadPreservesMetadataAndOriginalBytes()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
        File.WriteAllText(store.FilePath(id), "{\"schemaVersion\":999,\"lastChangedAt\":\"2020-01-01T00:00:00Z\",\"lastChangedBy\":\"prior-write\"}");
        var before = File.ReadAllBytes(store.FilePath(id));
        Assert.Equal("prior-write", Property(store.TryGet(id)!, "LastChangedBy"));
        Assert.NotNull(store.Conflict(id, true));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath(id)));
    }

    [Fact]
    public async Task AdminAuditUsesAuthenticatedActorAndIgnoresBodyMetadata()
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var person = ApiSafetyHost.Person(); var admin = Guid.NewGuid();
        store.GetOrCreate(person.Id, () => new PersonSettings());
        var controller = Admin(store, person);
        controller.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
            { new System.Security.Claims.Claim(ClaimsHelper.UserIdClaim, admin.ToString()), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "Administrator") }, "Test"));
        var body = JsonSerializer.Deserialize<PersonSettings>("{\"enabled\":false,\"lastChangedBy\":\"spoofed\",\"lastChangedAt\":\"2099-01-01T00:00:00Z\"}", BabelfinJson.Options)!;
        Assert.IsType<OkObjectResult>((await controller.PutUserSettings(person.Id, body)).Result);
        Assert.Equal("admin:" + admin.ToString("N"), Property(store.TryGet(person.Id)!, "LastChangedBy"));
        Assert.Equal("Administrator", Property(store.TryGet(person.Id)!, "LastChangedByName"));
        Assert.True((DateTime)typeof(PersonSettings).GetProperty("LastChangedAt")!.GetValue(store.TryGet(person.Id))! < DateTime.UtcNow.AddMinutes(1));
    }
    [Theory]
    [InlineData("That person can't see this item.", "item-unavailable")]
    [InlineData("That person doesn't exist.", "person-unavailable")]
    [InlineData("The file contains no importable settings.", "empty-import")]
    [InlineData("The library is temporarily unavailable. Saved-choice status could not be checked; retry later.", "library-unavailable")]
    [InlineData("A simulation is already running.", "simulation-busy")]
    public void NonValidationProblemsHaveDistinctStableCodes(string message, string code)
    {
        Assert.Equal(code, Property(new ApiProblem(message, Array.Empty<ValidationError>()), "Code"));
    }

    [Fact]
    public void UnclassifiedProblemsKeepEnglishFallbackInsteadOfClaimingInvalidSettings()
    {
        var problem = new ApiProblem("Specific guidance from an additional endpoint.", Array.Empty<ValidationError>());
        Assert.Equal("request-failed", Property(problem, "Code"));
        Assert.Equal("Specific guidance from an additional endpoint.", problem.Message);
    }

    [Fact]
    public void RealValidationProblemsHaveValidationCode()
    {
        Assert.Equal("invalid-settings", Property(new ApiProblem("The settings are not valid.", new[] { new ValidationError("enabled", "Invalid value") }), "Code"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("/jellyfin", false)]
    [InlineData("", true)]
    [InlineData("/jellyfin", true)]
    public async Task EmbeddedConfigurationPageRestrictsFramingEvenWithoutInjection(string baseUrl, bool enabled)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["hostwebclient"] = enabled ? "true" : "false" }).Build();
        var middleware = new BabelfinMiddleware(context =>
        {
            context.Response.ContentType = "text/html";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'";
            return Task.CompletedTask;
        }, NullLogger<BabelfinMiddleware>.Instance, new HostSettings { Resolved = true, BaseUrl = baseUrl }, configuration);
        var context = new DefaultHttpContext(); context.Request.Path = baseUrl + "/web/ConfigurationPage"; context.Request.QueryString = new QueryString("?name=Babelfin");
        await middleware.InvokeAsync(context);
        Assert.Contains("frame-ancestors 'self'", context.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Contains("default-src 'self'", context.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task CopyRejectsTargetChangedAfterVisibilityCheckAndKeepsNewHiddenOverride()
    {
        using var host = new LocalStorageTestHost(); var inner = host.Store;
        var source = ApiSafetyHost.Person("Source", Guid.NewGuid()); var target = ApiSafetyHost.Person("Target", Guid.NewGuid()); var hidden = Guid.NewGuid();
        inner.GetOrCreate(source.Id, () => new PersonSettings { Everyday = new SettingsLayer { Reads = new[] { "fr" } } });
        inner.GetOrCreate(target.Id, () => new PersonSettings());
        var sourceBefore = File.ReadAllBytes(inner.FilePath(source.Id)); byte[]? changedTarget = null;
        var store = new ConcurrentUpdateHookStore(inner) { BeforeUpdate = () =>
        {
            inner.Update(target.Id, current => { var copy = current!.Clone(); copy.Overrides.Add(Override(hidden)); return copy; });
            changedTarget = File.ReadAllBytes(inner.FilePath(target.Id));
        } };
        var library = CallProxy.Create<ILibraryManager>((method, args) => method.Name == "GetItemById" ? ((Guid)args[0]! == hidden ? null : new Movie { Id = (Guid)args[0]! }) : null).Proxy;
        var controller = new AdminController(library, ApiSafetyHost.Users(source, target), store, new FakeLibraryFacts(), CallProxy.Create<IProfileResolver>().Proxy);
        var response = Assert.IsAssignableFrom<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, () => Task.FromResult<IActionResult?>(controller.CopyTo(source.Id, target.Id).Result)));
        Assert.Equal(409, response.StatusCode);
        Assert.NotNull(inner.TryGet(target.Id)!.FindOverride(hidden));
        Assert.Equal(changedTarget, File.ReadAllBytes(inner.FilePath(target.Id)));
        Assert.Equal(sourceBefore, File.ReadAllBytes(inner.FilePath(source.Id)));
    }

    [Fact]
    public async Task ExistingFramingAllowlistCannotBroadenSameOriginProtection()
    {
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; frame-ancestors 'self' https://example.test";
        await ResponseWriter.WriteAsync(context, new Payload(Encoding.UTF8.GetBytes("<html></html>"), "text/html", true), "html", "no-cache");
        Assert.Contains(context.Response.Headers.ContentSecurityPolicy, policy => policy == "frame-ancestors 'self'");
    }
}