using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;
using static Babelfin.Engine.Tests.Settings.Ctx;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>the active table precedence, provenance and ignored legacy switches.</summary>
public class PrecedenceTests
{
    // Each active table layer beats all lower layers, across both people's table modes.
    private static readonly (string Marker, string Label)[] Table =
    {
        ("my-title", "your settings for this title"),
        ("my-season", "your settings for this season"),
        ("my-series", "your settings for this show"),
        ("my-genre", "your Mine genre settings"),
        ("my-row", "your Shows library settings"),
        ("admin-genre", "the admin's Theirs genre defaults"),
        ("admin-row", "the admin's Shows library defaults"),
    };
    public static IEnumerable<object[]> Rows =>
        from row in Enumerable.Range(0, Table.Length)
        from personPerLibrary in new[] { false, true }
        from adminPerLibrary in new[] { false, true }
        select new object[] { row, personPerLibrary, adminPerLibrary };
    private static ItemContext Context() => Episode(genres: new[] { "Drama" }, collections: new[] { (Ids.Cloudvale, "Cloudvale") });
    private static SettingsLayer TableLayer(string marker) => new()
    {
        SubtitleLanguages = new[] { marker }, AudioChain = Chain(Lang(marker)), WhenAudioNotUnderstood = SubtitleWant.Full,
    };
    private static string Label(int row, bool personPerLibrary, bool adminPerLibrary) => Table[row].Marker switch
    {
        "my-row" when !personPerLibrary => "your settings for every library",
        "admin-row" when !adminPerLibrary => ProfileResolver.AdminEverydayLabel,
        _ => Table[row].Label,
    };
    private static (PersonSettings Person, PluginConfiguration Admin) Stack(int from, bool everywhere = false,
        bool personPerLibrary = true, bool adminPerLibrary = true)
    {
        var person = Person(); person.UseEverydayEverywhere = everywhere; person.PerLibrary = personPerLibrary;
        var admin = new PluginConfiguration { PerLibraryDefaults = adminPerLibrary };
        bool On(string marker) => Array.FindIndex(Table, t => t.Marker == marker) >= from;
        person.Locks = TableLayer("hidden-lock");
        void Scope(Guid id, OverrideScope scope, string marker)
        {
            if (On(marker)) person.Overrides.Add(Override(id, scope, TableLayer(marker)));
            admin.Titles.Add(Title(id, scope, TableLayer("hidden-admin-scope")));
        }
        Scope(Ids.Item, OverrideScope.Title, "my-title");
        Scope(Ids.Season, OverrideScope.Season, "my-season");
        Scope(Ids.Series, OverrideScope.Series, "my-series");
        admin.Titles.Add(Title(Ids.Cloudvale, OverrideScope.Collection, TableLayer("hidden-admin-collection")));
        person.SmartRules.Add(Rule("Hidden general rule", TableLayer("hidden-general"), When(ConditionKind.ContentType, "episode")));
        admin.SmartRules.Add(Rule("Hidden admin rule", TableLayer("hidden-admin-general"), When(ConditionKind.ContentType, "episode")));
        person.Retired.GenreRules.Add(Rule("Archived genre", TableLayer("archived-genre"), When(ConditionKind.Genre, "Drama")));
        if (On("my-genre")) person.SmartRules.Add(Rule("Mine", TableLayer("my-genre"), When(ConditionKind.Genre, "drama")));
        if (On("admin-genre")) admin.SmartRules.Add(Rule("Theirs", TableLayer("admin-genre"), When(ConditionKind.Genre, "Drama")));
        // Stashed rows from the other table mode must stay stored without taking precedence.
        if (personPerLibrary) person.Everyday = TableLayer("inactive-person-all");
        else person.Libraries.Add(MyLibrary(Ids.Shows, TableLayer("inactive-person-library")));
        if (adminPerLibrary) admin.Everyday = TableLayer("inactive-admin-all");
        else admin.Libraries.Add(TheirLibrary(Ids.Shows, TableLayer("inactive-admin-library")));
        if (On("my-row"))
        {
            if (personPerLibrary) person.Libraries.Add(MyLibrary(Ids.Shows, TableLayer("my-row")));
            else person.Everyday = TableLayer("my-row");
        }
        if (On("admin-row"))
        {
            if (adminPerLibrary) admin.Libraries.Add(TheirLibrary(Ids.Shows, TableLayer("admin-row")));
            else admin.Everyday = TableLayer("admin-row");
        }
        return (person, admin);
    }
    [Theory]
    [MemberData(nameof(Rows))]
    public void TableLayer_BeatsEveryLowerLayer_AndHiddenDecoysCannotWin(int row, bool personPerLibrary, bool adminPerLibrary)
    {
        var (person, admin) = Stack(row, personPerLibrary: personPerLibrary, adminPerLibrary: adminPerLibrary);
        var result = Resolve(person, admin, Context());
        Assert.Equal(new[] { Table[row].Marker }, result.Profile.SubtitleLanguages);
        Assert.Equal(Table[row].Marker, result.Profile.AudioChain[0].Language);
        Assert.Equal(SubtitleWant.Full, result.Profile.WhenAudioNotUnderstood);
        foreach (var field in new[] { SettingsFields.SubtitleLanguages, SettingsFields.AudioChain, SettingsFields.WhenAudioNotUnderstood })
            Assert.Equal(Label(row, personPerLibrary, adminPerLibrary), result.Sources[field]);
    }

