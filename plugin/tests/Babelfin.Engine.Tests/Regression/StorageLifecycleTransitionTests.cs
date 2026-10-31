using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Babelfin.Activity;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Seeding;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class StorageLifecycleTransitionTests
{
    [Fact]
    public async Task DelayedCreationAfterDeletionCannotReopenAnAccountAbsentFromJellyfin()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var deletedUser = ApiSafetyHost.Person();
        store.GetOrCreate(deletedUser.Id, () => new PersonSettings { Enabled = false });
        var original = File.ReadAllBytes(store.FilePath(deletedUser.Id));
        var activity = Log(host.Folder);
        activity.Add(Entry(deletedUser.Id));
        var users = ApiSafetyHost.Users();
        var lifecycle = new UserDataLifecycle(store, activity, users, NullLogger<UserDataLifecycle>.Instance);
        await lifecycle.OnEvent(new UserDeletedEventArgs(deletedUser));

        // The creation callback holds an older entity, but Jellyfin now reports this id as absent.
        await new NewUserTemplateConsumer(store, null, NullLogger<NewUserTemplateConsumer>.Instance, users).OnEvent(new UserCreatedEventArgs(deletedUser));

        Assert.Null(store.TryGet(deletedUser.Id));
        Assert.True(File.Exists(store.FilePath(deletedUser.Id) + ".deleted"));
        Assert.Throws<IOException>(() => store.GetOrCreate(deletedUser.Id, () => new PersonSettings()));
        Assert.False(store.Update(deletedUser.Id, _ => new PersonSettings { UserId = deletedUser.Id }));
        activity.Add(Entry(deletedUser.Id));
        Assert.Empty(activity.Query(20));
        Assert.Empty(Log(host.Folder).Query(20));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(deletedUser.Id))!, "*.json.deleted-*"))));
    }

    [Fact]
    public async Task CreationRevalidatesInsideTheGateAndSeedsFromTheCurrentEntity()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var stale = ApiSafetyHost.Person("Stale event name");
        stale.AudioLanguagePreference = "ja";
        var current = ApiSafetyHost.Person("Current account name", stale.Id);
        current.AudioLanguagePreference = "fr";
        var lookups = 0;
        var users = CallProxy.Create<IUserManager>((method, args) =>
        {
            if (method.Name != "GetUserById") return null;
            Assert.True(Monitor.IsEntered(UserLifecycleTransitions.For(store)));
            Assert.Equal(stale.Id, args[0]);
            lookups++;
            return current;
        }).Proxy;
        store.GetOrCreate(stale.Id, () => new PersonSettings { Enabled = false });
        store.Delete(stale.Id);

        await new NewUserTemplateConsumer(store, null, NullLogger<NewUserTemplateConsumer>.Instance, users).OnEvent(new UserCreatedEventArgs(stale));

        Assert.Equal(1, lookups);
        Assert.Equal(current.Username, store.TryGet(current.Id)!.UserName);
        Assert.Equal("fr", store.TryGet(current.Id)!.Seed!.JellyfinAudioLanguage);
        Assert.True(store.TryGet(current.Id)!.Enabled);
    }

    [Fact]
    public async Task SettingsBodyCapturedBeforeRecreationCannotChangeTheNewAccount()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var user = ApiSafetyHost.Person();
        var earlier = store.GetOrCreate(user.Id, () => new PersonSettings()).Clone();
        var body = earlier.Clone();
        body.Enabled = false;
        store.Delete(user.Id);
        await new NewUserTemplateConsumer(store, null, NullLogger<NewUserTemplateConsumer>.Instance, ApiSafetyHost.Users(user)).OnEvent(new UserCreatedEventArgs(user));
        var original = File.ReadAllBytes(store.FilePath(user.Id));
        Assert.Throws<PersonStorageConflictException>(() => PersonDocuments.SaveMerged(store, earlier, body, false, user, true));
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(user.Id)));
        Assert.True(store.TryGet(user.Id)!.Enabled);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("override")]
    [InlineData("delete")]
    [InlineData("burned-in")]
    [InlineData("copy")]
    public async Task CapturedApiSnapshotsAreCheckedInsideTheUpdateLock(string operation)
    {
        using var host = new LocalStorageTestHost();
        var inner = host.Store;
        var user = ApiSafetyHost.Person();
        var source = ApiSafetyHost.Person("Source", Guid.NewGuid());
        inner.GetOrCreate(user.Id, () => new PersonSettings
        {
            Overrides = new List<ItemOverride> { new() { ItemId = Ids.Item.ToString("N"), Settings = new SettingsLayer { AvoidCommentary = false } } },
        });
        var store = new PausingStore(inner);
        byte[]? recreated = null;
        store.BeforeUpdate = () =>
        {
            inner.Delete(user.Id);
            new NewUserTemplateConsumer(store, null, NullLogger<NewUserTemplateConsumer>.Instance, ApiSafetyHost.Users(user)).OnEvent(new UserCreatedEventArgs(user)).GetAwaiter().GetResult();
            recreated = File.ReadAllBytes(inner.FilePath(user.Id));
        };
        var me = ApiSafetyHost.Me(store, user);
        var admin = ApiSafetyHost.Admin(store, null, source, user);
        var result = operation switch
        {
            "settings" => await StorageApiSafetyTests.InvokeApi(me, async () => (await me.PutSettings(new PersonSettings(), true)).Result),
            "override" => await StorageApiSafetyTests.InvokeApi(me, () => Task.FromResult<IActionResult?>(me.PutOverride(Ids.Item, new SettingsLayer(), null, null).Result)),
            "delete" => await StorageApiSafetyTests.InvokeApi(me, () => Task.FromResult<IActionResult?>(me.DeleteOverride(Ids.Item, null, null))),
            "burned-in" => await StorageApiSafetyTests.InvokeApi(me, () => Task.FromResult<IActionResult?>(me.PostBurnedIn(Ids.Item, new BurnedInRequest { Languages = new List<string> { "en" } }, null).Result)),
            _ => await StorageApiSafetyTests.InvokeApi(admin, () => Task.FromResult<IActionResult?>(admin.CopyTo(source.Id, user.Id).Result)),
        };
        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.NotNull(recreated);
        Assert.Equal(recreated, File.ReadAllBytes(inner.FilePath(user.Id)));
    }

    [Fact]
    public void ReactivationChangedHandlerCanCompleteAnIndependentStoreUpdate()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var id = Guid.NewGuid();
        store.GetOrCreate(id, () => new PersonSettings());
        store.Delete(id);
        var completed = false;
        store.Changed += (_, e) =>
        {
            if (e.UserId != id) return;
            var other = Guid.NewGuid();
            var update = Task.Run(() => store.Update(other, _ => new PersonSettings { UserId = other }));
            completed = SpinWait.SpinUntil(() => update.IsCompleted, TimeSpan.FromSeconds(2));
        };
        store.Activate(id);
        Assert.True(completed);
    }

    [Fact]
    public async Task CreationWaitsForBothDeletionArchivesBeforeReopeningHistory()
    {
        using var host = new LocalStorageTestHost();
        var inner = host.Store;
        var user = ApiSafetyHost.Person();
        inner.GetOrCreate(user.Id, () => new PersonSettings());
        var activity = Log(host.Folder);
        activity.Add(Entry(user.Id));
        using var deleted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var creationStarted = new ManualResetEventSlim();
        var store = new PausingStore(inner) { AfterDelete = () => { deleted.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        var lifecycle = new UserDataLifecycle(store, activity, ApiSafetyHost.Users(user), NullLogger<UserDataLifecycle>.Instance);
        var deletion = Task.Run(() => lifecycle.OnEvent(new UserDeletedEventArgs(user)));
        Task creation = Task.CompletedTask;
        try
        {
            Assert.True(deleted.Wait(TimeSpan.FromSeconds(5)));
            store.AfterDelete = null;
            creation = Task.Run(() =>
            {
                creationStarted.Set();
                return new NewUserTemplateConsumer(store, null, NullLogger<NewUserTemplateConsumer>.Instance, ApiSafetyHost.Users(user)).OnEvent(new UserCreatedEventArgs(user));
            });
            Assert.True(creationStarted.Wait(TimeSpan.FromSeconds(5)));
            await Task.WhenAny(creation, Task.Delay(150));
        }
        finally { release.Set(); }
        await Task.WhenAll(deletion, creation).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(inner.TryGet(user.Id));
        activity.Add(Entry(user.Id));
        Assert.Single(activity.Query(20));
        Assert.Empty(Log(host.Folder).Query(20));
    }

    [Fact]
    public async Task StaleStartupListingCannotReopenAnAccountDeletedDuringTheListing()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings());
        var activity = Log(host.Folder);
        activity.Add(Entry(user.Id));
        using var listed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var exists = true;
        var users = CallProxy.Create<IUserManager>((method, _) =>
        {
            if (method.Name == "GetUsers") { listed.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); return new[] { user }; }
            return method.Name == "GetUserById" && exists ? user : null;
        }).Proxy;
        var lifecycle = new UserDataLifecycle(store, activity, users, NullLogger<UserDataLifecycle>.Instance);
        var startup = Task.Run(() => lifecycle.StartAsync(CancellationToken.None));
        try
        {
            Assert.True(listed.Wait(TimeSpan.FromSeconds(5)));
            exists = false;
            await lifecycle.OnEvent(new UserDeletedEventArgs(user));
        }
        finally { release.Set(); }
        await startup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(store.TryGet(user.Id));
        Assert.Throws<IOException>(() => store.GetOrCreate(user.Id, () => new PersonSettings()));
        activity.Add(Entry(user.Id));
        Assert.Empty(activity.Query(20));
    }

    [Fact]
    public async Task UncachedUnreadableSettingsAreAConflictInsteadOfASeedOrOmittedExport()
    {
        using var host = new LocalStorageTestHost();
        host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var store = host.Store;
        var user = ApiSafetyHost.Person();
        store.GetOrCreate(user.Id, () => new PersonSettings());
        var original = File.ReadAllBytes(store.FilePath(user.Id));
        var uncached = host.Store;
        using (var held = new FileStream(store.FilePath(user.Id), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.NotNull(uncached.Conflict(user.Id, false));
            var admin = ApiSafetyHost.Admin(uncached, null, user);
            var me = ApiSafetyHost.Me(uncached, user);
            Assert.Equal(409, Assert.IsType<ObjectResult>(await StorageApiSafetyTests.InvokeApi(admin, () => Task.FromResult<IActionResult?>(admin.GetUserSettings(user.Id).Result))).StatusCode);
            Assert.Equal(409, Assert.IsType<ObjectResult>(await StorageApiSafetyTests.InvokeApi(me, () => Task.FromResult<IActionResult?>(me.GetSettings().Result))).StatusCode);
            Assert.Equal(409, Assert.IsType<ObjectResult>(await StorageApiSafetyTests.InvokeApi(admin, () => Task.FromResult<IActionResult?>(admin.Export()))).StatusCode);
        }
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(user.Id)));
    }

    private static ActivityLog Log(string folder) => new();
    private static ActivityEntry Entry(Guid id) => new(DateTime.UtcNow, ActivityKinds.SettingsChanged, id, null, "Synthetic history");

    private sealed class PausingStore(PersonStore inner) : IPersonStore, IPersonStoreSafety
    {
        internal Action? AfterDelete;
        internal Action? BeforeUpdate;
        public event EventHandler<PersonStoreChangedEventArgs>? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
        public event Action<Guid>? Reactivating { add => inner.Reactivating += value; remove => inner.Reactivating -= value; }
        public string DataFolder => inner.DataFolder;
        public PersonSettings? TryGet(Guid id) => inner.TryGet(id);
        public PersonSettings GetOrCreate(Guid id, Func<PersonSettings> create) => inner.GetOrCreate(id, create);
        public bool Update(Guid id, Func<PersonSettings?, PersonSettings?> change)
        {
            var pending = BeforeUpdate;
            BeforeUpdate = null;
            pending?.Invoke();
            return inner.Update(id, change);
        }
        public bool Delete(Guid id) { var result = inner.Delete(id); AfterDelete?.Invoke(); return result; }
        public IReadOnlyList<Guid> UserIds() => inner.UserIds();
        public void Activate(Guid id) => inner.Activate(id);
        public string? Conflict(Guid id, bool writing) => inner.Conflict(id, writing);
    }
}
