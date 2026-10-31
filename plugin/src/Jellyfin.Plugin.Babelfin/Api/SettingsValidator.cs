using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>
/// Checks settings before they are stored . Pure: no Jellyfin calls, no I/O; the access checks on item ids
/// are the controllers' job. Every PUT and the import run through it.
/// </summary>
/// <remarks>
/// Decisions where the design is silent:
/// <list type="bullet">
/// <item>The <c>Prepare*</c> methods normalise a body before validation: absent lists become empty, smart rules get an id
/// (<see cref="SmartRule.EnsureId"/>), and every person rule (also those of the admin's new-user template, which become person rules)
/// gets <see cref="RuleCondition.IsRegex"/> forced off. Validation itself tolerates nulls, so it is safe on an unprepared document.</item>
/// <item>Ids that name Jellyfin items (overrides, burned-in flags, collection order, library layers, admin title and library defaults)
/// must parse as GUIDs; duplicates of the same item in one list are rejected, as are duplicate rule ids (case-insensitive).</item>
/// <item>A condition that needs values (everything except "original language understood") must have at least one; content-type values
/// must be "movie" or "episode"; original-language values must resolve through the language table.</item>
/// <item>A language step needs a language; <see cref="UnlabelledAudioRule.SpecificLanguage"/> needs <see cref="SettingsLayer.UnlabelledAudioLanguage"/>.</item>
/// <item>Enum values that are not defined (numbers sent instead of names) are rejected.</item>
/// <item>Admin-only tables have their own caps: <see cref="MaxKeywordsPerList"/> words per keyword list, <see cref="MaxStarterWords"/> starter words or genres,
/// <see cref="MaxAliases"/> language aliases; percentages are 0–100 and the library-facts refresh interval 1–365 days. An alias code must be empty
/// ("not a language") or resolve.</item>
/// <item>The collection order shares the override cap (<see cref="MaxOverrides"/>). A person's overrides count against it only when they have at least one explicit setting:
/// an entry that holds only a remembered track change (its settings are empty) is not counted, because those are added freely during playback and an export, a backup or a copy must not fail over them.</item>
/// <item>App modes : at most <see cref="MaxAppModes"/> rows; a client name is required, at most <see cref="MaxClientLength"/> characters,
/// trimmed by <see cref="PrepareAdmin"/>, and unique ignoring case; the mode, the unknown-app mode and "correct after start" must be defined values.
/// The timing numbers have fixed ranges: manual change after 0–600 s, served window 1–1440 min, autoplay window 0–3600 s.</item>
/// </list>
/// </remarks>
public static class SettingsValidator
{
    /// <summary>The most languages in one list.</summary>
    public const int MaxLanguagesPerList = 50;

    /// <summary>The longest string accepted anywhere.</summary>
    public const int MaxStringLength = 200;

    /// <summary>The most steps in an audio chain.</summary>
    public const int MaxAudioSteps = 20;

    /// <summary>The most smart rules in one list.</summary>
    public const int MaxRules = 200;

    /// <summary>The most conditions in one rule.</summary>
    public const int MaxConditionsPerRule = 20;

    /// <summary>The most values in one condition.</summary>
    public const int MaxValuesPerCondition = 50;

    /// <summary>The most overrides (and title defaults, and collection-order entries). A person's overrides are counted by their explicit settings only (<c>PUT Me/Overrides</c> and <see cref="ValidatePerson"/> alike): remembered track changes are not capped.</summary>
    public const int MaxOverrides = 500;

    /// <summary>The most burned-in flags.</summary>
    public const int MaxBurnedIn = 200;

    /// <summary>The most library layers.</summary>
    public const int MaxLibraries = 100;

    /// <summary>The most tie-breaks.</summary>
    public const int MaxTieBreaks = 20;

    /// <summary>The most words in one keyword list (the built-in lists are well below this).</summary>
    public const int MaxKeywordsPerList = 1000;

    /// <summary>The most starter library words or genres.</summary>
    public const int MaxStarterWords = 100;

    /// <summary>The most admin language aliases.</summary>
    public const int MaxAliases = 1000;