    [Fact]
    public void NoLayerSetsTheField_BabelfinsDefaults()
    {
        var (person, admin) = Stack(Table.Length);

        var result = Resolve(person, admin, Context());

        Assert.Empty(result.Profile.SubtitleLanguages);
        Assert.Equal("Babelfin's defaults", result.Sources[SettingsFields.SubtitleLanguages]);
    }

    [Fact]
    public void HiddenTableLock_CannotBeatThePersonsTitleOverride_VisibleAdvancedStillApplies()
    {
        var person = Person();
        person.Locks = new() { WhenAudioNotUnderstood = SubtitleWant.Full, AudioChain = Chain(Step(AudioStepKind.MyLanguages)) };
        person.Overrides.Add(Override(Ids.Item, OverrideScope.Title, new() { WhenAudioNotUnderstood = SubtitleWant.Off,
            AudioChain = Chain(Step(AudioStepKind.OriginalLanguage)), Format = FormatPreference.TextOnly }));
        var result = Resolve(person, null, Episode());
        Assert.Equal(SubtitleWant.Off, result.Profile.WhenAudioNotUnderstood);
        Assert.Equal(new[] { AudioStepKind.OriginalLanguage }, result.Profile.Kinds());
        Assert.Equal("your settings for this title", result.Sources[SettingsFields.AudioChain]);
        Assert.Equal("your settings for this title", result.Sources[SettingsFields.WhenAudioNotUnderstood]);
        Assert.Equal(FormatPreference.TextOnly, result.Profile.Format);
        Assert.Equal("your settings for this title", result.Sources[SettingsFields.Format]);
        Assert.NotNull(person.Locks.AudioChain); // resolution does not erase the recoverable stored value
    }

    [Fact]
    public void HiddenTableLock_IsIgnoredEvenWithRetiredEverydayEverywhereSwitch()
    {
        var (person, admin) = Stack(0, everywhere: true);
        var result = Resolve(person, admin, Context());
        Assert.Equal(new[] { "my-title" }, result.Profile.SubtitleLanguages);
        Assert.Equal("your settings for this title", result.Sources[SettingsFields.SubtitleLanguages]);
    }

    // "Use my settings everywhere" is retired (the table's two modes replaced it): a stored true changes nothing.
    [Theory]
    [MemberData(nameof(Rows))]
    public void EverydayEverywhere_IsIgnored(int row, bool personPerLibrary, bool adminPerLibrary)
    {
        var (person, admin) = Stack(row, everywhere: true, personPerLibrary: personPerLibrary, adminPerLibrary: adminPerLibrary);
        var result = Resolve(person, admin, Context());
        Assert.Equal(new[] { Table[row].Marker }, result.Profile.SubtitleLanguages);
        Assert.Equal(Table[row].Marker, result.Profile.AudioChain[0].Language);
        Assert.Equal(Label(row, personPerLibrary, adminPerLibrary), result.Sources[SettingsFields.AudioChain]);
    }

