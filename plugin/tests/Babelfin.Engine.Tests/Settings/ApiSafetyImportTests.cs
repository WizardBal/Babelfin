using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

public class ApiSafetyImportTests
{
    private static string Json(object? value) => JsonSerializer.Serialize(value, BabelfinJson.Options);
    private static PluginConfiguration Admin() => new() { ConfigVersion = PluginConfiguration.CurrentVersion };
    private static readonly IReadOnlyList<ImportTargetUser> Users = new[] { new ImportTargetUser(Ids.Person, "Alice"), new ImportTargetUser(Ids.Series, "Bob") };

    // Each omitted property is classified explicitly: metadata/runtime, XML proxy, or a retired field.
    private static readonly IReadOnlyDictionary<string, string> ExcludedAdmin = new Dictionary<string, string>
    {
        [nameof(PluginConfiguration.ConfigVersion)] = "runtime: the running build owns the version",
        [nameof(PluginConfiguration.ActivityRetentionDays)] = "retired: no longer used",
        [nameof(PluginConfiguration.LastUpgradeNote)] = "retired: local history, not imported",
        [nameof(PluginConfiguration.AppModesXml)] = "proxy: XML representation of compared AppModes",
        [nameof(PluginConfiguration.ReplaceRequestIndices)] = "retired: replaced by AppModes",
        [nameof(PluginConfiguration.SendImportantEventsToJellyfinActivity)] = "retired: no longer acts",
        [nameof(PluginConfiguration.ProbeAudioLanguage)] = "retired: probe setting",
        [nameof(PluginConfiguration.ProbeSubtitleLanguage)] = "retired: probe setting",
        [nameof(PluginConfiguration.ProbeReplaceRequestIndices)] = "retired: probe setting",
    };

    private static readonly IReadOnlyDictionary<string, Action<PluginConfiguration>> MutateAdmin = new Dictionary<string, Action<PluginConfiguration>>
    {
        [nameof(PluginConfiguration.Enabled)] = c => c.Enabled = false,
        [nameof(PluginConfiguration.DryRun)] = c => c.DryRun = true,
        [nameof(PluginConfiguration.PerLibraryDefaults)] = c => c.PerLibraryDefaults = true,
        [nameof(PluginConfiguration.UnknownAppMode)] = c => c.UnknownAppMode = AppModeKind.ReplaceUnlessManual,
        [nameof(PluginConfiguration.CorrectAfterStart)] = c => c.CorrectAfterStart = CorrectAfterStartMode.Off,
        [nameof(PluginConfiguration.CompatibilityMode)] = c => c.CompatibilityMode = true,
        [nameof(PluginConfiguration.AppModes)] = c => c.AppModes.Clear(),
        [nameof(PluginConfiguration.ManualChangeAfterSeconds)] = c => c.ManualChangeAfterSeconds++,
        [nameof(PluginConfiguration.ServedWindowMinutes)] = c => c.ServedWindowMinutes++,
        [nameof(PluginConfiguration.AutoplayWindowSeconds)] = c => c.AutoplayWindowSeconds++,
        [nameof(PluginConfiguration.Everyday)] = c => c.Everyday.Reads = new[] { "fr" },
        [nameof(PluginConfiguration.Libraries)] = c => c.Libraries.Add(new LibraryDefault { LibraryId = Ids.N(Ids.Movies) }),
        [nameof(PluginConfiguration.SmartRules)] = c => c.SmartRules.Add(new SmartRule { Name = "rule" }),
        [nameof(PluginConfiguration.Titles)] = c => c.Titles.Add(new TitleDefault { ItemId = Ids.N(Ids.Item) }),
        [nameof(PluginConfiguration.BurnedIn)] = c => c.BurnedIn.Add(new BurnedInFlag { ItemId = Ids.N(Ids.Item) }),
        [nameof(PluginConfiguration.NewUserTemplate)] = c => c.NewUserTemplate = new PersonSettings { PerLibrary = true },
        [nameof(PluginConfiguration.Starter)] = c => c.Starter.Enabled = true,
        [nameof(PluginConfiguration.Keywords)] = c => c.Keywords.FullWords.Add("f1.11"),
        [nameof(PluginConfiguration.LanguageAliases)] = c => c.LanguageAliases.Add(new LanguageAlias { Raw = "example", Code = "en" }),
        [nameof(PluginConfiguration.LibraryFactsRefreshDays)] = c => c.LibraryFactsRefreshDays++,
        [nameof(PluginConfiguration.Retired)] = c => c.Retired.Rules.Add(new SmartRule { Name = "archived" }),
    };