    /// <summary>The most rows in the app mode table.</summary>
    public const int MaxAppModes = 100;

    /// <summary>The longest client name in the app mode table.</summary>
    public const int MaxClientLength = 100;

    /// <summary>The regex options admin patterns are compiled with (the same as the rule matcher).</summary>
    public const RegexOptions AdminRegexOptions = RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Normalises a person's document before validation: empty lists for absent ones, rule ids, and plain-text title patterns.</summary>
    /// <param name="person">The document (changed in place).</param>
    public static void PreparePerson(PersonSettings person)
    {
        ArgumentNullException.ThrowIfNull(person);
        person.Everyday ??= new SettingsLayer();
        person.Locks ??= new SettingsLayer();
        person.Libraries ??= new List<LibraryLayer>();
        person.SmartRules ??= new List<SmartRule>();
        person.Overrides ??= new List<ItemOverride>();
        person.CollectionOrder ??= new List<string>();
        person.BurnedIn ??= new List<BurnedInFlag>();
        person.Libraries.RemoveAll(l => l is null);
        person.Overrides.RemoveAll(o => o is null);
        person.BurnedIn.RemoveAll(b => b is null);
        person.CollectionOrder.RemoveAll(c => c is null);
        foreach (var library in person.Libraries)
        {
            library.Settings ??= new SettingsLayer();
        }

        foreach (var o in person.Overrides)
        {
            o.Settings ??= new SettingsLayer();
        }

        foreach (var flag in person.BurnedIn)
        {
            flag.Languages ??= new List<string>();
        }

        PrepareRules(person.SmartRules, personRules: true);
    }

    /// <summary>Normalises the admin configuration before validation: empty lists for absent ones, rule ids, and the template as a person document.</summary>
    /// <param name="config">The configuration (changed in place).</param>
    public static void PrepareAdmin(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Everyday ??= new SettingsLayer();
        config.Libraries ??= new List<LibraryDefault>();
        config.SmartRules ??= new List<SmartRule>();
        config.Titles ??= new List<TitleDefault>();
        config.BurnedIn ??= new List<BurnedInFlag>();
        config.Starter ??= new StarterSettings();
        config.Keywords ??= new KeywordSettings();
        config.LanguageAliases ??= new List<LanguageAlias>();
        config.AppModes ??= new List<AppMode>();
        config.LastUpgradeNote ??= string.Empty;
        config.AppModes.RemoveAll(a => a is null);
        foreach (var app in config.AppModes)
        {
            app.Client = app.Client?.Trim() ?? string.Empty;
        }

        config.Libraries.RemoveAll(l => l is null);
        config.Titles.RemoveAll(t => t is null);
        config.BurnedIn.RemoveAll(b => b is null);
        config.LanguageAliases.RemoveAll(a => a is null);
        foreach (var library in config.Libraries)
        {
            library.Settings ??= new SettingsLayer();
        }

        foreach (var title in config.Titles)
        {
            title.Settings ??= new SettingsLayer();
        }

        foreach (var flag in config.BurnedIn)
        {
            flag.Languages ??= new List<string>();
        }

        PrepareRules(config.SmartRules, personRules: false);
        if (config.NewUserTemplate is { } template)
        {
            PreparePerson(template);
        }
    }