    // "Choose for each library": the person's everyday table fields don't count; a library left on Default gets the server's default.
    [Fact]
    public void PerLibraryMode_IgnoresThePersonsEverydayTableFields()
    {
        var person = Person();
        person.PerLibrary = true;
        person.Everyday.AudioChain = Chain(Lang("en"), Step(AudioStepKind.VideoDefault));
        person.Everyday.WhenAudioUnderstood = SubtitleWant.Off;
        person.Everyday.Sdh = Preference.Prefer;
        var admin = new PluginConfiguration();
        admin.Libraries.Add(TheirLibrary(Ids.Shows, new SettingsLayer { AudioChain = Chain(Lang("ja")) }));

        var shows = Resolve(person, admin, Episode());
        var anime = Resolve(person, admin, Episode(libraryName: "Anime"));

        Assert.Equal("ja", shows.Profile.AudioChain[0].Language);
        Assert.Equal("the admin's Shows library defaults", shows.Sources[SettingsFields.AudioChain]);
        Assert.Equal(new[] { AudioStepKind.VideoDefault }, anime.Profile.Kinds());
        Assert.Equal(SubtitleWant.VideoDefault, anime.Profile.WhenAudioUnderstood);
        Assert.Equal(Preference.Prefer, anime.Profile.Sdh); // not a table field: still the person's
    }

    // "Same for every library": the person's choice applies everywhere, above the admin's per-library defaults; their library entries'
    // table fields don't count, other fields in them still do.
    [Fact]
    public void EveryLibraryMode_BeatsTheAdminsLibraryDefaults_AndIgnoresTheirOwnRows()
    {
        var person = Person();
        person.PerLibrary = false;
        person.Everyday.AudioChain = Chain(Lang("en"), Step(AudioStepKind.VideoDefault));
        person.Everyday.WhenAudioUnderstood = SubtitleWant.Off;
        person.Everyday.WhenAudioNotUnderstood = SubtitleWant.Off;
        person.Libraries.Add(MyLibrary(Ids.Anime, new SettingsLayer { AudioChain = Chain(Lang("ja")), Format = FormatPreference.TextOnly }));
        var admin = new PluginConfiguration();
        admin.Libraries.Add(TheirLibrary(Ids.Anime, new SettingsLayer { AudioChain = Chain(Lang("ko")), WhenAudioNotUnderstood = SubtitleWant.Full }));

        var anime = Resolve(person, admin, Episode(libraryName: "Anime"));

        Assert.Equal("en", anime.Profile.AudioChain[0].Language);
        Assert.Equal("your settings for every library", anime.Sources[SettingsFields.AudioChain]);
        Assert.Equal(SubtitleWant.Off, anime.Profile.WhenAudioNotUnderstood);
        Assert.Equal(FormatPreference.TextOnly, anime.Profile.Format);
        Assert.Equal("your Anime library settings", anime.Sources[SettingsFields.Format]);
    }

    // The automatic mode: per library as soon as one library entry sets a table field, else the same for every library.
    [Fact]
    public void AutomaticMode_FollowsTheStoredRows()
    {
        var plain = Person();
        plain.Everyday.AudioChain = Chain(Lang("en"));
        var rows = Person();
        rows.Everyday.AudioChain = Chain(Lang("en"));
        rows.Libraries.Add(MyLibrary(Ids.Anime, new SettingsLayer { AudioChain = Chain(Lang("ja")) }));
        var extraOnly = Person();
        extraOnly.Libraries.Add(MyLibrary(Ids.Anime, new SettingsLayer { Format = FormatPreference.TextOnly }));

        Assert.False(TableModes.PersonPerLibrary(plain));
        Assert.True(TableModes.PersonPerLibrary(rows));
        Assert.False(TableModes.PersonPerLibrary(extraOnly));
        Assert.Equal("en", Resolve(plain, null, Episode(libraryName: "Anime")).Profile.AudioChain[0].Language);
        Assert.Equal("ja", Resolve(rows, null, Episode(libraryName: "Anime")).Profile.AudioChain[0].Language);
        Assert.Equal(new[] { AudioStepKind.VideoDefault }, Resolve(rows, null, Episode()).Profile.Kinds());
    }

