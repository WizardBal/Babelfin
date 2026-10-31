using System;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;
using static Babelfin.Engine.Tests.Settings.Ctx;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>
/// the original-language library rules (by name or facts, the genre rule). They are off unless the admin switches
/// them on, and Babelfin's own defaults leave both tracks to the video; these tests switch the rules on (StarterOn).
/// </summary>
public class StarterRuleTests
{
    private static readonly AudioStepKind[] OriginalChain = { AudioStepKind.OriginalLanguage, AudioStepKind.LibraryUsualOriginalLanguage, AudioStepKind.FileDefault };

    // Nobody chose an audio order: the audio is left to the video.
    private static readonly AudioStepKind[] NeutralChain = { AudioStepKind.VideoDefault };

    private static PluginConfiguration StarterOn()
    {
        var admin = new PluginConfiguration();
        admin.Starter.Enabled = true;
        return admin;
    }

    [Fact]
    public void Starter_IsOffByDefault_SoAnAnimeLibraryIsLeftToTheVideo()
    {
        Assert.False(new PluginConfiguration().Starter.Enabled);

        var result = Resolve(Person(), null, Episode(libraryName: "Anime", original: "ja"));

        Assert.Equal(NeutralChain, result.Profile.Kinds());
        Assert.Equal(StarterRules.DefaultsLabel, result.Sources[SettingsFields.AudioChain]);
        Assert.Equal(SubtitleWant.VideoDefault, result.Profile.WhenAudioUnderstood);
        Assert.Equal(SubtitleWant.VideoDefault, result.Profile.WhenAudioNotUnderstood);
    }

