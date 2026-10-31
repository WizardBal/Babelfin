using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Activity;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Babelfin.Seeding;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class StorageApiSafetyTests
{
    [Fact]
    public void OpenFileMigrationKeepsOneExactBackupAndCanBeSavedAfterwards()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
        File.WriteAllText(store.FilePath(id), "{\"schemaVersion\":1,\"userName\":\"Before\"}");
        var original = File.ReadAllBytes(store.FilePath(id));
        using (var held = new FileStream(store.FilePath(id), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(PersonSettings.CurrentSchemaVersion, Assert.IsType<PersonSettings>(store.TryGet(id)).SchemaVersion);
                var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.bak"));
                Assert.Equal(original, File.ReadAllBytes(backup));
            }

            if (OperatingSystem.IsWindows())
            {
                // Windows denies replacement without FileShare.Delete: the failed migration must leave its source intact.
                Assert.Equal(original, File.ReadAllBytes(store.FilePath(id)));
            }
            else
            {
                // Unix permits renaming over an open file: successful migration must persist the current schema.
                using var persisted = JsonDocument.Parse(File.ReadAllBytes(store.FilePath(id)));
                Assert.Equal(PersonSettings.CurrentSchemaVersion, persisted.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("Before", persisted.RootElement.GetProperty("userName").GetString());
            }
        }
        Assert.True(store.Update(id, current => { var copy = current!.Clone(); copy.UserName = "After"; return copy; }));
        Assert.Equal("After", host.Store.TryGet(id)!.UserName);
        var preserved = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.bak"));
        Assert.Equal(original, File.ReadAllBytes(preserved));
    }

    [Fact]
    public void UnsupportedFileIsReportedOnceUntilItChanges()
    {
        using var host = new LocalStorageTestHost();
        var logger = new StorageTestLogger<PersonStore>();
        var store = new PersonStore(host.Folder, logger);
        var id = Guid.NewGuid();
        WriteFuture(store, id, undecodable: true);
        var bytes = File.ReadAllBytes(store.FilePath(id));
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(store.TryGet(id));
            Assert.Throws<IOException>(() => store.GetOrCreate(id, () => throw new InvalidOperationException("Must not seed")));
            Assert.False(store.Update(id, _ => throw new InvalidOperationException("Must not mutate")));
        }
        Assert.Single(logger.Entries);
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath(id)));
        File.WriteAllText(store.FilePath(id), "{\"schemaVersion\":3,\"userName\":\"Repaired\"}");
        Assert.Equal("Repaired", store.GetOrCreate(id, () => throw new InvalidOperationException("Must read repaired file")).UserName);
    }

    [Fact]
    public void DeletionArchivesOnlyTheDeletedPersonsHistory()
    {
        using var host = new LocalStorageTestHost();
        var deleted = Guid.NewGuid();
        var active = Guid.NewGuid();
        var log = Log(host.Folder);
        log.Add(Entry(active, "Active secret"));
        log.Add(Entry(deleted, "Deleted history"));
        log.Add(new ActivityEntry(DateTime.UtcNow, ActivityKinds.SettingsChanged, null, null, "Server secret"));
        log.RemoveUser(deleted);
        Assert.Equal(2, log.Query(20).Count);
    }

    [Fact]
    public async Task AuthoritativeStartupRestoresSameIdButOrdinaryWritersCannotResurrectIt()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings { UserName = "Old settings", Enabled = false });
        var stale = store.TryGet(user.Id)!.Clone();
        store.Delete(user.Id);
        Assert.False(store.Update(user.Id, _ => stale));
        Assert.Throws<IOException>(() => store.GetOrCreate(user.Id, () => stale));
        var users = CallProxy.Create<IUserManager>((method, _) => method.Name switch { "GetUsers" => new[] { user }, "GetUserById" => user, _ => null }).Proxy;
        var log = Log(host.Folder);
        log.RemoveUser(user.Id);
        await new UserDataLifecycle(store, log, users, NullLogger<UserDataLifecycle>.Instance).StartAsync(CancellationToken.None);
        var fresh = store.GetOrCreate(user.Id, () => new PersonSettings { UserName = "Fresh settings" });
        Assert.True(fresh.Enabled);
        Assert.Equal("Fresh settings", fresh.UserName);
        Assert.False(store.Update(user.Id, _ => stale));
        log.Add(Entry(user.Id, "New account history"));
        Assert.Single(log.Query(20));
        Assert.NotNull(host.Store.TryGet(user.Id));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(user.Id))!, "*.json.deleted-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSettingsSavePreservesBurnedInFlagAddedConcurrently(bool admin)
    {
        var store = new ApiSafetyStore();
        var user = ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings { UserId = user.Id });
        var earlier = store.TryGet(user.Id)!.Clone();
        var body = earlier.Clone();
        store.BeforeUpdate = () => store.Inner.Update(user.Id, current =>
        {
            var copy = current!.Clone();
            copy.BurnedIn.Add(new BurnedInFlag { ItemId = Ids.Item.ToString("N"), Languages = new List<string> { "en" } });
            return copy;
        });
        PersonDocuments.SaveMerged(store, earlier, body, admin, user, keepOverrides: true);
        Assert.Single(store.TryGet(user.Id)!.BurnedIn);
    }

    [Fact]
    public async Task CreationEventReopensSameIdWithoutRestoringItsOldSettingsOrHistory()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings { Enabled = false, UserName = "Deleted account" });
        var stale = store.TryGet(user.Id)!.Clone();
        var original = File.ReadAllBytes(store.FilePath(user.Id));
        var log = Log(host.Folder);
        log.Add(Entry(user.Id, "Old history"));
        var lifecycle = new UserDataLifecycle(store, log, ApiSafetyHost.Users(user), NullLogger<UserDataLifecycle>.Instance);
        await lifecycle.OnEvent(new UserDeletedEventArgs(user));
        await new NewUserTemplateConsumer(store, null, NullLogger<NewUserTemplateConsumer>.Instance, ApiSafetyHost.Users(user)).OnEvent(new UserCreatedEventArgs(user));
        Assert.True(store.TryGet(user.Id)!.Enabled);
        Assert.Equal(user.Username, store.TryGet(user.Id)!.UserName);
        Assert.False(store.Update(user.Id, _ => stale));
        Assert.Empty(log.Query(20));
        log.Add(Entry(user.Id, "New history"));
        Assert.Equal("New history", Assert.Single(log.Query(20)).Text);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(user.Id))!, "*.json.deleted-*"))));
    }

    [Fact]
    public async Task OrdinaryAuthenticatedAccessDoesNotReopenATombstone()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings());
        store.Delete(user.Id);
        var me = ApiSafetyHost.Me(store, user);
        var admin = ApiSafetyHost.Admin(store, null, user);
        Assert.Equal(409, Assert.IsType<ObjectResult>(await InvokeApi(me, () => Task.FromResult<IActionResult?>(me.GetSettings().Result))).StatusCode);
        Assert.Equal(409, Assert.IsType<ObjectResult>(await InvokeApi(admin, () => Task.FromResult<IActionResult?>(admin.GetUserSettings(user.Id).Result))).StatusCode);
        Assert.True(File.Exists(store.FilePath(user.Id) + ".deleted"));
        Assert.False(File.Exists(store.FilePath(user.Id)));
        Assert.False(store.Update(user.Id, _ => throw new InvalidOperationException("Must not write")));
    }

    [Fact]
    public async Task FailedPositiveReactivationStillBlocksSeedsAndWrites()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings { Enabled = false });
        var original = File.ReadAllBytes(store.FilePath(user.Id));
        store.Delete(user.Id);
        Directory.CreateDirectory(store.FilePath(user.Id) + ".deleted-blocker");
        // A directory at the marker path makes reactivation fail after preservation, while the memory gate remains closed.
        File.Delete(store.FilePath(user.Id) + ".deleted");
        Directory.Move(store.FilePath(user.Id) + ".deleted-blocker", store.FilePath(user.Id) + ".deleted");
        var logger = new StorageTestLogger<NewUserTemplateConsumer>();
        await new NewUserTemplateConsumer(store, null, logger, ApiSafetyHost.Users(user)).OnEvent(new UserCreatedEventArgs(user));
        Assert.Null(store.TryGet(user.Id));
        Assert.Throws<IOException>(() => store.GetOrCreate(user.Id, () => throw new InvalidOperationException("Must not seed")));
        Assert.False(store.Update(user.Id, _ => throw new InvalidOperationException("Must not write")));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(user.Id))!, "*.json.deleted-*"))));
        Assert.Contains(logger.Entries, entry => entry.Error is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public void ConflictHandlerLogsTheActualIoExceptionAndDoesNotExposeItsPath()
    {
        var logger = new StorageTestLogger<StorageApiErrorsAttribute>();
        using var services = new ServiceCollection().AddSingleton<ILogger<StorageApiErrorsAttribute>>(logger).BuildServiceProvider();
        var error = new IOException("Cannot open C:\\data\\settings.json");
        var context = new ExceptionContext(new ActionContext(new DefaultHttpContext { RequestServices = services }, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>()) { Exception = error };
        new StorageApiErrorsAttribute().OnException(context);
        Assert.True(context.ExceptionHandled);
        Assert.Same(error, Assert.Single(logger.Entries).Error);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(409, result.StatusCode);
        Assert.DoesNotContain("data", Assert.IsType<ApiProblem>(result.Value).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictHandlerDoesNotLogRepeatedFutureFormatConflicts()
    {
        using var host = new LocalStorageTestHost();
        var storeLogger = new StorageTestLogger<PersonStore>();
        var filterLogger = new StorageTestLogger<StorageApiErrorsAttribute>();
        var store = new PersonStore(host.Folder, storeLogger);
        var id = Guid.NewGuid();
        WriteFuture(store, id, undecodable: true);
        var original = File.ReadAllBytes(store.FilePath(id));
        using var services = new ServiceCollection().AddSingleton<ILogger<StorageApiErrorsAttribute>>(filterLogger).BuildServiceProvider();

        for (var i = 0; i < 5; i++)
        {
            var conflict = Assert.Throws<PersonStorageConflictException>(() => PersonStorageAccess.Require(store, id, writing: true));
            var context = new ExceptionContext(new ActionContext(new DefaultHttpContext { RequestServices = services }, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>()) { Exception = conflict };
            new StorageApiErrorsAttribute().OnException(context);
            Assert.True(context.ExceptionHandled);
            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(409, result.StatusCode);
            Assert.Contains("Upgrade Babelfin", Assert.IsType<ApiProblem>(result.Value).Message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(filterLogger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(LogLevel.Warning, Assert.Single(storeLogger.Entries).Level);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FutureFileBlocksCopyImportAndResetBeforeAnythingIsChanged(bool undecodable)
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var store = host.Store;
        var blocked = ApiSafetyHost.Person();
        var other = ApiSafetyHost.Person("Other", Guid.NewGuid());
        WriteFuture(store, blocked.Id, undecodable);
        var original = File.ReadAllBytes(store.FilePath(blocked.Id));
        store.GetOrCreate(other.Id, () => new PersonSettings { UserName = other.Username });
        var otherBytes = File.ReadAllBytes(store.FilePath(other.Id));
        var users = CallProxy.Create<IUserManager>((method, args) => method.Name switch
        {
            "GetUsers" => new[] { blocked, other },
            "GetUserById" => (Guid)args[0]! == blocked.Id ? blocked : other,
            _ => null,
        }).Proxy;
        var admin = new AdminController(ApiSafetyHost.Library(), users, store, new FakeLibraryFacts(), CallProxy.Create<IProfileResolver>().Proxy);
        var document = new ExportDocument
        {
            Admin = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, Enabled = false },
            Users = new List<PersonSettings> { new() { UserId = other.Id, Enabled = false }, new() { UserId = blocked.Id } },
        };
        var preview = Assert.IsType<ImportPreview>(Assert.IsType<OkObjectResult>((await admin.Import(document)).Result).Value);
        Assert.False(preview.Applied);
        Assert.Contains(preview.Users, row => row.UserId == blocked.Id && row.Action == ImportUserPreview.Skip && row.Reason!.Contains("Upgrade Babelfin", StringComparison.Ordinal));
        var actions = new Func<Task<IActionResult?>>[]
        {
            () => Task.FromResult<IActionResult?>(admin.CopyTo(blocked.Id, other.Id).Result),
            () => Task.FromResult<IActionResult?>(admin.CopyTo(other.Id, blocked.Id).Result),
            async () => (await admin.Import(document, false)).Result,
            async () => (await admin.ResetAll()).Result,
        };
        foreach (var action in actions)
        {
            var response = Assert.IsAssignableFrom<ObjectResult>(await InvokeApi(admin, action));
            Assert.Equal(409, response.StatusCode);
            Assert.Contains("Upgrade Babelfin", Assert.IsType<ApiProblem>(response.Value).Message, StringComparison.Ordinal);
            Assert.Equal(original, File.ReadAllBytes(store.FilePath(blocked.Id)));
            Assert.Equal(otherBytes, File.ReadAllBytes(store.FilePath(other.Id)));
            Assert.True(plugin.Configuration.Enabled);
        }
    }

    [Fact]
    public async Task UnsupportedFileReplacedAfterCachingCannotBeOverwrittenByAnApiSave()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings());
        WriteFuture(store, user.Id, true);
        var original = File.ReadAllBytes(store.FilePath(user.Id));
        var me = ApiSafetyHost.Me(store, user);
        var result = Assert.IsType<ObjectResult>(await InvokeApi(me, async () => (await me.PutSettings(new PersonSettings(), true)).Result));
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(user.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FutureFilesReturnActionableConflictForEveryMutation(bool undecodable)
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var user = ApiSafetyHost.Person();
        WriteFuture(store, user.Id, undecodable);
        var bytes = File.ReadAllBytes(store.FilePath(user.Id));
        var me = ApiSafetyHost.Me(store, user);
        var admin = ApiSafetyHost.Admin(store, null, user);
        var actions = new (ControllerBase Controller, Func<Task<IActionResult?>> Run)[]
        {
            (me, () => Task.FromResult<IActionResult?>(me.DeleteOverrides())),
            (me, () => Task.FromResult<IActionResult?>(me.DeleteOverride(Ids.Item, null, null))),
            (me, () => Task.FromResult<IActionResult?>(me.DeleteBurnedIn(Ids.Item, null, null))),
            (me, () => Task.FromResult<IActionResult?>(me.PutOverride(Ids.Item, new SettingsLayer(), null, null).Result)),
            (me, () => Task.FromResult<IActionResult?>(me.PostBurnedIn(Ids.Item, new BurnedInRequest { Languages = new List<string> { "en" } }, null).Result)),
            (me, async () => (await me.Reset()).Result),
            (me, async () => (await me.PutSettings(new PersonSettings(), true)).Result),
            (me, async () => (await me.Import(new PersonSettings())).Result),
            (admin, () => Task.FromResult<IActionResult?>(admin.DeleteUserOverrides(user.Id))),
            (admin, () => Task.FromResult<IActionResult?>(admin.DeleteUserOverride(user.Id, Ids.Item, null))),
            (admin, async () => (await admin.PutUserSettings(user.Id, new PersonSettings(), true)).Result),
        };
        foreach (var action in actions)
        {
            var result = Assert.IsAssignableFrom<ObjectResult>(await InvokeApi(action.Controller, action.Run));
            Assert.Equal(409, result.StatusCode);
            var problem = Assert.IsType<ApiProblem>(result.Value);
            Assert.Contains("Upgrade Babelfin", problem.Message, StringComparison.Ordinal);
            Assert.Equal(bytes, File.ReadAllBytes(store.FilePath(user.Id)));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(user.Id))!));
        }
    }

    internal static async Task<IActionResult?> InvokeApi(ControllerBase controller, Func<Task<IActionResult?>> action)
    {
        try { return await action(); }
        catch (Exception ex)
        {
            var context = new ExceptionContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>()) { Exception = ex };
            foreach (var filter in controller.GetType().GetCustomAttributes(true).OfType<IExceptionFilter>()) filter.OnException(context);
            return context.ExceptionHandled ? context.Result : new ObjectResult(ex.Message) { StatusCode = 500 };
        }
    }

    private static void WriteFuture(PersonStore store, Guid id, bool undecodable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
        File.WriteAllText(store.FilePath(id), "{\"schemaVersion\":999,\"everyday\":" + (undecodable ? "\"future-layout\"" : "{}") + "}");
    }

    private static ActivityLog Log(string folder) => new();
    private static ActivityEntry Entry(Guid id, string text) => new(DateTime.UtcNow, ActivityKinds.SettingsChanged, id, null, text);
}
