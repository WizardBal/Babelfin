using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;
using static Babelfin.Engine.Tests.Settings.Ctx;
using Fx = Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>"Audio tracks without a language" is admin-only: a person's own layers never set it; the admin's layers and the admin's locks do.</summary>
public class AdminOnlyFieldsTests
{
    private const string Defaults = "Babelfin's defaults";

    /// <summary>Every layer a person owns.</summary>
    public static IEnumerable<object[]> PersonLayers => new[]
    {
        "everyday", "library", "library, same for every library", "title", "season", "show", "collection", "smart rule",
    }.Select(layer => new object[] { layer });

    private static ItemContext Context() => Episode(collections: new[] { (Ids.Cloudvale, "Cloudvale") });

    private static SettingsLayer Unlabelled(string language) => new() { UnlabelledAudio = UnlabelledAudioRule.SpecificLanguage, UnlabelledAudioLanguage = language };

    /// <summary>A person whose given layer says audio without a language is Japanese.</summary>
    private static PersonSettings JapaneseIn(string layer, PersonSettings? person = null)
    {
        person ??= Person();
        var ja = Unlabelled("ja");
        switch (layer)
        {
            case "everyday":
                person.Everyday.UnlabelledAudio = ja.UnlabelledAudio;
                person.Everyday.UnlabelledAudioLanguage = ja.UnlabelledAudioLanguage;
                break;
            case "library":
                person.PerLibrary = true;
                person.Libraries.Add(MyLibrary(Ids.Shows, ja));
                break;
            case "library, same for every library":
                person.PerLibrary = false;
                person.Libraries.Add(MyLibrary(Ids.Shows, ja));
                break;
            case "title":
                person.Overrides.Add(Override(Ids.Item, OverrideScope.Title, ja));
                break;
            case "season":
                person.Overrides.Add(Override(Ids.Season, OverrideScope.Season, ja));
                break;
            case "show":
                person.Overrides.Add(Override(Ids.Series, OverrideScope.Series, ja));
                break;
            case "collection":
                person.Overrides.Add(Override(Ids.Cloudvale, OverrideScope.Collection, ja));
                break;
            default:
                person.SmartRules.Add(Rule("Mine", ja, When(ConditionKind.ContentType, "episode")));
                break;
        }

        return person;
    }

    /// <summary>A person who says Japanese in every layer they own.</summary>
    private static PersonSettings JapaneseEverywhere()
    {
        var person = Person();
        foreach (var layer in PersonLayers.Select(row => (string)row[0]).Where(l => l != "library, same for every library"))
        {
            JapaneseIn(layer, person);
        }

        return person;
    }

    [Fact]
    public void TheAdminOnlyFields_AreAudioWithoutALanguage()
    {
        Assert.Equal(new[] { SettingsFields.UnlabelledAudio }, AdminOnlyFields.All);
    }

    [Theory]
    [MemberData(nameof(PersonLayers))]
    public void PersonsLayer_IsIgnored_TheBuiltInRuleApplies(string layer)
    {
        var result = Resolve(JapaneseIn(layer), null, Context());

        Assert.Equal(UnlabelledAudioRule.OriginalWhenOnlyTrack, result.Profile.UnlabelledAudio);
        Assert.Null(result.Profile.UnlabelledAudioLanguage);
        Assert.Equal(Defaults, result.Sources[SettingsFields.UnlabelledAudio]);
    }

    [Theory]
    [MemberData(nameof(PersonLayers))]
    public void PersonsLayer_IsIgnored_TheAdminsEverydayApplies(string layer)
    {
        var admin = new PluginConfiguration { Everyday = Unlabelled("ko") };

        var result = Resolve(JapaneseIn(layer), admin, Context());

        Assert.Equal(UnlabelledAudioRule.SpecificLanguage, result.Profile.UnlabelledAudio);
        Assert.Equal("ko", result.Profile.UnlabelledAudioLanguage);
        Assert.Equal(ProfileResolver.AdminEverydayLabel, result.Sources[SettingsFields.UnlabelledAudio]);
    }

