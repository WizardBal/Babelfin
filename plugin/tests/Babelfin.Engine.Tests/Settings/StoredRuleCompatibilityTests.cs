using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

public sealed class StoredRuleCompatibilityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "babelfin-stored-rules-" + Guid.NewGuid().ToString("N"));

    private static ItemContext Context => ItemContext.None with
    {
        Name = "Harbor Voyage",
        SeriesName = "Harbor Chronicles",
        Kind = ContentKind.Episode,
        OriginalLanguage = "jpn",
    };

    [Theory]
    [InlineData(ConditionKind.TitleMatches, @"^Harbor\s+(Voyage|Chronicles)$", true, false)]
    [InlineData(ConditionKind.OriginalLanguage, "Japanese", false, false)]
    [InlineData(ConditionKind.OriginalLanguageUnderstood, null, false, false)]
    [InlineData(ConditionKind.OriginalLanguage, "ja", false, true)]
    [InlineData(ConditionKind.OriginalLanguageUnderstood, null, false, true)]
    public void CurrentAdminConditionsSurviveImportAndExportWithoutBecomingActive(ConditionKind kind, string? value, bool regex, bool negate)
    {
        var condition = new RuleCondition { Kind = kind, IsRegex = regex, Negate = negate };
        if (value is not null) condition.Values.Add(value);
        var rule = Rule(condition);
        var incoming = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion };
        incoming.SmartRules.Add(rule);
        var imported = new List<PluginConfiguration>();
        var preview = ImportPlanner.Run(RoundTrip(new ExportDocument { Admin = incoming }), new PluginConfiguration(),
            Array.Empty<ImportTargetUser>(), Store(), LanguageTable.Default, imported.Add, dryRun: false);

        Assert.True(preview.Applied);
        Assert.Empty(preview.Warnings);
        var saved = Assert.Single(imported);
        AssertRulePreserved(rule, Assert.Single(saved.SmartRules));
        var exported = RoundTrip(ImportPlanner.BuildExport(saved, Array.Empty<PersonSettings>(), "1.0.0.0", DateTime.UtcNow));
        AssertRulePreserved(rule, Assert.Single(exported.Admin!.SmartRules));
        AssertInactive(saved, new PersonSettings { Everyday = new SettingsLayer { Understood = new[] { "ja" } } });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void OlderAdminTitleAndLanguageConditionsRemainArchivedAcrossImportAndExport(int version)
    {
        var incoming = new PluginConfiguration { ConfigVersion = version };
        var rule = Rule(new RuleCondition { Kind = ConditionKind.TitleMatches, IsRegex = true, Values = new() { "^Harbor" } });
        rule.Conditions.Add(new RuleCondition { Kind = ConditionKind.OriginalLanguage, Values = new() { "Japanese" } });
        incoming.SmartRules.Add(rule);
        var imported = new List<PluginConfiguration>();

        var preview = ImportPlanner.Run(RoundTrip(new ExportDocument { Admin = incoming }), new PluginConfiguration(),
            Array.Empty<ImportTargetUser>(), Store(), LanguageTable.Default, imported.Add, dryRun: false);

        Assert.True(preview.Applied);
        Assert.Empty(preview.Warnings);
        var saved = Assert.Single(imported);
        Assert.Empty(saved.SmartRules);
        AssertRulePreserved(rule, Assert.Single(saved.Retired.Rules));
        var exported = RoundTrip(ImportPlanner.BuildExport(saved, Array.Empty<PersonSettings>(), "1.0.0.0", DateTime.UtcNow));
        AssertRulePreserved(rule, Assert.Single(exported.Admin!.Retired.Rules));
        AssertInactive(saved, new PersonSettings());
    }

    [Fact]
    public void ImportedPersonTitleConditionSurvivesReloadAndExportWithoutBecomingActive()
    {
        var id = Guid.NewGuid();
        var person = new PersonSettings { UserId = id, UserName = "Test viewer" };
        var rule = Rule(new RuleCondition { Kind = ConditionKind.TitleMatches, IsRegex = true, Values = new() { "^Harbor" } });
        person.SmartRules.Add(rule);
        var document = RoundTrip(new ExportDocument { Users = new() { person } });

        var preview = ImportPlanner.Run(document, new PluginConfiguration(), new[] { new ImportTargetUser(id, "Test viewer") },
            Store(), LanguageTable.Default, _ => throw new InvalidOperationException("No admin settings were supplied."), dryRun: false);

        Assert.True(preview.Applied);
        Assert.Equal(ImportUserPreview.Create, Assert.Single(preview.Users).Action);
        var reloaded = Assert.IsType<PersonSettings>(Store().TryGet(id));
        var expected = rule.Clone();
        expected.Conditions[0].IsRegex = false;
        AssertRulePreserved(expected, Assert.Single(reloaded.SmartRules));
        var exported = RoundTrip(ImportPlanner.BuildExport(new PluginConfiguration(), new[] { reloaded }, "1.0.0.0", DateTime.UtcNow));
        AssertRulePreserved(expected, Assert.Single(Assert.Single(exported.Users).SmartRules));
        AssertInactive(new PluginConfiguration(), reloaded);
        AssertInactive(new PluginConfiguration(), reloaded, Context with { Name = "The ^Harbor Voyage" });
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private PersonStore Store() => new(_folder, NullLogger<PersonStore>.Instance);

    private static ExportDocument RoundTrip(ExportDocument document) =>
        JsonSerializer.Deserialize<ExportDocument>(JsonSerializer.Serialize(document, BabelfinJson.Options), BabelfinJson.Options)!;

    private static void AssertRulePreserved(SmartRule expected, SmartRule actual) =>
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected, BabelfinJson.Options),
            JsonSerializer.SerializeToUtf8Bytes(actual, BabelfinJson.Options));

    private static SmartRule Rule(RuleCondition condition) => new()
    {
        Id = "stored-rule",
        Name = "Harbor titles",
        Conditions = new() { condition },
        Settings = new SettingsLayer
        {
            AudioChain = new[] { new AudioStepSetting(AudioStepKind.OriginalLanguage) },
            Memory = MemoryScope.DontRemember,
        },
    };

    private static void AssertInactive(PluginConfiguration admin, PersonSettings person, ItemContext? context = null)
    {
        var adminBefore = JsonSerializer.SerializeToUtf8Bytes(admin, BabelfinJson.Options);
        var personBefore = JsonSerializer.SerializeToUtf8Bytes(person, BabelfinJson.Options);
        var result = ProfileResolver.Resolve(person, admin, context ?? Context, LanguageTable.Default);
        Assert.Equal(StarterRules.DefaultsLabel, result.Sources[SettingsFields.AudioChain]);
        Assert.Equal(StarterRules.DefaultsLabel, result.Sources[SettingsFields.Memory]);
        Assert.Equal(MemoryScope.RestOfShow, TrackChangeRecorder.MemoryFor(person, admin, result));
        Assert.Equal(adminBefore, JsonSerializer.SerializeToUtf8Bytes(admin, BabelfinJson.Options));
        Assert.Equal(personBefore, JsonSerializer.SerializeToUtf8Bytes(person, BabelfinJson.Options));
    }
}
