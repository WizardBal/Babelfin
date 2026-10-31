using System;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

public sealed class SubtitleMetadataRegressionTests
{
    private static ResolvedProfile EnglishSubtitles => Starter("en") with
    {
        AudioChain = new[] { Lang("ja"), Step(AudioStepKind.VideoDefault) },
        WhenAudioUnderstood = SubtitleWant.Full,
        WhenAudioNotUnderstood = SubtitleWant.Full,
        SubtitleLanguages = new[] { "en" },
    };

    private static ResolvedProfile OrderedSubtitles => EnglishSubtitles with { SubtitleLanguages = new[] { "en", "es" } };

    [Theory]
    [InlineData(null, true, "Dialogue", Preference.Neutral)]
    [InlineData("Forced", true, "Dialogue", Preference.Neutral)]
    [InlineData("Forced", false, "Dialogue", Preference.Neutral)]
    [InlineData("Forcé", true, "SDH", Preference.Neutral)]
    [InlineData("強制", true, "SDH", Preference.Avoid)]
    public void OrderedLanguages_IdentifiedFullOrSdh_BeatsEarlierUncertainForced(string? title, bool forced, string identifiedTitle, Preference sdh)
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "eng", title, isDefault: true, isForced: forced), Sub(3, "spa", identifiedTitle) },
            OrderedSubtitles with { Sdh = sdh });

        Assert.Equal(3, d.SubtitleIndex);
        var match = Assert.Single(d.Trace, x => x.Code == "subs.matched");
        Assert.Equal("identified", match.Args!["coverage"]);
        Assert.Equal("es", match.Args["language"]);
        Assert.Equal("2", match.Args["position"]);
        Assert.Contains("so your next language", d.Explain(), StringComparison.Ordinal);
        Assert.DoesNotContain("metadata does not identify dialogue coverage", d.Explain(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    public void OrderedLanguages_IdentifiedTracks_KeepRequestedLanguageOrder(bool spanishFirst, int expected)
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "eng", isDefault: true, isForced: true), Sub(3, "en", "Dialogue"), Sub(4, "es", "SDH", isDefault: true) },
            OrderedSubtitles with { SubtitleLanguages = spanishFirst ? new[] { "es", "en" } : new[] { "en", "es" } });

        Assert.Equal(expected, d.SubtitleIndex);
        Assert.Contains(d.Trace, x => x.Code == "subs.matched" && x.Args!["coverage"] == "identified" && x.Args["position"] == "1");
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    public void OrderedLanguages_OnlyUncertainForcedTracks_KeepRequestedLanguageOrder(bool spanishFirst, int expected)
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "eng", isForced: true), Sub(3, "spa", "Forced", isDefault: true, isForced: true) },
            OrderedSubtitles with { SubtitleLanguages = spanishFirst ? new[] { "es", "en" } : new[] { "en", "es" } });

        Assert.Equal(expected, d.SubtitleIndex);
        Assert.Contains(d.Trace, x => x.Code == "subs.matched" && x.Args!["coverage"] == "unknown" && x.Args["position"] == "1");
        Assert.Contains("metadata does not identify dialogue coverage", d.Explain(), StringComparison.Ordinal);
        Assert.DoesNotContain(d.Trace, x => x.Code == "subs.fallback");
        Assert.DoesNotContain(d.Trace, x => x.Code == "subs.skipped.language" && x.Args!["language"] == (spanishFirst ? "es" : "en"));
    }

    [Theory]
    [InlineData("Signs and songs")]
    [InlineData("Foreign parts only")]
    public void OrderedLanguages_ExplicitPartial_DoesNotBlockLaterUncertainForced(string partialTitle)
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "en", partialTitle, isDefault: true, isForced: true), Sub(3, "es", isForced: true) }, OrderedSubtitles);

        Assert.Equal(3, d.SubtitleIndex);
        Assert.Contains(d.Trace, x => x.Code == "subs.skipped.partial" && x.TrackIndex == 2);
        Assert.Contains(d.Trace, x => x.Code == "subs.matched" && x.Args!["coverage"] == "unknown" && x.Args["position"] == "2");
    }

    [Theory]
    [InlineData(FormatPreference.PreferText, 3)]
    [InlineData(FormatPreference.Neutral, 3)]
    [InlineData(FormatPreference.TextOnly, 2)]
    public void OrderedLanguages_IdentifiedImageTrack_RespectsFormatEligibility(FormatPreference format, int expected)
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "en", isForced: true, format: SubtitleFormat.Text), Sub(3, "es", "Dialogue", format: SubtitleFormat.Image) },
            OrderedSubtitles with { Format = format });

        Assert.Equal(expected, d.SubtitleIndex);
        Assert.Contains(d.Trace, x => x.Code == "subs.matched" && x.Args!["coverage"] == (expected == 2 ? "unknown" : "identified"));
        Assert.Equal(format == FormatPreference.TextOnly, d.Trace.Any(x => x.Code == "subs.skipped.image" && x.TrackIndex == 3));
    }

    [Fact]
    public void OrderedLanguages_UncertainForcedFallback_StillRejectsTextOnlyImages()
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "en", isForced: true, format: SubtitleFormat.Image), Sub(3, "es", isForced: true, format: SubtitleFormat.Text) },
            OrderedSubtitles with { Format = FormatPreference.TextOnly });

        Assert.Equal(3, d.SubtitleIndex);
        Assert.Contains(d.Trace, x => x.Code == "subs.skipped.image" && x.TrackIndex == 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fr")]
    public void OrderedLanguages_IdentifiedTrackOutsideRequestedLanguages_DoesNotDisplaceUncertainForced(string? language)
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "en", isForced: true), Sub(3, language, "Dialogue", isDefault: true) }, OrderedSubtitles);

        Assert.Equal(2, d.SubtitleIndex);
        Assert.Contains(d.Trace, x => x.Code == "subs.matched" && x.Args!["coverage"] == "unknown");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrderedLanguages_ExplicitOrRememberedOff_StillWins(bool remembered)
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "en", isForced: true), Sub(3, "es", "Dialogue") },
            remembered ? OrderedSubtitles : OrderedSubtitles with { WhenAudioNotUnderstood = SubtitleWant.Off },
            manual: remembered ? new ManualOverride(null, true, null) : null);

        Assert.Null(d.SubtitleIndex);
        Assert.Contains(d.Trace, x => x.Code == (remembered ? "subs.manual.off" : "subs.off"));
        Assert.DoesNotContain(d.Trace, x => x.Code == "subs.matched");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("English - Forced")]
    [InlineData("English - Forcé")]
    [InlineData("English - 強制")]
    public void RequestedLanguage_UsesGenericForcedMetadata_WhenNoIdentifiedFullTrackExists(string? title)
    {
        var d = Decide(new[] { Audio(1, "eng", isDefault: true), Audio(2, "jpn"), Sub(3, "eng", title, isDefault: true, isForced: true) }, EnglishSubtitles);
        Assert.Equal(2, d.AudioIndex);
        Assert.Equal(3, d.SubtitleIndex);
        Assert.Contains(d.Trace, x => x.Code == "subs.matched" && x.Args!["coverage"] == "unknown");
        Assert.Contains("metadata does not identify dialogue coverage", d.Explain(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void ForcedSubtitleIndex_IsNotTreatedAsOffOrAsAnArrayPosition(int subtitleIndex)
    {
        var d = Decide(new[] { Sub(subtitleIndex, "eng", "English - Default - Forced - ASS", isDefault: true, isForced: true), Audio(12, "eng", isDefault: true), Audio(29, "jpn") }, EnglishSubtitles);
        Assert.Equal(29, d.AudioIndex);
        Assert.Equal(subtitleIndex, d.SubtitleIndex);
    }

    [Fact]
    public void IdentifiedFullTrack_BeatsAmbiguousForcedDefault_EvenWithGenericLanguageMatch()
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "en-GB", "Forced", isDefault: true, isForced: true), Sub(3, "en", "Dialogue") },
            EnglishSubtitles with { SubtitleLanguages = new[] { "en-GB" } });
        Assert.Equal(3, d.SubtitleIndex);
    }

    [Theory]
    [InlineData("Signs and songs")]
    [InlineData("Foreign parts only")]
    [InlineData("Forced signs")]
    public void ExplicitPartialContent_IsNotPromotedToFull(string title)
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "en", title, isForced: true) }, EnglishSubtitles);
        Assert.Null(d.SubtitleIndex);
        Assert.Contains(d.Trace, x => x.Code == "subs.skipped.partial");
    }

    [Fact]
    public void CustomPartialKeyword_IsRespected()
    {
        var keywords = KeywordTables.Default with { PartialWords = KeywordTables.Default.PartialWords.Concat(new[] { "abridged" }).ToArray() };
        var selector = new TrackSelector(LanguageTable.Default, new TrackClassifier(LanguageTable.Default, keywords));
        var d = selector.Decide(new[] { Audio(1, "ja"), Sub(2, "en", "Abridged", isForced: true) }, ItemFacts.None, EnglishSubtitles);
        Assert.Null(d.SubtitleIndex);
    }

    [Fact]
    public void ForcedMetadata_DoesNotGuessAnUnknownOrDifferentLanguage()
    {
        foreach (var language in new string?[] { null, "fr" })
            Assert.Null(Decide(new[] { Audio(1, "ja"), Sub(2, language, "Forced", isForced: true) }, EnglishSubtitles).SubtitleIndex);
    }

    [Fact]
    public void ExplicitOff_StillWins()
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "en", isForced: true) }, EnglishSubtitles with { WhenAudioNotUnderstood = SubtitleWant.Off });
        Assert.Null(d.SubtitleIndex);
    }

    [Fact]
    public void RememberedOff_StillWins()
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "en", isForced: true) }, EnglishSubtitles, manual: new ManualOverride(null, true, null));
        Assert.Null(d.SubtitleIndex);
    }

    [Fact]
    public void TextOnly_DoesNotAcceptAnImageTrackBasedOnItsForcedFlag()
    {
        var d = Decide(new[] { Audio(1, "ja"), Sub(2, "en", isForced: true, format: SubtitleFormat.Image) }, EnglishSubtitles with { Format = FormatPreference.TextOnly });
        Assert.Null(d.SubtitleIndex);
    }

    [Fact]
    public void SignsOnly_CanUseAnUnlabelledForcedTrack_AsTheLastResort()
    {
        var d = Decide(new[] { Audio(1, "en"), Sub(2, null, isForced: true) }, Starter("en"));
        Assert.Equal(2, d.SubtitleIndex);
        Assert.Contains(d.Trace, x => x.Code == "subs.fallback");
        Assert.DoesNotContain(d.Trace, x => x.Code == "subs.unlabelled.ignored" && x.TrackIndex == 2);
    }

    [Fact]
    public void SignsOnly_PrefersTheRequestedLanguage_OverAnUnlabelledForcedDefault()
    {
        var d = Decide(new[] { Audio(1, "en"), Sub(2, null, isDefault: true, isForced: true), Sub(3, "en", "Signs") }, Starter("en"));
        Assert.Equal(3, d.SubtitleIndex);
    }

    [Fact]
    public void SignsOnly_DoesNotUseUnlabelledNonForcedOrExplicitFullTracks()
    {
        foreach (var track in new[] { Sub(2, null), Sub(2, null, "Full", isForced: true) })
            Assert.Null(Decide(new[] { Audio(1, "en"), track }, Starter("en")).SubtitleIndex);
    }
}