    // In both of the admin's table modes the everyday value counts (it isn't a table field).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdminsEveryday_AppliesInBothTableModes(bool perLibrary)
    {
        var admin = new PluginConfiguration { PerLibraryDefaults = perLibrary, Everyday = Unlabelled("ko") };

        var result = Resolve(JapaneseEverywhere(), admin, Context());

        Assert.Equal("ko", result.Profile.UnlabelledAudioLanguage);
        Assert.Equal(ProfileResolver.AdminEverydayLabel, result.Sources[SettingsFields.UnlabelledAudio]);
    }

    [Fact]
    public void AdminLibraryUnlabelledDefaultApplies_GeneralRulesAndGenreExtrasCannotOverrideIt()
    {
        var admin = new PluginConfiguration { Everyday = Unlabelled("ko") };
        admin.Libraries.Add(TheirLibrary(Ids.Shows, Unlabelled("zh")));
        var library = Resolve(JapaneseEverywhere(), admin, Context());
        admin.SmartRules.Add(Rule("Theirs", Unlabelled("fr"), When(ConditionKind.ContentType, "episode")));
        var extra = Unlabelled("de"); extra.WhenAudioNotUnderstood = SubtitleWant.Full;
        admin.SmartRules.Add(Rule("Drama", extra, When(ConditionKind.Genre, "Drama")));
        var genre = Resolve(JapaneseEverywhere(), admin, Context() with { Genres = new[] { "Drama" } });
        foreach (var result in new[] { library, genre })
        {
            Assert.Equal("zh", result.Profile.UnlabelledAudioLanguage);
            Assert.Equal("the admin's Shows library defaults", result.Sources[SettingsFields.UnlabelledAudio]);
        }
        Assert.Equal(SubtitleWant.Full, genre.Profile.WhenAudioNotUnderstood);
        Assert.Equal("the admin's Drama genre defaults", genre.Sources[SettingsFields.WhenAudioNotUnderstood]);
    }

    [Fact]
    public void AdminsLock_OnThePerson_StillApplies()
    {
        var person = JapaneseEverywhere();
        person.Locks = Unlabelled("de");
        var admin = new PluginConfiguration { Everyday = Unlabelled("ko") };
        admin.SmartRules.Add(Rule("Theirs", Unlabelled("fr"), When(ConditionKind.ContentType, "episode")));

        var result = Resolve(person, admin, Context());

        Assert.Equal(UnlabelledAudioRule.SpecificLanguage, result.Profile.UnlabelledAudio);
        Assert.Equal("de", result.Profile.UnlabelledAudioLanguage);
        Assert.Equal(ProfileResolver.LockLabel, result.Sources[SettingsFields.UnlabelledAudio]);
    }

    // Visible Advanced settings remain active; extra fields in unsupported rules remain recoverable.
    [Fact]
    public void VisibleAdvancedFieldsCount_GeneralAndGenreExtrasStayStoredWithoutApplying()
    {
        var person = Person(); person.Everyday.Sdh = Preference.Avoid;
        var layer = Unlabelled("ja"); layer.Format = FormatPreference.TextOnly;
        person.Overrides.Add(Override(Ids.Item, OverrideScope.Title, layer));
        var ruleLayer = Unlabelled("ja"); ruleLayer.Sdh = Preference.Prefer;
        person.SmartRules.Add(Rule("Mine", ruleLayer, When(ConditionKind.ContentType, "episode")));
        var genreLayer = ruleLayer.Clone(); genreLayer.WhenAudioNotUnderstood = SubtitleWant.Full;
        person.SmartRules.Add(Rule("Drama", genreLayer, When(ConditionKind.Genre, "Drama")));
        var result = Resolve(person, null, Context() with { Genres = new[] { "Drama" } });
        Assert.Equal(FormatPreference.TextOnly, result.Profile.Format);
        Assert.Equal("your settings for this title", result.Sources[SettingsFields.Format]);
        Assert.Equal(Preference.Avoid, result.Profile.Sdh);
        Assert.Equal("your everyday settings", result.Sources[SettingsFields.Sdh]);
        Assert.Equal(SubtitleWant.Full, result.Profile.WhenAudioNotUnderstood);
        Assert.Equal("your Drama genre settings", result.Sources[SettingsFields.WhenAudioNotUnderstood]);
        Assert.Equal(Defaults, result.Sources[SettingsFields.UnlabelledAudio]);
        Assert.Equal(UnlabelledAudioRule.SpecificLanguage, person.Overrides[0].Settings.UnlabelledAudio);
        Assert.Equal("ja", person.Overrides[0].Settings.UnlabelledAudioLanguage);
        Assert.All(person.SmartRules, r => { Assert.Equal("ja", r.Settings.UnlabelledAudioLanguage); Assert.Equal(Preference.Prefer, r.Settings.Sdh); });
    }

