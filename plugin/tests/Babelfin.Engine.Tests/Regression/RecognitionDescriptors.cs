using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Regression;

public class RecognitionDescriptors
{
    [Fact]
    public void RememberedCantoneseRejectsMandarinEvenWithMatchingCodecAndChannels()
    {
        var remembered = TrackDescriptor.From(Classify(Audio(1, "yue", codec: "aac", channels: 2)));
        var after = new[] { Audio(1, "yue", codec: "ac3", channels: 6), Audio(2, "cmn", codec: "aac", channels: 2) };
        Assert.Equal(0, remembered.Score(Classify(after[1])));
        Assert.True(remembered.Score(Classify(after[0])) >= TrackDescriptor.MatchThreshold);
        var decision = Selector.Decide(after, ItemFacts.None, Starter("en"), manual: new ManualOverride(remembered, false, null));
        Assert.Equal(1, decision.AudioIndex);
        Assert.Contains(decision.Trace, line => line.Code == "audio.manual");
    }

    [Fact]
    public void RememberedFullEnglishRejectsForcedEnglishDespiteIdenticalTitleAndCodec()
    {
        var remembered = TrackDescriptor.From(Classify(Sub(4, "en", "English", codec: "subrip")));
        var after = new[] { Audio(1, "ja"), Sub(3, "en", "English", isForced: true, codec: "subrip"), Sub(4, "en", "English (Full)", codec: "subrip") };
        Assert.Equal(0, remembered.Score(Classify(after[1])));
        var decision = Selector.Decide(after, ItemFacts.None, Starter("en"), manual: new ManualOverride(null, false, remembered));
        Assert.Equal(4, decision.SubtitleIndex);
        Assert.Contains(decision.Trace, line => line.Code == "subs.manual");
    }

    [Theory]
    [InlineData("zh-Hant", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hant")]
    [InlineData("pt-BR", "pt-PT")]
    [InlineData("yue", "cmn")]
    public void IncompatibleVarietyScriptOrRegionScoresZero(string wanted, string actual)
    {
        var remembered = TrackDescriptor.From(Classify(Sub(3, wanted, "Main", codec: "subrip")));
        Assert.Equal(0, remembered.Score(Classify(Sub(4, actual, "Main", codec: "subrip"))));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PartialNeverMatchesFullOrSdhInEitherDirection(bool rememberPartial, bool sdh)
    {
        var partial = Classify(Sub(3, "en", "English", isForced: true, codec: "subrip"));
        var full = Classify(Sub(4, "en", "English", hi: sdh, codec: "subrip"));
        Assert.Equal(0, TrackDescriptor.From(rememberPartial ? partial : full).Score(rememberPartial ? full : partial));
    }

    [Fact]
    public void FullAndSdhRemainCompatibleButExactKindRanksHigher()
    {
        var full = Classify(Sub(3, "en", "English", codec: "subrip"));
        var sdh = Classify(Sub(4, "en", "English", hi: true, codec: "subrip"));
        var remembered = TrackDescriptor.From(full);
        Assert.True(remembered.Score(sdh) >= TrackDescriptor.MatchThreshold);
        Assert.True(remembered.Score(full) > remembered.Score(sdh));
    }

    [Fact]
    public void SpecificLanguageStillAllowsGenericFallback()
    {
        var remembered = TrackDescriptor.From(Classify(Audio(1, "yue", codec: "aac", channels: 2)));
        Assert.True(remembered.Score(Classify(Audio(2, "zh", codec: "aac", channels: 2))) >= TrackDescriptor.MatchThreshold);
    }

    [Fact]
    public void CustomAliasesThatShadowIsoCodesKeepCanonicalDescriptorMatches()
    {
        var languages = new LanguageTable(new[]
        {
            new LanguageEntry("ja", "jpn", null, "Japanese"),
            new LanguageEntry("en", "eng", null, "English"),
        }, new AliasTable(new Dictionary<string, string> { ["ja"] = "en" }), new VarietyKeywordTable(Array.Empty<VarietyKeyword>()));
        var classifier = new TrackClassifier(languages, KeywordTables.Default);
        var candidate = classifier.Classify(Audio(2, "jpn", "Main", codec: "aac", channels: 2));
        var remembered = TrackDescriptor.From(candidate);
        Assert.Equal("jpn", remembered.Language);
        Assert.Equal(0, remembered.Score(candidate));
        Assert.Equal(10, remembered.Score(candidate, languages));
    }

    [Fact]
    public void ScoreUsesTheOptionalCustomLanguageTable()
    {
        var languages = new LanguageTable(new[] { new LanguageEntry("ja", "jpn", null, "Japanese") },
            new AliasTable(new Dictionary<string, string> { ["studio"] = "ja" }), new VarietyKeywordTable(Array.Empty<VarietyKeyword>()));
        var classifier = new TrackClassifier(languages, KeywordTables.Default);
        var candidate = classifier.Classify(Audio(2, "studio", "Main", codec: "aac", channels: 2));
        var remembered = new TrackDescriptor("studio", null, "normal", "Main", "aac", 2);
        Assert.Equal(0, remembered.Score(candidate));
        Assert.Equal(10, remembered.Score(candidate, languages));
    }
}
