using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;
using static Babelfin.Engine.Tests.Settings.Ctx;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>The enabled, single positive genre contract used by profile resolution.</summary>
public class RuleMatcherTests
{
    [Theory]
    [InlineData("Anime", "Anime", true)]
    [InlineData("  aNiMe\t", "\tANIME  ", true)]
    [InlineData("Action", "Live Action", false)]
    [InlineData("Drama", "Anime", false)]
    public void GenreRequiresWholeName_IgnoringCaseAndSurroundingWhitespace(string wanted, string actual, bool expected)
    {
        var rule = Rule("genre", new SettingsLayer(), When(ConditionKind.Genre, wanted));

        Assert.Equal(expected, RuleMatcher.MatchesGenre(rule, Episode(genres: new[] { actual })));
    }

    [Theory]
    [InlineData(RuleMatch.All)]
    [InlineData(RuleMatch.Any)]
    public void SingleGenreMatchesAnyItemGenre_ForEitherStoredMatchMode(RuleMatch match)
    {
        var rule = Rule("genre", new SettingsLayer(), When(ConditionKind.Genre, "Action"));
        rule.Match = match;

        Assert.True(RuleMatcher.MatchesGenre(rule, Episode(genres: new[] { "Anime", " Action " })));
    }

    [Fact]
    public void MissingGenresCannotMatchOtherItemFacts()
    {
        var rule = Rule("genre", new SettingsLayer(), When(ConditionKind.Genre, "Anime"));
        var context = Episode(libraryName: "Anime", name: "Anime", seriesName: "Anime",
            tags: new[] { "Anime" }, studios: new[] { "Anime" }, countries: new[] { "Anime" });

        Assert.False(RuleMatcher.MatchesGenre(rule, context));
        Assert.False(RuleMatcher.MatchesGenre(rule, context with { Genres = new[] { null!, "", " " } }));
    }

    [Theory]
    [InlineData(ConditionKind.Library)]
    [InlineData(ConditionKind.Tag)]
    [InlineData(ConditionKind.Collection)]
    [InlineData(ConditionKind.Studio)]
    [InlineData(ConditionKind.Country)]
    [InlineData(ConditionKind.OriginalLanguage)]
    [InlineData(ConditionKind.OriginalLanguageUnderstood)]
    [InlineData(ConditionKind.ContentType)]
    [InlineData(ConditionKind.Client)]
    [InlineData(ConditionKind.TitleMatches)]
    public void GeneralConditionsRemainInactive(ConditionKind kind)
    {
        var value = kind == ConditionKind.ContentType ? "episode" : "Anime";
        var rule = Rule("stored", new SettingsLayer(), When(kind, value));
        var context = Episode(libraryName: "Anime", original: "Anime", genres: new[] { "Anime" },
            collections: new[] { (Ids.Cloudvale, "Anime") }, name: "Anime", seriesName: "Anime", client: "Anime",
            tags: new[] { "Anime" }, studios: new[] { "Anime" }, countries: new[] { "Anime" });

        Assert.False(RuleMatcher.MatchesGenre(rule, context));
    }

    [Fact]
    public void DisabledNegatedAndRegexGenreRowsRemainInactive()
    {
        var context = Episode(genres: new[] { "Anime" });
        var rule = Rule("genre", new SettingsLayer(), When(ConditionKind.Genre, "Anime"));
        rule.Enabled = false;
        Assert.False(RuleMatcher.MatchesGenre(rule, context));

        rule.Enabled = true;
        rule.Conditions[0].Negate = true;
        Assert.False(RuleMatcher.MatchesGenre(rule, context));
        Assert.False(RuleMatcher.MatchesGenre(rule, Episode(genres: new[] { "Drama" })));

        rule.Conditions[0].Negate = false;
        rule.Conditions[0].IsRegex = true;
        Assert.False(RuleMatcher.MatchesGenre(rule, context));
    }

    [Theory]
    [InlineData(RuleMatch.All)]
    [InlineData(RuleMatch.Any)]
    public void MultipleConditionsOrGenreNamesRemainInactive(RuleMatch match)
    {
        var context = Episode(genres: new[] { "Anime", "Action" });
        var rule = Rule("stored", new SettingsLayer(), When(ConditionKind.Genre, "Anime", "Action"));
        rule.Match = match;
        Assert.False(RuleMatcher.MatchesGenre(rule, context));

        rule.Conditions = new() { When(ConditionKind.Genre, "Anime"), When(ConditionKind.Genre, "Action") };
        Assert.False(RuleMatcher.MatchesGenre(rule, context));
    }

    [Fact]
    public void IncompleteGenreRowsAndUnknownMatchModesRemainInactive()
    {
        var context = Episode(genres: new[] { "Anime" });
        var rule = Rule("stored", new SettingsLayer());
        Assert.False(RuleMatcher.MatchesGenre(rule, context));
        rule.Conditions = null!;
        Assert.False(RuleMatcher.MatchesGenre(rule, context));
        rule.Conditions = new() { null! };
        Assert.False(RuleMatcher.MatchesGenre(rule, context));

        var condition = When(ConditionKind.Genre);
        rule.Conditions = new() { condition };
        Assert.False(RuleMatcher.MatchesGenre(rule, context));
        condition.Values = null!;
        Assert.False(RuleMatcher.MatchesGenre(rule, context));
        condition.Values = new() { " \t" };
        Assert.False(RuleMatcher.MatchesGenre(rule, context));
        condition.Values = new() { null! };
        Assert.False(RuleMatcher.MatchesGenre(rule, context));
        condition.Values = new() { "Anime" };
        rule.Match = (RuleMatch)99;
        Assert.False(RuleMatcher.MatchesGenre(rule, context));
    }
}
