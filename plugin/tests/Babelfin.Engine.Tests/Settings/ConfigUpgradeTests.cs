using System.Linq;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;
using static Babelfin.Engine.Tests.Settings.Ctx;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>The version 4 settings upgrade: Babelfin becomes neutral without touching anything else.</summary>
public class ConfigUpgradeTests
{
    /// <summary>A version 3 fixture with server-wide track defaults, an Anime library default, the starter on, and other settings.</summary>
    private static PluginConfiguration Version3()
    {
        var config = new PluginConfiguration
        {
            ConfigVersion = 3,
            Everyday = new SettingsLayer
            {
                AudioChain = Chain(Step(AudioStepKind.OriginalLanguage), Lang("en"), Step(AudioStepKind.FileDefault)),
                ForeignTitles = ForeignTitlesRule.OriginalWithSubtitles,
                WhenAudioUnderstood = SubtitleWant.PartialOnly,
                WhenAudioNotUnderstood = SubtitleWant.Full,
                SubtitleLanguages = new[] { "en" },
                IfNothingMatches = WhenNothingMatches.PartialInMyLanguage,
                Sdh = Preference.Avoid,
            },
        };
        config.Starter.Enabled = true;
        config.Libraries.Add(TheirLibrary(Ids.Anime, new SettingsLayer
        {
            AudioChain = Chain(Lang("ja"), Step(AudioStepKind.OriginalLanguage), Step(AudioStepKind.FileDefault)),
            UnlabelledAudio = UnlabelledAudioRule.SpecificLanguage,
            UnlabelledAudioLanguage = "ja",
        }, OriginalAudioChoice.Yes));
        config.SmartRules.Add(Rule("Mine", new SettingsLayer { AudioChain = Chain(Lang("fr")) }, When(ConditionKind.Genre, "French")));
        config.Titles.Add(Title(Ids.Series, OverrideScope.Series, new SettingsLayer { AudioChain = Chain(Lang("ko")) }));
        return config;
    }

    [Fact]
    public void FromVersion3_ClearsTheTrackDefaults_AndSwitchesTheStarterOff()
    {
        var config = Version3();

        Assert.True(config.Upgrade());

        Assert.Equal(PluginConfiguration.CurrentVersion, config.ConfigVersion);
        Assert.False(config.Starter.Enabled);
        foreach (var layer in new[] { config.Everyday, config.Libraries[0].Settings })
        {
            Assert.Null(layer.AudioChain);
            Assert.Null(layer.ForeignTitles);
            Assert.Null(layer.WhenAudioUnderstood);
            Assert.Null(layer.WhenAudioNotUnderstood);
            Assert.Null(layer.SubtitleLanguages);
        }
    }

