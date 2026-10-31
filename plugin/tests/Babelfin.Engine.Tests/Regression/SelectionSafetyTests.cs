using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Regression;

public class SelectionSafetyTests
{
    private static ResolvedProfile JapaneseAndVideoSubs() => Starter("en").Chain(Lang("ja")) with
    {
        WhenAudioUnderstood = SubtitleWant.VideoDefault,
        WhenAudioNotUnderstood = SubtitleWant.VideoDefault,
    };

    // Expectations come from tools/native-policy-check against the LOCAL 12.2 DLL.
    // Default and Smart may legitimately retain forced Signs; Always chooses Full.
    [Theory]
    [InlineData("Default", 3)]
    [InlineData("Smart", 3)]
    [InlineData("Always", 4)]
    [InlineData("OnlyForced", 3)]
    [InlineData("None", null)]
    public void ChosenAudioRecomputesTheNativePolicy_NotThePlanExample(string mode, int? expected)
    {
        var tracks = new[] { Audio(1, "eng", isDefault: true), Audio(2, "jpn"), Sub(3, "eng", "Signs & Songs", isDefault: true, isForced: true), Sub(4, "eng", "Full") };
        var facts = ItemFacts.None with { JellyfinAudioIndex = 1, JellyfinSubtitleIndex = 3, JellyfinSubtitleMode = mode, JellyfinSubtitleLanguage = "eng" };
        var result = new EngineServices(null).Selector.Decide(tracks, facts, JapaneseAndVideoSubs());
        Assert.Equal(2, result.AudioIndex);
        Assert.Equal(expected, result.SubtitleIndex);
        Assert.False(result.AudioLeftToVideo);
        Assert.False(result.SubtitlesLeftToVideo);
        Assert.Equal("true", Assert.Single(result.Trace, l => l.Code == TraceCodes.SubsVideoDefault).Args!["recomputed"]);
    }

    [Theory]
    [InlineData("Default", -1)]
    [InlineData("Smart", -1)]
    [InlineData("Always", -1)]
    [InlineData("OnlyForced", -1)]
    [InlineData("None", -1)]
    [InlineData("Smart", null)]
    [InlineData("Always", 4)]
    [InlineData("None", 4)]
    public void SelectingTheSameAudioPreservesJellyfinsActualSubtitleChoice(string mode, int? subtitle)
    {
        var tracks = new[] { Audio(1, "eng", isDefault: true), Audio(2, "jpn"), Sub(3, "eng", isForced: true), Sub(4, "eng", "Full") };
        var facts = ItemFacts.None with { JellyfinAudioIndex = 2, JellyfinSubtitleIndex = subtitle, JellyfinSubtitleMode = mode, JellyfinSubtitleLanguage = "eng" };
        var result = new EngineServices(null).Selector.Decide(tracks, facts, JapaneseAndVideoSubs());
        Assert.Equal(2, result.AudioIndex);
        Assert.False(result.AudioLeftToVideo);
        Assert.True(result.SubtitlesLeftToVideo);
        Assert.Equal(subtitle is >= 0 ? subtitle : null, result.SubtitleIndex);
        Assert.DoesNotContain(result.Trace, l => l.Args?.ContainsKey("recomputed") == true);
    }

    [Theory]
    [InlineData("Default", null)]
    [InlineData("Smart", 4)]
    [InlineData("Always", 4)]
    [InlineData("OnlyForced", null)]
    [InlineData("None", null)]
    public void NoSignsTrack_NativeSmartFindsFullForJapaneseAudio(string mode, int? expected)
    {
        var tracks = new[] { Audio(1, "eng", isDefault: true), Audio(2, "jpn"), Sub(4, "eng") };
        var facts = ItemFacts.None with { JellyfinAudioIndex = 1, JellyfinSubtitleMode = mode, JellyfinSubtitleLanguage = "eng" };
        Assert.Equal(expected, new EngineServices(null).Selector.Decide(tracks, facts, JapaneseAndVideoSubs()).SubtitleIndex);
    }

    [Theory]
    [InlineData("OnlyForced", "jpn", null)]
    [InlineData("Smart", "eng", null)]
    [InlineData("Smart", "en", 4)]
    public void NativeUsesSuppliedLanguageCodesWithoutBabelfinAliasExpansion(string mode, string audioLanguage, int? nativeExpected)
    {
        var tracks = new[] { Sub(3, "en", isForced: true), Sub(4, "eng") };
        var classified = new EngineServices(null).Classifier.ClassifyAll(tracks);
        Assert.Equal(nativeExpected, VideoSubtitleSelector.Choose(classified, mode, "eng", audioLanguage));
    }

