using System;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Seeding;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>each person's starting point is seeded from their Jellyfin settings, and every guess is marked "assumed".</summary>
public class SeederTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static PersonSettings Seed(string? audio, string? subtitle, string? mode = "Default", string? ui = null, string? server = "en") =>
        PersonSeeder.Seed(new SeedInputs(Ids.Person, "Test", audio, subtitle, mode, ui, server, Now), LanguageTable.Default);

    [Fact]
    public void BothPreferencesPresent_NothingAssumed()
    {
        var p = Seed("jpn", "eng");

        Assert.Equal(new[] { "ja" }, p.Everyday.Understood);
        Assert.Equal(new[] { "en" }, p.Everyday.Reads);
        Assert.Empty(p.Seed!.Assumed);
        Assert.Equal(Ids.Person, p.UserId);
        Assert.Equal("Test", p.UserName);
        Assert.Equal(Now, p.Seed.SeededAt);
        Assert.Equal("jpn", p.Seed.JellyfinAudioLanguage);
        Assert.Equal("eng", p.Seed.JellyfinSubtitleLanguage);
        Assert.Null(p.Everyday.ForeignTitles);
    }

    [Fact]
    public void OriginalLanguageSentinel_IsNotALanguage_AndChoosesNothing()
    {
        var p = Seed("OriginalLanguage", "eng");

        // Only languages are detected; which audio plays stays the video's until the person chooses.
        Assert.Null(p.Everyday.ForeignTitles);
        Assert.Null(p.Everyday.AudioChain);
        Assert.Equal(new[] { "en" }, p.Everyday.Understood);
        Assert.Equal("other-field", Assert.Single(p.Seed!.Assumed).From);
    }

    [Fact]
    public void EmptyAudio_WithSubtitles_CopiesTheOtherField()
    {
        var p = Seed(string.Empty, "eng");

        Assert.Equal(new[] { "en" }, p.Everyday.Understood);
        var assumed = Assert.Single(p.Seed!.Assumed);
        Assert.Equal(SettingsFields.Understood, assumed.Field);
        Assert.Equal("en", assumed.Language);
        Assert.Equal("other-field", assumed.From);
    }

    [Fact]
    public void EmptySubtitles_WithAudio_CopiesTheOtherField()
    {
        var p = Seed("ger", null);

        Assert.Equal(new[] { "de" }, p.Everyday.Reads);
        Assert.Equal(SettingsFields.Reads, Assert.Single(p.Seed!.Assumed).Field);
    }

    [Fact]
    public void BothEmpty_ServerLanguageAssumed()
    {
        var p = Seed(null, " ", server: "fr");

        Assert.Equal(new[] { "fr" }, p.Everyday.Understood);
        Assert.Equal(new[] { "fr" }, p.Everyday.Reads);
        Assert.Equal(2, p.Seed!.Assumed.Count);

        // Understood is guessed from the server; Reads is then copied from Understood (the seeder fills one list at a time).
        Assert.Equal(new[] { SettingsFields.Understood, SettingsFields.Reads }, p.Seed.Assumed.Select(a => a.Field));
        Assert.Equal(new[] { "server", "server" }, p.Seed.Assumed.Select(a => a.From));
        Assert.All(p.Seed.Assumed, a => Assert.Equal("fr", a.Language));
    }

    [Fact]
    public void BothEmpty_UiLanguageReported_UsesIt()
    {
        var p = Seed(null, null, ui: "pt-BR", server: "en");

        Assert.Equal(new[] { "pt-BR" }, p.Everyday.Understood);
        Assert.Equal(new[] { "ui", "ui" }, p.Seed!.Assumed.Select(a => a.From));
    }

    [Fact]
    public void BothEmpty_NoServerLanguage_NothingAssumed()
    {
        var p = Seed(null, null, server: null);

        Assert.Empty(p.Everyday.Understood!);
        Assert.Empty(p.Everyday.Reads!);
        Assert.Empty(p.Seed!.Assumed);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("OnlyForced")]
    [InlineData("Always")]
    [InlineData("Smart")]
    [InlineData("Default")]
    [InlineData(null)]
    public void SubtitleMode_IsRecorded_ButNotCopied(string? mode)
    {
        var p = Seed("eng", "eng", mode);

        // Jellyfin keeps applying its own subtitle mode while Babelfin leaves the subtitles to the video, so nothing is copied.
        Assert.Null(p.Everyday.WhenAudioUnderstood);
        Assert.Null(p.Everyday.WhenAudioNotUnderstood);
        Assert.Null(p.Everyday.SubtitleLanguages);
        Assert.Equal(mode, p.Seed!.JellyfinSubtitleMode);
    }

    [Theory]
    [InlineData("Smart")]
    [InlineData("Default")]
    [InlineData(null)]
    public void SubtitleMode_SmartOrDefault_LeavesTheSubtitlesAlone(string? mode)
    {
        var p = Seed("eng", "eng", mode);

        Assert.Null(p.Everyday.WhenAudioUnderstood);
        Assert.Null(p.Everyday.WhenAudioNotUnderstood);
    }

    [Fact]
    public void UnknownCodes_AreIgnored()
    {
        var p = Seed("xx-nonsense, jpn", "qqq");

        Assert.Equal(new[] { "ja" }, p.Everyday.Understood);
        Assert.Equal(new[] { "ja" }, p.Everyday.Reads);
        Assert.Equal("other-field", Assert.Single(p.Seed!.Assumed).From);
    }

    [Fact]
    public void ListsAreDeduplicatedAndOrdered()
    {
        var p = Seed("eng,jpn,en", "spa");

        Assert.Equal(new[] { "en", "ja" }, p.Everyday.Understood);
        Assert.Equal(new[] { "es" }, p.Everyday.Reads);
    }

    [Fact]
    public void Seed_LeavesEverythingElseToInherit()
    {
        var p = Seed("eng", "eng", "Smart");

        Assert.Equal(new[] { SettingsFields.Understood, SettingsFields.Reads }, p.Everyday.SetFields());
        Assert.True(p.Locks.IsEmpty);
        Assert.True(p.Enabled);
        Assert.False(p.UseEverydayEverywhere);
    }
}
