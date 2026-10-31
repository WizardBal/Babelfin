using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>the settings validator used by every PUT and the import.</summary>
public class ValidatorTests
{
    private static readonly LanguageTable Languages = LanguageTable.Default;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static PersonSettings Person(Action<PersonSettings>? change = null)
    {
        var person = new PersonSettings { UserId = Guid.NewGuid(), UserName = "alice" };
        person.Everyday.Understood = new[] { "en" };
        person.Everyday.Reads = new[] { "en" };
        change?.Invoke(person);
        SettingsValidator.PreparePerson(person);
        return person;
    }

    private static IReadOnlyList<ValidationError> ValidatePerson(PersonSettings person) => SettingsValidator.ValidatePerson(person, Languages);

    private static SmartRule TitleRule(string name, string pattern, bool isRegex) => new()
    {
        Name = name,
        Conditions = new List<RuleCondition> { new() { Kind = ConditionKind.TitleMatches, Values = new List<string> { pattern }, IsRegex = isRegex } },
        Settings = new SettingsLayer { Understood = new[] { "ja" } },
    };

    private static IReadOnlyList<ValidationError> ValidateAdmin(PluginConfiguration config)
    {
        SettingsValidator.PrepareAdmin(config);
        return SettingsValidator.ValidateAdmin(config, Languages);
    }