    [Fact]
    public void BothVideoDefaultsRemainNeutralEvenWithPolicyFacts()
    {
        var profile = JapaneseAndVideoSubs().Chain(Step(AudioStepKind.VideoDefault));
        var facts = ItemFacts.None with { JellyfinAudioIndex = 1, JellyfinSubtitleIndex = 3, JellyfinSubtitleMode = "Always", JellyfinSubtitleLanguage = "eng" };
        var result = new EngineServices(null).Selector.Decide(Slv(), facts, profile);
        Assert.True(result.AudioLeftToVideo);
        Assert.True(result.SubtitlesLeftToVideo);
        Assert.Equal(1, result.AudioIndex);
        Assert.Equal(3, result.SubtitleIndex);
    }

    [Theory]
    [InlineData("eng")]
    [InlineData("jpn")]
    [InlineData("und")]
    public void AlwaysShowSubtitlesOverridesOffForEveryAudioLanguage(string audioLanguage)
    {
        var profile = Starter("en").Chain(Step(AudioStepKind.FirstTrack)) with
        {
            PrefersSdh = true, WhenAudioUnderstood = SubtitleWant.Off, WhenAudioNotUnderstood = SubtitleWant.Off,
        };
        var result = new EngineServices(null).Selector.Decide(new[] { Audio(1, audioLanguage), Sub(2, "eng"), Sub(3, "eng", hi: true) }, ItemFacts.None, profile);
        Assert.Equal(3, result.SubtitleIndex);
        Assert.True(result.SubtitlesWanted);
        Assert.Contains("Always show subtitles", result.Trace.Single(l => l.Code == TraceCodes.SubsWant).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Commentary")]
    [InlineData("Audio Description")]
    public void VideoDefaultSkipsAvoidedAudioOnlyWithinTheSameLanguage(string title)
    {
        var tracks = new[] { Audio(1, "eng", title, isDefault: true), Audio(2, "eng", channels: 2), Audio(3, "eng", channels: 6), Audio(4, "jpn", channels: 8) };
        var result = new EngineServices(null).Selector.Decide(tracks, ItemFacts.None with { JellyfinAudioIndex = 1 }, Starter("en").Chain(Step(AudioStepKind.VideoDefault)));
        Assert.Equal(3, result.AudioIndex);
        Assert.False(result.AudioLeftToVideo);
        Assert.Contains("same language", result.Explain(), StringComparison.Ordinal);
        Assert.Equal("true", result.Trace.Single(l => l.Code == TraceCodes.AudioVideoDefault).Args!["filtered"]);
    }

    [Theory]
    [InlineData("eng", "jpn")]
    [InlineData("yue", "cmn")]
    [InlineData("pt-BR", "pt-PT")]
    [InlineData("zh-Hant", "zh-Hans")]
    [InlineData("und", "eng")]
    public void WithoutUsableSameLanguageAlternative_LeaveJellyfinsPick(string chosenLanguage, string otherLanguage)
    {
        var tracks = new[] { Audio(1, chosenLanguage, "Commentary", isDefault: true), Audio(2, otherLanguage) };
        var result = new EngineServices(null).Selector.Decide(tracks, ItemFacts.None with { JellyfinAudioIndex = 1 }, Starter("en").Chain(Step(AudioStepKind.VideoDefault)));
        Assert.Equal(1, result.AudioIndex);
        Assert.True(result.AudioLeftToVideo);
    }

    [Theory]
    [InlineData("Commentary", false)]
    [InlineData("Audio Description", true)]
    public void AllowedCommentaryOrRequestedDescriptionStaysSelected(string title, bool usesDescription)
    {
        var profile = Starter("en").Chain(Step(AudioStepKind.VideoDefault)) with { AvoidCommentary = false, UsesAudioDescription = usesDescription };
        var result = new EngineServices(null).Selector.Decide(new[] { Audio(1, "eng", title), Audio(2, "eng") }, ItemFacts.None with { JellyfinAudioIndex = 1 }, profile);
        Assert.Equal(1, result.AudioIndex);
        Assert.True(result.AudioLeftToVideo);
    }

    [Theory]
    [InlineData("Commentary")]
    [InlineData("Audio Description")]
    public void OnlyUsableUnlabelledAudioExcludesAvoidedTracks(string avoidedTitle)
    {
        var tracks = new[] { Audio(1, "und"), Audio(2, "eng", avoidedTitle), Sub(3, "eng") };
        var result = new EngineServices(null).Selector.Decide(tracks, Item("ja"), OriginalAudio("en"));
        Assert.Equal(1, result.AudioIndex);
        Assert.Equal(3, result.SubtitleIndex);
        Assert.Equal("ja", result.Trace.Single(l => l.Code == TraceCodes.AudioUnlabelledAssumed && l.TrackIndex == 1).Args!["language"]);
        Assert.DoesNotContain(result.Trace, l => l.Code == TraceCodes.AudioUnlabelledAssumed && l.TrackIndex == 2);
    }

    [Theory]
    [InlineData("Commentary", false)]
    [InlineData("Audio Description", true)]
    public void AllowedSecondaryAudioStillCounts(string secondTitle, bool usesDescription)
    {
        var profile = OriginalAudio("en") with { AvoidCommentary = false, UsesAudioDescription = usesDescription };
        var result = new EngineServices(null).Selector.Decide(new[] { Audio(1, "und"), Audio(2, "eng", secondTitle) }, Item("ja"), profile);
        Assert.Contains(result.Trace, l => l.Code == TraceCodes.AudioUnlabelledUnknown && l.TrackIndex == 1);
    }

    private static readonly Regex BannedPersonLanguage = new(@"\b(?:understand(?:s|ing)?|understood|read(?:s|ing)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void EveryActualTraceCodeAndExplainUsesMyLanguages_NotUnderstandOrRead()
    {
        var declared = typeof(TraceCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).Order().ToArray();
        Assert.Equal(declared, TraceCodes.All.Order());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var violations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in TraceCorpus())
        {
            if (BannedPersonLanguage.IsMatch(decision.Explain())) violations.Add(decision.Explain());
            foreach (var line in decision.Trace)
            {
                seen.Add(line.Code);
                Assert.False(string.IsNullOrWhiteSpace(line.Text));
                if (BannedPersonLanguage.IsMatch(line.Text)) violations.Add(line.Code + ": " + line.Text);
                // Exercise Explain's handling of each real trace code independently as well.
                var single = (decision with { Trace = new[] { line } }).Explain();
                if (BannedPersonLanguage.IsMatch(single)) violations.Add(line.Code + ": " + single);
            }
        }
        Assert.True(TraceCodes.All.All(seen.Contains) && violations.Count == 0, "Covered " + seen.Count + "/" + TraceCodes.All.Count + ". Unexercised: " + string.Join(", ", TraceCodes.All.Except(seen)) + ". Banned wording: " + string.Join(Environment.NewLine, violations));
    }

    internal static IEnumerable<Decision> TraceCorpus(bool includeTrace = true)
    {
        var services = new EngineServices(null);
        var fixtures = new[]
        {
            Array.Empty<Track>(), new[] { Audio(1, "und") }, new[] { Audio(1, "eng") }, new[] { Audio(1, "jpn") },
            new[] { Audio(1, "eng", "Commentary") },
            new[] { Audio(1, "jpn"), Sub(2, "eng", "Signs & Songs", isForced: true) },
            new[] { Audio(1, "eng"), Sub(2, "eng") },
            new[] { Audio(1, "jpn"), Sub(2, "ita") },
            new[] { Audio(1, "jpn"), Sub(2, null), Sub(3, "eng", format: SubtitleFormat.Image), Sub(4, "eng", hi: true), Sub(5, "eng"), Sub(6, "eng", "Signs & Songs", isForced: true) },
            Slv(),
        };
        var basic = Starter("en");
        var profiles = new List<ResolvedProfile>();
        foreach (var want in Enum.GetValues<SubtitleWant>())
        {
            profiles.Add(basic with { WhenAudioUnderstood = want, WhenAudioNotUnderstood = want, Sdh = Preference.Avoid, Format = FormatPreference.TextOnly });
            profiles.Add(basic with { WhenAudioUnderstood = want, WhenAudioNotUnderstood = want, AudioChain = new[] { Step(AudioStepKind.VideoDefault) } });
        }
        profiles.Add(basic with { Understood = new[] { "not-a-real-language" }, Reads = new[] { "en" }, AudioChain = Array.Empty<AudioStep>(), WhenAudioNotUnderstood = SubtitleWant.Full });
        profiles.Add(basic with { AudioChain = new[] { Lang("not-a-real-language"), Lang("de") }, SubtitleLanguages = new[] { "de" }, IfNothingMatches = WhenNothingMatches.PartialInMyLanguage });
        profiles.Add(basic with { Reads = new[] { "en", "it" }, SubtitleLanguages = new[] { "de" }, WhenAudioUnderstood = SubtitleWant.Full, IfNothingMatches = WhenNothingMatches.FullInAnyBackupLanguage });
        profiles.Add(basic with { Understood = Array.Empty<string>(), Reads = Array.Empty<string>() });
        var missing = new ManualOverride(TrackDescriptor.From(Classify(Audio(10, "de"))), false, TrackDescriptor.From(Classify(Sub(11, "de"))));
        var remembered = new ManualOverride(TrackDescriptor.From(Classify(Audio(1, "eng"))), false, TrackDescriptor.From(Classify(Sub(5, "eng"))));
        var manuals = new ManualOverride?[] { null, missing, remembered, new(null, true, null) };
        var facts = new[] { Item("ja"), Item("ja", burned: new[] { "en" }), ItemFacts.None with { JellyfinAudioIndex = 1, JellyfinSubtitleIndex = 3, JellyfinSubtitleMode = "Smart", JellyfinSubtitleLanguage = "eng" } };
        foreach (var tracks in fixtures)
        foreach (var profile in profiles)
        foreach (var item in facts)
        foreach (var manual in manuals)
            yield return services.Selector.Decide(tracks, item, profile, manual: manual, includeTrace: includeTrace);
    }
}