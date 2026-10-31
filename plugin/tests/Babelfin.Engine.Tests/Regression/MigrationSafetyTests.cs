using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

public sealed class MigrationSafetyTests
{
    private static ItemOverride Row(DateTime saved, DateTime memory) => new()
    {
        ItemId = Guid.NewGuid().ToString("N"), Scope = OverrideScope.Series, Name = "A show", UpdatedAt = saved,
        Settings = new() { AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "ja") }, WhenAudioNotUnderstood = SubtitleWant.Full },
        Manual = new() { Audio = new() { Language = "en" }, SubtitlesOff = true, At = memory },
    };

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void MigrationKeepsNewerChoice_FieldByField(int savedOffset, bool keepMemory)
    {
        var now = DateTime.UtcNow;
        var row = Row(now.AddMinutes(savedOffset), now);
        var doc = new PersonSettings { SchemaVersion = 1, Overrides = new() { row } };
        PersonStore.Migrate(doc);
        Assert.Equal(keepMemory, row.Manual is not null);
        Assert.Equal(keepMemory ? 0 : 1, doc.MigrationNotes.Count);
        Assert.Equal("ja", row.Settings.AudioChain![0].Language);
    }

    [Fact]
    public void MigrationChangingOnlySubtitlesRetainsAudioMemory()
    {
        var row = Row(DateTime.UtcNow, DateTime.UtcNow.AddMinutes(-1));
        row.Settings.AudioChain = null;
        var doc = new PersonSettings { SchemaVersion = 1, Overrides = new() { row } };
        PersonStore.Migrate(doc);
        Assert.NotNull(row.Manual!.Audio);
        Assert.False(row.Manual.SubtitlesOff);
    }

    [Fact]
    public void HiddenChoicesAreArchived_GenresActivateOnce_AndVisibleOlderOverridesStay()
    {
        var genre = new SmartRule { Name = "Animation", Conditions = new() { new() { Kind = ConditionKind.Genre, Values = new() { "Animation" } } }, Settings = new() { WhenAudioNotUnderstood = SubtitleWant.Full } };
        var other = genre.Clone(); other.Conditions[0].Kind = ConditionKind.Tag;
        var collection = new ItemOverride { ItemId = Guid.NewGuid().ToString("N"), Scope = OverrideScope.Collection };
        var season = new ItemOverride { ItemId = Guid.NewGuid().ToString("N"), Scope = OverrideScope.Season };
        var doc = new PersonSettings { SchemaVersion = 1, SmartRules = new() { genre, other }, Overrides = new() { collection, season },
            CollectionOrder = new() { collection.ItemId }, Locks = new() { AvoidCommentary = false }, UseEverydayEverywhere = true,
            Everyday = new() { OriginalSource = OriginalLanguageSource.FlagThenMetadata } };
        PersonStore.Migrate(doc);
        Assert.Equal(3, doc.SchemaVersion);
        var active = Assert.Single(doc.SmartRules);
        Assert.Equal("Animation", active.Name);
        Assert.Equal(SubtitleWant.Full, active.Settings.WhenAudioNotUnderstood);
        Assert.False(string.IsNullOrWhiteSpace(active.Id));
        var archived = Assert.Single(doc.Retired.GenreRules);
        Assert.NotSame(archived.Settings, active.Settings);
        Assert.Equal(string.Empty, archived.Id);
        Assert.Single(doc.Retired.Rules);
        Assert.Single(doc.Retired.Collections);
        Assert.Single(doc.Overrides);
        Assert.Same(season, doc.Overrides[0]);
        Assert.True(doc.Locks.IsEmpty);
        Assert.False(doc.Retired.Locks.AvoidCommentary);
        Assert.Null(doc.Everyday.OriginalSource);
        Assert.True(doc.UseEverydayEverywhere);
        var count = doc.MigrationNotes.Count;
        var archive = JsonSerializer.Serialize(doc.Retired);
        var activeId = active.Id;
        PersonStore.Migrate(doc);
        Assert.Equal(count, doc.MigrationNotes.Count);
        Assert.Equal(activeId, Assert.Single(doc.SmartRules).Id);
        Assert.Equal(archive, JsonSerializer.Serialize(doc.Retired));
        doc.SmartRules.Clear();
        PersonStore.Migrate(doc);
        Assert.Empty(doc.SmartRules);
        Assert.Equal(archive, JsonSerializer.Serialize(doc.Retired));
    }

    [Fact]
    public void UnchangedPeopleGetNoMigrationMessage()
    {
        var doc = new PersonSettings { SchemaVersion = 1, UseEverydayEverywhere = true };
        PersonStore.Migrate(doc);
        Assert.Empty(doc.MigrationNotes);
        Assert.True(doc.UseEverydayEverywhere);
    }

    [Fact]
    public void BackupContainsOriginalBytesBeforeSchema3FileMigration()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bf-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PersonStore(folder, NullLogger<PersonStore>.Instance);
            var id = Guid.NewGuid();
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
            var json = JsonSerializer.Serialize(new PersonSettings { SchemaVersion = 1, UserId = id, Locks = new() { AvoidCommentary = false } });
            File.WriteAllText(store.FilePath(id), json);
            var doc = store.TryGet(id)!;
            Assert.True(doc.Locks.IsEmpty);
            Assert.Equal(json, File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.bak"))));
            Assert.Equal(3, new PersonStore(folder, NullLogger<PersonStore>.Instance).TryGet(id)!.SchemaVersion);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.bak"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void EveryStoredPersonFieldHasAnExplicitClassification()
    {
        var visible = new[] { "Enabled", "PerLibrary", "HandleNextEpisode", "Everyday", "Libraries", "Overrides", "BurnedIn", "SmartRules" };
        var migrated = new[] { "CollectionOrder", "Locks" };
        var inactive = new[] { "UseEverydayEverywhere", "Retired" };
        var operational = new[] { "SchemaVersion", "UserId", "UserName", "JellyfinChanges", "Seed", "UpdatedAt", "LastChangedAt", "LastChangedBy", "LastChangedByName", "MigrationNotes" };
        Assert.Equal(typeof(PersonSettings).GetProperties().Select(p => p.Name).OrderBy(n => n), visible.Concat(migrated).Concat(inactive).Concat(operational).OrderBy(n => n));
    }

    [Fact]
    public void NewerConfigurationIsNeverNormalizedOrDowngraded()
    {
        var cfg = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion + 10, ProbeAudioLanguage = "future", LastUpgradeNote = "keep", AppModes = new() { new() { Client = "same" }, new() { Client = "same" } } };
        Assert.False(cfg.Upgrade());
        Assert.Equal(PluginConfiguration.CurrentVersion + 10, cfg.ConfigVersion);
        Assert.Equal("future", cfg.ProbeAudioLanguage);
        Assert.Equal("keep", cfg.LastUpgradeNote);
        Assert.Equal(2, cfg.AppModes.Count);
    }

    [Fact]
    public void ServerHiddenFieldsArePreservedAndDoNotAct()
    {
        var cfg = new PluginConfiguration { ConfigVersion = 6, DryRun = true, Everyday = new() { Format = FormatPreference.TextOnly }, SmartRules = new() { new() { Name = "Hidden" } } };
        cfg.Starter.Enabled = true;
        Assert.True(cfg.Upgrade());
        Assert.False(cfg.DryRun);
        Assert.False(cfg.Starter.Enabled);
        Assert.Null(cfg.Everyday.Format);
        Assert.Single(cfg.Retired.Rules);
        Assert.NotEmpty(cfg.LastUpgradeNote);
        var note = cfg.LastUpgradeNote;
        Assert.False(cfg.Upgrade());
        Assert.Equal(note, cfg.LastUpgradeNote);
    }
    [Fact]
    public void NullArchiveListsCannotBreakTheSettingsPageOrNextSave()
    {
        var doc = JsonSerializer.Deserialize<PersonSettings>("{\"schemaVersion\":1,\"retired\":{\"rules\":null,\"genreRules\":[null,{\"conditions\":null,\"settings\":null}],\"collections\":null,\"layers\":[null,{\"settings\":null}],\"locks\":null}}", BabelfinJson.Options)!;
        PersonStore.Migrate(doc);
        var copied = doc.Clone();
        Assert.Empty(copied.Retired.Rules);
        Assert.Single(copied.Retired.GenreRules);
        Assert.Single(copied.Retired.Layers);
        Assert.True(copied.Retired.Layers[0].Settings.IsEmpty);
        Assert.Empty(copied.MigrationNotes);
    }

}