    [Fact]
    public void EveryConfigurationPropertyIsComparedOrExplicitlyExcluded()
    {
        var properties = typeof(PluginConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite).Select(p => p.Name).OrderBy(p => p);
        var compared = ImportPlanner.Sections.SelectMany(s => s.Properties).ToArray();
        Assert.Equal(properties, compared.Concat(ExcludedAdmin.Keys).OrderBy(p => p));
        Assert.Equal(compared.OrderBy(p => p), MutateAdmin.Keys.OrderBy(p => p));
        foreach (var section in ImportPlanner.Sections)
        {
            foreach (var property in section.Properties)
            {
                var incoming = Admin();
                MutateAdmin[property](incoming);
                Assert.Equal(new[] { section.Name }, ImportPlanner.ChangedSections(Admin(), incoming));
            }
        }
        Assert.All(ExcludedAdmin.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void LegacyActivityRetentionIsIgnoredOnImportAndOmittedFromExport()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v0181-admin-export.json"));
        Assert.Contains("\"activityRetentionDays\": 90", json, StringComparison.Ordinal);
        var file = JsonSerializer.Deserialize<ExportDocument>(json, BabelfinJson.Options)!;
        var current = Admin();
        current.DryRun = true;
        var replacements = new List<PluginConfiguration>();
        var preview = ImportPlanner.Run(file, current, Users, new ApiSafetyStore(), LanguageTable.Default, replacements.Add, true);
        Assert.True(preview.Admin.WillChange);
        Assert.DoesNotContain("activity", preview.Admin.ChangedSections);
        var result = ImportPlanner.Run(file, current, Users, new ApiSafetyStore(), LanguageTable.Default, replacements.Add, false);
        Assert.True(result.Applied);
        Assert.False(Assert.Single(replacements).DryRun);
        var olderWithGroup = json.Replace("\"activityRetentionDays\": 90", "\"activityRetentionDays\": 3651, \"activity\": { \"activityRetentionDays\": 3651 }", StringComparison.Ordinal);
        var legacy = JsonSerializer.Deserialize<ExportDocument>(olderWithGroup, BabelfinJson.Options)!;
        var legacyPreview = ImportPlanner.Run(legacy, current, Users, new ApiSafetyStore(), LanguageTable.Default, _ => { }, true);
        Assert.True(legacyPreview.Admin.WillChange);
        Assert.DoesNotContain("activity", legacyPreview.Admin.ChangedSections);
        var exported = Json(ImportPlanner.BuildExport(replacements[0], Array.Empty<PersonSettings>(), "0.19.0.0", DateTime.UtcNow));
        Assert.DoesNotContain("activityRetentionDays", exported, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeAndRetiredChangesDoNotTriggerAnAdminReplacement()
    {
        var incoming = Admin();
        incoming.ConfigVersion++;
        incoming.LastUpgradeNote = "another server";
        incoming.ProbeAudioLanguage = "ja";
        incoming.ProbeSubtitleLanguage = "fr";
        incoming.ProbeReplaceRequestIndices = true;
        incoming.ReplaceRequestIndices = true;
        incoming.SendImportantEventsToJellyfinActivity = true;
        Assert.Empty(ImportPlanner.ChangedSections(Admin(), incoming));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, false)]
    [InlineData(false, null)]
    public void ImportWithOnlyLibraryModeChangedIsPreviewedAndApplied(bool? before, bool? after)
    {
        var current = Admin();
        current.PerLibraryDefaults = before;
        var incoming = ImportPlanner.CloneAdmin(current);
        incoming.PerLibraryDefaults = after;
        var file = new ExportDocument { Admin = incoming };
        var replacements = new List<PluginConfiguration>();
        var preview = ImportPlanner.Run(file, current, Users, new ApiSafetyStore(), LanguageTable.Default, replacements.Add, true);
        Assert.Equal(new[] { "perLibraryDefaults" }, preview.Admin.ChangedSections);
        Assert.True(preview.Admin.WillChange);
        Assert.Empty(replacements);
        var result = ImportPlanner.Run(file, current, Users, new ApiSafetyStore(), LanguageTable.Default, replacements.Add, false);
        Assert.True(result.Applied);
        Assert.Equal(after, Assert.Single(replacements).PerLibraryDefaults);
        Assert.Equal(before, current.PerLibraryDefaults);
    }

    [Fact]
    public void PersonImportPreservesEveryChosenFieldIncludingLibraryMode()
    {
        var fields = new[] { "Enabled", "UseEverydayEverywhere", "PerLibrary", "HandleNextEpisode", "Seed", "Everyday", "Libraries", "SmartRules", "Overrides", "CollectionOrder", "BurnedIn", "Locks", "Retired", "MigrationNotes" };
        var excluded = new Dictionary<string, string>
        {
            [nameof(PersonSettings.SchemaVersion)] = "runtime schema owned by the running build",
            [nameof(PersonSettings.UserId)] = "identity remapped to the local person",
            [nameof(PersonSettings.UserName)] = "identity replaced by the local name",
            [nameof(PersonSettings.UpdatedAt)] = "runtime save timestamp",
            [nameof(PersonSettings.LastChangedAt)] = "local successful write timestamp",
            [nameof(PersonSettings.LastChangedBy)] = "local saving account",
            [nameof(PersonSettings.LastChangedByName)] = "local saving account name",
            [nameof(PersonSettings.JellyfinChanges)] = "local runtime undo record retained under the lock",
        };
        Assert.Equal(typeof(PersonSettings).GetProperties().Where(p => p.CanWrite).Select(p => p.Name).OrderBy(p => p), fields.Concat(excluded.Keys).OrderBy(p => p));
        var incoming = new PersonSettings
        {
            UserId = Ids.Person, PerLibrary = true, Enabled = false, HandleNextEpisode = true, UseEverydayEverywhere = true,
            Everyday = new SettingsLayer { Reads = new[] { "fr" } }, Seed = new SeedRecord { Confirmed = true },
            Libraries = new List<LibraryLayer> { new() { LibraryId = Ids.N(Ids.Movies), Settings = new SettingsLayer { Reads = new[] { "ja" } } } },
            SmartRules = new List<SmartRule> { new() { Id = "stable", Name = "personal row" } },
            MigrationNotes = new List<string> { "archived on this server" },
            Retired = new RetiredPersonSettings { Rules = new List<SmartRule> { new() { Name = "archived rule" } } },
            Locks = new SettingsLayer { AvoidCommentary = false }, CollectionOrder = new List<string> { Ids.N(Ids.Cloudvale) },
            BurnedIn = new List<BurnedInFlag> { new() { ItemId = Ids.N(Ids.Item), Languages = new List<string> { "en" } } },
            Overrides = new List<ItemOverride> { new() { ItemId = Ids.N(Ids.Item), Manual = new ManualMemory { SubtitlesOff = true } } },
        };
        var store = new ApiSafetyStore();
        var result = ImportPlanner.Run(new ExportDocument { Users = new List<PersonSettings> { incoming } }, Admin(), Users, store, LanguageTable.Default, _ => { }, false);
        Assert.True(result.Applied);
        var saved = Assert.IsType<PersonSettings>(store.TryGet(Ids.Person));
        var expected = incoming.Clone();
        SettingsValidator.PreparePerson(expected);
        foreach (var field in fields)
        {
            var property = typeof(PersonSettings).GetProperty(field)!;
            Assert.Equal(Json(property.GetValue(expected)), Json(property.GetValue(saved)));
        }
        var body = incoming.Clone();
        Assert.True(PersonDocuments.Merge(new PersonSettings { UserId = Ids.Person, PerLibrary = false }, body, true, ApiSafetyHost.Person()).PerLibrary);
    }

    [Fact]
    public void AdminImportKeepsTheLatestLocalJellyfinUndoRecordAndReplacesSelectedFields()
    {
        var store = new ApiSafetyStore();
        store.Inner.Save(new PersonSettings { UserId = Ids.Person, Everyday = new SettingsLayer { Reads = new[] { "en" } } });
        var latest = new JellyfinChanges { RememberAudioSelections = true, RememberSubtitleSelections = false, ChangedAt = DateTime.UtcNow };
        store.BeforeUpdate = () => store.Inner.Update(Ids.Person, current => { var copy = current!.Clone(); copy.JellyfinChanges = latest; return copy; });
        var incoming = new PersonSettings { UserId = Ids.Person, PerLibrary = true, Everyday = new SettingsLayer { Reads = new[] { "fr" } }, JellyfinChanges = new JellyfinChanges { RememberAudioSelections = false } };
        PersonSettings? observedBefore = null;
        var result = ImportPlanner.Run(new ExportDocument { Users = new List<PersonSettings> { incoming } }, Admin(), Users, store, LanguageTable.Default, _ => { }, false, (before, _) => observedBefore = before);
        Assert.True(result.Applied);
        Assert.Equal(Json(latest), Json(store.TryGet(Ids.Person)!.JellyfinChanges));
        Assert.Equal(Json(latest), Json(observedBefore!.JellyfinChanges));
        Assert.True(store.TryGet(Ids.Person)!.PerLibrary);
        Assert.Equal(new[] { "fr" }, store.TryGet(Ids.Person)!.Everyday.Reads);
        Assert.False(incoming.JellyfinChanges.RememberAudioSelections);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void ImportReportsPartialAndTotalSaveFailuresHonestly(int failures, bool applied)
    {
        var store = new ApiSafetyStore();
        foreach (var user in Users.Take(failures)) { store.Failing.Add(user.Id); }
        var incoming = Users.Select(u => new PersonSettings { UserId = u.Id }).ToList();
        var result = ImportPlanner.Run(new ExportDocument { Users = incoming }, Admin(), Users, store, LanguageTable.Default, _ => { }, false);
        Assert.Equal(applied, result.Applied);
        Assert.Equal(failures, result.Users.Count(r => r.Action == ImportUserPreview.Skip));
        foreach (var user in Users.Take(failures))
        {
            Assert.Contains(result.Warnings, w => w.Contains(user.Name, StringComparison.Ordinal) && w.Contains("could not be saved", StringComparison.Ordinal));
            Assert.Null(store.TryGet(user.Id));
        }
        foreach (var user in Users.Skip(failures)) { Assert.NotNull(store.TryGet(user.Id)); }
    }

    [Fact]
    public void AdminSaveFailureDoesNotBlockValidPersonImports()
    {
        var incoming = Admin();
        incoming.PerLibraryDefaults = true;
        var store = new ApiSafetyStore();
        var result = ImportPlanner.Run(new ExportDocument { Admin = incoming, Users = new List<PersonSettings> { new() { UserId = Ids.Person } } }, Admin(), Users, store, LanguageTable.Default, _ => throw new IOException("Test failure"), false);
        Assert.True(result.Applied);
        Assert.Contains(result.Warnings, w => w.Contains("admin settings could not be saved", StringComparison.Ordinal));
        Assert.NotNull(store.TryGet(Ids.Person));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackupRoundTripRestoresInactiveAdminAndPersonArchives(bool existing)
    {
        var admin = Admin();
        admin.Retired = new RetiredAdminSettings
        {
            Rules = new List<SmartRule> { new() { Name = "old server rule" } },
            GenreRules = new List<SmartRule> { new() { Name = "old server genre" } },
            Titles = new List<TitleDefault> { new() { ItemId = Ids.N(Ids.Item) } },
            BurnedIn = new List<BurnedInFlag> { new() { ItemId = Ids.N(Ids.Item), Languages = new List<string> { "en" } } },
            Layers = new List<RetiredLayer> { new() { Location = "everyday", Settings = new SettingsLayer { AvoidCommentary = false } } },
            Starter = new StarterSettings { Enabled = true }, DryRun = true,
        };
        var person = new PersonSettings
        {
            UserId = Ids.Person, UserName = "Alice", PerLibrary = true,
            MigrationNotes = new List<string> { "old rule archived", "newer audio choice kept" },
            Retired = new RetiredPersonSettings
            {
                Rules = new List<SmartRule> { new() { Name = "old person rule" } },
                GenreRules = new List<SmartRule> { new() { Name = "old person genre" } },
                Collections = new List<ItemOverride> { new() { ItemId = Ids.N(Ids.Cloudvale), Scope = OverrideScope.Collection, Manual = new ManualMemory { SubtitlesOff = true } } },
                CollectionOrder = new List<string> { Ids.N(Ids.Cloudvale) }, Locks = new SettingsLayer { AvoidCommentary = false },
                Layers = new List<RetiredLayer> { new() { Location = "everyday", Settings = new SettingsLayer { Reads = new[] { "en" } } } },
            },
            JellyfinChanges = new JellyfinChanges { RememberAudioSelections = false },
        };
        var backup = ImportPlanner.BuildExport(admin, new[] { person }, "test", DateTime.UtcNow);
        var serialized = Json(backup);
        var file = JsonSerializer.Deserialize<ExportDocument>(serialized, BabelfinJson.Options)!;
        var store = new ApiSafetyStore();
        if (existing)
        {
            var local = new PersonSettings { UserId = Ids.Person, JellyfinChanges = new JellyfinChanges { RememberAudioSelections = true } };
            local.Retired.Rules.Add(new SmartRule { Name = "replaced local archive" });
            local.MigrationNotes.Add("replaced local history");
            store.Inner.Save(local);
        }
        var replacements = new List<PluginConfiguration>();
        var preview = ImportPlanner.Run(file, Admin(), Users, store, LanguageTable.Default, replacements.Add, true);
        Assert.Equal(new[] { "retired" }, preview.Admin.ChangedSections);
        Assert.Empty(replacements);
        Assert.Equal(0, store.Updates);
        var result = ImportPlanner.Run(file, Admin(), Users, store, LanguageTable.Default, replacements.Add, false);
        Assert.True(result.Applied);
        Assert.Equal(Json(admin.Retired), Json(Assert.Single(replacements).Retired));
        var saved = store.TryGet(Ids.Person)!;
        Assert.Equal(Json(person.Retired), Json(saved.Retired));
        Assert.Equal(person.MigrationNotes, saved.MigrationNotes);
        Assert.True(saved.PerLibrary);
        Assert.Empty(saved.SmartRules);
        Assert.Empty(saved.Overrides);
        Assert.True(saved.Locks.IsEmpty);
        Assert.Empty(replacements[0].SmartRules);
        Assert.Empty(replacements[0].Titles);
        Assert.Equal(existing ? true : (bool?)null, saved.JellyfinChanges?.RememberAudioSelections);
        Assert.Equal(serialized, Json(file));
        saved.Retired.Rules[0].Name = "changed after restore";
        replacements[0].Retired.Rules[0].Name = "changed after restore";
        Assert.Equal("old person rule", file.Users[0].Retired.Rules[0].Name);
        Assert.Equal("old server rule", file.Admin!.Retired.Rules[0].Name);
    }
}
