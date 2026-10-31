using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Playback;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
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

    [Theory]
    [InlineData("yue", "cmn")]
    [InlineData("pt-BR", "pt-PT")]
    [InlineData("zh-Hant", "zh-Hans")]
    public void PlaybackComparerRecognizesVarietyChanges(string first, string second)
    {
        var source = new MediaSourceInfo { MediaStreams = new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = first, Codec = "aac" },
            new() { Index = 2, Type = MediaStreamType.Audio, Language = second, Codec = "aac" },
            new() { Index = 3, Type = MediaStreamType.Subtitle, Language = first, Codec = "subrip" },
            new() { Index = 4, Type = MediaStreamType.Subtitle, Language = second, Codec = "subrip" },
        } };
        Assert.True(TrackComparer.Disagree(source, 1, null, 2, null, Classifier));
        Assert.True(TrackComparer.Disagree(source, null, 3, null, 4, Classifier));
        source.MediaStreams[1].Language = first;
        source.MediaStreams[3].Language = first;
        Assert.False(TrackComparer.Disagree(source, 1, 3, 2, 4, Classifier));
    }

    [Theory]
    [InlineData("en", "en-US")]
    [InlineData("zh", "yue")]
    [InlineData("zh-Hant", "zh-Hant-HK")]
    [InlineData("pt", "pt-BR")]
    [InlineData("no", "nb")]
    [InlineData("zh-Hant", "yue")]
    public void PlaybackComparerKeepsGenericAxesCompatibleInBothDirections(string generic, string specific)
    {
        var source = new MediaSourceInfo { MediaStreams = new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = generic },
            new() { Index = 2, Type = MediaStreamType.Audio, Language = specific },
            new() { Index = 3, Type = MediaStreamType.Subtitle, Language = generic },
            new() { Index = 4, Type = MediaStreamType.Subtitle, Language = specific },
        } };
        Assert.False(TrackComparer.Disagree(source, 1, 3, 2, 4, Classifier));
        Assert.False(TrackComparer.Disagree(source, 2, 4, 1, 3, Classifier));
    }

    [Theory]
    [InlineData("yue-Hans", "zh-Hant")]
    [InlineData("zh-Hant-HK", "zh-Hant-TW")]
    public void PlaybackComparerRejectsExplicitConflictsEvenWhenOtherAxesAreGeneric(string first, string second)
    {
        var source = new MediaSourceInfo { MediaStreams = new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = first },
            new() { Index = 2, Type = MediaStreamType.Audio, Language = second },
            new() { Index = 3, Type = MediaStreamType.Subtitle, Language = first },
            new() { Index = 4, Type = MediaStreamType.Subtitle, Language = second },
        } };
        Assert.True(TrackComparer.Disagree(source, 1, null, 2, null, Classifier));
        Assert.True(TrackComparer.Disagree(source, null, 4, null, 3, Classifier));
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