    // The admin's two modes: per library ignores the admin's everyday table fields; the same for every library ignores the library
    // entries' table fields but keeps their other fields (audio tracks without a language).
    [Fact]
    public void AdminModes_OneOrTheOther()
    {
        var perLibrary = new PluginConfiguration { PerLibraryDefaults = true, Everyday = new SettingsLayer { AudioChain = Chain(Lang("fr")) } };
        perLibrary.Libraries.Add(TheirLibrary(Ids.Shows, new SettingsLayer { AudioChain = Chain(Lang("ja")) }));
        var everyLibrary = new PluginConfiguration { PerLibraryDefaults = false, Everyday = new SettingsLayer { AudioChain = Chain(Lang("fr")) } };
        everyLibrary.Libraries.Add(TheirLibrary(Ids.Shows, new SettingsLayer { AudioChain = Chain(Lang("ja")), UnlabelledAudio = UnlabelledAudioRule.SpecificLanguage, UnlabelledAudioLanguage = "ja" }));

        Assert.Equal("ja", Resolve(Person(), perLibrary, Episode()).Profile.AudioChain[0].Language);
        Assert.Equal(new[] { AudioStepKind.VideoDefault }, Resolve(Person(), perLibrary, Episode(libraryName: "Anime")).Profile.Kinds());
        var every = Resolve(Person(), everyLibrary, Episode());
        Assert.Equal("fr", every.Profile.AudioChain[0].Language);
        Assert.Equal(UnlabelledAudioRule.SpecificLanguage, every.Profile.UnlabelledAudio);
        Assert.Equal("the admin's Shows library defaults", every.Sources[SettingsFields.UnlabelledAudio]);
    }

    [Fact]
    public void FieldByField_FallsThroughAcrossLayers()
    {
        var person = Person(new[] { "de" }, new[] { "it" });
        person.Overrides.Add(Override(Ids.Item, OverrideScope.Title, new SettingsLayer { WhenAudioUnderstood = SubtitleWant.Off }));
        var admin = new PluginConfiguration { Everyday = new SettingsLayer { Format = FormatPreference.Neutral } };
        admin.Libraries.Add(TheirLibrary(Ids.Shows, new SettingsLayer { SubtitleLanguages = new[] { "en" } }));
        admin.Titles.Add(Title(Ids.Series, OverrideScope.Series, new SettingsLayer { Embedded = EmbeddedPreference.PreferExternal }));

        var result = Resolve(person, admin, Episode());

        Assert.Equal(SubtitleWant.Off, result.Profile.WhenAudioUnderstood);
        Assert.Equal("your settings for this title", result.Sources[SettingsFields.WhenAudioUnderstood]);
        Assert.Equal(EmbeddedPreference.PreferExternal, result.Profile.Embedded);
        Assert.Equal("the admin's defaults for this show", result.Sources[SettingsFields.Embedded]);
        Assert.Equal(new[] { "en" }, result.Profile.SubtitleLanguages);
        Assert.Equal("the admin's Shows library defaults", result.Sources[SettingsFields.SubtitleLanguages]);
        Assert.Equal(FormatPreference.Neutral, result.Profile.Format);
        Assert.Equal("the admin's everyday defaults", result.Sources[SettingsFields.Format]);
        Assert.Equal(new[] { "de" }, result.Profile.Understood);
        Assert.Equal("your everyday settings", result.Sources[SettingsFields.Understood]);
        Assert.Equal(new[] { "it" }, result.Profile.Reads);
        Assert.Equal("Babelfin's defaults", result.Sources[SettingsFields.Memory]);
    }

    // Audio without a language is admin-only, so both layers are the admin's: the library default's rule and language win together.
    [Fact]
    public void UnlabelledAudio_TravelsWithItsLanguage()
    {
        var admin = new PluginConfiguration { Everyday = new SettingsLayer { UnlabelledAudio = UnlabelledAudioRule.Unknown, UnlabelledAudioLanguage = "fr" } };
        admin.Libraries.Add(TheirLibrary(Ids.Shows, new SettingsLayer { UnlabelledAudio = UnlabelledAudioRule.SpecificLanguage, UnlabelledAudioLanguage = "ja" }));

        var result = Resolve(Person(), admin, Episode());

        Assert.Equal(UnlabelledAudioRule.SpecificLanguage, result.Profile.UnlabelledAudio);
        Assert.Equal("ja", result.Profile.UnlabelledAudioLanguage);
        Assert.Equal("the admin's Shows library defaults", result.Sources[SettingsFields.UnlabelledAudio]);
    }

