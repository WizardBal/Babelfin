using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Compat;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

public sealed class PlaybackSafetyCompatTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "babelfin-playback-safety", Guid.NewGuid().ToString("N"));
    private readonly User _user = new("Test", "Default", "Default") { Id = Guid.NewGuid(), RememberAudioSelections = true, RememberSubtitleSelections = true };
    private readonly Dictionary<Guid, BaseItem> _items = new();
    private readonly Dictionary<Guid, UserItemData> _data = new();
    private bool _failSaves;
    private bool _failReads;
    private int _saves;
    private Action? _onSave;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task PruneDropsOnlySafeStaleEntriesAndKeepsRecoveryRecords()
    {
        var stillOurs = AddItem(2, 4);
        var changedBoth = AddItem(8, 9);
        var changedOne = AddItem(8, 4);
        var gone = Guid.NewGuid();
        var fresh = AddItem(8, 9);
        var entries = new[] { Entry(stillOurs), Entry(changedBoth), Entry(changedOne), Entry(gone), Entry(fresh, stale: false) };
        SaveLedger(entries);
        var writer = Writer();
        await writer.PruneAsync(CancellationToken.None);
        Assert.Equal(3, writer.LedgerCount);
        var kept = ReadLedger().Entries.Select(e => e.ItemId).ToArray();
        Assert.Contains(stillOurs, kept);
        Assert.Contains(changedOne, kept);
        Assert.Contains(fresh, kept);
        Assert.DoesNotContain(changedBoth, kept);
        Assert.DoesNotContain(gone, kept);
        Assert.Equal(0, _saves);
        await writer.UndoAllAsync(CancellationToken.None);
        Assert.Equal((1, 3), (_data[stillOurs].AudioStreamIndex, _data[stillOurs].SubtitleStreamIndex));
        Assert.Equal((8, 3), (_data[changedOne].AudioStreamIndex, _data[changedOne].SubtitleStreamIndex));
        Assert.Equal((8, 9), (_data[changedBoth].AudioStreamIndex, _data[changedBoth].SubtitleStreamIndex));
        Assert.Equal(0, writer.LedgerCount);
    }

    [Fact]
    public async Task ReadAndRestoreFailuresRetainDurableUndoRecords()
    {
        var item = AddItem(2, 4);
        SaveLedger(new[] { Entry(item) });
        var writer = Writer();
        _failReads = true;
        await writer.PruneAsync(CancellationToken.None);
        Assert.Equal(1, writer.LedgerCount);
        _failReads = false;
        _failSaves = true;
        await writer.UndoAllAsync(CancellationToken.None);
        Assert.Equal(1, writer.LedgerCount);
        Assert.Equal((2, 4), (_data[item].AudioStreamIndex, _data[item].SubtitleStreamIndex));
        _failSaves = false;
        var reloaded = Writer();
        await reloaded.UndoAllAsync(CancellationToken.None);
        Assert.Equal((1, 3), (_data[item].AudioStreamIndex, _data[item].SubtitleStreamIndex));
        Assert.Equal(0, reloaded.LedgerCount);
    }

    [Fact]
    public async Task WritingOneFieldDoesNotTakeOwnershipOfAnExternalChangeToTheOther()
    {
        var item = AddItem(1, 3);
        var writer = Writer();
        await writer.WriteAsync(_user, _items[item], 2, null, CancellationToken.None);
        _data[item].SubtitleStreamIndex = 8;
        await writer.WriteAsync(_user, _items[item], 5, null, CancellationToken.None);
        await writer.UndoAllAsync(CancellationToken.None);
        Assert.Equal((1, 8), (_data[item].AudioStreamIndex, _data[item].SubtitleStreamIndex));
    }

    [Fact]
    public async Task FailedLaterWriteCanStillRestoreTheEarlierSuccessfulWrite()
    {
        var item = AddItem(1, 3);
        var writer = Writer();
        await writer.WriteAsync(_user, _items[item], 2, 4, CancellationToken.None);
        _failSaves = true;
        await writer.WriteAsync(_user, _items[item], 5, 6, CancellationToken.None);
        Assert.Equal((2, 4), (_data[item].AudioStreamIndex, _data[item].SubtitleStreamIndex));
        Assert.True(Assert.Single(ReadLedger().Entries).PendingWrite);
        _failSaves = false;
        var reloaded = Writer();
        await reloaded.PruneAsync(CancellationToken.None);
        Assert.Equal(1, reloaded.LedgerCount);
        await reloaded.UndoAllAsync(CancellationToken.None);
        Assert.Equal((1, 3), (_data[item].AudioStreamIndex, _data[item].SubtitleStreamIndex));
        Assert.Equal(0, reloaded.LedgerCount);
    }

    [Theory]
    [InlineData(2, 4)]
    [InlineData(5, 6)]
    public async Task InterruptedPreparedWriteRestoresEitherPossibleCommittedState(int audio, int subtitle)
    {
        var item = AddItem(audio, subtitle);
        var entry = Entry(item);
        entry.WrittenAudio = 5;
        entry.WrittenSubtitle = 6;
        entry.PendingWrite = true;
        entry.PendingPreviousAudio = 2;
        entry.PendingPreviousSubtitle = 4;
        entry.PendingAudioWasOurs = true;
        entry.PendingSubtitleWasOurs = true;
        SaveLedger(new[] { entry });
        var writer = Writer();
        await writer.PruneAsync(CancellationToken.None);
        Assert.Equal(1, writer.LedgerCount);
        await writer.UndoAllAsync(CancellationToken.None);
        Assert.Equal((1, 3), (_data[item].AudioStreamIndex, _data[item].SubtitleStreamIndex));
    }

    [Fact]
    public async Task UndoPersistsCompletedBatchesAndRetainsCancelledRemainder()
    {
        var entries = Enumerable.Range(0, CompatibilityWriter.BatchSize * 2 + 1).Select(_ => Entry(AddItem(2, 4))).ToArray();
        SaveLedger(entries);
        using var cancel = new CancellationTokenSource();
        _onSave = () => { if (_saves == CompatibilityWriter.BatchSize) { cancel.Cancel(); } };
        var writer = Writer();
        await writer.UndoAllAsync(cancel.Token);
        Assert.Equal(CompatibilityWriter.BatchSize, _saves);
        Assert.Equal(entries.Length - CompatibilityWriter.BatchSize, writer.LedgerCount);
        Assert.Equal(writer.LedgerCount, ReadLedger().Entries.Count);
        _onSave = null;
        await writer.UndoAllAsync(CancellationToken.None);
        Assert.Equal(0, writer.LedgerCount);
        Assert.All(_data.Values, d => Assert.Equal((1, 3), (d.AudioStreamIndex, d.SubtitleStreamIndex)));
    }

    [Fact]
    public async Task ConcurrentWritesRunBetweenUndoBatchesAndKeepTheirRecoveryRecords()
    {
        var entries = Enumerable.Range(0, CompatibilityWriter.BatchSize * 2 + 1).Select(_ => Entry(AddItem(2, 4))).ToArray();
        SaveLedger(entries);
        var writer = Writer();
        // Initialize the prune schedule so the concurrent write is a single short write.
        await writer.WriteAsync(_user, _items[entries[0].ItemId], 2, 4, CancellationToken.None);
        var target = entries[CompatibilityWriter.BatchSize].ItemId;
        Task? concurrentWrite = null;
        var beforeConcurrentWrite = -1;
        _onSave = () =>
        {
            if (_saves == CompatibilityWriter.BatchSize)
            {
                concurrentWrite = writer.WriteAsync(_user, _items[target], 5, 6, CancellationToken.None);
            }
            else if (beforeConcurrentWrite < 0 && _data[target].AudioStreamIndex == 5)
            {
                beforeConcurrentWrite = _saves - 1;
            }
        };

        var previousContext = SynchronizationContext.Current;
        var queue = new StepContext();
        Task undo;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queue);
            undo = writer.UndoAllAsync(CancellationToken.None);
            queue.Drain();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await undo;
        Assert.NotNull(concurrentWrite);
        await concurrentWrite!;
        Assert.Equal(CompatibilityWriter.BatchSize, beforeConcurrentWrite);
        Assert.Equal(1, writer.LedgerCount);
        Assert.Equal((5, 6), (_data[target].AudioStreamIndex, _data[target].SubtitleStreamIndex));
        _onSave = null;
        await writer.UndoAllAsync(CancellationToken.None);
        Assert.Equal((1, 3), (_data[target].AudioStreamIndex, _data[target].SubtitleStreamIndex));
        Assert.Equal(0, writer.LedgerCount);
    }

    private sealed class StepContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));
        public void Drain()
        {
            while (_queue.TryDequeue(out var work))
            {
                work.Callback(work.State);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RememberFlagsLedgerUpdatesPreserveConcurrentPersonChanges(bool disablePerson)
    {
        var inner = new FakePersonStore();
        inner.Save(new PersonSettings { UserId = _user.Id, HandleNextEpisode = true });
        var updates = 0;
        var store = CallProxy.Create<IPersonStore>((method, args) =>
        {
            if (method.Name == nameof(IPersonStore.TryGet))
            {
                return inner.TryGet((Guid)args[0]!);
            }

            if (method.Name == nameof(IPersonStore.Update))
            {
                updates++;
                // Another writer saves right before the atomic change acquires the document.
                inner.Update(_user.Id, current => { var next = current!.Clone(); next.Enabled = !disablePerson; next.Everyday.SubtitleLanguages = new[] { "ja" }; return next; });
                return inner.Update((Guid)args[0]!, (Func<PersonSettings?, PersonSettings?>)args[1]!);
            }

            return null;
        }).Proxy;
        var users = CallProxy.Create<IUserManager>((method, args) =>
        {
            if (method.Name == nameof(IUserManager.UpdateConfigurationAsync))
            {
                var config = (UserConfiguration)args[1]!;
                _user.RememberAudioSelections = config.RememberAudioSelections;
                _user.RememberSubtitleSelections = config.RememberSubtitleSelections;
                return Task.CompletedTask;
            }

            return null;
        }).Proxy;
        var flags = new RememberFlags(users, store, new ListActivityLog(), NullLogger<RememberFlags>.Instance);
        await flags.ApplyAsync(_user, CancellationToken.None);
        Assert.Equal(!disablePerson, inner.TryGet(_user.Id)!.Enabled);
        Assert.Equal(new[] { "ja" }, inner.TryGet(_user.Id)!.Everyday.SubtitleLanguages);
        Assert.Equal(!disablePerson, inner.TryGet(_user.Id)!.JellyfinChanges is not null);
        Assert.Equal(disablePerson, _user.RememberAudioSelections);
        await flags.RestoreAsync(_user, CancellationToken.None);
        Assert.True(_user.RememberAudioSelections);
        Assert.True(_user.RememberSubtitleSelections);
        Assert.Null(inner.TryGet(_user.Id)!.JellyfinChanges);
        Assert.Equal(!disablePerson, inner.TryGet(_user.Id)!.Enabled);
        Assert.Equal(new[] { "ja" }, inner.TryGet(_user.Id)!.Everyday.SubtitleLanguages);
        Assert.True(inner.TryGet(_user.Id)!.HandleNextEpisode);
        Assert.Equal(disablePerson ? 1 : 2, updates);
    }

    private Guid AddItem(int audio, int subtitle)
    {
        var item = new Movie { Id = Guid.NewGuid() };
        _items[item.Id] = item;
        _data[item.Id] = new UserItemData { Key = item.Id.ToString("N"), AudioStreamIndex = audio, SubtitleStreamIndex = subtitle };
        return item.Id;
    }

    private CompatEntry Entry(Guid itemId, bool stale = true) => new()
    {
        UserId = _user.Id, ItemId = itemId, PreviousAudio = 1, PreviousSubtitle = 3, WrittenAudio = 2, WrittenSubtitle = 4,
        WrittenAt = DateTime.UtcNow.AddDays(stale ? -90 : 0), FirstWrittenAt = DateTime.UtcNow.AddDays(-90),
    };

    private void SaveLedger(IEnumerable<CompatEntry> entries)
        => AtomicJsonFile.Write(Path.Combine(_folder, CompatibilityWriter.FileName), new CompatLedger { Entries = entries.ToList() }, BabelfinJson.Options);

    private CompatLedger ReadLedger()
    {
        AtomicJsonFile.Read<CompatLedger>(Path.Combine(_folder, CompatibilityWriter.FileName), BabelfinJson.Options, NullLogger.Instance, out var ledger);
        return ledger!;
    }

    private CompatibilityWriter Writer()
    {
        var users = CallProxy.Create<IUserManager>((method, args) => method.Name == nameof(IUserManager.GetUserById) && (Guid)args[0]! == _user.Id ? _user : null).Proxy;
        var library = CallProxy.Create<ILibraryManager>((method, args) => method.Name == nameof(ILibraryManager.GetItemById) && args[0] is Guid id && _items.TryGetValue(id, out var item) ? item : null).Proxy;
        var data = CallProxy.Create<IUserDataManager>((method, args) =>
        {
            if (method.Name == nameof(IUserDataManager.GetUserData))
            {
                if (_failReads) { throw new IOException("temporary read failure"); }
                return _data[((BaseItem)args[1]!).Id];
            }

            if (method.Name == nameof(IUserDataManager.SaveUserData))
            {
                if (_failSaves) { throw new IOException("database locked"); }
                _saves++;
                _onSave?.Invoke();
            }

            return null;
        }).Proxy;
        return new CompatibilityWriter(_folder, data, users, library, new ListActivityLog(), NullLogger<CompatibilityWriter>.Instance);
    }
}
