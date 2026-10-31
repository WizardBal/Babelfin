using System;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>The "Default (…)" values of the person's "Audio and subtitles" table.</summary>
public class TableDefaultsTests
{
    private static readonly LibraryInfoDto[] Libraries =
    {
        new(Ids.N(Ids.Anime), "Anime", "tvshows", true, "ja"),
        new(Ids.N(Ids.Movies), "Movies", "movies", false, null),
    };

    private static PluginConfiguration Admin()
    {
        var admin = new PluginConfiguration();
        admin.Libraries.Add(new LibraryDefault { LibraryId = Ids.N(Ids.Anime), Name = "Anime", OriginalAudio = OriginalAudioChoice.Yes });
        return admin;
    }

    [Fact]
    public void AllLibraries_LeavesOutThePersonsOwnChoice()
    {
        var person = Ctx.Person();
        person.Everyday.AudioChain = Ctx.Chain(Ctx.Lang("en"), Ctx.Step(AudioStepKind.FileDefault));
        person.Everyday.WhenAudioUnderstood = SubtitleWant.Off;
        person.Everyday.WhenAudioNotUnderstood = SubtitleWant.Off;

        var d = TableDefaults.Build(person, Admin(), Libraries, null, LanguageTable.Default);

        // Nobody chose anything above the person: both tracks are left to the video.
        Assert.Equal(AudioStepKind.VideoDefault, Assert.Single(d.AllLibraries.AudioChain).Kind);
        Assert.Equal(SubtitleWant.VideoDefault, d.AllLibraries.WhenAudioUnderstood);
        Assert.Equal(SubtitleWant.VideoDefault, d.AllLibraries.WhenAudioNotUnderstood);
        Assert.Equal(new[] { "en" }, d.AllLibraries.Understood.ToArray());

        // The stored document is not touched.
        Assert.Equal("en", person.Everyday.AudioChain![0].Language);
        Assert.Equal(SubtitleWant.Off, person.Everyday.WhenAudioUnderstood);
    }

    // Out of the box every row is the video's default, even a library called Anime.
    [Fact]
    public void Neutral_EveryRowIsTheVideosDefault()
    {
        var d = TableDefaults.Build(Ctx.Person(), new PluginConfiguration(), Libraries, null, LanguageTable.Default);

        foreach (var row in d.Libraries.Values.Append(d.AllLibraries))
        {
            Assert.Equal(AudioStepKind.VideoDefault, Assert.Single(row.AudioChain).Kind);
            Assert.Equal(SubtitleWant.VideoDefault, row.WhenAudioUnderstood);
            Assert.Equal(SubtitleWant.VideoDefault, row.WhenAudioNotUnderstood);
            Assert.False(row.AudioFromLibrary);
            Assert.False(row.SubtitlesFromLibrary);
        }
    }

    [Fact]
    public void LibraryRow_ShowsDetection_AndLeavesOutThePersonsLibraryChoice()
    {
        var person = Ctx.Person();
        person.Libraries.Add(new LibraryLayer { LibraryId = Ids.N(Ids.Anime), Name = "Anime", Settings = new SettingsLayer { AudioChain = Ctx.Chain(Ctx.Lang("en")) } });
        var admin = Admin();
        admin.Starter.Enabled = true; // the original-language library rule is opt-in

        var d = TableDefaults.Build(person, admin, Libraries, null, LanguageTable.Default);

        var anime = d.Libraries[Ids.N(Ids.Anime)];
        Assert.Equal(AudioStepKind.OriginalLanguage, anime.AudioChain[0].Kind);
        Assert.False(anime.AudioFromLibrary);
        Assert.False(anime.SubtitlesFromLibrary);
        Assert.Equal(AudioStepKind.VideoDefault, d.Libraries[Ids.N(Ids.Movies)].AudioChain[0].Kind);
        Assert.Equal("en", person.Libraries[0].Settings.AudioChain![0].Language);
    }

    [Fact]
    public void LibraryRow_FlagsTheAdminsLibraryDefault()
    {
        var admin = Admin();
        admin.Libraries.Add(new LibraryDefault
        {
            LibraryId = Ids.N(Ids.Movies),
            Name = "Movies",
            Settings = new SettingsLayer { WhenAudioUnderstood = SubtitleWant.Off, WhenAudioNotUnderstood = SubtitleWant.Off },
        });

        var d = TableDefaults.Build(Ctx.Person(), admin, Libraries, null, LanguageTable.Default);

        var movies = d.Libraries[Ids.N(Ids.Movies)];
        Assert.True(movies.SubtitlesFromLibrary);
        Assert.False(movies.AudioFromLibrary);
        Assert.Equal(SubtitleWant.Off, movies.WhenAudioUnderstood);
    }

    // The admin's "same for every library" mode: the library rows' defaults come from the admin's everyday choice, not their rows.
    [Fact]
    public void AdminEveryLibraryMode_RowsShowTheEverydayDefault()
    {
        var admin = Admin();
        admin.PerLibraryDefaults = false;
        admin.Everyday.AudioChain = Ctx.Chain(Ctx.Lang("de"), Ctx.Step(AudioStepKind.VideoDefault));
        admin.Libraries.Add(new LibraryDefault
        {
            LibraryId = Ids.N(Ids.Movies),
            Settings = new SettingsLayer { AudioChain = Ctx.Chain(Ctx.Lang("fr")) },
        });

        var d = TableDefaults.Build(Ctx.Person(), admin, Libraries, null, LanguageTable.Default);

        Assert.False(d.Libraries[Ids.N(Ids.Movies)].AudioFromLibrary);
        Assert.Equal("de", d.Libraries[Ids.N(Ids.Movies)].AudioChain[0].Language);
        Assert.Equal("de", d.AllLibraries.AudioChain[0].Language);
    }

    // The admin's per-library mode: each row shows its own default, and rows without one are the video's.
    [Fact]
    public void AdminPerLibraryMode_RowsShowTheirOwnDefault()
    {
        var admin = new PluginConfiguration { PerLibraryDefaults = true };
        admin.Everyday.AudioChain = Ctx.Chain(Ctx.Lang("de"));
        admin.Libraries.Add(new LibraryDefault { LibraryId = Ids.N(Ids.Movies), Settings = new SettingsLayer { AudioChain = Ctx.Chain(Ctx.Lang("fr")) } });

        var d = TableDefaults.Build(Ctx.Person(), admin, Libraries, null, LanguageTable.Default);

        Assert.True(d.Libraries[Ids.N(Ids.Movies)].AudioFromLibrary);
        Assert.Equal("fr", d.Libraries[Ids.N(Ids.Movies)].AudioChain[0].Language);
        Assert.Equal(AudioStepKind.VideoDefault, d.Libraries[Ids.N(Ids.Anime)].AudioChain[0].Kind);
    }

    // The person's mode is reported (their choice, else the automatic one) so the page opens in it.
    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void PersonsMode_IsReported(bool? stored, bool hasRows, bool expected)
    {
        var person = Ctx.Person();
        person.PerLibrary = stored;
        if (hasRows)
        {
            person.Libraries.Add(Ctx.MyLibrary(Ids.Anime, new SettingsLayer { AudioChain = Ctx.Chain(Ctx.Lang("ja")) }));
        }

        Assert.Equal(expected, TableDefaults.Build(person, new PluginConfiguration(), Libraries, null, LanguageTable.Default).PerLibrary);
    }
}
