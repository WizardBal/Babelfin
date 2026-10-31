using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Data.Events.Users;
using User = Jellyfin.Database.Implementations.Entities.User;
using Jellyfin.Plugin.Babelfin.Activity;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class StorageLifecycleRegressionTests
{
    [Theory]
    [InlineData("\"overrides\":[{\"itemId\":\"00000000000000000000000000000001\",\"scope\":\"future-scope\"}]")]
    [InlineData("\"everyday\":\"future-layout\"")]
    [InlineData("\"overrides\":[{\"itemId\":\"00000000000000000000000000000001\",\"scope\":99999}]")]
    public void UndecodableFuturePersonNeverSeedsResetsOrOverwrites(string field)
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
        var bytes = Encoding.UTF8.GetBytes("/* preserved */ {\"SchemaVersion\":" + (PersonSettings.CurrentSchemaVersion + 1) + "," + field + ",}\r\n");
        File.WriteAllBytes(store.FilePath(id), bytes);
        Assert.Null(store.TryGet(id));
        var called = false;
        Assert.Throws<IOException>(() => store.GetOrCreate(id, () => { called = true; return new PersonSettings(); }));
        Assert.False(called);
        Assert.False(store.Update(id, _ => { called = true; return new PersonSettings { UserId = id }; }));
        Assert.False(((IExplicitPersonUpdates)store).UpdateExplicit(id, _ => { called = true; return new PersonSettings { UserId = id }; }));
        Assert.False(called);
        Assert.Null(host.Store.TryGet(id));
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath(id)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!));
    }

    [Theory]
    [InlineData("\"schemaVersion\":999,\"SchemaVersion\":1")]
    [InlineData("\"SchemaVersion\":1,\"schemaVersion\":999")]
    [InlineData("\"schemaVersion\":\"999\",\"SchemaVersion\":1")]
    public void AmbiguousFutureSchemaNeverUsesAnOlderDuplicateToOverwriteData(string versions)
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
        var bytes = Encoding.UTF8.GetBytes("{" + versions + ",\"overrides\":[]}");
        File.WriteAllBytes(store.FilePath(id), bytes);
        Assert.Null(store.TryGet(id));
        Assert.Throws<IOException>(() => store.GetOrCreate(id, () => throw new InvalidOperationException("Must not seed")));
        Assert.False(store.Update(id, _ => throw new InvalidOperationException("Must not overwrite")));
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath(id)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!));
    }

    [Fact]
    public void OversizedFutureSchemaIsPreservedInsteadOfQuarantined()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
        var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":2147483648,\"overrides\":[]}");
        File.WriteAllBytes(store.FilePath(id), bytes);
        Assert.Throws<IOException>(() => store.GetOrCreate(id, () => throw new InvalidOperationException("Must not seed")));
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath(id)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!));
    }

    [Fact]
    public async Task DeletedUserIsArchivedExactlyAndLateWritesStayBlockedAfterRestart()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var deleted = new User("Deleted example", "Default", "Default") { Id = Guid.NewGuid() };
        var active = Guid.NewGuid();
        store.Save(new PersonSettings { UserId = deleted.Id, UserName = deleted.Username });
        store.Save(new PersonSettings { UserId = active });
        var personBytes = File.ReadAllBytes(store.FilePath(deleted.Id));
        var log = Log(host.Folder);
        log.Add(Entry(deleted.Id));
        log.Add(Entry(active));
        var lifecycle = new UserDataLifecycle(store, log, CallProxy.Create<IUserManager>().Proxy, NullLogger<UserDataLifecycle>.Instance);
        await lifecycle.OnEvent(new UserDeletedEventArgs(deleted));
        Assert.Null(store.TryGet(deleted.Id));
        Assert.Equal(personBytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(deleted.Id))!, "*.json.deleted-*"))));
        Assert.Equal(active, Assert.Single(log.Query(20)).UserId);
        Assert.NotNull(store.TryGet(active));
        var restartedStore = host.Store;
        var restartedLog = Log(host.Folder);
        Assert.Throws<IOException>(() => restartedStore.GetOrCreate(deleted.Id, () => new PersonSettings()));
        var explicitWriteCalled = false;
        Assert.False(restartedStore.UpdateExplicit(deleted.Id, _ =>
        {
            explicitWriteCalled = true;
            return new PersonSettings { UserId = deleted.Id };
        }));
        Assert.False(explicitWriteCalled);
        Assert.False(restartedStore.Update(deleted.Id, _ => throw new InvalidOperationException("Must not run")));
        restartedLog.Add(Entry(deleted.Id));
        Assert.Equal(deleted.Id, Assert.Single(restartedLog.Query(20)).UserId);
        await lifecycle.OnEvent(new UserDeletedEventArgs(deleted));
    }

    [Fact]
    public async Task StartupReconcilesHistoryOutsideTheRingWithoutDeletingRecoveryCopies()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var deletedId = Guid.NewGuid();
        var active = new User("Active example", "Default", "Default") { Id = Guid.NewGuid() };
        store.Save(new PersonSettings { UserId = deletedId });
        store.Save(new PersonSettings { UserId = active.Id });
        var log = Log(host.Folder);
        log.Add(Entry(deletedId));
        log.Add(Entry(active.Id));
        var users = CallProxy.Create<IUserManager>((method, _) => method.Name == "GetUsers" ? new[] { active } : null).Proxy;
        await new UserDataLifecycle(store, log, users, NullLogger<UserDataLifecycle>.Instance).StartAsync(CancellationToken.None);
        Assert.Null(store.TryGet(deletedId));
        Assert.NotNull(store.TryGet(active.Id));
        Assert.Equal(active.Id, Assert.Single(log.Query(20)).UserId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserListingFailurePreservesSettingsAndSuccessfulReconciliationBlocksDeletedUser(bool failListing)
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var id = Guid.NewGuid();
        store.Save(new PersonSettings { UserId = id });
        var log = Log(host.Folder);
        log.Add(Entry(id));
        var person = File.ReadAllBytes(store.FilePath(id));
        var users = CallProxy.Create<IUserManager>((method, _) => method.Name == "GetUsers"
            ? failListing ? throw new IOException("User database unavailable") : new[] { new User("Active", "Default", "Default") { Id = Guid.NewGuid() } } : null).Proxy;
        var logger = new StorageTestLogger<UserDataLifecycle>();
        await new UserDataLifecycle(store, log, users, logger).StartAsync(CancellationToken.None);
        if (failListing)
        {
            Assert.Equal(person, File.ReadAllBytes(store.FilePath(id)));
            Assert.NotNull(store.TryGet(id));
        }
        else
        {
            Assert.Null(store.TryGet(id));
            Assert.Equal(person, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.json.deleted-*"))));
            Assert.False(store.Update(id, _ => throw new InvalidOperationException("Deleted user must stay blocked")));
            Assert.Empty(log.Query(20));
            log.Add(Entry(id));
            Assert.Empty(log.Query(20));
        }
        if (failListing) Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Error is IOException);
    }

    [Fact]
    public void FailedPersonMarkerStillBlocksLateWritesAndInvalidatesTheResolver()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var id = Guid.NewGuid();
        store.Save(new PersonSettings { UserId = id });
        var bytes = File.ReadAllBytes(store.FilePath(id));
        Directory.CreateDirectory(store.FilePath(id) + ".deleted");
        var changed = 0;
        store.Changed += (_, e) => { if (e.UserId == id) changed++; };
        var failure = Record.Exception(() => store.Delete(id));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(1, changed);
        Assert.Null(store.TryGet(id));
        Assert.DoesNotContain(id, store.UserIds());
        Assert.False(store.Update(id, _ => throw new InvalidOperationException("Must not overwrite")));
        Assert.False(store.UpdateExplicit(id, _ => throw new InvalidOperationException("Must not overwrite")));
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath(id)));
    }

    [Fact]
    public async Task DeletionEventBlocksSettingsAndInMemoryActivity()
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var deleted = new User("Deleted example", "Default", "Default") { Id = Guid.NewGuid() };
        store.Save(new PersonSettings { UserId = deleted.Id });
        var log = Log(host.Folder);
        log.Add(Entry(deleted.Id));
        var lifecycle = new UserDataLifecycle(store, log, CallProxy.Create<IUserManager>().Proxy, NullLogger<UserDataLifecycle>.Instance);
        await lifecycle.OnEvent(new UserDeletedEventArgs(deleted));
        Assert.Null(store.TryGet(deleted.Id));
        Assert.False(store.Update(deleted.Id, _ => throw new InvalidOperationException("Must not overwrite")));
        log.Add(Entry(deleted.Id));
        Assert.Empty(log.Query(20));
    }

    private static ActivityLog Log(string folder) => new();
    private static ActivityEntry Entry(Guid id) => new(DateTime.UtcNow, ActivityKinds.SettingsChanged, id, null, "Synthetic activity");
}
