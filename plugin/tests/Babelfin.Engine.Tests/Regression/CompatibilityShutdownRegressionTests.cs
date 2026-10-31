using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin;
using Jellyfin.Plugin.Babelfin.Compat;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class CompatibilityShutdownRegressionTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DisabledShutdownStopsSuspendedAndFutureWritesBeforeRestoringFlags()
    {
        using var fixture = new WriterFixture();
        await fixture.Writer.WriteAsync(fixture.User, fixture.Item, 2, 4, CancellationToken.None);
        var (pending, queue) = SuspendWrite(fixture, 5, 6);
        var blockedBeforeFlags = false;

        await fixture.ShutdownAsync(restoreFlags: async () =>
        {
            await fixture.Writer.WriteAsync(fixture.User, fixture.Item, 7, 8, CancellationToken.None);
            blockedBeforeFlags = fixture.Saves == 1 && fixture.Indices == (2, 4);
        }).WaitAsync(Watchdog);

        Assert.True(blockedBeforeFlags);
        Assert.Equal((1, 3), fixture.Indices);
        Assert.Equal(0, fixture.Writer.LedgerCount);
        var restoredBytes = File.ReadAllBytes(fixture.Writer.FilePath);
        queue.Drain();
        await pending.WaitAsync(Watchdog);
        await fixture.Writer.WriteAsync(fixture.User, fixture.Item, 9, 10, CancellationToken.None);
        Assert.Equal(2, fixture.Saves);
        Assert.Equal((1, 3), fixture.Indices);
        Assert.Equal(restoredBytes, File.ReadAllBytes(fixture.Writer.FilePath));
    }

    [Fact]
    public async Task StopDoesNotWaitForAnInFlightWriteAndShutdownUndoRunsAfterItFinishes()
    {
        using var fixture = new WriterFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flagsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnSave = () =>
        {
            if (fixture.Saves != 1) return;
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var write = Task.Run(() => fixture.Writer.WriteAsync(fixture.User, fixture.Item, 2, 4, CancellationToken.None));
        Task? shutdown = null;
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            // Closing admission must not acquire the lock held by the synchronous Jellyfin save.
            await Task.Run(() => ((ICompatibilityWriter)fixture.Writer).StopWrites()).WaitAsync(Watchdog);
            shutdown = fixture.ShutdownAsync(restoreFlags: () =>
            {
                flagsEntered.SetResult();
                return Task.CompletedTask;
            });
            await flagsEntered.Task.WaitAsync(Watchdog);
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await write.WaitAsync(Watchdog);
            if (shutdown is not null) await shutdown.WaitAsync(Watchdog);
        }

        Assert.Equal(2, fixture.Saves);
        Assert.Equal((1, 3), fixture.Indices);
        Assert.Equal(0, fixture.Writer.LedgerCount);
        Assert.Equal(JsonReadOutcome.Loaded, AtomicJsonFile.Read<CompatLedger>(fixture.Writer.FilePath,
            BabelfinJson.Options, NullLogger.Instance, out var ledger));
        Assert.Empty(ledger!.Entries);
    }

    [Fact]
    public async Task CancellationBoundsShutdownWhileARealWriteHoldsTheLedgerLock()
    {
        using var fixture = new WriterFixture();
        using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flagsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnSave = () =>
        {
            if (fixture.Saves != 1) return;
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var write = Task.Run(() => fixture.Writer.WriteAsync(fixture.User, fixture.Item, 2, 4, CancellationToken.None));
        Task? shutdown = null;
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            shutdown = fixture.ShutdownAsync(stopping.Token, () =>
            {
                flagsEntered.SetResult();
                return Task.CompletedTask;
            });
            await flagsEntered.Task.WaitAsync(Watchdog);
            stopping.Cancel();
            await shutdown.WaitAsync(Watchdog);
            Assert.False(write.IsCompleted);
            Assert.Contains(fixture.Logger.Entries, e => e.Error is OperationCanceledException);
        }
        finally
        {
            release.TrySetResult();
            await write.WaitAsync(Watchdog);
            if (shutdown is not null) await shutdown.WaitAsync(Watchdog);
        }

        await fixture.Writer.WriteAsync(fixture.User, fixture.Item, 5, 6, CancellationToken.None);
        Assert.Equal((2, 4), fixture.Indices);
        Assert.Equal(1, fixture.Writer.LedgerCount);
        // The cancelled pass retains the recovery record; an explicit retry can still undo it.
        await fixture.Writer.UndoAllAsync(CancellationToken.None).WaitAsync(Watchdog);
        Assert.Equal((1, 3), fixture.Indices);
        Assert.Equal(0, fixture.Writer.LedgerCount);
    }

    [Fact]
    public async Task AlreadyCancelledShutdownStillPermanentlyStopsWrites()
    {
        using var fixture = new WriterFixture();
        await fixture.Writer.WriteAsync(fixture.User, fixture.Item, 2, 4, CancellationToken.None);
        var original = File.ReadAllBytes(fixture.Writer.FilePath);
        await fixture.ShutdownAsync(new CancellationToken(canceled: true)).WaitAsync(Watchdog);
        await fixture.Writer.WriteAsync(fixture.User, fixture.Item, 5, 6, CancellationToken.None);
        Assert.Equal(1, fixture.Saves);
        Assert.Equal((2, 4), fixture.Indices);
        Assert.Equal(original, File.ReadAllBytes(fixture.Writer.FilePath));
    }

    [Theory]
    [InlineData(8, 4, 8, 3)]
    [InlineData(2, 9, 1, 9)]
    [InlineData(8, 9, 8, 9)]
    public async Task ShutdownUndoPreservesLaterExternalChanges(int audio, int subtitle, int expectedAudio, int expectedSubtitle)
    {
        using var fixture = new WriterFixture();
        await fixture.Writer.WriteAsync(fixture.User, fixture.Item, 2, 4, CancellationToken.None);
        fixture.Data.AudioStreamIndex = audio;
        fixture.Data.SubtitleStreamIndex = subtitle;
        await fixture.ShutdownAsync().WaitAsync(Watchdog);
        Assert.Equal((expectedAudio, expectedSubtitle), fixture.Indices);
        Assert.Equal(0, fixture.Writer.LedgerCount);
    }

    [Fact]
    public async Task NormalUndoAllowsFutureWrites()
    {
        using var fixture = new WriterFixture();
        await fixture.Writer.WriteAsync(fixture.User, fixture.Item, 2, 4, CancellationToken.None);
        await fixture.Writer.UndoAllAsync(CancellationToken.None);
        Assert.Equal((1, 3), fixture.Indices);
        await fixture.Writer.WriteAsync(fixture.User, fixture.Item, 5, 6, CancellationToken.None);
        Assert.Equal((5, 6), fixture.Indices);
        Assert.Equal(1, fixture.Writer.LedgerCount);
        await fixture.Writer.UndoAllAsync(CancellationToken.None);
        Assert.Equal((1, 3), fixture.Indices);
        Assert.Equal(0, fixture.Writer.LedgerCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringPruneRejectsWriteThatPassedEarlyChecksAndOnlyNormalUndoAllowsReenable(bool permanent)
    {
        using var fixture = new WriterFixture();
        fixture.Data.AudioStreamIndex = 2;
        fixture.Data.SubtitleStreamIndex = 4;
        // A fresh writer must prune this stale, still-owned entry. Pause that prune after the early mode check.
        AtomicJsonFile.Write(fixture.Writer.FilePath, new CompatLedger
        {
            Entries = new List<CompatEntry> { new()
            {
                UserId = fixture.User.Id, ItemId = fixture.Item.Id,
                PreviousAudio = 1, PreviousSubtitle = 3, WrittenAudio = 2, WrittenSubtitle = 4,
                WrittenAt = DateTime.UtcNow.AddDays(-90), FirstWrittenAt = DateTime.UtcNow.AddDays(-90),
            } },
        }, BabelfinJson.Options);
        var (pending, queue) = SuspendWrite(fixture, 5, 6);
        Assert.True(queue.RunNext());
        Assert.False(pending.IsCompleted);
        Assert.True(queue.HasWork); // The prune's yielded batch, after the early compatibility-mode check.
        if (permanent)
            await fixture.ShutdownAsync().WaitAsync(Watchdog);
        else
        {
            fixture.Plugin.Configuration.CompatibilityMode = false;
            await fixture.Writer.UndoAllAsync(CancellationToken.None).WaitAsync(Watchdog);
        }
        Assert.Equal((1, 3), fixture.Indices);
        var restoredBytes = File.ReadAllBytes(fixture.Writer.FilePath);
        queue.Drain();
        await pending.WaitAsync(Watchdog);
        Assert.Equal(1, fixture.Saves);
        Assert.Equal((1, 3), fixture.Indices);
        Assert.Equal(restoredBytes, File.ReadAllBytes(fixture.Writer.FilePath));

        fixture.Plugin.Configuration.CompatibilityMode = true;
        await fixture.Writer.WriteAsync(fixture.User, fixture.Item, 7, 8, CancellationToken.None);
        Assert.Equal(permanent ? (1, 3) : (7, 8), fixture.Indices);
        Assert.Equal(permanent ? 0 : 1, fixture.Writer.LedgerCount);
    }

    private static (Task Pending, StepContext Queue) SuspendWrite(WriterFixture fixture, int audio, int subtitle)
    {
        var queue = new StepContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queue);
            return (fixture.Writer.WriteAsync(fixture.User, fixture.Item, audio, subtitle, CancellationToken.None), queue);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class StepContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        internal bool HasWork => !_queue.IsEmpty;
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));
        internal bool RunNext()
        {
            if (!_queue.TryDequeue(out var work)) return false;
            var previous = Current;
            try
            {
                SetSynchronizationContext(this);
                work.Callback(work.State);
            }
            finally { SetSynchronizationContext(previous); }
            return true;
        }
        internal void Drain() { while (RunNext()) { } }
    }

    private sealed class WriterFixture : IDisposable
    {
        private readonly LocalStorageTestHost _host = new();
        private int _saves;
        internal User User { get; } = new("Shutdown test", "Default", "Default") { Id = Guid.NewGuid() };
        internal Movie Item { get; } = new() { Id = Guid.NewGuid(), Name = "Synthetic shutdown title" };
        internal UserItemData Data { get; } = new() { Key = "synthetic-shutdown-item", AudioStreamIndex = 1, SubtitleStreamIndex = 3 };
        internal Plugin Plugin { get; }
        internal CompatibilityWriter Writer { get; }
        internal StorageTestLogger<PluginDisableCleanup> Logger { get; } = new();
        internal Action? OnSave { get; set; }
        internal int Saves => Volatile.Read(ref _saves);
        internal (int? Audio, int? Subtitle) Indices => (Data.AudioStreamIndex, Data.SubtitleStreamIndex);

        internal WriterFixture()
        {
            Plugin = _host.CreatePlugin(new PluginConfiguration
            {
                ConfigVersion = PluginConfiguration.CurrentVersion, CompatibilityMode = true,
            });
            var users = CallProxy.Create<IUserManager>((method, _) =>
                method.Name == nameof(IUserManager.GetUserById) ? User : null).Proxy;
            var library = CallProxy.Create<ILibraryManager>((method, _) =>
                method.Name == nameof(ILibraryManager.GetItemById) ? Item : null).Proxy;
            var data = CallProxy.Create<IUserDataManager>((method, _) =>
            {
                if (method.Name == nameof(IUserDataManager.GetUserData)) return Data;
                if (method.Name == nameof(IUserDataManager.SaveUserData))
                {
                    Interlocked.Increment(ref _saves);
                    OnSave?.Invoke();
                }
                return null;
            }).Proxy;
            Writer = new CompatibilityWriter(_host.Folder, data, users, library,
                new ListActivityLog(), NullLogger<CompatibilityWriter>.Instance);
        }

        internal async Task ShutdownAsync(CancellationToken stopping = default, Func<Task>? restoreFlags = null)
        {
            var flags = CallProxy.Create<IRememberFlags>((method, _) =>
                method.Name == nameof(IRememberFlags.RestoreAllAsync) ? restoreFlags?.Invoke() : null).Proxy;
            using var services = new ServiceCollection().AddSingleton(flags).AddSingleton<ICompatibilityWriter>(Writer).BuildServiceProvider();
            var manifest = new PluginManifest { Status = PluginStatus.Disabled };
            var version = typeof(Plugin).Assembly.GetName().Version!;
            var versionProperty = typeof(PluginManifest).GetProperty("Version")!;
            versionProperty.SetValue(manifest, versionProperty.PropertyType == typeof(string) ? version.ToString() : version);
            var local = new LocalPlugin(_host.Folder, true, manifest);
            var plugins = CallProxy.Create<IPluginManager>((method, _) =>
                method.Name == nameof(IPluginManager.GetPlugin) ? local : null).Proxy;
            await new PluginDisableCleanup(plugins, services, Logger).StopAsync(stopping).ConfigureAwait(false);
        }

        public void Dispose() => _host.Dispose();
    }
}