    // Hidden settings are archived, while supported genre table choices become active schema 3/config 8 rows.
    [Fact]
    public void FromVersion3_ArchivesHiddenChoices_ActivatesVisibleGenresOnce_AndKeepsTheRest()
    {
        var config = Version3();
        config.DryRun = true;
        config.BurnedIn.Add(new BurnedInFlag { ItemId = Ids.N(Ids.Series), Scope = OverrideScope.Series, Languages = new() { "en" } });
        config.Everyday.UnlabelledAudio = UnlabelledAudioRule.SpecificLanguage;
        config.Everyday.UnlabelledAudioLanguage = "ja";

        config.Upgrade();

        var genre = Assert.Single(config.SmartRules);
        Assert.Equal("French", genre.Name);
        Assert.Equal("fr", genre.Settings.AudioChain![0].Language);
        var archived = Assert.Single(config.Retired.GenreRules);
        Assert.Equal("Mine", archived.Name);
        Assert.Equal("fr", archived.Settings.AudioChain![0].Language);
        Assert.NotSame(archived.Settings.AudioChain, genre.Settings.AudioChain);
        Assert.Empty(config.Titles);
        Assert.Empty(config.BurnedIn);
        Assert.False(config.DryRun);
        Assert.Null(config.Everyday.IfNothingMatches);
        var anime = Assert.Single(config.Libraries);
        Assert.Equal(OriginalAudioChoice.Yes, anime.OriginalAudio);
        Assert.True(anime.Settings.IsEmpty);
        Assert.Null(config.Everyday.Sdh);
        Assert.Equal(Preference.Avoid, Assert.Single(config.Retired.Layers, l => l.Location == "server defaults").Settings.Sdh);
        Assert.Equal(UnlabelledAudioRule.SpecificLanguage, config.Everyday.UnlabelledAudio);
        Assert.Equal("ja", config.Everyday.UnlabelledAudioLanguage);
        Assert.Null(config.PerLibraryDefaults);
        var note = config.LastUpgradeNote;
        var id = genre.Id;
        var archive = System.Text.Json.JsonSerializer.Serialize(config.Retired);
        Assert.False(config.Upgrade());
        Assert.Equal(note, config.LastUpgradeNote);
        Assert.Equal(id, Assert.Single(config.SmartRules).Id);
        Assert.Equal(archive, System.Text.Json.JsonSerializer.Serialize(config.Retired));
        config.SmartRules.Clear();
        Assert.False(config.Upgrade());
        Assert.Empty(config.SmartRules);
        Assert.Equal(archive, System.Text.Json.JsonSerializer.Serialize(config.Retired));
    }

    // A schema-version 4 file keeps the administrator's table choices; only the hidden extras are retired.
    [Fact]
    public void FromVersion4_KeepsTheTable()
    {
        var config = new PluginConfiguration { ConfigVersion = 4 };
        config.Everyday.AudioChain = Chain(Lang("en"), Step(AudioStepKind.VideoDefault));
        config.Libraries.Add(TheirLibrary(Ids.Anime, new SettingsLayer
        {
            AudioChain = Chain(Lang("ja"), Step(AudioStepKind.VideoDefault)),
            WhenAudioNotUnderstood = SubtitleWant.Full,
            UnlabelledAudio = UnlabelledAudioRule.SpecificLanguage,
        }));
        config.SmartRules.Add(Rule("Hidden", new SettingsLayer { Format = FormatPreference.TextOnly }, When(ConditionKind.Genre, "Anime")));

        Assert.True(config.Upgrade());

        Assert.Equal(PluginConfiguration.CurrentVersion, config.ConfigVersion);
        Assert.Equal("en", config.Everyday.AudioChain![0].Language);
        var anime = Assert.Single(config.Libraries);
        Assert.Equal("ja", anime.Settings.AudioChain![0].Language);
        Assert.Equal(SubtitleWant.Full, anime.Settings.WhenAudioNotUnderstood);
        Assert.Null(anime.Settings.UnlabelledAudio);
        Assert.Empty(config.SmartRules);
    }

    // Choices the admin makes after the upgrade survive every later start.
    // Version 6: the New users template keeps only languages (the top table is the live default for everyone).
    [Fact]
    public void FromVersion5_TemplateKeepsOnlyLanguages()
    {
        var config = new PluginConfiguration { ConfigVersion = 5 };
        var template = Person(new[] { "ja" }, new[] { "en" });
        template.PerLibrary = true;
        template.Everyday.AudioChain = Chain(Lang("en"), Step(AudioStepKind.VideoDefault));
        template.Everyday.WhenAudioUnderstood = SubtitleWant.Off;
        template.Everyday.Format = FormatPreference.TextOnly;
        template.Libraries.Add(MyLibrary(Ids.Anime, new SettingsLayer { AudioChain = Chain(Lang("ja")) }));
        config.NewUserTemplate = template;

        Assert.True(config.Upgrade());

        var t = config.NewUserTemplate!;
        Assert.Equal(new[] { "ja" }, t.Everyday.Understood);
        Assert.Null(t.Everyday.Reads);
        Assert.Equal(new[] { "en" }, config.Retired.NewUserTemplate!.Everyday.Reads);
        Assert.Null(t.Everyday.AudioChain);
        Assert.Null(t.Everyday.WhenAudioUnderstood);
        Assert.Null(t.Everyday.Format);
        Assert.Equal(FormatPreference.TextOnly, config.Retired.NewUserTemplate!.Everyday.Format);
        Assert.Empty(t.Libraries);
        Assert.Null(t.PerLibrary);
    }

