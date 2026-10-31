using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;
using C = Babelfin.Engine.Tests.Settings.Ctx;
using F = Babelfin.Engine.Tests.Selection.Fx;
using Ids = Babelfin.Engine.Tests.Settings.Ids;

namespace Babelfin.Engine.Tests.Regression;

public class PreferenceApiTests
{
    internal static SettingsLayer Audio(string language) => new() { AudioChain = C.Chain(C.Lang(language), C.Step(AudioStepKind.VideoDefault)) };
    internal static SmartRule Genre(string genre, SettingsLayer settings, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"), Name = genre, Settings = settings,
        Conditions = new() { new() { Kind = ConditionKind.Genre, Values = new() { genre } } },
    };

    [Fact]
    public void MyLanguagesOrderAndVideoDefaultFallbackAlreadyWork()
    {
        var profile = F.Starter("de", "en").Chain(F.Step(AudioStepKind.MyLanguages), F.Step(AudioStepKind.VideoDefault));
        var tracks = new[] { F.Audio(1, "jpn", isDefault: true), F.Audio(2, "eng"), F.Audio(3, "ger") };
        Assert.Equal(3, F.Selector.Decide(tracks, ItemFacts.None, profile).AudioIndex);
        Assert.Equal(2, F.Selector.Decide(tracks.Take(2).ToArray(), ItemFacts.None, profile).AudioIndex);
        Assert.True(F.Selector.Decide(tracks.Take(1).ToArray(), ItemFacts.None, profile).AudioLeftToVideo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersonGenreBeatsTheirActiveLibraryOrAllRow(bool perLibrary)
    {
        var person = C.Person(); person.PerLibrary = perLibrary;
        person.Everyday = Audio("en");
        person.Libraries.Add(C.MyLibrary(Ids.Shows, Audio("de")));
        person.SmartRules.Add(Genre("Anime", Audio("ja")));
        var resolved = C.Resolve(person, null, C.Episode(genres: new[] { "aNiMe" }));
        Assert.Equal("ja", resolved.Profile.AudioChain[0].Language);
        Assert.Contains("Anime", resolved.Sources[SettingsFields.AudioChain]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersonActiveRowBeatsAdminGenreFieldByField(bool perLibrary)
    {
        var person = C.Person(); person.PerLibrary = perLibrary;
        if (perLibrary) person.Libraries.Add(C.MyLibrary(Ids.Shows, Audio("en")));
        else person.Everyday.AudioChain = Audio("en").AudioChain;
        var admin = new PluginConfiguration();
        var settings = Audio("ja"); settings.WhenAudioNotUnderstood = SubtitleWant.Full;
        admin.SmartRules.Add(Genre("Anime", settings));
        var result = C.Resolve(person, admin, C.Episode(genres: new[] { "Anime" }));
        Assert.Equal("en", result.Profile.AudioChain[0].Language);
        Assert.Equal(SubtitleWant.Full, result.Profile.WhenAudioNotUnderstood);
    }

    [Fact]
    public void AdminGenreBeatsAdminLibrary_AndAppliesToAnUnconfiguredPerson()
    {
        var admin = new PluginConfiguration { PerLibraryDefaults = true };
        admin.Libraries.Add(C.TheirLibrary(Ids.Shows, Audio("de")));
        admin.SmartRules.Add(Genre("Anime", Audio("ja")));
        Assert.Equal("ja", C.Resolve(C.Person(), admin, C.Episode(genres: new[] { "Anime" })).Profile.AudioChain[0].Language);
        Assert.Equal("de", C.Resolve(C.Person(), admin, C.Episode(genres: new[] { "Drama" })).Profile.AudioChain[0].Language);
    }

    [Fact]
    public void OrderedGenresResolveFirstMatchingField_AndShowManualBeatsGenres()
    {
        var person = C.Person();
        person.SmartRules.Add(Genre("Animation", Audio("ja")));
        person.SmartRules.Add(Genre("Anime", new() { AudioChain = Audio("ko").AudioChain, WhenAudioNotUnderstood = SubtitleWant.Full }));
        var context = C.Episode(genres: new[] { "Anime", "Animation" });
        var first = C.Resolve(person, null, context);
        Assert.Equal("ja", first.Profile.AudioChain[0].Language);
        Assert.Equal(SubtitleWant.Full, first.Profile.WhenAudioNotUnderstood);
        person.Overrides.Add(C.Override(Ids.Series, OverrideScope.Series, Audio("de"), new ManualMemory { Audio = new() { Language = "en", Kind = "normal" } }));
        var overridden = C.Resolve(person, null, context);
        Assert.Equal("de", overridden.Profile.AudioChain[0].Language);
        Assert.Equal(1, F.Selector.Decide(new[] { F.Audio(1, "eng"), F.Audio(2, "ger"), F.Audio(3, "jpn") }, ItemFacts.None, overridden.Profile, manual: overridden.Manual).AudioIndex);
    }

    [Fact]
    public void InactiveTableModeAndGeneralRulesCannotOverrideGenreRows()
    {
        var person = C.Person(); person.PerLibrary = true;
        person.Everyday.AudioChain = Audio("ko").AudioChain;
        var nonGenre = Genre("Anime", Audio("fr")); nonGenre.Conditions[0].Kind = ConditionKind.Tag;
        var negated = Genre("Drama", Audio("de")); negated.Conditions[0].Negate = true;
        var disabled = Genre("Anime", Audio("it")); disabled.Enabled = false;
        person.SmartRules.AddRange(new[] { nonGenre, negated, disabled, Genre("Anime", Audio("ja")) });
        Assert.Equal("ja", C.Resolve(person, null, C.Episode(genres: new[] { "Anime" }, tags: new[] { "Anime" })).Profile.AudioChain[0].Language);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MigrationPromotesOnce_KeepsArchivesAndDoesNotResurrectRemovedRows(int schema)
    {
        var row = Genre("Anime", Audio("ja"), "genre-one");
        var person = new PersonSettings { SchemaVersion = schema };
        if (schema == 1) person.SmartRules.Add(row);
        else person.Retired.GenreRules.Add(row);
        PersonStore.Migrate(person);
        Assert.Equal(PersonSettings.CurrentSchemaVersion, person.SchemaVersion);
        Assert.Equal("genre-one", Assert.Single(person.SmartRules).Id);
        Assert.Equal("ja", Assert.Single(person.Retired.GenreRules).Settings.AudioChain![0].Language);
        var notes = person.MigrationNotes.ToArray();
        person.SmartRules.Clear();
        PersonStore.Migrate(person);
        Assert.Empty(person.SmartRules);
        Assert.Equal(notes, person.MigrationNotes);
        var roundtrip = JsonSerializer.Deserialize<PersonSettings>(JsonSerializer.Serialize(person))!;
        PersonStore.Migrate(roundtrip);
        Assert.Empty(roundtrip.SmartRules);
        Assert.Single(roundtrip.Retired.GenreRules);
    }

    [Fact]
    public void SingleGenreExtraFieldsAreArchivedButTableChoicesAreRecovered()
    {
        var original = Genre("Anime", Audio("ja")); original.Settings.AvoidCommentary = false;
        var person = new PersonSettings { SchemaVersion = 2 };
        person.Retired.Rules.Add(original);
        PersonStore.Migrate(person);
        var active = Assert.Single(person.SmartRules);
        Assert.Equal("ja", active.Settings.AudioChain![0].Language);
        Assert.Null(active.Settings.AvoidCommentary);
        Assert.False(Assert.Single(person.Retired.Rules).Settings.AvoidCommentary);
        Assert.NotSame(original.Settings.AudioChain, active.Settings.AudioChain);
    }

    [Fact]
    public void Config8PromotesArchivesAndKeepsRemovalsAcrossXmlAndImportClone()
    {
        var admin = new PluginConfiguration { ConfigVersion = 7 };
        admin.Retired.GenreRules.Add(Genre("Anime", Audio("ja"), "stable"));
        Assert.True(admin.Upgrade());
        Assert.Equal(PluginConfiguration.CurrentVersion, admin.ConfigVersion);
        Assert.Single(admin.SmartRules);
        Assert.Single(admin.Retired.GenreRules);
        var clone = ImportPlanner.CloneAdmin(admin);
        Assert.Equal("stable", Assert.Single(clone.SmartRules).Id);
        clone.SmartRules.Clear();
        Assert.False(clone.Upgrade());
        var xml = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter(); xml.Serialize(writer, clone);
        using var reader = new StringReader(writer.ToString());
        var loaded = (PluginConfiguration)xml.Deserialize(reader)!;
        Assert.False(loaded.Upgrade());
        Assert.Empty(loaded.SmartRules);
        Assert.Single(loaded.Retired.GenreRules);
    }

    [Fact]
    public void ImportRunsTheConfig8AndPerson3ConversionsAndPreservesBothArchives()
    {
        var admin = new PluginConfiguration { ConfigVersion = 7 };
        admin.Retired.GenreRules.Add(Genre("Anime", Audio("ja"), "admin-row"));
        var person = C.Person(); person.SchemaVersion = 2;
        person.Retired.GenreRules.Add(Genre("Animation", Audio("de"), "person-row"));
        var export = ImportPlanner.BuildExport(admin, new[] { person }, "test", DateTime.UtcNow);
        var store = new Babelfin.Engine.Tests.Settings.FakePersonStore();
        PluginConfiguration? applied = null;
        var result = ImportPlanner.Run(export, new(), new[] { new ImportTargetUser(person.UserId, person.UserName!) }, store,
            Jellyfin.Plugin.Babelfin.Engine.Languages.LanguageTable.Default, a => applied = a, false);
        Assert.True(result.Applied, string.Join("; ", result.Warnings));
        Assert.NotNull(applied);
        Assert.Equal(PluginConfiguration.CurrentVersion, applied.ConfigVersion);
        Assert.Equal("admin-row", Assert.Single(applied.SmartRules).Id);
        Assert.Single(applied.Retired.GenreRules);
        var imported = store.TryGet(person.UserId)!;
        Assert.Equal(PersonSettings.CurrentSchemaVersion, imported.SchemaVersion);
        Assert.Equal("person-row", Assert.Single(imported.SmartRules).Id);
        Assert.Single(imported.Retired.GenreRules);
    }
    [Fact]
    public void Schema2BackupHasOriginalBytesBeforeGenreActivation_OnlyOnce()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bf-preference-api-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = Guid.NewGuid();
            var store = new PersonStore(folder, NullLogger<PersonStore>.Instance);
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
            var doc = new PersonSettings { UserId = id, SchemaVersion = 2 };
            doc.Retired.GenreRules.Add(Genre("Anime", Audio("ja")));
            var original = JsonSerializer.Serialize(doc);
            File.WriteAllText(store.FilePath(id), original);
            Assert.Single(store.TryGet(id)!.SmartRules);
            var backups = Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.bak");
            Assert.Equal(original, File.ReadAllText(Assert.Single(backups)));
            Assert.Single(new PersonStore(folder, NullLogger<PersonStore>.Instance).TryGet(id)!.SmartRules);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.bak"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void MultiNameExpansionCapsAt100_PreservesOverflowAndOriginalBackupBytes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bf-config-overflow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = Guid.NewGuid();
            var person = new PersonSettings { UserId = id, SchemaVersion = 2 };
            var rule = Genre("Legacy OR list", Audio("ja"));
            rule.Conditions[0].Values = Enumerable.Range(0, 130).Select(i => "Genre " + i).ToList();
            person.Retired.GenreRules.Add(rule);
            var store = new PersonStore(folder, NullLogger<PersonStore>.Instance);
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
            var original = JsonSerializer.Serialize(person);
            File.WriteAllText(store.FilePath(id), original);
            var migrated = store.TryGet(id)!;
            Assert.Equal(100, migrated.SmartRules.Count);
            Assert.Equal(130, Assert.Single(migrated.Retired.GenreRules).Conditions[0].Values.Count);
            Assert.Contains(migrated.MigrationNotes, n => n.Contains("30 additional genre rows remain archived and inactive", StringComparison.Ordinal));
            Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.bak"))));
            SettingsValidator.PreparePerson(migrated);
            Assert.Empty(SettingsValidator.ValidatePerson(migrated, Jellyfin.Plugin.Babelfin.Engine.Languages.LanguageTable.Default));
            migrated.SmartRules.RemoveAt(0); store.Save(migrated);
            Assert.Equal(99, new PersonStore(folder, NullLogger<PersonStore>.Instance).TryGet(id)!.SmartRules.Count);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void AdminExpansionAlsoCapsAt100_AndDuplicateGenresKeepDistinctOrderedChoices()
    {
        var admin = new PluginConfiguration { ConfigVersion = 7 };
        var first = Genre("Anime", Audio("ja"), "first");
        var second = Genre("Anime", new() { WhenAudioNotUnderstood = SubtitleWant.Full }, "second");
        admin.Retired.GenreRules.Add(first); admin.Retired.GenreRules.Add(second);
        var other = Genre("Many", Audio("de"));
        other.Conditions[0].Values = Enumerable.Range(0, 110).Select(i => "Genre " + i).ToList();
        admin.Retired.GenreRules.Add(other);
        admin.Upgrade();
        Assert.Equal(100, admin.SmartRules.Count);
        Assert.Equal(new[] { "first", "second" }, admin.SmartRules.Take(2).Select(r => r.Id));
        var resolved = C.Resolve(C.Person(), admin, C.Episode(genres: new[] { "Anime" }));
        Assert.Equal("ja", resolved.Profile.AudioChain[0].Language);
        Assert.Equal(SubtitleWant.Full, resolved.Profile.WhenAudioNotUnderstood);
        Assert.Contains("12 additional genre rows remain archived and inactive", admin.LastUpgradeNote, StringComparison.Ordinal);
        Assert.Equal(3, admin.Retired.GenreRules.Count);
        SettingsValidator.PrepareAdmin(admin);
        Assert.Empty(SettingsValidator.ValidateAdmin(admin, Jellyfin.Plugin.Babelfin.Engine.Languages.LanguageTable.Default));
    }

    [Fact]
    public void LegacyMultipleGenreNamesBecomeVisibleSingleNameRowsWithStableIds()
    {
        var original = Genre("Animation", Audio("ja"), "original-id");
        original.Conditions[0].Values = new() { "Anime", "Animation" };
        var p = new PersonSettings { SchemaVersion = 2 };
        p.Retired.GenreRules.Add(original);
        PersonStore.Migrate(p);
        Assert.Equal(new[] { "Anime", "Animation" }, p.SmartRules.Select(r => Assert.Single(r.Conditions[0].Values)));
        Assert.All(p.SmartRules, r => { Assert.True(PersonMigrations.IsGenreRow(r)); Assert.False(string.IsNullOrWhiteSpace(r.Id)); });
        Assert.Equal("original-id", p.SmartRules[0].Id);
        Assert.NotEqual(p.SmartRules[0].Id, p.SmartRules[1].Id);
        Assert.Equal(2, Assert.Single(p.Retired.GenreRules).Conditions[0].Values.Count);
        var ids = p.SmartRules.Select(r => r.Id).ToArray();
        PersonStore.Migrate(p);
        Assert.Equal(ids, p.SmartRules.Select(r => r.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-real-language")]
    public void CanonicalOriginalChoiceNeverUsesFileFlag_WhenMetadataMissing(string? metadata)
    {
        var profile = F.Starter("en").Chain(F.Step(AudioStepKind.OriginalLanguage), F.Step(AudioStepKind.VideoDefault));
        var tracks = new[] { F.Audio(1, "eng", isDefault: true), F.Audio(2, "jpn", isOriginal: true) };
        var result = F.Selector.Decide(tracks, F.Item(metadata), profile);
        Assert.Equal(1, result.AudioIndex);
        Assert.True(result.AudioLeftToVideo);
        Assert.Contains("metadata", result.Explain(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Trace, l => l.Code == TraceCodes.AudioStepSkipped && l.Text.Contains("metadata", StringComparison.Ordinal));
    }

    [Fact]
    public void MetadataWinsEvenWhenLegacyOriginalSourcePrefersTheFlag()
    {
        var profile = F.Starter("en").Chain(F.Step(AudioStepKind.OriginalLanguage), F.Step(AudioStepKind.VideoDefault)) with { OriginalSource = OriginalLanguageSource.FlagThenMetadata };
        var tracks = new[] { F.Audio(1, "eng", isOriginal: true), F.Audio(2, "jpn") };
        Assert.Equal(2, F.Selector.Decide(tracks, F.Item("ja"), profile).AudioIndex);
        var legacy = profile.Chain(F.Step(AudioStepKind.OriginalLanguage), F.Step(AudioStepKind.FileDefault));
        Assert.Equal(1, F.Selector.Decide(tracks, F.Item("ja"), legacy).AudioIndex);
    }
}
[Collection("Selection cache globals")]
public class ConfigurationBackupTests
{
    [Fact]
    public void Config7OriginalBytesAreBackedUpBeforeConfig8GenreActivation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bf-config-backup-" + Guid.NewGuid().ToString("N"));
        var prior = Jellyfin.Plugin.Babelfin.Plugin.Instance;
        var instance = typeof(Jellyfin.Plugin.Babelfin.Plugin).GetField("<Instance>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Directory.CreateDirectory(folder);
        try
        {
            var paths = Babelfin.Engine.Tests.Settings.CallProxy.Create<MediaBrowser.Common.Configuration.IApplicationPaths>((m, _) => m.ReturnType == typeof(string) ? folder : null).Proxy;
            var seed = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion };
            var serializer = Babelfin.Engine.Tests.Settings.CallProxy.Create<MediaBrowser.Model.Serialization.IXmlSerializer>((m, _) => m.Name.StartsWith("Deserialize", StringComparison.Ordinal) ? seed : null).Proxy;
            var first = new Jellyfin.Plugin.Babelfin.Plugin(paths, serializer);
            var path = first.ConfigurationFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            const string original = "original schema-7 configuration bytes";
            File.WriteAllText(path, original);
            seed = new PluginConfiguration { ConfigVersion = 7 };
            seed.Retired.GenreRules.Add(PreferenceApiTests.Genre("Anime", PreferenceApiTests.Audio("ja")));
            var migrated = new Jellyfin.Plugin.Babelfin.Plugin(paths, serializer);
            Assert.Equal(PluginConfiguration.CurrentVersion, migrated.Configuration.ConfigVersion);
            Assert.Single(migrated.Configuration.SmartRules);
            Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(migrated.BackupsFolder, "settings-version7-*.xml"))));
        }
        finally
        {
            instance.SetValue(null, prior);
            EngineServices.Reset();
            _ = EngineServices.Current;
            Directory.Delete(folder, true);
        }
    }
}
