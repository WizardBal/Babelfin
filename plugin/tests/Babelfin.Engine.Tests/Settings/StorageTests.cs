using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>per-person JSON files with atomic writes, a cache, and corrupt files kept.</summary>
public sealed class StorageTests : IDisposable
{
    private readonly string _folder;

    public StorageTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "babelfin-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    private string UsersFolder => Path.Combine(_folder, "users");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind doesn't matter.
        }
    }

    // A saved document reads back the same from a fresh store (a server restart).
    [Fact]
    public void Save_ThenNewStore_RoundTrips()
    {
        var id = Guid.NewGuid();
        var doc = Sample(id);
        NewStore().Save(doc);

        var loaded = NewStore().TryGet(id);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded!.UserId);
        Assert.Equal("Bal", loaded.UserName);
        Assert.True(loaded.UseEverydayEverywhere);
        Assert.Equal(new[] { "en" }, loaded.Everyday.Understood);
        Assert.Equal(ForeignTitlesRule.OriginalWithSubtitles, loaded.Everyday.ForeignTitles);
        Assert.Equal(SubtitleWant.Full, loaded.Everyday.WhenAudioNotUnderstood);
        Assert.Null(loaded.Everyday.Reads);
        Assert.Single(loaded.Libraries);
        Assert.Equal(new[] { "ja" }, loaded.Libraries[0].Settings.Understood);
        Assert.Single(loaded.Overrides);
        Assert.Equal(OverrideScope.Series, loaded.Overrides[0].Scope);
        Assert.Equal("ja", loaded.Overrides[0].Manual!.Audio!.Language);
        Assert.Single(loaded.BurnedIn);
        Assert.Equal(new List<string> { "en" }, loaded.BurnedIn[0].Languages);
        Assert.Equal(true, loaded.Locks.AvoidCommentary);
        Assert.Equal("server", loaded.Seed!.Assumed.Single().From);
        Assert.Equal(PersonSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    // GetOrCreate builds and writes the document once; later calls (and a restarted store) read it back.
    [Fact]
    public void GetOrCreate_CreatesAndSavesOnce()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        var calls = 0;
        var changed = 0;
        store.Changed += (_, _) => changed++;

        var first = store.GetOrCreate(id, () => { calls++; return Sample(Guid.Empty); });
        var second = store.GetOrCreate(id, () => { calls++; return Sample(Guid.Empty); });

        Assert.Equal(1, calls);
        Assert.Same(first, second);
        Assert.Equal(id, first.UserId);
        Assert.Equal(1, changed);
        Assert.True(File.Exists(store.FilePath(id)));

        var restarted = NewStore().GetOrCreate(id, () => { calls++; return new PersonSettings(); });
        Assert.Equal(1, calls);
        Assert.Equal("Bal", restarted.UserName);
    }

    // Save keeps a copy: later changes to the caller's object don't leak into the cache. It sets UpdatedAt and raises Changed.
    [Fact]
    public void Save_StoresClone_SetsUpdatedAt_RaisesChanged()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        var events = new List<Guid?>();
        store.Changed += (_, e) => events.Add(e.UserId);
        var doc = Sample(id);
        doc.UpdatedAt = DateTime.MinValue;
        var before = DateTime.UtcNow.AddSeconds(-1);

        store.Save(doc);
        doc.UserName = "Changed afterwards";
        doc.Everyday.Understood = new[] { "fr" };

        var cached = store.TryGet(id)!;
        Assert.NotSame(doc, cached);
        Assert.Equal("Bal", cached.UserName);
        Assert.Equal(new[] { "en" }, cached.Everyday.Understood);
        Assert.True(cached.UpdatedAt >= before);
        Assert.Equal(new Guid?[] { id }, events);
    }

    // A document without a user id can't name its file.
    [Fact]
    public void UpdateExplicit_WithoutUserId_Throws()
    {
        Assert.Throws<ArgumentException>(() => NewStore().UpdateExplicit(Guid.Empty, _ => new PersonSettings()));
    }

    // Delete drops the person from the store but keeps the file under another name (nothing is destroyed).
    [Fact]
    public void Delete_RemovesFromStore_KeepsRenamedFile()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        store.Save(Sample(id));
        var events = 0;
        store.Changed += (_, _) => events++;

        Assert.True(store.Delete(id));

        Assert.Null(store.TryGet(id));
        Assert.Null(NewStore().TryGet(id));
        Assert.False(File.Exists(store.FilePath(id)));
        Assert.Single(Directory.GetFiles(UsersFolder, id.ToString("N") + ".json.deleted-*"));
        Assert.DoesNotContain(id, store.UserIds());
        Assert.Equal(1, events);
        Assert.False(store.Delete(id));
    }

    // UserIds lists every person with a file, and nothing else in the folder.
    [Fact]
    public void UserIds_ListsPeopleWithFiles()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var store = NewStore();
        store.Save(Sample(a));
        store.GetOrCreate(b, () => Sample(Guid.Empty));
        File.WriteAllText(Path.Combine(UsersFolder, "notes.json"), "{}");
        File.WriteAllText(Path.Combine(UsersFolder, Guid.NewGuid().ToString("N") + ".json.corrupt-1"), "x");

        var fresh = NewStore().UserIds();

        Assert.Equal(new[] { a, b }.OrderBy(g => g), fresh);
        Assert.Empty(NewStore(Path.Combine(_folder, "empty")).UserIds());
    }

    // A file that doesn't parse is renamed *.corrupt-<ticks>, never deleted, and the person counts as having no settings.
    [Fact]
    public void CorruptFile_RenamedAndTreatedAsMissing()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        Directory.CreateDirectory(UsersFolder);
        const string Broken = "{ \"everyday\": { \"understood\": [ \"en\" ";
        File.WriteAllText(store.FilePath(id), Broken);

        Assert.Null(store.TryGet(id));

        Assert.False(File.Exists(store.FilePath(id)));
        var kept = Assert.Single(Directory.GetFiles(UsersFolder, id.ToString("N") + ".json.corrupt-*"));
        Assert.Equal(Broken, File.ReadAllText(kept));

        var created = store.GetOrCreate(id, () => Sample(Guid.Empty));
        Assert.Equal(id, created.UserId);
        Assert.True(File.Exists(store.FilePath(id)));
        Assert.Equal(Broken, File.ReadAllText(kept));
    }

    // A JSON null document counts as corrupt too.
    [Fact]
    public void NullDocument_RenamedAsCorrupt()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        Directory.CreateDirectory(UsersFolder);
        File.WriteAllText(store.FilePath(id), "null");

        Assert.Null(store.TryGet(id));
        Assert.Single(Directory.GetFiles(UsersFolder, "*.corrupt-*"));
    }

    // Writes go through a temp file that never stays behind; temp files left by a crash go on the next write of that file only.
    [Fact]
    public void Save_CleansTempFilesOfThatFileOnly()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var store = NewStore();
        Directory.CreateDirectory(UsersFolder);
        var stale = store.FilePath(id) + ".abc123.tmp";
        var otherStale = store.FilePath(other) + ".def456.tmp";
        File.WriteAllText(stale, "half a file");
        File.WriteAllText(otherStale, "half a file");

        store.Save(Sample(id));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(otherStale));
        Assert.Empty(Directory.GetFiles(UsersFolder, id.ToString("N") + ".json.*.tmp"));
        Assert.True(File.Exists(store.FilePath(id)));
    }

    // Every file says which schema it was written with (camelCase, as BabelfinJson writes).
    [Fact]
    public void Save_WritesSchemaVersion()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        var doc = Sample(id);
        doc.SchemaVersion = 0;
        store.Save(doc);

        using var json = JsonDocument.Parse(File.ReadAllText(store.FilePath(id)));
        Assert.Equal(PersonSettings.CurrentSchemaVersion, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Bal", json.RootElement.GetProperty("userName").GetString());
    }

    // Properties a newer Babelfin (or a person) added are ignored; null lists are repaired; the id follows the file name.
    [Fact]
    public void UnknownProperties_Ignored_NullListsRepaired()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        Directory.CreateDirectory(UsersFolder);
        File.WriteAllText(store.FilePath(id), """
            {
              "schemaVersion": 1,
              "userId": "00000000-0000-0000-0000-000000000001",
              "userName": "Bal",
              "futureSetting": { "deep": [1, 2, 3] },
              "everyday": { "understood": ["ja", null], "somethingNew": true, "foreignTitles": "OriginalWithSubtitles" },
              "libraries": null,
              "overrides": [ null, { "itemId": "abc", "settings": null }, { "itemId": "a0000000-0000-0000-0000-000000000001", "settings": null } ],
              "burnedIn": [ { "itemId": "abc", "languages": null } ],
              // comments and trailing commas are tolerated
              "smartRules": [],
            }
            """);

        var loaded = store.TryGet(id);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded!.UserId);
        Assert.Equal(new[] { "ja" }, loaded.Everyday.Understood);
        Assert.Equal(ForeignTitlesRule.OriginalWithSubtitles, loaded.Everyday.ForeignTitles);
        Assert.Empty(loaded.Libraries);
        Assert.NotNull(Assert.Single(loaded.Overrides).Settings);
        Assert.Empty(loaded.BurnedIn.Single().Languages);
        Assert.NotNull(loaded.Locks);
        Assert.Empty(Directory.GetFiles(UsersFolder, "*.corrupt-*"));
    }

    // A file from a newer Babelfin is read, and copied aside before the first save over it.
    [Fact]
    public void NewerSchema_BackedUpBeforeOverwrite()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        Directory.CreateDirectory(UsersFolder);
        File.WriteAllText(store.FilePath(id), "{ \"schemaVersion\": 99, \"userName\": \"Future\" }");

        var loaded = store.TryGet(id)!;
        Assert.Equal("Future", loaded.UserName);

        var copy = loaded.Clone();
        copy.UserName = "Now";
        Assert.True(store.UpdateExplicit(id, _ => copy));

        var backup = Assert.Single(Directory.GetFiles(UsersFolder, id.ToString("N") + ".json.schema-99-*.bak"));
        Assert.Contains("Future", File.ReadAllText(backup), StringComparison.Ordinal);
        Assert.Equal("Now", NewStore().TryGet(id)!.UserName);
    }

    // A missing file is "no settings", and the store creates its folders on the first write.
    [Fact]
    public void MissingFile_IsNoSettings()
    {
        var store = NewStore(Path.Combine(_folder, "not-yet"));

        Assert.Null(store.TryGet(Guid.NewGuid()));
        Assert.Empty(store.UserIds());
        Assert.Equal(JsonReadOutcome.Missing, AtomicJsonFile.Read<PersonSettings>(Path.Combine(_folder, "nope.json"), BabelfinJson.Options, NullLogger.Instance, out var value));
        Assert.Null(value);
    }

    // The atomic writer replaces an existing file whole and leaves no temp file.
    [Fact]
    public void AtomicWrite_ReplacesFile()
    {
        var path = Path.Combine(_folder, "sub", "doc.json");
        AtomicJsonFile.Write(path, new PersonSettings { UserName = "one" }, BabelfinJson.Options);
        AtomicJsonFile.Write(path, new PersonSettings { UserName = "two" }, BabelfinJson.Options);

        Assert.Equal(JsonReadOutcome.Loaded, AtomicJsonFile.Read<PersonSettings>(path, BabelfinJson.Options, NullLogger.Instance, out var value));
        Assert.Equal("two", value!.UserName);
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, "sub")));
    }

    // A backup never replaces a file: the same name again gets -2, then -3; every file keeps its own content and no temp file is left.
    [Fact]
    public void WriteNew_NeverReplacesAFile()
    {
        var folder = Path.Combine(_folder, "backups");

        var first = AtomicJsonFile.WriteNew(folder, "backup", new PersonSettings { UserName = "one" }, BabelfinJson.Options);
        var second = AtomicJsonFile.WriteNew(folder, "backup", new PersonSettings { UserName = "two" }, BabelfinJson.Options);
        var third = AtomicJsonFile.WriteNew(folder, "backup", new PersonSettings { UserName = "three" }, BabelfinJson.Options);

        Assert.Equal(new[] { "backup.json", "backup-2.json", "backup-3.json" }, new[] { Path.GetFileName(first), Path.GetFileName(second), Path.GetFileName(third) });
        Assert.Equal(3, Directory.GetFiles(folder).Length);
        foreach (var (path, name) in new[] { (first, "one"), (second, "two"), (third, "three") })
        {
            Assert.Equal(JsonReadOutcome.Loaded, AtomicJsonFile.Read<PersonSettings>(path, BabelfinJson.Options, NullLogger.Instance, out var value));
            Assert.Equal(name, value!.UserName);
        }
    }

    // Overrides with no usable item id can never apply and a page has no id to remove them by: loading, saving and creating a document drops them.
    [Fact]
    public void Migrate_DropsOverridesWithNoUsableItemId_AndKeepsTheOthersInOrder()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var doc = new PersonSettings
        {
            Overrides = new List<ItemOverride>
            {
                new() { ItemId = first.ToString("N") },
                new() { ItemId = string.Empty },
                null!,
                new() { ItemId = null! },
                new() { ItemId = "abc" },
                new() { ItemId = Guid.Empty.ToString("N") },
                new() { ItemId = "  " },
                new() { ItemId = second.ToString("D").ToUpperInvariant(), Settings = null! },
            },
        };

        var migrated = PersonStore.Migrate(doc);

        Assert.Same(doc, migrated);
        Assert.Equal(new[] { first.ToString("N"), second.ToString("D").ToUpperInvariant() }, migrated.Overrides.Select(o => o.ItemId));
        Assert.All(migrated.Overrides, o => Assert.NotNull(o.Settings));
    }

    [Fact]
    public void Load_DropsOverridesWithNoUsableItemId_AndSaysSoOnceForTheDocument()
    {
        var id = Guid.NewGuid();
        var good = Guid.NewGuid().ToString("N");
        var log = new LogLines();
        var store = NewStore(log: log);
        Directory.CreateDirectory(UsersFolder);
        File.WriteAllText(store.FilePath(id), "{ \"overrides\": [ { \"itemId\": \"\" }, { \"itemId\": \"" + good + "\" }, { \"settings\": { } }, { \"itemId\": \"junk\" } ] }");

        var loaded = store.TryGet(id)!;
        store.TryGet(id);
        store.GetOrCreate(id, () => new PersonSettings());

        Assert.Equal(new[] { good }, loaded.Overrides.Select(o => o.ItemId));
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("3 override(s)", line.Message, StringComparison.Ordinal);
        Assert.Contains(id.ToString(), line.Message, StringComparison.OrdinalIgnoreCase);

        // Saved again, the document has nothing left to drop, so it says nothing more.
        store.Save(loaded);
        Assert.Single(log.Lines);
        Assert.Equal(new[] { good }, NewStore().TryGet(id)!.Overrides.Select(o => o.ItemId));
    }

    [Fact]
    public void Save_DropsOverridesWithNoUsableItemId()
    {
        var id = Guid.NewGuid();
        var log = new LogLines();
        var store = NewStore(log: log);
        var doc = Sample(id);
        doc.Overrides.Add(new ItemOverride { ItemId = string.Empty, Settings = new SettingsLayer { Format = FormatPreference.TextOnly } });

        store.Save(doc);

        Assert.Single(store.TryGet(id)!.Overrides);
        Assert.Single(NewStore().TryGet(id)!.Overrides);
        Assert.Single(log.Lines);
        Assert.Contains("1 override(s)", log.Lines[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetOrCreate_DropsOverridesWithNoUsableItemId()
    {
        var id = Guid.NewGuid();
        var created = NewStore().GetOrCreate(id, () =>
        {
            var doc = Sample(Guid.Empty);
            doc.Overrides.Add(new ItemOverride { ItemId = "x" });
            return doc;
        });

        Assert.Single(created.Overrides);
    }

    // Update: read, change and write as one step.
    [Fact]
    public void Update_ChangesTheStoredDocument_SavesIt_RaisesChangedOnce()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        store.Save(Sample(id));
        var events = new List<Guid?>();
        store.Changed += (_, e) => events.Add(e.UserId);
        PersonSettings? seen = null;
        var added = Guid.NewGuid().ToString("N");

        var saved = store.Update(id, current =>
        {
            seen = current;
            var copy = current!.Clone();
            copy.Overrides.Add(new ItemOverride { ItemId = added, Scope = OverrideScope.Title, Manual = new ManualMemory { SubtitlesOff = true } });
            return copy;
        });

        Assert.True(saved);
        Assert.Single(seen!.Overrides);
        Assert.Equal(2, store.TryGet(id)!.Overrides.Count);
        Assert.Equal(added, NewStore().TryGet(id)!.Overrides[1].ItemId);
        Assert.Equal(new Guid?[] { id }, events);
        Assert.True(store.TryGet(id)!.UpdatedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Update_HandsOverTheCachedDocument_NotACopy()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        store.Save(Sample(id));
        var cached = store.TryGet(id);

        store.Update(id, current =>
        {
            Assert.Same(cached, current);
            return null;
        });
    }

    [Fact]
    public void Update_ForAPersonWithNoFile_HandsOverNull_AndWritesNothingWhenTheChangeAnswersNull()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        var events = 0;
        store.Changed += (_, _) => events++;
        var calls = 0;

        var saved = store.Update(id, current =>
        {
            calls++;
            Assert.Null(current);
            return null;
        });

        Assert.False(saved);
        Assert.Equal(1, calls);
        Assert.Equal(0, events);
        Assert.False(File.Exists(store.FilePath(id)));
        Assert.Null(store.TryGet(id));
    }

    [Fact]
    public void Update_CanCreateAPersonsFile()
    {
        var id = Guid.NewGuid();
        var store = NewStore();

        Assert.True(store.Update(id, _ => Sample(id)));

        Assert.True(File.Exists(store.FilePath(id)));
        Assert.Equal("Bal", NewStore().TryGet(id)!.UserName);
    }

    [Fact]
    public void Update_ReturningNull_LeavesTheFileAsItWas()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        store.Save(Sample(id));
        var before = File.ReadAllText(store.FilePath(id));
        var events = 0;
        store.Changed += (_, _) => events++;

        Assert.False(store.Update(id, _ => null));

        Assert.Equal(before, File.ReadAllText(store.FilePath(id)));
        Assert.Equal(0, events);
    }

    [Fact]
    public void Update_ADocumentOfAnotherPerson_IsRefused_AndNothingIsWritten()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var store = NewStore();
        store.Save(Sample(id));

        Assert.Throws<ArgumentException>(() => store.Update(id, _ => Sample(other)));
        Assert.Throws<ArgumentException>(() => store.Update(Guid.Empty, _ => Sample(id)));

        Assert.False(File.Exists(store.FilePath(other)));
        Assert.Equal("Bal", store.TryGet(id)!.UserName);
    }

    // A change that throws saves nothing, the exception reaches the caller, and the store stays usable (the lock is released).
    [Fact]
    public async Task Update_AChangeThatThrows_SavesNothing_AndReleasesTheLock()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        store.Save(Sample(id));
        var before = File.ReadAllText(store.FilePath(id));
        var events = 0;
        store.Changed += (_, _) => events++;

        var thrown = Assert.Throws<InvalidOperationException>(() => store.Update(id, current =>
        {
            var copy = current!.Clone();
            copy.UserName = "Half done";
            throw new InvalidOperationException("The change failed");
        }));

        Assert.Equal("The change failed", thrown.Message);
        Assert.Equal(before, File.ReadAllText(store.FilePath(id)));
        Assert.Equal("Bal", store.TryGet(id)!.UserName);
        Assert.Equal(0, events);
        var later = Sample(id);
        later.UserName = "Later";
        await Task.Run(() => store.Save(later)).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Run(() => store.Update(id, c => c!.Clone())).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Later", NewStore().TryGet(id)!.UserName);
    }

    // The change runs inside the store's lock: a save from another thread waits until the update has written, so it can't fall between the read and the write.
    [Fact]
    public async Task Update_NoSaveFallsBetweenTheReadAndTheWrite()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        store.Save(Sample(id));
        var intruder = Sample(id);
        intruder.UserName = "Intruder";
        Task? save = null;
        var intruderFinishedInside = false;

        store.Update(id, current =>
        {
            save = Task.Run(() => store.Save(intruder));
            Thread.Sleep(300);
            intruderFinishedInside = save.IsCompleted;
            var copy = current!.Clone();
            copy.UserName = "Updater";
            return copy;
        });

        Assert.False(intruderFinishedInside);
        await save!.WaitAsync(TimeSpan.FromSeconds(10));
        // The update wrote first; the save came after it and wins, whole.
        Assert.Equal("Intruder", store.TryGet(id)!.UserName);
        Assert.Equal("Intruder", NewStore().TryGet(id)!.UserName);
    }

    // Changed is raised after the lock is released: a handler that needs the store from another thread must not wait for ever.
    [Fact]
    public void Update_RaisesChangedOutsideTheLock()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        store.Save(Sample(id));
        var handlerRan = false;
        var otherThreadSaved = false;
        store.Changed += (_, e) =>
        {
            if (e.UserId == id && !handlerRan)
            {
                handlerRan = true;
                var other = Task.Run(() => store.Save(Sample(Guid.NewGuid())));
                otherThreadSaved = SpinWait.SpinUntil(() => other.IsCompleted, TimeSpan.FromSeconds(10));
            }
        };

        Assert.True(store.Update(id, c => c!.Clone()));
        Assert.True(handlerRan);
        Assert.True(otherThreadSaved);
    }

    // Many threads adding to one person's overrides through Update: not one of the additions is lost (a read, change and Save of a copy would lose some).
    [Fact]
    public void Update_ManyWritersAtOnce_LoseNothing()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        store.Save(Sample(id));
        const int Writers = 8;
        const int Each = 25;
        var added = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, Writers, new ParallelOptions { MaxDegreeOfParallelism = Writers }, writer =>
        {
            for (var i = 0; i < Each; i++)
            {
                var itemId = Guid.NewGuid().ToString("N");
                Assert.True(store.Update(id, current =>
                {
                    var copy = current!.Clone();
                    copy.Overrides.Add(new ItemOverride { ItemId = itemId, Scope = OverrideScope.Title, Manual = new ManualMemory { SubtitlesOff = true } });
                    return copy;
                }));
                added.Add(itemId);
            }
        });

        var expected = added.ToHashSet();
        Assert.Equal(Writers * Each, expected.Count);
        Assert.Equal(Writers * Each + 1, store.TryGet(id)!.Overrides.Count);
        Assert.Equal(Writers * Each + 1, NewStore().TryGet(id)!.Overrides.Count);
        Assert.True(expected.IsSubsetOf(NewStore().TryGet(id)!.Overrides.Select(o => o.ItemId)));
    }

    // "Remove all" and recorded changes, both through Update, meeting at random: nothing throws, and the cache and the file end up the same.
    [Fact]
    public async Task Update_RemoveAllAndRecordedChangesMeeting_LeaveTheCacheAndTheFileAlike()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        store.Save(Sample(id));
        var recorder = Task.Run(() =>
        {
            for (var i = 0; i < 60; i++)
            {
                store.Update(id, current =>
                {
                    var copy = current!.Clone();
                    copy.Overrides.Add(new ItemOverride { ItemId = Guid.NewGuid().ToString("N"), Manual = new ManualMemory { SubtitlesOff = true } });
                    return copy;
                });
            }
        });
        var remover = Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                store.Update(id, current => current!.Overrides.Count == 0 ? null : WithoutOverrides(current));
            }
        });

        await Task.WhenAll(recorder, remover).WaitAsync(TimeSpan.FromSeconds(60));

        var onDisk = NewStore().TryGet(id)!;
        Assert.Equal(store.TryGet(id)!.Overrides.Select(o => o.ItemId), onDisk.Overrides.Select(o => o.ItemId));
        Assert.Equal("Bal", onDisk.UserName);
    }

    private static PersonSettings WithoutOverrides(PersonSettings stored)
    {
        var copy = stored.Clone();
        copy.Overrides.Clear();
        return copy;
    }

    private PersonStore NewStore(string? folder = null, ILogger<PersonStore>? log = null) => new(folder ?? _folder, log ?? NullLogger<PersonStore>.Instance);

    /// <summary>Keeps what the store logs.</summary>
    private sealed class LogLines : ILogger<PersonStore>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static PersonSettings Sample(Guid id) => new()
    {
        UserId = id,
        UserName = "Bal",
        UseEverydayEverywhere = true,
        Seed = new SeedRecord
        {
            SeededAt = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc),
            JellyfinSubtitleLanguage = "eng",
            Assumed = new List<AssumedLanguage> { new() { Field = SettingsFields.Understood, Language = "en", From = "server" } },
        },
        Everyday = new SettingsLayer
        {
            Understood = new[] { "en" },
            ForeignTitles = ForeignTitlesRule.OriginalWithSubtitles,
            WhenAudioNotUnderstood = SubtitleWant.Full,
        },
        Libraries = new List<LibraryLayer>
        {
            new() { LibraryId = Guid.NewGuid().ToString("N"), Name = "Anime", Settings = new SettingsLayer { Understood = new[] { "ja" } } },
        },
        Overrides = new List<ItemOverride>
        {
            new()
            {
                ItemId = Guid.NewGuid().ToString("N"),
                Scope = OverrideScope.Series,
                Name = "Riverglass",
                Manual = new ManualMemory { Audio = new RememberedTrack { Language = "ja", Kind = "normal" }, At = DateTime.UtcNow },
            },
        },
        BurnedIn = new List<BurnedInFlag> { new() { ItemId = Guid.NewGuid().ToString("N"), Scope = OverrideScope.Title, Languages = new List<string> { "en" } } },
        Locks = new SettingsLayer { AvoidCommentary = true },
    };
}
