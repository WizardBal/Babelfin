using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

public sealed class RecorderMemoryCompatibilityTests
{
    [Theory]
    [InlineData(false, MemoryScope.RestOfShow)]
    [InlineData(false, MemoryScope.Season)]
    [InlineData(false, MemoryScope.TitleOnly)]
    [InlineData(false, MemoryScope.DontRemember)]
    [InlineData(true, MemoryScope.RestOfShow)]
    [InlineData(true, MemoryScope.Season)]
    [InlineData(true, MemoryScope.TitleOnly)]
    [InlineData(true, MemoryScope.DontRemember)]
    public void GenreTableChoicesCannotReplaceEverydayMemory(bool adminRule, MemoryScope memory)
    {
        var person = new PersonSettings();
        var admin = new PluginConfiguration();
        var owner = adminRule ? admin.Everyday : person.Everyday;
        owner.Memory = memory;
        var rules = adminRule ? admin.SmartRules : person.SmartRules;
        rules.Add(new SmartRule
        {
            Name = "Adventure",
            Conditions = new List<RuleCondition> { new() { Kind = ConditionKind.Genre, Values = new() { "Adventure" } } },
            Settings = new SettingsLayer
            {
                AudioChain = new[] { new AudioStepSetting(AudioStepKind.OriginalLanguage) },
                Memory = memory == MemoryScope.DontRemember ? MemoryScope.TitleOnly : MemoryScope.DontRemember,
            },
        });
        var context = ItemContext.None with { Kind = ContentKind.Episode, Genres = new[] { "Adventure" } };

        var result = ProfileResolver.Resolve(person, admin, context, LanguageTable.Default);

        Assert.Contains("genre", result.Sources[SettingsFields.AudioChain]);
        Assert.Equal(adminRule ? ProfileResolver.AdminEverydayLabel : ProfileResolver.PersonEverydayLabel,
            result.Sources[SettingsFields.Memory]);
        Assert.Equal(memory, TrackChangeRecorder.MemoryFor(person, admin, result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoredGeneralRulesCannotSupplyMemory(bool adminRule)
    {
        var person = new PersonSettings();
        var admin = new PluginConfiguration();
        var rules = adminRule ? admin.SmartRules : person.SmartRules;
        var rule = new SmartRule
        {
            Name = "Harbor titles",
            Conditions = new() { new() { Kind = ConditionKind.TitleMatches, Values = new() { "Harbor" } } },
            Settings = new SettingsLayer { Memory = MemoryScope.DontRemember },
        };
        rules.Add(rule);
        var context = ItemContext.None with { Name = "Harbor Voyage", Kind = ContentKind.Movie };

        var result = ProfileResolver.Resolve(person, admin, context, LanguageTable.Default);

        Assert.Equal(StarterRules.DefaultsLabel, result.Sources[SettingsFields.Memory]);
        Assert.Equal(MemoryScope.RestOfShow, TrackChangeRecorder.MemoryFor(person, admin, result));
    }
}
