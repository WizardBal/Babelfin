using System;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

public class FallbackTests
{
    private static readonly ResolvedProfile JapaneseAudio = OriginalAudio("en");
    private static readonly ItemFacts Japanese = Item(original: "ja");

    [Fact]
    public void WantedFullLanguageMissing_ShowsNothingAndExplainsWhy()
    {
        var d = Decide(new[] { Audio(1, "jpn"), Sub(2, "spa") }, JapaneseAudio, Japanese);
        Assert.True(d.SubtitlesWanted);
        Assert.Null(d.SubtitleIndex);
        Assert.True(d.Has("subs.nothing"));
        Assert.Contains("Subtitles: none found in English.", d.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void WantedFullLanguagePresent_SelectsDialogue()
    {
        var d = Decide(new[] { Audio(1, "jpn"), Sub(2, "eng", "Dialogue"), Sub(3, "eng", "Signs & Songs", isForced: true) }, JapaneseAudio, Japanese);
        Assert.Equal(2, d.SubtitleIndex);
        Assert.True(d.Has("subs.skipped.partial", 3));
    }

    [Theory]
    [InlineData(WhenNothingMatches.ShowNothing, null)]
    [InlineData(WhenNothingMatches.PartialInMyLanguage, 2)]
    [InlineData(WhenNothingMatches.FullInAnyBackupLanguage, null)]
    public void ExplicitPartialTrackUsesOnlyThePartialFallback(WhenNothingMatches fallback, int? expected)
    {
        var d = Decide(new[] { Audio(1, "jpn"), Sub(2, "eng", "Signs & Songs", isForced: true) },
            JapaneseAudio with { IfNothingMatches = fallback }, Japanese);
        Assert.Equal(expected, d.SubtitleIndex);
        Assert.True(d.Has("subs.skipped.partial", 2));
        Assert.Equal(expected.HasValue, d.Has("subs.fallback"));
    }

    [Fact]
    public void PartialOnly_DoesNotUseFullDialogue()
    {
        var d = Decide(new[] { Audio(1, "eng"), Sub(2, "eng", "Dialogue") }, Starter("en"));
        Assert.Null(d.SubtitleIndex);
        Assert.True(d.Has("subs.skipped.full", 2));
    }

    [Fact]
    public void BurnedInLanguageSatisfiesSubtitleRequest()
    {
        var d = Decide(new[] { Audio(1, "jpn") }, JapaneseAudio, Item("ja", null, "en"));
        Assert.True(d.SubtitlesWanted);
        Assert.Null(d.SubtitleIndex);
        Assert.True(d.Has("subs.burnedin"));
        Assert.Contains("Subtitles: none (English is burned in).", d.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void NextWantedLanguageIsUsedBeforeFallback()
    {
        var d = Decide(new[] { Audio(1, "jpn"), Sub(2, "spa") }, JapaneseAudio with { SubtitleLanguages = new[] { "en", "es" } }, Japanese);
        Assert.Equal(2, d.SubtitleIndex);
        Assert.True(d.Has("subs.matched", 2));
        Assert.False(d.Has("subs.fallback"));
        Assert.Contains("so your next language", d.Explain(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullBackupCanComeFromReadableOrUnderstoodLanguages(bool spokenBackup)
    {
        var profile = Fx.Starter(spokenBackup ? new[] { "en", "es" } : new[] { "en" },
            spokenBackup ? new[] { "en" } : new[] { "en", "es" }) with
        { SubtitleLanguages = new[] { "en" }, IfNothingMatches = WhenNothingMatches.FullInAnyBackupLanguage };
        var d = Decide(new[] { Audio(1, "jpn"), Sub(2, "spa") }, profile, Japanese);
        Assert.Equal(2, d.SubtitleIndex);
        Assert.True(d.Has("subs.fallback", 2));
        Assert.Contains("nothing in English, so a fallback.", d.Explain(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WhenNothingMatches.PartialInMyLanguage, "Signs & Songs")]
    [InlineData(WhenNothingMatches.FullInAnyBackupLanguage, "Dialogue")]
    public void FallbackRejectsLanguagesThePersonDoesNotReadOrUnderstand(WhenNothingMatches fallback, string title)
    {
        var d = Decide(new[] { Audio(1, "jpn"), Sub(2, "spa", title) }, JapaneseAudio with { IfNothingMatches = fallback }, Japanese);
        Assert.Null(d.SubtitleIndex);
        Assert.False(d.Has("subs.fallback"));
    }

    [Theory]
    [InlineData(FormatPreference.TextOnly, null)]
    [InlineData(FormatPreference.PreferText, 3)]
    public void ImageTrackRespectsTextPolicy(FormatPreference format, int? expected)
    {
        var d = Decide(MidnightCircuit(), Fx.Starter(new[] { "de" }, new[] { "en" }) with { Format = format });
        Assert.Equal(expected, d.SubtitleIndex);
        if (expected.HasValue) Assert.Equal(SubtitleFormat.Image, d.Tracks[2].Track.Format);
        else Assert.True(d.Has("subs.skipped.image", 3));
    }
}