    [Fact]
    public void ValidPerson_HasNoErrors()
    {
        var person = Person(p =>
        {
            p.Everyday.AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "ja"), new AudioStepSetting(AudioStepKind.FileDefault) };
            p.Everyday.SubtitleLanguages = new[] { "pt-BR", "zh-Hant", "yue" };
            p.Overrides.Add(new ItemOverride { ItemId = NewId(), Scope = OverrideScope.Series, Settings = new SettingsLayer { Reads = new[] { "Japanese" } } });
            p.BurnedIn.Add(new BurnedInFlag { ItemId = NewId(), Scope = OverrideScope.Title, Languages = new List<string> { "eng" } });
        });

        Assert.Empty(ValidatePerson(person));
    }

    [Theory]
    [InlineData("xx-nope")]
    [InlineData("klingonese")]
    [InlineData("und")]
    public void UnknownLanguage_IsRejected(string language)
    {
        var person = Person(p => p.Everyday.Understood = new[] { "en", language });

        var error = Assert.Single(ValidatePerson(person));

        Assert.Equal("everyday.understood[1]", error.Path);
        Assert.Contains("unknown language", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownLanguage_InAudioStepAndBurnedIn_IsRejected()
    {
        var person = Person(p =>
        {
            p.Everyday.AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "notalanguage") };
            p.BurnedIn.Add(new BurnedInFlag { ItemId = NewId(), Languages = new List<string> { "zz-bogus" } });
        });

        var errors = ValidatePerson(person);

        Assert.Contains(errors, e => e.Path == "everyday.audioChain[0].language");
        Assert.Contains(errors, e => e.Path == "burnedIn[0].languages[0]");
    }

    [Fact]
    public void LanguageStep_WithoutLanguage_IsRejected()
    {
        var person = Person(p => p.Everyday.AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language) });

        Assert.Contains(ValidatePerson(person), e => e.Path == "everyday.audioChain[0].language");
    }

    [Fact]
    public void Limits_LanguagesPerList()
    {
        var person = Person(p => p.Everyday.Reads = Enumerable.Repeat("en", SettingsValidator.MaxLanguagesPerList + 1).ToArray());

        Assert.Contains(ValidatePerson(person), e => e.Path == "everyday.reads" && e.Message.Contains("at most 50", StringComparison.Ordinal));
    }

    [Fact]
    public void Limits_AtTheCapIsAccepted()
    {
        var person = Person(p => p.Everyday.Reads = Enumerable.Repeat("en", SettingsValidator.MaxLanguagesPerList).ToArray());

        Assert.Empty(ValidatePerson(person));
    }

    [Fact]
    public void Limits_StringLength()
    {
        var person = Person(p => p.SmartRules.Add(TitleRule(new string('x', SettingsValidator.MaxStringLength + 1), "abc", false)));

        Assert.Contains(ValidatePerson(person), e => e.Path == "smartRules[0].name");
    }

    [Fact]
    public void Limits_AudioSteps()
    {
        var person = Person(p => p.Everyday.AudioChain = Enumerable.Range(0, SettingsValidator.MaxAudioSteps + 1).Select(_ => new AudioStepSetting(AudioStepKind.FileDefault)).ToArray());

        Assert.Contains(ValidatePerson(person), e => e.Path == "everyday.audioChain");
    }

    [Fact]
    public void Limits_RulesConditionsAndValues()
    {
        var person = Person(p =>
        {
            for (var i = 0; i <= SettingsValidator.MaxRules; i++)
            {
                p.SmartRules.Add(TitleRule("r" + i, "abc", false));
            }

            p.SmartRules[0].Conditions = Enumerable.Range(0, SettingsValidator.MaxConditionsPerRule + 1)
                .Select(_ => new RuleCondition { Kind = ConditionKind.Genre, Values = new List<string> { "Anime" } })
                .ToList();
            p.SmartRules[1].Conditions[0].Values = Enumerable.Range(0, SettingsValidator.MaxValuesPerCondition + 1).Select(i => "v" + i).ToList();
        });

        var errors = ValidatePerson(person);

        Assert.Contains(errors, e => e.Path == "smartRules");
        Assert.Contains(errors, e => e.Path == "smartRules[0].conditions");
        Assert.Contains(errors, e => e.Path == "smartRules[1].conditions[0].values");
    }

    [Fact]
    public void Limits_OverridesBurnedInAndLibraries()
    {
        var person = Person(p =>
        {
            p.Overrides.AddRange(Enumerable.Range(0, SettingsValidator.MaxOverrides + 1).Select(_ => Explicit()));
            p.BurnedIn.AddRange(Enumerable.Range(0, SettingsValidator.MaxBurnedIn + 1).Select(_ => new BurnedInFlag { ItemId = NewId(), Languages = new List<string> { "en" } }));
            p.Libraries.AddRange(Enumerable.Range(0, SettingsValidator.MaxLibraries + 1).Select(_ => new LibraryLayer { LibraryId = NewId() }));
        });

        var errors = ValidatePerson(person);

        Assert.Contains(errors, e => e.Path == "overrides");
        Assert.Contains(errors, e => e.Path == "burnedIn");
        Assert.Contains(errors, e => e.Path == "libraries");
    }

    private static ItemOverride Explicit() => new() { ItemId = NewId(), Scope = OverrideScope.Title, Settings = new SettingsLayer { Format = FormatPreference.TextOnly } };

    private static ItemOverride Remembered() => new() { ItemId = NewId(), Scope = OverrideScope.Title, Manual = new ManualMemory { SubtitlesOff = true } };

    // Remembered track changes are not capped (TrackChangeRecorder adds them freely), so they must not count against the 500 overrides: a person who has
    // many of them is still valid for an admin import, the backup ResetAll writes and a whole-document PUT.
    [Theory]
    [InlineData(0, 600)]
    [InlineData(0, 5000)]
    [InlineData(3, 600)]
    [InlineData(500, 0)]
    [InlineData(500, 100)]
    [InlineData(500, 5000)]
    public void Overrides_RememberedTrackChangesDoNotCountAgainstTheCap(int explicitCount, int rememberedCount)
    {
        var person = Person(p =>
        {
            p.Overrides.AddRange(Enumerable.Range(0, explicitCount).Select(_ => Explicit()));
            p.Overrides.AddRange(Enumerable.Range(0, rememberedCount).Select(_ => Remembered()));
        });

        Assert.Empty(ValidatePerson(person));
        Assert.Equal(explicitCount + rememberedCount, person.Overrides.Count);
    }

    [Theory]
    [InlineData(501, 0)]
    [InlineData(501, 600)]
    [InlineData(1200, 3)]
    public void Overrides_ExplicitSettingsOverTheCap_AreRefused_WithOrWithoutRememberedOnes(int explicitCount, int rememberedCount)
    {
        var person = Person(p =>
        {
            p.Overrides.AddRange(Enumerable.Range(0, rememberedCount).Select(_ => Remembered()));
            p.Overrides.AddRange(Enumerable.Range(0, explicitCount).Select(_ => Explicit()));
        });

        var error = Assert.Single(ValidatePerson(person));

        Assert.Equal("overrides", error.Path);
        Assert.Contains($"at most {SettingsValidator.MaxOverrides} overrides are allowed ({explicitCount} sent)", error.Message, StringComparison.Ordinal);
    }

    // An entry that keeps a remembered track change and also has an explicit setting counts as the explicit one it is.
    [Fact]
    public void Overrides_AnEntryWithBothCountsOnce_AndAnEmptyOneWithNothingRememberedIsNotCounted()
    {
        var person = Person(p =>
        {
            p.Overrides.AddRange(Enumerable.Range(0, SettingsValidator.MaxOverrides).Select(_ =>
            {
                var both = Explicit();
                both.Manual = new ManualMemory { SubtitlesOff = true };
                return both;
            }));
            p.Overrides.AddRange(Enumerable.Range(0, 10).Select(_ => new ItemOverride { ItemId = NewId() }));
        });

        Assert.Empty(ValidatePerson(person));

        person.Overrides.Add(Explicit());

        Assert.Contains(ValidatePerson(person), e => e.Path == "overrides");
    }

    // Validation tolerates an unprepared document: an entry or its settings may be null, and neither counts nor throws.
    [Fact]
    public void Overrides_NullEntriesOrSettings_AreToleratedAndNotCounted()
    {
        var person = new PersonSettings { UserId = Guid.NewGuid() };
        person.Overrides.AddRange(Enumerable.Range(0, SettingsValidator.MaxOverrides).Select(_ => Explicit()));
        person.Overrides.Add(null!);
        person.Overrides.Add(new ItemOverride { ItemId = NewId(), Settings = null! });

        var errors = ValidatePerson(person);

        Assert.DoesNotContain(errors, e => e.Path == "overrides");
        Assert.Contains(errors, e => e.Path == "overrides[500]" && e.Message.Contains("empty", StringComparison.Ordinal));
    }

    // The same rule reaches the admin's import, which validates each person like a PUT (includeLocks: true).
    [Fact]
    public void Overrides_TheRuleAlsoHoldsWithLocks()
    {
        var person = Person(p => p.Overrides.AddRange(Enumerable.Range(0, 800).Select(_ => Remembered())));

        Assert.Empty(SettingsValidator.ValidatePerson(person, Languages, includeLocks: true));
        Assert.Empty(SettingsValidator.ValidatePerson(person, Languages, includeLocks: false));
    }

    [Fact]
    public void PersonRule_IsRegexIsForcedOff_AndPatternIsPlainText()
    {
        var person = new PersonSettings { UserId = Guid.NewGuid() };
        person.SmartRules.Add(TitleRule("Mine", "(a+)+\\1(?=x", isRegex: true));

        SettingsValidator.PreparePerson(person);

        Assert.False(person.SmartRules[0].Conditions[0].IsRegex);
        Assert.Empty(ValidatePerson(person));
    }

    [Fact]
    public void TemplateRules_AreForcedToPlainText()
    {
        var config = new PluginConfiguration { NewUserTemplate = new PersonSettings() };
        config.NewUserTemplate.SmartRules.Add(TitleRule("Template", "(", isRegex: true));

        Assert.Empty(ValidateAdmin(config));
        Assert.False(config.NewUserTemplate.SmartRules[0].Conditions[0].IsRegex);
    }

    [Fact]
    public void AdminRegex_Invalid_IsRejectedWithRuleName()
    {
        var config = new PluginConfiguration();
        config.SmartRules.Add(TitleRule("Broken pattern", "(unclosed", isRegex: true));

        var error = Assert.Single(ValidateAdmin(config));

        Assert.Equal("smartRules[0].conditions[0].values[0]", error.Path);
        Assert.Contains("Broken pattern", error.Message, StringComparison.Ordinal);
        Assert.Contains("not a valid regular expression", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"(\w+) \1")]
    [InlineData(@"foo(?=bar)")]
    [InlineData(@"(?<!x)y")]
    public void AdminRegex_BacktrackingOnlyConstructs_AreRejected(string pattern)
    {
        var config = new PluginConfiguration();
        config.SmartRules.Add(TitleRule("Needs backtracking", pattern, isRegex: true));

        var error = Assert.Single(ValidateAdmin(config));

        Assert.Contains("Needs backtracking", error.Message, StringComparison.Ordinal);
        Assert.Contains("doesn't support", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"^(Lanternbound|Cloudward)\b")]
    [InlineData(@"(?i)one piece")]
    [InlineData(@"S\d{2}E\d{2}")]
    public void AdminRegex_ValidNonBacktracking_IsAccepted(string pattern)
    {
        var config = new PluginConfiguration();
        config.SmartRules.Add(TitleRule("Fine", pattern, isRegex: true));

        Assert.Empty(ValidateAdmin(config));
        Assert.Null(SettingsValidator.CheckAdminPattern(pattern));
    }

    [Fact]
    public void AdminPlainTextPattern_IsNotCompiled()
    {
        var config = new PluginConfiguration();
        config.SmartRules.Add(TitleRule("Plain", "(unclosed", isRegex: false));

        Assert.Empty(ValidateAdmin(config));
    }

    [Fact]
    public void DuplicateRuleIds_AreRejected()
    {
        var person = Person(p =>
        {
            p.SmartRules.Add(TitleRule("One", "a", false));
            p.SmartRules.Add(TitleRule("Two", "b", false));
            p.SmartRules[0].Id = "same";
            p.SmartRules[1].Id = "SAME";
        });

        Assert.Contains(ValidatePerson(person), e => e.Path == "smartRules[1].id" && e.Message.Contains("duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRuleIds_AreAssigned()
    {
        var person = Person(p =>
        {
            p.SmartRules.Add(TitleRule("One", "a", false));
            p.SmartRules.Add(TitleRule("Two", "b", false));
        });

        Assert.All(person.SmartRules, r => Assert.False(string.IsNullOrWhiteSpace(r.Id)));
        Assert.NotEqual(person.SmartRules[0].Id, person.SmartRules[1].Id);
        Assert.Empty(ValidatePerson(person));
    }

    [Fact]
    public void LibraryScopeBurnedIn_IsRejectedForAPerson()
    {
        var person = Person(p => p.BurnedIn.Add(new BurnedInFlag { ItemId = NewId(), Scope = OverrideScope.Library, Languages = new List<string> { "en" } }));

        var error = Assert.Single(ValidatePerson(person));

        Assert.Equal("burnedIn[0].scope", error.Path);
    }

    [Fact]
    public void LibraryScopeBurnedIn_IsAllowedForTheAdmin()
    {
        var config = new PluginConfiguration();
        config.BurnedIn.Add(new BurnedInFlag { ItemId = NewId(), Scope = OverrideScope.Library, Languages = new List<string> { "en" } });

        Assert.Empty(ValidateAdmin(config));
    }

    [Fact]
    public void Ids_MustBeGuids_AndNotRepeated()
    {
        var id = NewId();
        var person = Person(p =>
        {
            p.Overrides.Add(new ItemOverride { ItemId = "../../etc/passwd" });
            p.Overrides.Add(new ItemOverride { ItemId = id });
            p.Overrides.Add(new ItemOverride { ItemId = id.ToUpperInvariant() });
            p.CollectionOrder.Add("not-an-id");
        });

        var errors = ValidatePerson(person);

        Assert.Contains(errors, e => e.Path == "overrides[0].itemId");
        Assert.Contains(errors, e => e.Path == "overrides[2].itemId" && e.Message.Contains("twice", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Path == "collectionOrder[0]");
    }

    [Fact]
    public void ContentTypeAndOriginalLanguageConditions_AreChecked()
    {
        var person = Person(p => p.SmartRules.Add(new SmartRule
        {
            Name = "r",
            Conditions = new List<RuleCondition>
            {
                new() { Kind = ConditionKind.ContentType, Values = new List<string> { "series" } },
                new() { Kind = ConditionKind.OriginalLanguage, Values = new List<string> { "ja", "nonsense-lang" } },
                new() { Kind = ConditionKind.OriginalLanguageUnderstood, Negate = true },
            },
        }));

        var errors = ValidatePerson(person);

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Path == "smartRules[0].conditions[0].values[0]");
        Assert.Contains(errors, e => e.Path == "smartRules[0].conditions[1].values[1]");
    }

    [Fact]
    public void UndefinedEnumValue_IsRejected()
    {
        var person = Person(p => p.Everyday.WhenAudioUnderstood = (SubtitleWant)42);

        Assert.Contains(ValidatePerson(person), e => e.Path == "everyday.whenAudioUnderstood");
    }

    [Fact]
    public void Locks_AreIgnoredWhenNotIncluded()
    {
        var person = Person(p => p.Locks.Understood = new[] { "bogus-language" });

        Assert.NotEmpty(SettingsValidator.ValidatePerson(person, Languages, includeLocks: true));
        Assert.Empty(SettingsValidator.ValidatePerson(person, Languages, includeLocks: false));
    }

    [Fact]
    public void FreshAdminConfiguration_IsValid()
    {
        Assert.Empty(ValidateAdmin(new PluginConfiguration()));
    }

    [Fact]
    public void AdminTables_AreChecked()
    {
        var config = new PluginConfiguration();
        config.Starter.GenreSharePercent = 150;
        config.LibraryFactsRefreshDays = 0;
        config.LanguageAliases.Add(new LanguageAlias { Raw = "vostfr", Code = "not-a-code" });
        config.LanguageAliases.Add(new LanguageAlias { Raw = "signs", Code = string.Empty });

        var errors = ValidateAdmin(config);

        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Path == "starter.genreSharePercent");
        Assert.Contains(errors, e => e.Path == "libraryFactsRefreshDays");
        Assert.Contains(errors, e => e.Path == "languageAliases[0].code");
    }

    [Fact]
    public void AdminTitleDefault_LibraryScope_IsRejected()
    {
        var config = new PluginConfiguration();
        config.Titles.Add(new TitleDefault { ItemId = NewId(), Scope = OverrideScope.Library });

        Assert.Contains(ValidateAdmin(config), e => e.Path == "titles[0].scope");
    }

    [Fact]
    public void Layer_ValidatesOnItsOwn()
    {
        Assert.Empty(SettingsValidator.ValidateLayer(new SettingsLayer { Reads = new[] { "fr-CA" } }, Languages));
        Assert.Single(SettingsValidator.ValidateLayer(new SettingsLayer { UnlabelledAudio = UnlabelledAudioRule.SpecificLanguage }, Languages));
        Assert.Single(SettingsValidator.ValidateLayer(null, Languages));
    }

    [Fact]
    public void BurnedInLanguages_NeedAtLeastOne()
    {
        Assert.Single(SettingsValidator.ValidateLanguages(new List<string>(), Languages));
        Assert.Empty(SettingsValidator.ValidateLanguages(new List<string> { "Japanese", "zh-Hans" }, Languages));
    }
}