    [Fact]
    public void Collections_PersonsOrderFirst_FirstMatchWinsPerField()
    {
        var person = Person();
        person.CollectionOrder.Add(Ids.N(Ids.Favourites));
        person.Overrides.Add(Override(Ids.Cloudvale, OverrideScope.Collection, new SettingsLayer { Format = FormatPreference.TextOnly, Sdh = Preference.Avoid }));
        var admin = new PluginConfiguration();
        admin.Titles.Add(Title(Ids.Favourites, OverrideScope.Collection, new SettingsLayer { Format = FormatPreference.Neutral }));

        var context = Movie(collections: new[] { (Ids.Cloudvale, "Cloudvale"), (Ids.Favourites, "Favourites") });
        var result = Resolve(person, admin, context);

        Assert.Equal(FormatPreference.Neutral, result.Profile.Format);
        Assert.Equal("the admin's defaults for the Favourites collection", result.Sources[SettingsFields.Format]);
        Assert.Equal(Preference.Avoid, result.Profile.Sdh);
        Assert.Equal("your settings for the Cloudvale collection", result.Sources[SettingsFields.Sdh]);
    }

    [Fact]
    public void Collections_WithoutAnOrder_FollowTheItemsOrder()
    {
        var person = Person();
        person.Overrides.Add(Override(Ids.Favourites, OverrideScope.Collection, new SettingsLayer { Format = FormatPreference.Neutral }));
        person.Overrides.Add(Override(Ids.Cloudvale, OverrideScope.Collection, new SettingsLayer { Format = FormatPreference.TextOnly }));

        var result = Resolve(person, null, Movie(collections: new[] { (Ids.Cloudvale, "Cloudvale"), (Ids.Favourites, "Favourites") }));

        Assert.Equal(FormatPreference.TextOnly, result.Profile.Format);
        Assert.Equal("your settings for the Cloudvale collection", result.Sources[SettingsFields.Format]);
    }

    [Fact]
    public void Collections_AnOrderedIdTheItemIsNotIn_IsIgnored()
    {
        var person = Person();
        person.CollectionOrder.Add(Ids.N(Guid.NewGuid()));
        person.Overrides.Add(Override(Ids.Cloudvale, OverrideScope.Collection, new SettingsLayer { Format = FormatPreference.TextOnly }));

        var result = Resolve(person, null, Movie(collections: new[] { (Ids.Cloudvale, "Cloudvale") }));

        Assert.Equal(FormatPreference.TextOnly, result.Profile.Format);
    }

    [Fact]
    public void GenreRows_FirstMatchingFieldWins_AndAdvancedExtrasCannotApply()
    {
        var person = Person();
        person.SmartRules.Add(Rule("Miss", new() { AudioChain = Chain(Lang("fr")) }, When(ConditionKind.Genre, "Comedy")));
        person.SmartRules.Add(Rule("First", new() { AudioChain = Chain(Lang("ja")), Format = FormatPreference.TextOnly }, When(ConditionKind.Genre, "Drama")));
        person.SmartRules.Add(Rule("Second", new() { AudioChain = Chain(Lang("de")), WhenAudioNotUnderstood = SubtitleWant.Full, Sdh = Preference.Prefer }, When(ConditionKind.Genre, "Drama")));
        var result = Resolve(person, null, Episode(genres: new[] { "Drama" }));
        Assert.Equal("ja", result.Profile.AudioChain[0].Language);
        Assert.Equal("your First genre settings", result.Sources[SettingsFields.AudioChain]);
        Assert.Equal(SubtitleWant.Full, result.Profile.WhenAudioNotUnderstood);
        Assert.Equal("your Second genre settings", result.Sources[SettingsFields.WhenAudioNotUnderstood]);
        Assert.Equal(FormatPreference.PreferText, result.Profile.Format);
        Assert.Equal(Preference.Neutral, result.Profile.Sdh);
        Assert.Equal("Babelfin's defaults", result.Sources[SettingsFields.Format]);
        Assert.Equal("Babelfin's defaults", result.Sources[SettingsFields.Sdh]);
    }

