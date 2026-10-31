using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Seeding;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Fx = Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>the acceptance people and libraries, through <see cref="ProfileResolver"/> and the real <see cref="TrackSelector"/>.</summary>
public class EndToEndTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>ViewerOne's Jellyfin settings today: audio preference empty, subtitle "eng", mode Smart; server language "en".</summary>
    private static PersonSettings ViewerOne() =>
        PersonSeeder.Seed(new SeedInputs(Ids.Person, "ViewerOne", string.Empty, "eng", "Smart", null, "en", Now), LanguageTable.Default);

    /// <summary>Understands Mandarin, reads English; "same for every library" with Mandarin audio (what the table writes).</summary>
    private static PersonSettings MandarinEnglish()
    {
        var p = Ctx.Person(new[] { "cmn" }, new[] { "en" });
        p.Everyday.AudioChain = Ctx.Chain(Ctx.Lang("cmn"), Ctx.Step(AudioStepKind.VideoDefault));
        return p;
    }

    /// <summary>Understands German, reads Italian; chose German audio and "when the audio isn't in my languages" for subtitles.</summary>
    private static PersonSettings GermanItalian()
    {
        var p = Ctx.Person(new[] { "de" }, new[] { "it" });
        p.Everyday.AudioChain = Ctx.Chain(Ctx.Lang("de"), Ctx.Step(AudioStepKind.VideoDefault));
        p.Everyday.WhenAudioUnderstood = SubtitleWant.PartialOnly;
        p.Everyday.WhenAudioNotUnderstood = SubtitleWant.Full;
        return p;
    }

    /// <summary>What the "Audio and subtitles" table writes for one library: an audio language and full subtitles in a language.</summary>
    private static PersonSettings WithLibrary(PersonSettings p, Guid libraryId, string audio, string subtitles)
    {
        p.Libraries.Add(Ctx.MyLibrary(libraryId, new SettingsLayer
        {
            AudioChain = Ctx.Chain(Ctx.Lang(audio), Ctx.Step(AudioStepKind.VideoDefault)),
            WhenAudioUnderstood = SubtitleWant.Full,
            WhenAudioNotUnderstood = SubtitleWant.Full,
            SubtitleLanguages = new[] { subtitles },
        }));
        return p;
    }

    /// <summary>An admin with a default per library: Anime strictly Japanese with English subtitles when the audio isn't understood;
    /// Shows only that subtitle rule (the audio is the video's).</summary>
    private static PluginConfiguration UsersAdmin()
    {
        var admin = new PluginConfiguration { PerLibraryDefaults = true };
        admin.Libraries.Add(Ctx.TheirLibrary(Ids.Anime, new SettingsLayer
        {
            AudioChain = Ctx.Chain(Ctx.Lang("ja"), Ctx.Step(AudioStepKind.OriginalLanguage), Ctx.Step(AudioStepKind.FileDefault)),
            WhenAudioUnderstood = SubtitleWant.PartialOnly,
            WhenAudioNotUnderstood = SubtitleWant.Full,
            SubtitleLanguages = new[] { "en" },
            UnlabelledAudio = UnlabelledAudioRule.SpecificLanguage,
            UnlabelledAudioLanguage = "ja",
        }));
        admin.Libraries.Add(Ctx.TheirLibrary(Ids.Shows, new SettingsLayer
        {
            WhenAudioUnderstood = SubtitleWant.PartialOnly,
            WhenAudioNotUnderstood = SubtitleWant.Full,
            SubtitleLanguages = new[] { "en" },
        }));
        return admin;
    }

    private static Decision Decide(PersonSettings person, PluginConfiguration? admin, ItemContext context, IReadOnlyList<Track> tracks)
    {
        var result = Ctx.Resolve(person, admin, context);
        var facts = new ItemFacts(context.OriginalLanguage, result.LibraryUsualOriginalLanguage, result.BurnedInLanguages, context.Kind == ContentKind.Episode);
        return Fx.Decide(tracks, result.Profile, facts, manual: result.Manual);
    }

    // Nothing chosen: even in a library called Anime, the video decides both tracks (nobody gets Japanese they didn't ask for).
    [Fact]
    public void ViewerOne_NothingChosen_TheVideoDecides()
    {
        var d = Decide(ViewerOne(), null, Ctx.Episode(libraryName: "Anime", original: "ja"), Fx.Slv());

        Assert.True(d.AudioLeftToVideo);
        Assert.True(d.SubtitlesLeftToVideo);
        Assert.True(Fx.Has(d, "audio.videodefault"));
        Assert.True(Fx.Has(d, "subs.videodefault"));
        Assert.EndsWith("the video's default. Subtitles: the video's default.", d.Explain(), StringComparison.Ordinal);
    }

    // Example: ViewerOne picks Japanese audio and English subtitles for Anime.
    [Fact]
    public void ViewerOne_ChoosesJapaneseAndEnglish_ForAnime()
    {
        var person = WithLibrary(ViewerOne(), Ids.Anime, "ja", "en");
        var tracks = Fx.Slv();
        tracks[2] = tracks[2] with { Title = "Signs & Songs" };

        var anime = Decide(person, null, Ctx.Episode(libraryName: "Anime", original: "ja"), tracks);
        var shows = Decide(person, null, Ctx.Episode(libraryName: "Shows", libraryId: Ids.Shows, original: "ja"), tracks);

        Assert.Equal(2, anime.AudioIndex);
        Assert.Equal(4, anime.SubtitleIndex);
        Assert.False(anime.AudioLeftToVideo);
        Assert.False(anime.SubtitlesLeftToVideo);
        Assert.True(Fx.Has(anime, "subs.skipped.partial", 3));
        Assert.Contains("your Anime library settings", anime.Explain(), StringComparison.Ordinal);
        Assert.True(shows.AudioLeftToVideo);
        Assert.True(shows.SubtitlesLeftToVideo);
    }

    // Example: English audio and no subtitles in every library.
    [Fact]
    public void viewerThree_EnglishAndNoSubtitles_Everywhere()
    {
        var person = Ctx.Person();
        person.Everyday.AudioChain = Ctx.Chain(Ctx.Lang("en"), Ctx.Step(AudioStepKind.VideoDefault));
        person.Everyday.WhenAudioUnderstood = SubtitleWant.Off;
        person.Everyday.WhenAudioNotUnderstood = SubtitleWant.Off;

        foreach (var library in new[] { "Anime", "Shows" })
        {
            var d = Decide(person, null, Ctx.Episode(libraryName: library, original: "ja"), Fx.Slv());
            Assert.Equal(1, d.AudioIndex);
            Assert.Null(d.SubtitleIndex);
            Assert.False(d.AudioLeftToVideo);
            Assert.False(d.SubtitlesLeftToVideo);
        }
    }

    // A chosen language the file doesn't have: the video's default plays instead (not some other guess).
    [Fact]
    public void ChosenLanguageMissing_FallsBackToTheVideosDefault()
    {
        var person = Ctx.Person();
        person.Everyday.AudioChain = Ctx.Chain(Ctx.Lang("fr"), Ctx.Step(AudioStepKind.VideoDefault));
        var tracks = new[] { Fx.Audio(1, "jpn"), Fx.Audio(2, "eng", isDefault: true), Fx.Sub(3, "eng") };

        var d = Decide(person, null, Ctx.Movie(original: "ja"), tracks);

        Assert.Equal(2, d.AudioIndex);
        Assert.True(d.AudioLeftToVideo);
        Assert.True(Fx.Has(d, "audio.step.skipped"));
    }

    [Fact]
    public void ViewerOne_IsSeededWithAssumedUnderstood()
    {
        var p = ViewerOne();

        Assert.Equal(new[] { "en" }, p.Everyday.Understood);
        Assert.Equal("other-field", Assert.Single(p.Seed!.Assumed).From);
    }

    /// <summary>Both modes stored: Japanese and English for every library, and per library Shows on the video's default.</summary>
    private static PersonSettings BothModes(bool perLibrary)
    {
        var person = Ctx.Person();
        person.PerLibrary = perLibrary;
        person.Everyday.AudioChain = Ctx.Chain(Ctx.Lang("ja"), Ctx.Step(AudioStepKind.VideoDefault));
        person.Everyday.WhenAudioUnderstood = SubtitleWant.Full;
        person.Everyday.WhenAudioNotUnderstood = SubtitleWant.Full;
        person.Everyday.SubtitleLanguages = new[] { "en" };
        person.Libraries.Add(Ctx.MyLibrary(Ids.Shows, new SettingsLayer
        {
            AudioChain = Ctx.Chain(Ctx.Step(AudioStepKind.VideoDefault)),
            WhenAudioUnderstood = SubtitleWant.VideoDefault,
            WhenAudioNotUnderstood = SubtitleWant.VideoDefault,
        }));
        return person;
    }

    // "Same for every library": the one choice applies in every library; the stored library rows wait for the other mode.
    [Fact]
    public void EveryLibraryMode_TheOneChoiceAppliesEverywhere()
    {
        var person = BothModes(perLibrary: false);

        var shows = Decide(person, null, Ctx.Episode(libraryName: "Shows", libraryId: Ids.Shows, original: "ja"), Fx.Slv());
        var anime = Decide(person, null, Ctx.Episode(libraryName: "Anime", original: "ja"), Fx.Slv());

        Assert.Equal(2, shows.AudioIndex);
        Assert.Equal(4, shows.SubtitleIndex);
        Assert.Equal(2, anime.AudioIndex);
        Assert.Equal(4, anime.SubtitleIndex);
    }

    // "Choose for each library": only the rows count; a library without a row is the video's (no admin default here).
    [Fact]
    public void PerLibraryMode_OnlyTheRowsCount()
    {
        var person = BothModes(perLibrary: true);

        var shows = Decide(person, null, Ctx.Episode(libraryName: "Shows", libraryId: Ids.Shows, original: "ja"), Fx.Slv());
        var anime = Decide(person, null, Ctx.Episode(libraryName: "Anime", original: "ja"), Fx.Slv());

        Assert.True(shows.AudioLeftToVideo);
        Assert.True(shows.SubtitlesLeftToVideo);
        Assert.True(anime.AudioLeftToVideo);
        Assert.True(anime.SubtitlesLeftToVideo);
    }

    // Example: English and no subtitles for everything, even where the admin set a per-library default.
    [Fact]
    public void EveryLibraryMode_BeatsTheAdminsAnimeDefault()
    {
        var person = Ctx.Person();
        person.PerLibrary = false;
        person.Everyday.AudioChain = Ctx.Chain(Ctx.Lang("en"), Ctx.Step(AudioStepKind.VideoDefault));
        person.Everyday.WhenAudioUnderstood = SubtitleWant.Off;
        person.Everyday.WhenAudioNotUnderstood = SubtitleWant.Off;

        var d = Decide(person, UsersAdmin(), Ctx.Episode(libraryName: "Anime", original: "ja"), Fx.Slv());

        Assert.Equal(1, d.AudioIndex);
        Assert.Null(d.SubtitleIndex);
        Assert.Contains("your settings for every library", d.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void UsersAdminSettings_AnimeLibrary_JapaneseEvenForAnEnglishOriginal()
    {
        var d = Decide(Ctx.Person(), UsersAdmin(), Ctx.Episode(libraryName: "Anime", original: "en"), Fx.Slv());

        Assert.Equal(2, d.AudioIndex);
        Assert.Equal(4, d.SubtitleIndex);
        Assert.Contains("the admin's Anime library defaults", d.Explain(), StringComparison.Ordinal);
    }

    private static Track[] WithMandarin() => new[]
    {
        Fx.Audio(1, "jpn", isDefault: true),
        Fx.Audio(2, "yue"),
        Fx.Audio(3, "cmn"),
        Fx.Sub(4, "eng"),
    };

    private static Track[] WithCantoneseOnly() => new[]
    {
        Fx.Audio(1, "jpn", isDefault: true),
        Fx.Audio(2, "yue"),
        Fx.Audio(3, "chi", "Cantonese"),
        Fx.Sub(4, "eng", "Signs", isForced: true),
        Fx.Sub(5, "eng"),
    };

    [Theory]
    [InlineData("Shows")]
    [InlineData("Anime")]
    public void MandarinEnglish_GetsMandarinWhenItExists(string library)
    {
        var d = Decide(MandarinEnglish(), UsersAdmin(), Ctx.Episode(libraryName: library, original: "ja"), WithMandarin());

        Assert.Equal(3, d.AudioIndex);
        Assert.True(d.AudioUnderstood);
    }

    [Theory]
    [InlineData("Shows")]
    [InlineData("Anime")]
    public void MandarinEnglish_NeverCantonese_ElseOriginalWithEnglishSubtitles(string library)
    {
        var d = Decide(MandarinEnglish(), UsersAdmin(), Ctx.Episode(libraryName: library, original: "ja"), WithCantoneseOnly());

        Assert.Equal(1, d.AudioIndex);
        Assert.False(d.AudioUnderstood);
        Assert.Equal(5, d.SubtitleIndex);
    }

    [Fact]
    public void MandarinEnglish_AnimeLibrary_SourcesNameTheirOwnLayers()
    {
        var result = Ctx.Resolve(MandarinEnglish(), UsersAdmin(), Ctx.Episode(libraryName: "Anime", original: "ja"));

        Assert.Equal("your settings for every library", result.Sources[SettingsFields.AudioChain]);
        Assert.Equal("the admin's Anime library defaults", result.Sources[SettingsFields.SubtitleLanguages]);
        Assert.Equal("the admin's Anime library defaults", result.Sources[SettingsFields.UnlabelledAudio]);
    }

    [Fact]
    public void GermanItalian_GermanAudio_NoItalianSubtitles()
    {
        var tracks = new[] { Fx.Audio(1, "eng", isDefault: true), Fx.Audio(2, "ger"), Fx.Sub(3, "ita"), Fx.Sub(4, "eng") };

        var d = Decide(GermanItalian(), null, Ctx.Movie(original: "en"), tracks);

        Assert.Equal(2, d.AudioIndex);
        Assert.True(d.AudioUnderstood);
        Assert.Null(d.SubtitleIndex);
    }

    [Fact]
    public void GermanItalian_NoGermanAudio_ItalianSubtitles()
    {
        var tracks = new[] { Fx.Audio(1, "eng", isDefault: true), Fx.Audio(2, "jpn"), Fx.Sub(3, "eng"), Fx.Sub(4, "ita") };

        var d = Decide(GermanItalian(), null, Ctx.Movie(original: "ja"), tracks);

        // No German track: the video's default (English) plays, which this person doesn't understand, so full Italian subtitles.
        Assert.Equal(1, d.AudioIndex);
        Assert.True(d.AudioLeftToVideo);
        Assert.False(d.AudioUnderstood);
        Assert.Equal(4, d.SubtitleIndex);
    }

    [Fact]
    public void PersonsSeriesOverride_BeatsTheAdminsLibraryDefault()
    {
        var person = Ctx.Person();
        person.Overrides.Add(Ctx.Override(Ids.Series, OverrideScope.Series, new SettingsLayer { AudioChain = Ctx.Chain(Ctx.Lang("en")) }));

        var d = Decide(person, UsersAdmin(), Ctx.Episode(libraryName: "Anime", original: "ja"), Fx.Slv());

        Assert.Equal(1, d.AudioIndex);
        Assert.Contains("your settings for this show", d.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void PersonalShowAudioBeatsHiddenDubLock_AndAdminLibraryStillSuppliesSubtitles()
    {
        var kid = Ctx.Person();
        kid.Locks = new() { AudioChain = Ctx.Chain(Ctx.Step(AudioStepKind.MyLanguages), Ctx.Step(AudioStepKind.FileDefault)) };
        kid.Overrides.Add(Ctx.Override(Ids.Series, OverrideScope.Series, new() { AudioChain = Ctx.Chain(Ctx.Step(AudioStepKind.OriginalLanguage)) }));
        var context = Ctx.Episode(libraryName: "Anime", original: "ja");
        var d = Decide(kid, UsersAdmin(), context, Fx.Slv());
        Assert.Equal(2, d.AudioIndex); Assert.Equal(4, d.SubtitleIndex);
        Assert.Contains("your settings for this show", d.Explain(), StringComparison.Ordinal);
        Assert.DoesNotContain("an admin lock", d.Explain(), StringComparison.Ordinal);
        kid.Overrides.Clear();
        var inherited = Decide(kid, UsersAdmin(), context, Fx.Slv());
        Assert.Equal(2, inherited.AudioIndex);
        Assert.Contains("the admin's Anime library defaults", inherited.Explain(), StringComparison.Ordinal);
        var plain = Decide(kid, null, context, Fx.Slv());
        Assert.True(plain.AudioLeftToVideo);
        Assert.DoesNotContain("an admin lock", plain.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void BurnedIn_EnglishFlag_NoEnglishSubtitles()
    {
        var person = Ctx.Person();
        person.BurnedIn.Add(new BurnedInFlag { ItemId = Ids.N(Ids.Series), Scope = OverrideScope.Series, Languages = new() { "eng" } });

        var d = Decide(person, null, Ctx.Episode(libraryName: "Anime", original: "ja"), Fx.Slv());

        // Subtitles are left to the video, but the burned-in flag still keeps a second English set off the picture.
        Assert.True(d.AudioLeftToVideo);
        Assert.Null(d.SubtitleIndex);
        Assert.False(d.SubtitlesLeftToVideo);
        Assert.True(Fx.Has(d, "subs.burnedin"));
    }
}

/// <summary>the cached resolver over in-memory fakes, and the engine decider on Jellyfin streams.</summary>
public class CachedResolverAndDeciderTests
{
    private static User NewUser(string name = "ViewerOne") => new(name, "Default", "Default")
    {
        AudioLanguagePreference = string.Empty,
        SubtitleLanguagePreference = "eng",
        SubtitleMode = SubtitlePlaybackMode.Smart,
    };

    private static (CachedProfileResolver Resolver, FakePersonStore Store, FakeContextBuilder Builder, FakeLibraryFacts Facts, NullProxy Library) Create()
    {
        var store = new FakePersonStore();
        var builder = new FakeContextBuilder();
        var facts = new FakeLibraryFacts();
        var (library, recorder) = NullProxy.Create<ILibraryManager>();
        var resolver = new CachedProfileResolver(store, builder, facts, library, null, NullLogger<CachedProfileResolver>.Instance);
        return (resolver, store, builder, facts, recorder);
    }

    private static MediaBrowser.Controller.Entities.TV.Episode NewEpisode() => new() { Id = Ids.Item, Name = "Episode 1" };

    private static List<MediaStream> Streams(IEnumerable<Track> tracks) => tracks.Select(t => new MediaStream
    {
        Index = t.Index,
        Type = t.Type == TrackType.Audio ? MediaStreamType.Audio : MediaStreamType.Subtitle,
        Language = t.Language,
        Title = t.Title,
        IsDefault = t.IsDefault,
        IsForced = t.IsForced,
        Codec = t.Type == TrackType.Audio ? "aac" : "subrip",
    }).ToList();

    [Fact]
    public void FirstUse_SeedsThePerson_AndCachesTheProfile()
    {
        var (resolver, store, builder, _, _) = Create();
        builder.Set(Ids.Item, Ctx.Episode(libraryName: "Anime", original: "ja"));
        var user = NewUser();
        var episode = NewEpisode();

        var first = resolver.Resolve(user, episode);
        var second = resolver.Resolve(user, episode);

        Assert.Same(first, second);
        Assert.Equal(1, builder.Builds);
        Assert.Equal(1, store.Creates);
        Assert.Equal(new[] { "en" }, store.TryGet(user.Id)!.Everyday.Understood);
        Assert.Equal(StarterRules.DefaultsLabel, first.Sources[SettingsFields.AudioChain]);
        Assert.Equal(1, resolver.Count);
    }

    [Fact]
    public void ClientIsPartOfTheKey()
    {
        var (resolver, _, builder, _, _) = Create();
        builder.Set(Ids.Item, Ctx.Episode());
        var user = NewUser();

        resolver.Resolve(user, NewEpisode(), "Jellyfin Web");
        resolver.Resolve(user, NewEpisode());

        Assert.Equal(2, builder.Builds);
    }

    [Fact]
    public void PersonSave_Invalidates()
    {
        var (resolver, store, builder, _, _) = Create();
        builder.Set(Ids.Item, Ctx.Episode());
        var user = NewUser();
        var first = resolver.Resolve(user, NewEpisode());

        var changed = store.TryGet(user.Id)!.Clone();
        changed.Everyday.Format = FormatPreference.TextOnly;
        store.Save(changed);
        var second = resolver.Resolve(user, NewEpisode());

        Assert.NotSame(first, second);
        Assert.Equal(FormatPreference.TextOnly, second.Profile.Format);
    }

    [Fact]
    public void FactsChange_Invalidates()
    {
        var (resolver, _, builder, facts, _) = Create();
        builder.Set(Ids.Item, Ctx.Episode());
        var user = NewUser();
        var first = resolver.Resolve(user, NewEpisode());

        facts.Set(Ctx.Facts(Ids.Shows, "Shows", detected: true));

        Assert.NotSame(first, resolver.Resolve(user, NewEpisode()));
    }

    [Fact]
    public void ItemUpdated_DropsThatItem()
    {
        var (resolver, _, builder, _, library) = Create();
        builder.Set(Ids.Item, Ctx.Episode());
        var user = NewUser();
        var first = resolver.Resolve(user, NewEpisode());

        library.Raise("ItemUpdated", null, new ItemChangeEventArgs { Item = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid() } });
        Assert.Same(first, resolver.Resolve(user, NewEpisode()));

        library.Raise("ItemUpdated", null, new ItemChangeEventArgs { Item = NewEpisode() });
        Assert.NotSame(first, resolver.Resolve(user, NewEpisode()));
    }

    [Fact]
    public void SeriesUpdated_DropsEverything()
    {
        var (resolver, _, builder, _, library) = Create();
        builder.Set(Ids.Item, Ctx.Episode());
        var user = NewUser();
        var first = resolver.Resolve(user, NewEpisode());

        library.Raise("ItemRemoved", null, new ItemChangeEventArgs { Item = new MediaBrowser.Controller.Entities.TV.Series { Id = Ids.Series } });

        Assert.Equal(0, resolver.Count);
        Assert.NotSame(first, resolver.Resolve(user, NewEpisode()));
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var (resolver, _, _, _, library) = Create();

        Assert.Single(library.Handlers["ItemUpdated"]);
        resolver.Dispose();

        Assert.Empty(library.Handlers["ItemUpdated"]);
        Assert.Empty(library.Handlers["ItemRemoved"]);
    }

    [Fact]
    public void Decider_AnimeLibrary_JapaneseAndFullEnglish()
    {
        var (resolver, store, builder, _, _) = Create();
        builder.Set(Ids.Item, Ctx.Episode(libraryName: "Anime", original: "ja"));
        var user = NewUser();
        var person = store.GetOrCreate(user.Id, () => Ctx.Person()).Clone();
        person.Libraries.Add(Ctx.MyLibrary(Ids.Anime, new SettingsLayer
        {
            AudioChain = Ctx.Chain(Ctx.Lang("ja"), Ctx.Step(AudioStepKind.VideoDefault)),
            WhenAudioUnderstood = SubtitleWant.Full,
            WhenAudioNotUnderstood = SubtitleWant.Full,
            SubtitleLanguages = new[] { "en" },
        }));
        store.Save(person);
        var decider = new EngineTrackDecider(resolver, NullLogger<EngineTrackDecider>.Instance);
        var source = new MediaSourceInfo { Id = "source", MediaStreams = Streams(Fx.Slv()), DefaultAudioStreamIndex = 1, DefaultSubtitleStreamIndex = 3 };

        var decision = decider.Decide(NewEpisode(), source, user);

        Assert.NotNull(decision);
        Assert.Equal(2, decision!.AudioIndex);
        Assert.Equal(4, decision.SubtitleIndex);
        Assert.Equal(1, decision.JellyfinAudioIndex);
        Assert.Equal(3, decision.JellyfinSubtitleIndex);
        Assert.NotEmpty(decision.Trace);
        Assert.Contains("your Anime library settings", decision.Explanation, StringComparison.Ordinal);
        Assert.NotNull(decision.Engine);
    }

    // Nothing chosen: Babelfin has nothing to say, so the media source, the PlaybackInfo filter and the monitor all stay out.
    [Fact]
    public void Decider_NothingChosen_ReturnsNull()
    {
        var (resolver, _, builder, _, _) = Create();
        builder.Set(Ids.Item, Ctx.Episode(libraryName: "Anime", original: "ja"));
        var decider = new EngineTrackDecider(resolver, NullLogger<EngineTrackDecider>.Instance);
        var source = new MediaSourceInfo { Id = "source", MediaStreams = Streams(Fx.Slv()), DefaultAudioStreamIndex = 1, DefaultSubtitleStreamIndex = 3 };

        Assert.Null(decider.Decide(NewEpisode(), source, NewUser()));
    }

    // Only the audio chosen: the subtitle index is "not set", so the media source keeps Jellyfin's own subtitle choice.
    [Fact]
    public void Decider_OnlyAudioChosen_RecomputesJellyfinSubtitlePolicy()
    {
        var (resolver, store, builder, _, _) = Create();
        builder.Set(Ids.Item, Ctx.Episode(libraryName: "Anime", original: "ja"));
        var user = NewUser();
        var person = store.GetOrCreate(user.Id, () => Ctx.Person()).Clone();
        person.Everyday.AudioChain = Ctx.Chain(Ctx.Lang("ja"), Ctx.Step(AudioStepKind.VideoDefault));
        store.Save(person);
        var decider = new EngineTrackDecider(resolver, NullLogger<EngineTrackDecider>.Instance);
        var source = new MediaSourceInfo { Id = "source", MediaStreams = Streams(Fx.Slv()), DefaultAudioStreamIndex = 1, DefaultSubtitleStreamIndex = 3 };

        var decision = decider.Decide(NewEpisode(), source, user);

        Assert.Equal(2, decision!.AudioIndex);
        Assert.Equal(3, decision.SubtitleIndex);
        Assert.Equal(3, decision.Engine!.SubtitleIndex);
        Assert.False(decision.Engine.SubtitlesLeftToVideo);
    }

    // Only the subtitles chosen ("when the audio isn't in my languages"): Jellyfin's audio stays, and it decides whether subtitles show.
    [Theory]
    [InlineData(1, 3)] // Jellyfin plays English (understood): signs and songs only, the forced English track
    [InlineData(2, 4)] // Jellyfin plays Japanese (not understood): full English
    public void Decider_OnlySubtitlesChosen_FollowJellyfinsAudio(int jellyfinAudio, int expectedSubtitle)
    {
        var (resolver, store, builder, _, _) = Create();
        builder.Set(Ids.Item, Ctx.Episode(libraryName: "Anime", original: "ja"));
        var user = NewUser();
        var person = store.GetOrCreate(user.Id, () => Ctx.Person()).Clone();
        person.Everyday.WhenAudioUnderstood = SubtitleWant.PartialOnly;
        person.Everyday.WhenAudioNotUnderstood = SubtitleWant.Full;
        store.Save(person);
        var decider = new EngineTrackDecider(resolver, NullLogger<EngineTrackDecider>.Instance);
        var source = new MediaSourceInfo { Id = "source", MediaStreams = Streams(Fx.Slv()), DefaultAudioStreamIndex = jellyfinAudio, DefaultSubtitleStreamIndex = -1 };

        var decision = decider.Decide(NewEpisode(), source, user);

        Assert.Null(decision!.AudioIndex);
        Assert.Equal(jellyfinAudio, decision.Engine!.AudioIndex);
        Assert.Equal(expectedSubtitle, decision.SubtitleIndex);
    }

    [Fact]
    public void Decider_NoSubtitlesWanted_IsMinusOne()
    {
        var (resolver, store, builder, _, _) = Create();
        builder.Set(Ids.Item, Ctx.Episode(libraryName: "Shows", original: "en"));
        var user = NewUser();
        var person = store.GetOrCreate(user.Id, () => Ctx.Person()).Clone();
        person.Everyday.WhenAudioUnderstood = SubtitleWant.Off;
        person.Everyday.WhenAudioNotUnderstood = SubtitleWant.Off;
        store.Save(person);
        var decider = new EngineTrackDecider(resolver, NullLogger<EngineTrackDecider>.Instance);
        var source = new MediaSourceInfo { Id = "source", MediaStreams = Streams(Fx.Slv()) };

        var decision = decider.Decide(NewEpisode(), source, user);

        // The audio is left to Jellyfin ("not set"); the subtitles are off.
        Assert.Null(decision!.AudioIndex);
        Assert.Equal(-1, decision.SubtitleIndex);
    }

    [Fact]
    public void Decider_PersonDisabled_ReturnsNull()
    {
        var (resolver, store, builder, _, _) = Create();
        builder.Set(Ids.Item, Ctx.Episode());
        var user = NewUser();
        var person = Ctx.Person();
        person.UserId = user.Id;
        person.Enabled = false;
        store.Save(person);
        var decider = new EngineTrackDecider(resolver, NullLogger<EngineTrackDecider>.Instance);

        Assert.Null(decider.Decide(NewEpisode(), new MediaSourceInfo { Id = "source", MediaStreams = Streams(Fx.Slv()) }, user));
    }

    [Fact]
    public void Decider_NoAudio_KeepsJellyfinsAudio()
    {
        var (resolver, store, builder, _, _) = Create();
        builder.Set(Ids.Item, Ctx.Episode());
        var decider = new EngineTrackDecider(resolver, NullLogger<EngineTrackDecider>.Instance);
        var empty = new MediaSourceInfo { Id = "source", MediaStreams = new List<MediaStream>() };

        // Nothing chosen: nothing to say at all.
        Assert.Null(decider.Decide(NewEpisode(), empty, NewUser()));

        // Subtitles chosen: the audio is still "not set" (Jellyfin's stays).
        var user = NewUser("viewerThree");
        var person = store.GetOrCreate(user.Id, () => Ctx.Person()).Clone();
        person.Everyday.WhenAudioUnderstood = SubtitleWant.Off;
        person.Everyday.WhenAudioNotUnderstood = SubtitleWant.Off;
        store.Save(person);
        var decision = decider.Decide(NewEpisode(), empty, user);

        Assert.Null(decision!.AudioIndex);
        Assert.Equal(-1, decision.SubtitleIndex);
    }
}