    [Theory]
    [InlineData("Anime", true)]
    [InlineData("anime", true)]
    [InlineData("Anime Movies", true)]
    [InlineData("My Animes", true)]
    [InlineData("Animé", true)]
    [InlineData("Anime_Series", true)]
    [InlineData("アニメ", true)]
    [InlineData("アニメ映画", true)]
    [InlineData("动漫", true)]
    [InlineData("애니메이션", true)]
    [InlineData("Animated Classics", false)]
    [InlineData("Animeworld", false)]
    [InlineData("Shows", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LibraryName_MatchesTheDefaultWordList(string? name, bool expected)
    {
        Assert.Equal(expected, StarterRules.LibraryNameMatches(name, new StarterSettings().OriginalAudioLibraryWords));
    }

    [Fact]
    public void LibraryName_WordListIsEditable()
    {
        var words = new[] { "cartoons" };

        Assert.True(StarterRules.LibraryNameMatches("Saturday Cartoons", words));
        Assert.False(StarterRules.LibraryNameMatches("Anime", words));
    }

    [Fact]
    public void AnimeLibraryByName_GetsTheOriginalAudioChain()
    {
        var result = Resolve(Person(), StarterOn(), Episode(libraryName: "Anime", original: "ja"));

        Assert.True(result.LibraryIsOriginalAudio);
        Assert.Equal(OriginalChain, result.Profile.Kinds());
        Assert.Equal(StarterRules.LibraryLabel, result.Sources[SettingsFields.AudioChain]);
    }

    [Fact]
    public void JapaneseLibraryName_GetsTheOriginalAudioChain()
    {
        var result = Resolve(Person(), StarterOn(), Episode(libraryName: "アニメ", libraryId: Guid.NewGuid()));

        Assert.True(result.LibraryIsOriginalAudio);
        Assert.Equal(OriginalChain, result.Profile.Kinds());
    }

    [Fact]
    public void AnimatedClassics_IsNotOriginalAudio()
    {
        var result = Resolve(Person(), StarterOn(), Episode(libraryName: "Animated Classics", libraryId: Guid.NewGuid()));

        Assert.False(result.LibraryIsOriginalAudio);
        Assert.Equal(NeutralChain, result.Profile.Kinds());
        Assert.Equal(StarterRules.DefaultsLabel, result.Sources[SettingsFields.AudioChain]);
    }

    // Facts computed by the library-facts calculator: a genre share at or above Starter.GenreSharePercent sets DetectedOriginalAudio.
    [Fact]
    public void ComputedFacts_GenreShare_MakeAnOriginalAudioLibrary()
    {
        var admin = StarterOn();
        var facts = Facts(Ids.Shows, "Cartoons", detected: true, genrePercent: admin.Starter.GenreSharePercent);

        var result = Resolve(Person(), admin, Episode(libraryName: "Cartoons", libraryId: Ids.Shows, facts: facts));

        Assert.True(result.LibraryIsOriginalAudio);
        Assert.Equal(OriginalChain, result.Profile.Kinds());
    }

    [Fact]
    public void ComputedFacts_DecideOverTheName()
    {
        var facts = Facts(Ids.Anime, "Anime", detected: false, genrePercent: 10);

        var result = Resolve(Person(), StarterOn(), Episode(libraryName: "Anime", facts: facts));

        Assert.False(result.LibraryIsOriginalAudio);
        Assert.Equal(NeutralChain, result.Profile.Kinds());
    }

    [Fact]
    public void FactsWithOnlyAName_FallBackToTheWordList()
    {
        var nameOnly = new LibraryFact { LibraryId = Ids.Anime, Name = "Anime" };

        var result = Resolve(Person(), StarterOn(), Episode(libraryName: "Anime", facts: nameOnly));

        Assert.True(result.LibraryIsOriginalAudio);
    }

    [Theory]
    [InlineData("Shows", OriginalAudioChoice.Yes, true)]
    [InlineData("Anime", OriginalAudioChoice.No, false)]
    [InlineData("Anime", OriginalAudioChoice.Auto, true)]
    [InlineData("Shows", OriginalAudioChoice.Auto, false)]
    public void AdminChoice_YesNoAuto(string library, OriginalAudioChoice choice, bool expected)
    {
        var admin = StarterOn();
        admin.Libraries.Add(TheirLibrary(LibraryIdFor(library), new SettingsLayer(), choice));
        var detected = Facts(LibraryIdFor(library), library, detected: library == "Anime");

        var byName = Resolve(Person(), admin, Episode(libraryName: library));
        var byFacts = Resolve(Person(), admin, Episode(libraryName: library, facts: detected));

        Assert.Equal(expected, byName.LibraryIsOriginalAudio);
        Assert.Equal(expected, byFacts.LibraryIsOriginalAudio);
        Assert.Equal(expected ? OriginalChain : NeutralChain, byName.Profile.Kinds());
    }

    [Fact]
    public void GenreRule_InAMixedLibrary()
    {
        var result = Resolve(Person(), StarterOn(), Episode(libraryName: "Shows", genres: new[] { "Action", "anime" }));

        Assert.False(result.LibraryIsOriginalAudio);
        Assert.Equal(OriginalChain, result.Profile.Kinds());
        Assert.Equal(StarterRules.GenreLabel, result.Sources[SettingsFields.AudioChain]);
    }

    [Fact]
    public void GenreRule_Off_NoChain()
    {
        var admin = StarterOn();
        admin.Starter.GenreRule = false;

        var result = Resolve(Person(), admin, Episode(libraryName: "Shows", genres: new[] { "Anime" }));

        Assert.Equal(NeutralChain, result.Profile.Kinds());
    }

    [Fact]
    public void GenreRule_GenreListIsEditable()
    {
        var admin = StarterOn();
        admin.Starter.OriginalAudioGenres = new() { "Donghua" };

        Assert.Equal(NeutralChain, Resolve(Person(), admin, Episode(genres: new[] { "Anime" })).Profile.Kinds());
        Assert.Equal(OriginalChain, Resolve(Person(), admin, Episode(genres: new[] { "Donghua" })).Profile.Kinds());
    }

    [Fact]
    public void StarterDisabled_PlainDefaults()
    {
        var admin = StarterOn();
        admin.Starter.Enabled = false;

        var result = Resolve(Person(), admin, Episode(libraryName: "Anime", genres: new[] { "Anime" }));

        Assert.Equal(NeutralChain, result.Profile.Kinds());
        Assert.Equal(StarterRules.DefaultsLabel, result.Sources[SettingsFields.AudioChain]);
        Assert.True(result.LibraryIsOriginalAudio);
    }

    // The starter's own "titles in a language I don't understand" value no longer picks an audio order: only a person or the admin does.
    [Fact]
    public void StarterForeignTitles_DoesNotChooseTheAudio()
    {
        var admin = StarterOn();
        admin.Starter.ForeignTitles = ForeignTitlesRule.OriginalWithSubtitles;

        var result = Resolve(Person(), admin, Episode());

        Assert.Equal(NeutralChain, result.Profile.Kinds());
        Assert.Equal(StarterRules.DefaultsLabel, result.Sources[SettingsFields.AudioChain]);
    }

    // A person's own rule for foreign titles still gives its audio order.
    [Fact]
    public void PersonsForeignTitles_StillGivesItsOrder()
    {
        var person = Person();
        person.Everyday.ForeignTitles = ForeignTitlesRule.OriginalWithSubtitles;

        var result = Resolve(person, null, Episode());

        Assert.Equal(new[] { AudioStepKind.OriginalLanguage, AudioStepKind.MyLanguages, AudioStepKind.FileDefault }, result.Profile.Kinds());
    }

    [Fact]
    public void AdminLibraryChain_BeatsTheStarter()
    {
        var admin = StarterOn();
        admin.Libraries.Add(TheirLibrary(Ids.Anime, new SettingsLayer { AudioChain = Chain(Lang("ja"), Step(AudioStepKind.OriginalLanguage)) }));

        var result = Resolve(Person(), admin, Episode(libraryName: "Anime"));

        Assert.Equal(new[] { AudioStepKind.Language, AudioStepKind.OriginalLanguage }, result.Profile.Kinds());
        Assert.Equal("the admin's Anime library defaults", result.Sources[SettingsFields.AudioChain]);
    }

    [Fact]
    public void UsualOriginalLanguage_IsFedIntoTheResult()
    {
        var facts = Facts(Ids.Anime, "Anime", detected: true, usual: "ja");

        var result = Resolve(Person(), StarterOn(), Episode(libraryName: "Anime", facts: facts));

        Assert.Equal("ja", result.LibraryUsualOriginalLanguage);
    }

    // With no original language in the metadata, the usual original language of the library picks the Japanese track.
    [Fact]
    public void UsualOriginalLanguage_DrivesTheSecondStep()
    {
        var facts = Facts(Ids.Anime, "Anime", detected: true, usual: "ja");
        var person = Person();
        person.Everyday.WhenAudioUnderstood = SubtitleWant.PartialOnly;
        person.Everyday.WhenAudioNotUnderstood = SubtitleWant.Full;
        var result = Resolve(person, StarterOn(), Episode(libraryName: "Anime", facts: facts));
        var item = new ItemFacts(null, result.LibraryUsualOriginalLanguage, result.BurnedInLanguages, true);

        var decision = Selection.Fx.Decide(Selection.Fx.Slv(), result.Profile, item);

        Assert.Equal(2, decision.AudioIndex);
        Assert.Equal(4, decision.SubtitleIndex);
    }

    [Fact]
    public void Defaults_SetEveryFieldButTheChain()
    {
        foreach (var rule in new[] { ForeignTitlesRule.DubInMyLanguage, ForeignTitlesRule.OriginalWithSubtitles })
        {
            var defaults = StarterRules.Defaults(rule);
            foreach (var field in SettingsFields.All)
            {
                Assert.Equal(field != SettingsFields.AudioChain, defaults.IsSet(field));
            }
        }
    }
}