    [Fact]
    public void GenreRows_PersonsTableFieldsBeforeAdmins_AdvancedExtrasStayInactive()
    {
        var person = Person();
        person.SmartRules.Add(Rule("Mine", new() { AudioChain = Chain(Lang("ja")), Format = FormatPreference.TextOnly }, When(ConditionKind.Genre, "Drama")));
        var admin = new PluginConfiguration();
        admin.SmartRules.Add(Rule("Theirs", new() { AudioChain = Chain(Lang("de")), WhenAudioNotUnderstood = SubtitleWant.Full }, When(ConditionKind.Genre, "Drama")));
        var result = Resolve(person, admin, Episode(genres: new[] { "Drama" }));
        Assert.Equal("ja", result.Profile.AudioChain[0].Language);
        Assert.Equal("your Mine genre settings", result.Sources[SettingsFields.AudioChain]);
        Assert.Equal(SubtitleWant.Full, result.Profile.WhenAudioNotUnderstood);
        Assert.Equal("the admin's Theirs genre defaults", result.Sources[SettingsFields.WhenAudioNotUnderstood]);
        Assert.Equal(FormatPreference.PreferText, result.Profile.Format);
    }

    [Fact]
    public void GenreRows_DisabledOrNotMatching_AreSkipped()
    {
        var person = Person();
        var disabled = Rule("Disabled", new() { AudioChain = Chain(Lang("ja")), Format = FormatPreference.TextOnly }, When(ConditionKind.Genre, "Drama"));
        disabled.Enabled = false; person.SmartRules.Add(disabled);
        person.SmartRules.Add(Rule("Comedy", new() { WhenAudioNotUnderstood = SubtitleWant.Full }, When(ConditionKind.Genre, "Comedy")));
        var result = Resolve(person, null, Episode(genres: new[] { "Drama" }));
        Assert.Equal(new[] { AudioStepKind.VideoDefault }, result.Profile.Kinds());
        Assert.Equal(SubtitleWant.VideoDefault, result.Profile.WhenAudioNotUnderstood);
        Assert.Equal(FormatPreference.PreferText, result.Profile.Format);
        Assert.Equal("Babelfin's defaults", result.Sources[SettingsFields.Format]);
    }

    [Fact]
    public void GenreRows_CannotSetSpokenWrittenLanguagesOrAdvancedFields()
    {
        var person = Person(new[] { "en" }, new[] { "en" });
        person.SmartRules.Add(Rule("Langs", new() { Understood = new[] { "ja" }, Reads = new[] { "fr" },
            Format = FormatPreference.Neutral, WhenAudioNotUnderstood = SubtitleWant.Full }, When(ConditionKind.Genre, "Drama")));
        var result = Resolve(person, null, Episode(genres: new[] { "Drama" }));
        Assert.Equal(new[] { "en" }, result.Profile.Understood); Assert.Equal(new[] { "en" }, result.Profile.Reads);
        Assert.Equal("your everyday settings", result.Sources[SettingsFields.Understood]);
        Assert.Equal("your everyday settings", result.Sources[SettingsFields.Reads]);
        Assert.Equal(FormatPreference.PreferText, result.Profile.Format);
        Assert.Equal("Babelfin's defaults", result.Sources[SettingsFields.Format]);
        Assert.Equal(SubtitleWant.Full, result.Profile.WhenAudioNotUnderstood);
        Assert.Equal("your Langs genre settings", result.Sources[SettingsFields.WhenAudioNotUnderstood]);
    }

    [Fact]
    public void RetiredLanguageConditionCannotApply_EvenWhenHigherVisibleLanguagesWouldMatch()
    {
        var person = Person(new[] { "en" });
        var scope = new SettingsLayer { Understood = new[] { "ja" }, WhenAudioUnderstood = SubtitleWant.PartialOnly };
        person.Overrides.Add(Override(Ids.Series, OverrideScope.Series, scope));
        person.SmartRules.Add(Rule("Understood original", new() { WhenAudioUnderstood = SubtitleWant.Off }, When(ConditionKind.OriginalLanguageUnderstood)));
        var result = Resolve(person, null, Episode(original: "ja"));
        Assert.Equal(new[] { "ja" }, result.Profile.Understood);
        Assert.Equal(SubtitleWant.PartialOnly, result.Profile.WhenAudioUnderstood);
        Assert.Equal("your settings for this show", result.Sources[SettingsFields.WhenAudioUnderstood]);
        scope.WhenAudioUnderstood = null;
        Assert.Equal(SubtitleWant.VideoDefault, Resolve(person, null, Episode(original: "ja")).Profile.WhenAudioUnderstood);
    }