    [Fact]
    public void Without_CopiesOnlyWhenTheLayerSetsTheField()
    {
        var plain = new SettingsLayer { Format = FormatPreference.TextOnly };
        var set = Unlabelled("ja");
        set.Format = FormatPreference.TextOnly;

        var stripped = AdminOnlyFields.Without(set)!;

        Assert.Null(AdminOnlyFields.Without(null));
        Assert.Same(plain, AdminOnlyFields.Without(plain));
        Assert.NotSame(set, stripped);
        Assert.Null(stripped.UnlabelledAudio);
        Assert.Null(stripped.UnlabelledAudioLanguage);
        Assert.Equal(FormatPreference.TextOnly, stripped.Format);
        Assert.Equal("ja", set.UnlabelledAudioLanguage);
    }

    /// <summary>Understands Japanese, reads English: Japanese audio, no subtitles when understood, full ones when not.</summary>
    private static PersonSettings JapaneseSpeaker()
    {
        var person = Person(new[] { "ja" }, new[] { "en" });
        person.Everyday.AudioChain = Chain(Lang("ja"), Step(AudioStepKind.VideoDefault));
        person.Everyday.WhenAudioUnderstood = SubtitleWant.Off;
        person.Everyday.WhenAudioNotUnderstood = SubtitleWant.Full;
        return person;
    }

    private static Decision Decide(PersonSettings person, PluginConfiguration? admin, IReadOnlyList<Track> tracks)
    {
        var context = Movie();
        var result = Resolve(person, admin, context);
        var facts = new ItemFacts(context.OriginalLanguage, result.LibraryUsualOriginalLanguage, result.BurnedInLanguages, false);
        return Fx.Decide(tracks, result.Profile, facts, manual: result.Manual);
    }

    private static Track[] OnlyTrackUnlabelled() => new[] { Fx.Audio(1, null), Fx.Sub(2, "eng") };

    // The admin says audio without a language is Japanese: the only audio track (no language, original unknown) is Japanese and understood.
    [Fact]
    public void AdminsJapanese_TheUnlabelledOnlyTrackIsJapanese()
    {
        var admin = new PluginConfiguration { Everyday = Unlabelled("ja") };

        var d = Decide(JapaneseSpeaker(), admin, OnlyTrackUnlabelled());

        Assert.Equal(1, d.AudioIndex);
        Assert.True(d.AudioUnderstood);
        Assert.Null(d.SubtitleIndex);
        Assert.Equal("ja", Assert.Single(d.Trace, l => l.Code == "audio.unlabelled.assumed").Args!["language"]);
    }

    // The person's own "Japanese" changes nothing: the built-in rule leaves the track unknown (the original is unknown), so full English subtitles.
    [Fact]
    public void PersonsJapaneseAlone_DoesNotMakeItJapanese()
    {
        var person = JapaneseIn("everyday", JapaneseSpeaker());

        var d = Decide(person, null, OnlyTrackUnlabelled());

        Assert.Equal(1, d.AudioIndex);
        Assert.False(d.AudioUnderstood);
        Assert.Equal(2, d.SubtitleIndex);
        Assert.True(Fx.Has(d, "audio.unlabelled.unknown", 1));
        Assert.False(Fx.Has(d, "audio.unlabelled.assumed"));
    }
}
