using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>export, import with a dry-run preview, matching by id and by name.</summary>
public class ImportExportTests
{
    private static readonly LanguageTable Languages = LanguageTable.Default;

    private static readonly Guid AliceId = Guid.NewGuid();
    private static readonly Guid BobId = Guid.NewGuid();

    private static readonly IReadOnlyList<ImportTargetUser> Users = new[]
    {
        new ImportTargetUser(AliceId, "Alice"),
        new ImportTargetUser(BobId, "Bob"),
    };

    private static PersonSettings Doc(Guid id, string name, params string[] understood) => new()
    {
        UserId = id,
        UserName = name,
        Everyday = new SettingsLayer { Understood = understood, Reads = new[] { "en" } },
        Seed = new SeedRecord { SeededAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) },
    };

    private static ExportDocument File(PluginConfiguration? admin, params PersonSettings[] users) => new()
    {
        BabelfinVersion = "1.0.0.0",
        ExportedAt = DateTime.UtcNow,
        Admin = admin,
        Users = users.ToList(),
    };

    private static PluginConfiguration ImportedAdmin()
    {
        var admin = new PluginConfiguration { ConfigVersion = 1, DryRun = true };
        admin.Everyday.Understood = new[] { "ja" };
        admin.SmartRules.Add(new SmartRule
        {
            Id = "r1",
            Name = "Anime titles",
            Conditions = new List<RuleCondition> { new() { Kind = ConditionKind.TitleMatches, Values = new List<string> { "^Lanternbound" }, IsRegex = true } },
            Settings = new SettingsLayer { AudioChain = new[] { new AudioStepSetting(AudioStepKind.OriginalLanguage) } },
        });
        return admin;
    }

    private static ImportPreview Run(ExportDocument file, PluginConfiguration current, MemoryStore store, bool dryRun, List<PluginConfiguration> replaced)
        => ImportPlanner.Run(file, current, Users, store, Languages, replaced.Add, dryRun);

    [Fact]
    public void Users_MatchedById()
    {
        var store = new MemoryStore();
        var preview = ImportPlanner.Plan(File(null, Doc(AliceId, "someone else", "fr")), new PluginConfiguration(), Users, store, Languages);

        var row = Assert.Single(preview.Users);
        Assert.Equal("id", row.MatchedBy);
        Assert.Equal(AliceId, row.UserId);
        Assert.Equal("Alice", row.UserName);
        Assert.Equal(ImportUserPreview.Create, row.Action);
    }

    [Fact]
    public void Users_MatchedByName_CaseInsensitive()
    {
        var otherServerId = Guid.NewGuid();
        var store = new MemoryStore();
        store.Save(Doc(BobId, "Bob", "de"));

        var preview = ImportPlanner.Plan(File(null, Doc(otherServerId, "bOB", "fr")), new PluginConfiguration(), Users, store, Languages);

        var row = Assert.Single(preview.Users);
        Assert.Equal("name", row.MatchedBy);
        Assert.Equal(BobId, row.UserId);
        Assert.Equal(otherServerId, row.SourceUserId);
        Assert.Equal(ImportUserPreview.Update, row.Action);
    }

    [Fact]
    public void Users_Unmatched_AreSkippedWithAReason()
    {
        var preview = ImportPlanner.Plan(File(null, Doc(Guid.NewGuid(), "Carol", "fr")), new PluginConfiguration(), Users, new MemoryStore(), Languages);

        var row = Assert.Single(preview.Users);
        Assert.Null(row.MatchedBy);
        Assert.Equal(ImportUserPreview.Skip, row.Action);
        Assert.False(string.IsNullOrWhiteSpace(row.Reason));
    }

    [Fact]
    public void Users_IdMatchWinsOverAnEarlierNameMatch()
    {
        // The first entry names Alice only by name; the second carries Alice's id. The id match keeps Alice.
        var preview = ImportPlanner.Plan(
            File(null, Doc(Guid.NewGuid(), "alice", "fr"), Doc(AliceId, "Alice", "ja")),
            new PluginConfiguration(),
            Users,
            new MemoryStore(),
            Languages);

        Assert.Equal(ImportUserPreview.Skip, preview.Users[0].Action);
        Assert.Equal("id", preview.Users[1].MatchedBy);
        Assert.Equal(ImportUserPreview.Create, preview.Users[1].Action);
    }

    [Fact]
    public void Users_InvalidDocument_IsSkippedWithTheProblem()
    {
        var preview = ImportPlanner.Plan(File(null, Doc(AliceId, "Alice", "not-a-real-language-zz")), new PluginConfiguration(), Users, new MemoryStore(), Languages);

        var row = Assert.Single(preview.Users);
        Assert.Equal(ImportUserPreview.Skip, row.Action);
        Assert.Contains("unknown language", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRun_ChangesNothing()
    {
        var store = new MemoryStore();
        var existing = Doc(BobId, "Bob", "de");
        store.Save(existing);
        var before = JsonSerializer.Serialize(store.TryGet(BobId), BabelfinJson.Options);
        var current = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion };
        var replaced = new List<PluginConfiguration>();

        var preview = Run(File(ImportedAdmin(), Doc(AliceId, "Alice", "ja"), Doc(BobId, "Bob", "fr")), current, store, dryRun: true, replaced);

        Assert.False(preview.Applied);
        Assert.True(preview.Admin.WillChange);
        Assert.Empty(replaced);
        Assert.Null(store.TryGet(AliceId));
        Assert.Equal(before, JsonSerializer.Serialize(store.TryGet(BobId), BabelfinJson.Options));
        Assert.Equal(1, store.Saves);
        Assert.False(current.DryRun);
        Assert.Empty(current.SmartRules);
    }

    [Fact]
    public void Apply_ReplacesDocumentsAndRemapsUserId()
    {
        var store = new MemoryStore();
        var old = Doc(BobId, "Bob", "de");
        old.Overrides.Add(new ItemOverride { ItemId = Guid.NewGuid().ToString("N"), Settings = new SettingsLayer { Reads = new[] { "de" } } });
        store.Save(old);
        var otherServerBob = Guid.NewGuid();
        var incoming = Doc(otherServerBob, "BOB", "fr");
        var replaced = new List<PluginConfiguration>();

        var preview = Run(File(null, incoming), new PluginConfiguration(), store, dryRun: false, replaced);

        Assert.True(preview.Applied);
        var saved = store.TryGet(BobId);
        Assert.NotNull(saved);
        Assert.Equal(BobId, saved.UserId);
        Assert.Equal("Bob", saved.UserName);
        Assert.Equal(new[] { "fr" }, saved.Everyday.Understood);
        Assert.Empty(saved.Overrides);
        Assert.Null(store.TryGet(otherServerBob));
        Assert.Empty(replaced);
    }

    [Fact]
    public void Apply_ReplacesAdmin_KeepsConfigVersionCurrent_AndNeverImportsProbeFields()
    {
        var admin = ImportedAdmin();
        admin.ConfigVersion = 99;
        admin.ProbeAudioLanguage = "jpn";
        admin.ProbeSubtitleLanguage = "eng";
        admin.ProbeReplaceRequestIndices = true;
        admin.LastUpgradeNote = "from the other server";
        var current = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, LastUpgradeNote = "local note" };
        var replaced = new List<PluginConfiguration>();

        var preview = Run(File(admin), current, new MemoryStore(), dryRun: false, replaced);

        Assert.True(preview.Applied);
        Assert.Contains("dryRun", preview.Admin.ChangedSections);
        Assert.Contains("smartRules", preview.Admin.ChangedSections);
        Assert.Contains("everyday", preview.Admin.ChangedSections);
        Assert.DoesNotContain("keywords", preview.Admin.ChangedSections);
        var applied = Assert.Single(replaced);
        Assert.Equal(PluginConfiguration.CurrentVersion, applied.ConfigVersion);
        Assert.Null(applied.ProbeAudioLanguage);
        Assert.Null(applied.ProbeSubtitleLanguage);
        Assert.Null(applied.ProbeReplaceRequestIndices);
        Assert.Equal(string.Empty, applied.LastUpgradeNote);
        Assert.True(applied.DryRun);
        Assert.Equal("Anime titles", Assert.Single(applied.SmartRules).Name);
        Assert.True(applied.SmartRules[0].Conditions[0].IsRegex);
        Assert.NotSame(admin, applied);
    }

    [Fact]
    public void Admin_InvalidRegex_IsNotImported_ButPeopleStillAre()
    {
        var admin = ImportedAdmin();
        admin.ConfigVersion = PluginConfiguration.CurrentVersion;
        admin.SmartRules[0].Conditions[0].Values[0] = @"(\w+)\1";
        var store = new MemoryStore();
        var replaced = new List<PluginConfiguration>();

        var preview = Run(File(admin, Doc(AliceId, "Alice", "ja")), new PluginConfiguration(), store, dryRun: false, replaced);

        Assert.False(preview.Admin.WillChange);
        Assert.Contains(preview.Warnings, w => w.Contains("Anime titles", StringComparison.Ordinal));
        Assert.Empty(replaced);
        Assert.NotNull(store.TryGet(AliceId));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(6, true)]
    [InlineData(6, false)]
    public void AdminLegacyInvalidRegex_IsArchivedBeforeValidation(int version, bool dryRun)
    {
        var admin = ImportedAdmin();
        admin.ConfigVersion = version;
        const string Pattern = @"(\w+)\1";
        admin.SmartRules[0].Conditions[0].Values[0] = Pattern;
        var file = File(admin, Doc(AliceId, "Alice", "ja"));
        var before = JsonSerializer.Serialize(file, BabelfinJson.Options);
        var store = new MemoryStore();
        var replaced = new List<PluginConfiguration>();
        var current = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion };

        var preview = Run(file, current, store, dryRun, replaced);

        Assert.True(preview.Admin.WillChange);
        Assert.Contains("retired", preview.Admin.ChangedSections);
        Assert.Empty(preview.Warnings);
        Assert.Equal(ImportUserPreview.Create, Assert.Single(preview.Users).Action);
        Assert.Equal(before, JsonSerializer.Serialize(file, BabelfinJson.Options));
        if (dryRun)
        {
            Assert.False(preview.Applied);
            Assert.Empty(replaced);
            Assert.Null(store.TryGet(AliceId));
            Assert.Equal(0, store.Saves);
        }
        else
        {
            Assert.True(preview.Applied);
            var saved = Assert.Single(replaced);
            Assert.Equal(PluginConfiguration.CurrentVersion, saved.ConfigVersion);
            Assert.Empty(saved.SmartRules);
            var archived = Assert.Single(saved.Retired.Rules);
            Assert.Equal("Anime titles", archived.Name);
            var condition = Assert.Single(archived.Conditions);
            Assert.True(condition.IsRegex);
            Assert.Equal(Pattern, Assert.Single(condition.Values));
            Assert.Equal(new[] { "ja" }, store.TryGet(AliceId)!.Everyday.Understood);
        }
    }

    [Fact]
    public void Admin_Unchanged_IsNotReplaced()
    {
        var current = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion };
        var replaced = new List<PluginConfiguration>();

        var preview = Run(File(ImportPlanner.CloneAdmin(current)), current, new MemoryStore(), dryRun: false, replaced);

        Assert.False(preview.Admin.WillChange);
        Assert.Empty(preview.Admin.ChangedSections);
        Assert.Empty(replaced);
    }

    [Fact]
    public void PersonRegex_IsForcedOffOnImport()
    {
        var doc = Doc(AliceId, "Alice", "ja");
        doc.SmartRules.Add(new SmartRule
        {
            Name = "mine",
            Conditions = new List<RuleCondition> { new() { Kind = ConditionKind.TitleMatches, Values = new List<string> { "(x+)+$" }, IsRegex = true } },
        });
        var store = new MemoryStore();

        Run(File(null, doc), new PluginConfiguration(), store, dryRun: false, new List<PluginConfiguration>());

        var saved = store.TryGet(AliceId);
        Assert.NotNull(saved);
        Assert.False(saved.SmartRules[0].Conditions[0].IsRegex);
        Assert.False(string.IsNullOrEmpty(saved.SmartRules[0].Id));
    }

    [Fact]
    public void UnsupportedSchema_IsRefusedAsAWhole()
    {
        var file = File(ImportedAdmin(), Doc(AliceId, "Alice", "ja"));
        file.SchemaVersion = 2;
        var store = new MemoryStore();
        var replaced = new List<PluginConfiguration>();

        var preview = Run(file, new PluginConfiguration(), store, dryRun: false, replaced);

        Assert.False(preview.Applied);
        Assert.NotEmpty(preview.Warnings);
        Assert.All(preview.Users, u => Assert.Equal(ImportUserPreview.Skip, u.Action));
        Assert.Empty(replaced);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void Export_RoundTripsThroughJson_AndImportsBackUnchanged()
    {
        var admin = ImportedAdmin();
        admin.ConfigVersion = PluginConfiguration.CurrentVersion;
        var alice = Doc(AliceId, "Alice", "ja");
        var export = ImportPlanner.BuildExport(admin, new[] { alice }, "1.0.0.0", DateTime.UtcNow);

        var json = JsonSerializer.Serialize(export, BabelfinJson.Options);
        var back = JsonSerializer.Deserialize<ExportDocument>(json, BabelfinJson.Options);

        Assert.NotNull(back);
        Assert.Equal(ExportDocument.CurrentSchemaVersion, back.SchemaVersion);
        Assert.DoesNotContain("probe", json, StringComparison.OrdinalIgnoreCase);
        var store = new MemoryStore();
        store.Save(alice);
        var preview = ImportPlanner.Plan(back, admin, Users, store, Languages);
        Assert.False(preview.Admin.WillChange);
        Assert.Equal(ImportUserPreview.Update, Assert.Single(preview.Users).Action);
    }

    /// <summary>An in-memory person store (this file's own fake, so the import tests don't depend on other test files).</summary>
    private sealed class MemoryStore : IPersonStore
    {
        private readonly Dictionary<Guid, PersonSettings> _docs = new();

        public event EventHandler<PersonStoreChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public string DataFolder => "memory";

        public int Saves { get; private set; }

        public PersonSettings? TryGet(Guid userId) => _docs.TryGetValue(userId, out var doc) ? doc : null;

        public PersonSettings GetOrCreate(Guid userId, Func<PersonSettings> create)
        {
            if (!_docs.TryGetValue(userId, out var doc))
            {
                doc = create();
                Save(doc);
                doc = _docs[userId];
            }

            return doc;
        }

        public void Save(PersonSettings settings)
        {
            var copy = settings.Clone();
            copy.UpdatedAt = DateTime.UtcNow;
            _docs[copy.UserId] = copy;
            Saves++;
        }

        public bool Update(Guid userId, Func<PersonSettings?, PersonSettings?> change)
        {
            var changed = change(TryGet(userId));
            if (changed is null)
            {
                return false;
            }

            Save(changed);
            return true;
        }

        public bool Delete(Guid userId) => _docs.Remove(userId);

        public IReadOnlyList<Guid> UserIds() => _docs.Keys.ToList();
    }
}