    [Fact]
    public void AudioChain_DerivedFromForeignTitles_WhenNoLayerSetsIt()
    {
        var person = Person();
        person.Everyday.ForeignTitles = ForeignTitlesRule.OriginalWithSubtitles;

        var result = Resolve(person, null, Episode());

        Assert.Equal(new[] { AudioStepKind.OriginalLanguage, AudioStepKind.MyLanguages, AudioStepKind.FileDefault }, result.Profile.Kinds());
        Assert.Equal("your settings for every library", result.Sources[SettingsFields.AudioChain]);
        Assert.Equal("your settings for every library", result.Sources[SettingsFields.ForeignTitles]);
    }

    [Fact]
    public void AudioChain_NobodyChose_IsLeftToTheVideo()
    {
        var result = Resolve(Person(), null, Episode());

        Assert.Equal(new[] { AudioStepKind.VideoDefault }, result.Profile.Kinds());
        Assert.Equal("Babelfin's defaults", result.Sources[SettingsFields.AudioChain]);
    }

    [Fact]
    public void AudioChain_SetByALayer_IgnoresForeignTitles()
    {
        var person = Person();
        person.Everyday.ForeignTitles = ForeignTitlesRule.OriginalWithSubtitles;
        var admin = new PluginConfiguration { Everyday = new SettingsLayer { AudioChain = Chain(Lang("ja"), Step(AudioStepKind.FileDefault)) } };

        var result = Resolve(person, admin, Episode());

        Assert.Equal(new[] { new AudioStep(AudioStepKind.Language, "ja"), new AudioStep(AudioStepKind.FileDefault) }, result.Profile.AudioChain);
        Assert.Equal("the admin's everyday defaults", result.Sources[SettingsFields.AudioChain]);
    }

    [Fact]
    public void Sources_AreTheProfilesFieldSources_AndCoverEveryField()
    {
        var result = Resolve(Person(), null, Episode());

        Assert.Same(result.Sources, result.Profile.FieldSources);
        foreach (var field in SettingsFields.All)
        {
            Assert.True(result.Sources.ContainsKey(field), field);
        }
    }

    [Fact]
    public void Defaults_AreTheEngineStarter_WithBothTracksLeftToTheVideo()
    {
        var result = Resolve(Person(new[] { "en" }, new[] { "en" }), null, ItemContext.None);
        var starter = Babelfin.Engine.Tests.Selection.Fx.Starter(new[] { "en" }, new[] { "en" });

        Assert.Equal(new[] { new AudioStep(AudioStepKind.VideoDefault) }, result.Profile.AudioChain);
        Assert.Equal(SubtitleWant.VideoDefault, result.Profile.WhenAudioUnderstood);
        Assert.Equal(SubtitleWant.VideoDefault, result.Profile.WhenAudioNotUnderstood);
        Assert.Equal(starter.TieBreaks, result.Profile.TieBreaks);
        Assert.Equal(
            starter with
            {
                AudioChain = result.Profile.AudioChain,
                TieBreaks = result.Profile.TieBreaks,
                Understood = result.Profile.Understood,
                Reads = result.Profile.Reads,
                SubtitleLanguages = result.Profile.SubtitleLanguages,
                FieldSources = result.Profile.FieldSources,
                WhenAudioUnderstood = SubtitleWant.VideoDefault,
                WhenAudioNotUnderstood = SubtitleWant.VideoDefault,
            },
            result.Profile);
    }

    [Fact]
    public void LibraryLabels_UseTheLibraryName()
    {
        var person = Person();
        person.Libraries.Add(MyLibrary(Ids.Anime, new SettingsLayer { Format = FormatPreference.Neutral }));
        var admin = new PluginConfiguration();
        admin.Libraries.Add(TheirLibrary(Ids.Anime, new SettingsLayer { Sdh = Preference.Avoid }));

        var result = Resolve(person, admin, Episode(libraryName: "Anime"));

        Assert.Equal("your Anime library settings", result.Sources[SettingsFields.Format]);
        Assert.Equal("the admin's Anime library defaults", result.Sources[SettingsFields.Sdh]);
    }

    [Fact]
    public void SeasonAndSeries_ContextsUseTheirOwnScopeLabels()
    {
        var person = Person();
        person.Overrides.Add(Override(Ids.Series, OverrideScope.Series, new SettingsLayer { Format = FormatPreference.Neutral }));
        var series = new ItemContext(Ids.Series, "Skyward Lantern Voyage", ContentKind.Other, null, Ids.Series, "Skyward Lantern Voyage", Array.Empty<Guid>(), Array.Empty<string>(), new[] { Ids.Shows }, "Shows", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "ja");

        var result = Resolve(person, null, series);

        Assert.Equal("your settings for this show", result.Sources[SettingsFields.Format]);
    }