    /// <summary>Validates a person's document (call <see cref="PreparePerson"/> first).</summary>
    /// <param name="person">The document.</param>
    /// <param name="languages">The language table.</param>
    /// <param name="includeLocks">Whether the locks layer is checked (false when the locks are kept from storage, as for <c>Me/Settings</c>).</param>
    /// <returns>The problems; empty when valid.</returns>
    public static IReadOnlyList<ValidationError> ValidatePerson(PersonSettings person, LanguageTable languages, bool includeLocks = true)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(languages);
        var c = new Collector(languages);
        c.Person(person, string.Empty, includeLocks);
        return c.Errors;
    }

    /// <summary>Validates the admin configuration (call <see cref="PrepareAdmin"/> first). Admin regex patterns must compile under <see cref="RegexOptions.NonBacktracking"/>.</summary>
    /// <param name="config">The configuration.</param>
    /// <param name="languages">The language table.</param>
    /// <returns>The problems; empty when valid.</returns>
    public static IReadOnlyList<ValidationError> ValidateAdmin(PluginConfiguration config, LanguageTable languages)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(languages);
        var c = new Collector(languages);
        c.Admin(config);
        return c.Errors;
    }

    /// <summary>Validates one settings layer.</summary>
    /// <param name="layer">The layer (null is reported).</param>
    /// <param name="languages">The language table.</param>
    /// <param name="path">The path prefix for messages ("" for a body that is the layer itself).</param>
    /// <returns>The problems; empty when valid.</returns>
    public static IReadOnlyList<ValidationError> ValidateLayer(SettingsLayer? layer, LanguageTable languages, string path = "")
    {
        ArgumentNullException.ThrowIfNull(languages);
        var c = new Collector(languages);
        c.Layer(layer, path);
        return c.Errors;
    }

    /// <summary>Validates a list of burned-in languages (at least one, each resolvable).</summary>
    /// <param name="values">The languages.</param>
    /// <param name="languages">The language table.</param>
    /// <param name="path">The path prefix.</param>
    /// <returns>The problems; empty when valid.</returns>
    public static IReadOnlyList<ValidationError> ValidateLanguages(IReadOnlyList<string>? values, LanguageTable languages, string path = "languages")
    {
        ArgumentNullException.ThrowIfNull(languages);
        var c = new Collector(languages);
        c.BurnedInLanguages(values, path);
        return c.Errors;
    }

    /// <summary>Validates a stored admin title pattern for import and settings compatibility; it is not evaluated during profile resolution.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>Null when it compiles, else the reason.</returns>
    public static string? CheckAdminPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return "the pattern is empty";
        }

        try
        {
            _ = new Regex(pattern, AdminRegexOptions, TimeSpan.FromMilliseconds(100));
            return null;
        }
        catch (NotSupportedException ex)
        {
            return "uses something the safe (non-backtracking) regular expression engine doesn't support, such as backreferences or lookarounds: " + ex.Message;
        }
        catch (ArgumentException ex)
        {
            return "is not a valid regular expression: " + ex.Message;
        }
    }

    private static void PrepareRules(List<SmartRule> rules, bool personRules)
    {
        rules.RemoveAll(r => r is null);
        foreach (var rule in rules)
        {
            rule.EnsureId();
            rule.Name ??= string.Empty;
            rule.Settings ??= new SettingsLayer();
            rule.Conditions ??= new List<RuleCondition>();
            rule.Conditions.RemoveAll(x => x is null);
            foreach (var condition in rule.Conditions)
            {
                condition.Values ??= new List<string>();
                if (personRules)
                {
                    // A person's title patterns are plain "contains" text, never compiled.
                    condition.IsRegex = false;
                }
            }
        }
    }

    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;

    private sealed class Collector
    {
        private readonly LanguageTable _languages;

        public Collector(LanguageTable languages) => _languages = languages;

        public List<ValidationError> Errors { get; } = new();

        public void Add(string path, string message) => Errors.Add(new ValidationError(path, message));

        public void Person(PersonSettings person, string prefix, bool includeLocks)
        {
            Layer(person.Everyday, Join(prefix, "everyday"));
            if (includeLocks)
            {
                Layer(person.Locks, Join(prefix, "locks"));
            }

            var libraries = person.Libraries ?? new List<LibraryLayer>();
            Count(libraries.Count, MaxLibraries, Join(prefix, "libraries"), "libraries");
            var seenLibraries = new HashSet<Guid>();
            for (var i = 0; i < libraries.Count; i++)
            {
                var path = $"{Join(prefix, "libraries")}[{i}]";
                var library = libraries[i];
                if (library is null)
                {
                    Add(path, "an entry is empty");
                    continue;
                }

                Id(library.LibraryId, path + ".libraryId", seenLibraries, "library");
                Text(library.Name, path + ".name");
                Layer(library.Settings, path + ".settings");
            }

            Rules(person.SmartRules, Join(prefix, "smartRules"), admin: false);

            var overrides = person.Overrides ?? new List<ItemOverride>();

            // Only overrides with explicit settings count (as for PUT Me/Overrides): remembered track changes are not capped, so a person who has
            // many of them can still be saved, restored from a backup or copied to.
            Count(overrides.Count(o => o?.Settings is { IsEmpty: false }), MaxOverrides, Join(prefix, "overrides"), "overrides");
            var seenOverrides = new HashSet<Guid>();
            for (var i = 0; i < overrides.Count; i++)
            {
                var path = $"{Join(prefix, "overrides")}[{i}]";
                var o = overrides[i];
                if (o is null)
                {
                    Add(path, "an entry is empty");
                    continue;
                }

                Id(o.ItemId, path + ".itemId", seenOverrides, "override");
                ItemScope(o.Scope, path + ".scope", "library settings belong in the libraries list, not in an override");
                Text(o.Name, path + ".name");
                Layer(o.Settings, path + ".settings");
            }

            var order = person.CollectionOrder ?? new List<string>();
            Count(order.Count, MaxOverrides, Join(prefix, "collectionOrder"), "collections");
            var seenOrder = new HashSet<Guid>();
            for (var i = 0; i < order.Count; i++)
            {
                Id(order[i], $"{Join(prefix, "collectionOrder")}[{i}]", seenOrder, "collection");
            }

            BurnedInFlags(person.BurnedIn, Join(prefix, "burnedIn"), allowLibrary: false);
        }

        public void Admin(PluginConfiguration config)
        {
            Layer(config.Everyday, "everyday");

            var libraries = config.Libraries ?? new List<LibraryDefault>();
            Count(libraries.Count, MaxLibraries, "libraries", "libraries");
            var seenLibraries = new HashSet<Guid>();
            for (var i = 0; i < libraries.Count; i++)
            {
                var path = $"libraries[{i}]";
                var library = libraries[i];
                if (library is null)
                {
                    Add(path, "an entry is empty");
                    continue;
                }

                Id(library.LibraryId, path + ".libraryId", seenLibraries, "library");
                Text(library.Name, path + ".name");
                Defined(library.OriginalAudio, path + ".originalAudio");
                Layer(library.Settings, path + ".settings");
            }

            Rules(config.SmartRules, "smartRules", admin: true);

            var titles = config.Titles ?? new List<TitleDefault>();
            Count(titles.Count, MaxOverrides, "titles", "title defaults");
            var seenTitles = new HashSet<Guid>();
            for (var i = 0; i < titles.Count; i++)
            {
                var path = $"titles[{i}]";
                var t = titles[i];
                if (t is null)
                {
                    Add(path, "an entry is empty");
                    continue;
                }

                Id(t.ItemId, path + ".itemId", seenTitles, "title default");
                ItemScope(t.Scope, path + ".scope", "library defaults belong in the libraries list, not in a title default");
                Text(t.Name, path + ".name");
                Layer(t.Settings, path + ".settings");
            }

            BurnedInFlags(config.BurnedIn, "burnedIn", allowLibrary: true);

            if (config.NewUserTemplate is { } template)
            {
                Person(template, "newUserTemplate", includeLocks: true);
            }

            if (config.Starter is { } starter)
            {
                Words(starter.OriginalAudioLibraryWords, "starter.originalAudioLibraryWords", MaxStarterWords);
                Words(starter.OriginalAudioGenres, "starter.originalAudioGenres", MaxStarterWords);
                Percent(starter.GenreSharePercent, "starter.genreSharePercent");
                Percent(starter.UsualOriginalLanguagePercent, "starter.usualOriginalLanguagePercent");
                Defined(starter.ForeignTitles, "starter.foreignTitles");
            }

            if (config.Keywords is { } keywords)
            {
                Words(keywords.PartialWords, "keywords.partialWords", MaxKeywordsPerList);
                Words(keywords.SdhWords, "keywords.sdhWords", MaxKeywordsPerList);
                Words(keywords.FullWords, "keywords.fullWords", MaxKeywordsPerList);
                Words(keywords.CommentaryWords, "keywords.commentaryWords", MaxKeywordsPerList);
                Words(keywords.AudioDescriptionWords, "keywords.audioDescriptionWords", MaxKeywordsPerList);
            }

            var aliases = config.LanguageAliases ?? new List<LanguageAlias>();
            Count(aliases.Count, MaxAliases, "languageAliases", "aliases");
            for (var i = 0; i < aliases.Count; i++)
            {
                var path = $"languageAliases[{i}]";
                var alias = aliases[i];
                if (alias is null)
                {
                    Add(path, "an entry is empty");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(alias.Raw))
                {
                    Add(path + ".raw", "the spelling is empty");
                }

                Text(alias.Raw, path + ".raw");
                if (!string.IsNullOrWhiteSpace(alias.Code))
                {
                    Language(alias.Code, path + ".code");
                }
            }

            if (config.LibraryFactsRefreshDays is < 1 or > 365)
            {
                Add("libraryFactsRefreshDays", "must be between 1 and 365 days");
            }

            AppModes(config.AppModes);
            Defined(config.UnknownAppMode, "unknownAppMode");
            Defined(config.CorrectAfterStart, "correctAfterStart");
            Range(config.ManualChangeAfterSeconds, 0, 600, "manualChangeAfterSeconds", "seconds");
            Range(config.ServedWindowMinutes, 1, 1440, "servedWindowMinutes", "minutes");
            Range(config.AutoplayWindowSeconds, 0, 3600, "autoplayWindowSeconds", "seconds");
        }

        private void AppModes(List<AppMode>? apps)
        {
            apps ??= new List<AppMode>();
            Count(apps.Count, MaxAppModes, "appModes", "apps");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < apps.Count; i++)
            {
                var path = $"appModes[{i}]";
                var app = apps[i];
                if (app is null)
                {
                    Add(path, "an entry is empty");
                    continue;
                }

                var client = app.Client?.Trim();
                if (string.IsNullOrEmpty(client))
                {
                    Add(path + ".client", "the app name is empty");
                }
                else if (client.Length > MaxClientLength)
                {
                    Add(path + ".client", $"is longer than {MaxClientLength} characters");
                }
                else if (!seen.Add(client))
                {
                    Add(path + ".client", $"the app '{Clip(client)}' appears twice");
                }

                Defined(app.Mode, path + ".mode");
            }
        }

        private void Range(int value, int min, int max, string path, string unit)
        {
            if (value < min || value > max)
            {
                Add(path, $"must be between {min} and {max} {unit}");
            }
        }

        public void Layer(SettingsLayer? layer, string path)
        {
            if (layer is null)
            {
                Add(path, "the settings are missing");
                return;
            }

            LanguageList(layer.Understood, Join(path, "understood"));
            LanguageList(layer.Reads, Join(path, "reads"));
            LanguageList(layer.SubtitleLanguages, Join(path, "subtitleLanguages"));

            if (layer.AudioChain is { } chain)
            {
                var chainPath = Join(path, "audioChain");
                Count(chain.Length, MaxAudioSteps, chainPath, "audio steps");
                for (var i = 0; i < chain.Length; i++)
                {
                    var stepPath = $"{chainPath}[{i}]";
                    var step = chain[i];
                    if (step is null)
                    {
                        Add(stepPath, "a step is empty");
                        continue;
                    }

                    Defined(step.Kind, stepPath + ".kind");
                    if (step.Kind == AudioStepKind.Language)
                    {
                        Language(step.Language, stepPath + ".language");
                    }
                    else
                    {
                        Text(step.Language, stepPath + ".language");
                    }

                    Text(step.TitleContains, stepPath + ".titleContains");
                }
            }

            if (layer.TieBreaks is { } ties)
            {
                Count(ties.Length, MaxTieBreaks, Join(path, "tieBreaks"), "tie-breaks");
                for (var i = 0; i < ties.Length; i++)
                {
                    Defined(ties[i], $"{Join(path, "tieBreaks")}[{i}]");
                }
            }

            Defined(layer.ForeignTitles, Join(path, "foreignTitles"));
            Defined(layer.UnlabelledAudio, Join(path, "unlabelledAudio"));
            if (layer.UnlabelledAudio == UnlabelledAudioRule.SpecificLanguage)
            {
                Language(layer.UnlabelledAudioLanguage, Join(path, "unlabelledAudioLanguage"));
            }
            else if (!string.IsNullOrWhiteSpace(layer.UnlabelledAudioLanguage))
            {
                Language(layer.UnlabelledAudioLanguage, Join(path, "unlabelledAudioLanguage"));
            }

            Defined(layer.OriginalSource, Join(path, "originalSource"));
            Defined(layer.WhenAudioUnderstood, Join(path, "whenAudioUnderstood"));
            Defined(layer.WhenAudioNotUnderstood, Join(path, "whenAudioNotUnderstood"));
            Defined(layer.Sdh, Join(path, "sdh"));
            Defined(layer.Format, Join(path, "format"));
            Defined(layer.Embedded, Join(path, "embedded"));
            Defined(layer.IfNothingMatches, Join(path, "ifNothingMatches"));
            Defined(layer.Memory, Join(path, "memory"));
        }

        public void BurnedInLanguages(IReadOnlyList<string>? values, string path)
        {
            if (values is null || values.Count == 0)
            {
                Add(path, "name at least one burned-in language");
                return;
            }

            LanguageList(values, path);
        }

        private void Rules(List<SmartRule>? rules, string path, bool admin)
        {
            if (rules is null)
            {
                return;
            }

            Count(rules.Count, MaxRules, path, "smart rules");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rules.Count; i++)
            {
                var rulePath = $"{path}[{i}]";
                var rule = rules[i];
                if (rule is null)
                {
                    Add(rulePath, "a rule is empty");
                    continue;
                }

                Text(rule.Id, rulePath + ".id");
                Text(rule.Name, rulePath + ".name");
                if (!string.IsNullOrWhiteSpace(rule.Id) && !ids.Add(rule.Id.Trim()))
                {
                    Add(rulePath + ".id", $"duplicate rule id '{rule.Id}'");
                }

                Defined(rule.Match, rulePath + ".match");
                Layer(rule.Settings, rulePath + ".settings");

                var conditions = rule.Conditions ?? new List<RuleCondition>();
                Count(conditions.Count, MaxConditionsPerRule, rulePath + ".conditions", "conditions");
                for (var j = 0; j < conditions.Count; j++)
                {
                    Condition(conditions[j], $"{rulePath}.conditions[{j}]", rule.Name, admin);
                }
            }
        }

        private void Condition(RuleCondition? condition, string path, string? ruleName, bool admin)
        {
            if (condition is null)
            {
                Add(path, "a condition is empty");
                return;
            }

            if (!Defined(condition.Kind, path + ".kind"))
            {
                return;
            }

            var values = condition.Values ?? new List<string>();
            Count(values.Count, MaxValuesPerCondition, path + ".values", "values");
            if (condition.Kind != ConditionKind.OriginalLanguageUnderstood && values.Count(v => !string.IsNullOrWhiteSpace(v)) == 0)
            {
                Add(path + ".values", "this condition needs at least one value");
            }

            for (var k = 0; k < values.Count; k++)
            {
                var valuePath = $"{path}.values[{k}]";
                var value = values[k];
                Text(value, valuePath);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                switch (condition.Kind)
                {
                    case ConditionKind.OriginalLanguage:
                        Language(value, valuePath);
                        break;
                    case ConditionKind.ContentType:
                        var trimmed = value.Trim();
                        if (!string.Equals(trimmed, "movie", StringComparison.OrdinalIgnoreCase) && !string.Equals(trimmed, "episode", StringComparison.OrdinalIgnoreCase))
                        {
                            Add(valuePath, $"content type must be 'movie' or 'episode', not '{value}'");
                        }

                        break;
                    case ConditionKind.TitleMatches when admin && condition.IsRegex:
                        var problem = CheckAdminPattern(value);
                        if (problem is not null)
                        {
                            Add(valuePath, $"smart rule '{ruleName}': the pattern '{value}' {problem}");
                        }

                        break;
                    default:
                        break;
                }
            }
        }

        private void BurnedInFlags(List<BurnedInFlag>? flags, string path, bool allowLibrary)
        {
            if (flags is null)
            {
                return;
            }

            Count(flags.Count, MaxBurnedIn, path, "burned-in flags");
            var seen = new HashSet<(Guid, OverrideScope)>();
            for (var i = 0; i < flags.Count; i++)
            {
                var flagPath = $"{path}[{i}]";
                var flag = flags[i];
                if (flag is null)
                {
                    Add(flagPath, "a flag is empty");
                    continue;
                }

                if (Guid.TryParse(flag.ItemId, out var id) && id != Guid.Empty)
                {
                    if (!seen.Add((id, flag.Scope)))
                    {
                        Add(flagPath + ".itemId", "the same item and scope appear twice");
                    }
                }
                else
                {
                    Add(flagPath + ".itemId", $"'{Clip(flag.ItemId)}' is not an item id");
                }

                if (Defined(flag.Scope, flagPath + ".scope") && flag.Scope == OverrideScope.Library && !allowLibrary)
                {
                    Add(flagPath + ".scope", "a whole-library burned-in flag can only be set by an admin");
                }

                Text(flag.Name, flagPath + ".name");
                Text(flag.SetBy, flagPath + ".setBy");
                BurnedInLanguages(flag.Languages, flagPath + ".languages");
            }
        }

        private void ItemScope(OverrideScope scope, string path, string libraryMessage)
        {
            if (Defined(scope, path) && scope == OverrideScope.Library)
            {
                Add(path, libraryMessage);
            }
        }

        private void LanguageList(IReadOnlyList<string>? values, string path)
        {
            if (values is null)
            {
                return;
            }

            Count(values.Count, MaxLanguagesPerList, path, "languages");
            for (var i = 0; i < values.Count; i++)
            {
                Language(values[i], $"{path}[{i}]");
            }
        }

        private void Language(string? value, string path)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Add(path, "a language is required");
                return;
            }

            if (!Text(value, path))
            {
                return;
            }

            if (_languages.Resolve(value) is null)
            {
                Add(path, $"unknown language '{value}'");
            }
        }

        private void Words(List<string>? words, string path, int max)
        {
            if (words is null)
            {
                return;
            }

            Count(words.Count, max, path, "entries");
            for (var i = 0; i < words.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(words[i]))
                {
                    Add($"{path}[{i}]", "an entry is empty");
                    continue;
                }

                Text(words[i], $"{path}[{i}]");
            }
        }

        private void Id(string? value, string path, HashSet<Guid> seen, string what)
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
            {
                Add(path, $"'{Clip(value)}' is not an item id");
                return;
            }

            if (!seen.Add(id))
            {
                Add(path, $"the same {what} appears twice");
            }
        }

        private bool Text(string? value, string path)
        {
            if (value is not null && value.Length > MaxStringLength)
            {
                Add(path, $"is longer than {MaxStringLength} characters");
                return false;
            }

            return true;
        }

        private void Count(int count, int max, string path, string what)
        {
            if (count > max)
            {
                Add(path, $"at most {max} {what} are allowed ({count} sent)");
            }
        }

        private void Percent(int value, string path)
        {
            if (value is < 0 or > 100)
            {
                Add(path, "must be between 0 and 100");
            }
        }

        private bool Defined<T>(T value, string path)
            where T : struct, Enum
        {
            if (!Enum.IsDefined(value))
            {
                Add(path, $"'{value}' is not a valid choice");
                return false;
            }

            return true;
        }

        private void Defined<T>(T? value, string path)
            where T : struct, Enum
        {
            if (value.HasValue)
            {
                Defined(value.Value, path);
            }
        }

        private static string Clip(string? value) => value is null ? string.Empty : value.Length > 40 ? value[..40] + "…" : value;
    }
}