    // Jellyfin Desktop runs jellyfin-web: an automatically added "keep the app's choice" row becomes "next episode"; an admin's other
    // choice is kept; a missing row is added.
    [Theory]
    [InlineData(null, AppModeKind.ReplaceOnAutoplay)]
    [InlineData(AppModeKind.Trust, AppModeKind.ReplaceOnAutoplay)]
    [InlineData(AppModeKind.ReplaceUnlessManual, AppModeKind.ReplaceUnlessManual)]
    public void FromVersion5_JellyfinDesktopFollowsTheWebApp(AppModeKind? stored, AppModeKind expected)
    {
        var config = new PluginConfiguration { ConfigVersion = 5 };
        config.AppModes.RemoveAll(a => a.Client == "Jellyfin Desktop");
        if (stored is { } mode)
        {
            config.AppModes.Add(new AppMode { Client = "Jellyfin Desktop", Mode = mode, Seen = true });
        }

        config.Upgrade();

        Assert.Equal(expected, Assert.Single(config.AppModes, a => a.Client == "Jellyfin Desktop").Mode);
    }

    [Fact]
    public void FreshInstall_ShipsJellyfinDesktop()
    {
        Assert.Equal(AppModeKind.ReplaceOnAutoplay, new PluginConfiguration().FindAppMode("Jellyfin Desktop")!.Mode);
    }

    // The Android app reports itself as "Jellyfin for Android". A missing row is added as shipped; a row that was only discovered
    // (with the mode for unknown apps) becomes the shipped row and keeps when it was seen; an admin's row is kept.
    [Fact]
    public void FreshInstall_ShipsJellyfinForAndroidBesideJellyfinAndroid()
    {
        var config = new PluginConfiguration();
        Assert.Equal(AppModeKind.ReplaceOnAutoplay, config.FindAppMode("Jellyfin for Android")!.Mode);
        Assert.Equal(AppModeKind.ReplaceOnAutoplay, config.FindAppMode("Jellyfin Android")!.Mode);
        Assert.True(config.RemembersPrePlayChoices("Jellyfin for Android"));
    }

    [Fact]
    public void FromVersion8_AddsTheJellyfinForAndroidRow()
    {
        var config = new PluginConfiguration { ConfigVersion = 8 };
        config.AppModes.RemoveAll(a => a.Client == "Jellyfin for Android");
        var others = config.AppModes.Count;

        Assert.True(config.Upgrade());

        var row = Assert.Single(config.AppModes, a => a.Client == "Jellyfin for Android");
        Assert.Equal(AppModeKind.ReplaceOnAutoplay, row.Mode);
        Assert.False(row.SeenOnly);
        Assert.Null(row.RememberPrePlayChoices);
        Assert.Equal(others + 1, config.AppModes.Count);
        Assert.Equal(PluginConfiguration.CurrentVersion, config.ConfigVersion);
    }

    [Theory]
    [InlineData(AppModeKind.Trust)]
    [InlineData(AppModeKind.ReplaceUnlessManual)]
    public void FromVersion8_ADiscoveredJellyfinForAndroidRowBecomesTheShippedRow(AppModeKind unknownAppMode)
    {
        var seen = new System.DateTime(2026, 10, 30, 11, 22, 38, System.DateTimeKind.Utc);
        var config = new PluginConfiguration { ConfigVersion = 8, UnknownAppMode = unknownAppMode };
        config.AppModes.RemoveAll(a => a.Client == "Jellyfin for Android");
        Assert.True(config.NoteAppSeen("Jellyfin for Android", seen));
        Assert.Equal(unknownAppMode, config.FindAppMode("Jellyfin for Android")!.Mode);

        config.Upgrade();

        var row = Assert.Single(config.AppModes, a => a.Client == "Jellyfin for Android");
        Assert.Equal(AppModeKind.ReplaceOnAutoplay, row.Mode);
        Assert.False(row.SeenOnly);
        Assert.True(row.Seen);
        Assert.Equal(seen, row.LastSeen);
        Assert.True(config.RemembersPrePlayChoices("Jellyfin for Android"));
        Assert.Equal(unknownAppMode, config.UnknownAppMode);
    }