    [Fact]
    public void BurnedIn_UnionOfPersonAndAdminFlags_ForItemSeasonSeriesCollectionAndLibrary()
    {
        var person = Person();
        person.BurnedIn.Add(new BurnedInFlag { ItemId = Ids.N(Ids.Season), Scope = OverrideScope.Season, Languages = new() { "eng" } });
        person.BurnedIn.Add(new BurnedInFlag { ItemId = Ids.N(Ids.Shows), Scope = OverrideScope.Library, Languages = new() { "de" } });
        person.BurnedIn.Add(new BurnedInFlag { ItemId = Ids.N(Guid.NewGuid()), Scope = OverrideScope.Series, Languages = new() { "ko" } });
        var admin = new PluginConfiguration();
        admin.BurnedIn.Add(new BurnedInFlag { ItemId = Ids.N(Ids.Series), Scope = OverrideScope.Series, Languages = new() { "en", "fr" } });
        admin.BurnedIn.Add(new BurnedInFlag { ItemId = Ids.N(Ids.Cloudvale), Scope = OverrideScope.Collection, Languages = new() { "es" } });
        admin.BurnedIn.Add(new BurnedInFlag { ItemId = Ids.N(Ids.Shows), Scope = OverrideScope.Library, Languages = new() { "it" } });

        var result = Resolve(person, admin, Episode(collections: new[] { (Ids.Cloudvale, "Cloudvale") }));

        Assert.Equal(new[] { "en", "fr", "es", "it" }, result.BurnedInLanguages);
    }

    [Fact]
    public void Manual_MostSpecificOverrideWithAMemory()
    {
        var showMemory = new ManualMemory { Audio = new RememberedTrack { Language = "ja" } };
        var seasonMemory = new ManualMemory { SubtitlesOff = true };
        var person = Person();
        person.Overrides.Add(Override(Ids.Series, OverrideScope.Series, new SettingsLayer(), showMemory));
        person.Overrides.Add(Override(Ids.Season, OverrideScope.Season, new SettingsLayer(), seasonMemory));
        person.Overrides.Add(Override(Ids.Item, OverrideScope.Title, new SettingsLayer { Format = FormatPreference.Neutral }));

        var result = Resolve(person, null, Episode());

        Assert.NotNull(result.Manual);
        Assert.True(result.Manual!.SubtitlesOff);
        Assert.Null(result.Manual.Audio);
    }

    [Fact]
    public void Manual_NoneWhenNothingRemembered()
    {
        var result = Resolve(Person(), null, Episode());

        Assert.Null(result.Manual);
    }

    [Fact]
    public void Assumed_ListedUntilConfirmed()
    {
        var person = Person();
        person.Seed = new SeedRecord { Assumed = new() { new AssumedLanguage { Field = SettingsFields.Understood, Language = "en", From = "server" } } };

        Assert.Single(Resolve(person, null, Episode()).Assumed);

        person.Seed.Confirmed = true;
        Assert.Empty(Resolve(person, null, Episode()).Assumed);
    }

    [Fact]
    public void PersonEnabled_IsCarried()
    {
        var person = Person();
        person.Enabled = false;

        Assert.False(Resolve(person, null, Episode()).PersonEnabled);
    }

    [Fact]
    public void None_ResolvesEverydayLayersOnly()
    {
        var person = Person();
        person.Libraries.Add(MyLibrary(Ids.Shows, new SettingsLayer { Format = FormatPreference.Neutral }));
        person.Everyday.Sdh = Preference.Avoid;

        var result = Resolve(person, null, ItemContext.None);

        Assert.Equal(FormatPreference.PreferText, result.Profile.Format);
        Assert.Equal("your everyday settings", result.Sources[SettingsFields.Sdh]);
        Assert.False(result.LibraryIsOriginalAudio);
        Assert.Empty(result.BurnedInLanguages);
    }

    [Fact]
    public void Result_HasNoFilePaths()
    {
        var result = Resolve(Person(), null, Episode(libraryName: "Anime"));

        foreach (var label in result.Sources.Values)
        {
            Assert.DoesNotContain("/", label, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", label, StringComparison.Ordinal);
        }
    }
}