    [Theory]
    [InlineData(AppModeKind.Trust, false)]
    [InlineData(AppModeKind.ReplaceUnlessManual, true)]
    [InlineData(AppModeKind.ReplaceOnAutoplay, false)]
    public void FromVersion8_AnAdminsJellyfinForAndroidRowIsKept(AppModeKind mode, bool remember)
    {
        var config = new PluginConfiguration { ConfigVersion = 8 };
        config.AppModes.RemoveAll(a => a.Client == "Jellyfin for Android");
        config.AppModes.Add(new AppMode { Client = "Jellyfin for Android", Mode = mode, Seen = true, RememberPrePlayChoices = remember });

        config.Upgrade();

        var row = Assert.Single(config.AppModes, a => a.Client == "Jellyfin for Android");
        Assert.Equal(mode, row.Mode);
        Assert.Equal(remember, row.RememberPrePlayChoices);
        Assert.False(row.SeenOnly);
    }

    [Fact]
    public void CurrentVersion_LeavesALaterDiscoveredJellyfinForAndroidRowAlone()
    {
        var config = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion };
        config.AppModes.RemoveAll(a => a.Client == "Jellyfin for Android");
        Assert.True(config.NoteAppSeen("Jellyfin for Android", new System.DateTime(2026, 10, 30, 0, 0, 0, System.DateTimeKind.Utc)));

        Assert.False(config.Upgrade());

        var row = Assert.Single(config.AppModes, a => a.Client == "Jellyfin for Android");
        Assert.True(row.SeenOnly);
        Assert.Equal(AppModeKind.Trust, row.Mode);
    }

    [Fact]
    public void CurrentVersion_IsLeftAlone()
    {
        var config = Version3();
        config.Upgrade();
        config.Everyday.AudioChain = Chain(Lang("en"), Step(AudioStepKind.VideoDefault));
        config.Everyday.WhenAudioUnderstood = SubtitleWant.Off;
        config.Starter.Enabled = true;

        Assert.False(config.Upgrade());

        Assert.Equal("en", config.Everyday.AudioChain![0].Language);
        Assert.Equal(SubtitleWant.Off, config.Everyday.WhenAudioUnderstood);
        Assert.True(config.Starter.Enabled);
    }

    [Fact]
    public void FreshInstall_IsNeutral()
    {
        var config = new PluginConfiguration();

        config.Upgrade();

        Assert.False(config.Starter.Enabled);
        Assert.True(config.Everyday.IsEmpty);
        Assert.Empty(config.Libraries);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OlderFiles_AlsoBecomeNeutral(int version)
    {
        var config = Version3();
        config.ConfigVersion = version;

        config.Upgrade();

        Assert.False(config.Starter.Enabled);
        Assert.Null(config.Everyday.AudioChain);
        Assert.Null(config.Libraries[0].Settings.AudioChain);
        Assert.Equal(PluginConfiguration.CurrentVersion, config.ConfigVersion);
    }

    [Fact]
    public void TheClearedFields_AreExactlyTheTablesFields()
    {
        Assert.Equal(
            new[] { SettingsFields.AudioChain, SettingsFields.ForeignTitles, SettingsFields.SubtitleLanguages, SettingsFields.WhenAudioNotUnderstood, SettingsFields.WhenAudioUnderstood },
            PluginConfiguration.TrackDefaultFields.OrderBy(f => f, System.StringComparer.Ordinal).ToArray());
    }
}
