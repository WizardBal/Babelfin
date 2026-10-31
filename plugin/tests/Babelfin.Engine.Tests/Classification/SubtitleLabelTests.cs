using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Classification;

public sealed class SubtitleLabelTests
{
    [Theory]
    [InlineData("English non-forced", SubtitleKind.Full)]
    [InlineData("English not forced", SubtitleKind.Full)]
    [InlineData("English non forcé", SubtitleKind.Full)]
    [InlineData("English NONFORCED", SubtitleKind.Full)]
    [InlineData("English (Non–Forced)", SubtitleKind.Full)]
    [InlineData("English [NOT FORCÉ]", SubtitleKind.Full)]
    [InlineData("English S+S", SubtitleKind.Partial)]
    [InlineData("English SnS", SubtitleKind.Partial)]
    [InlineData("English (s + s)", SubtitleKind.Partial)]
    [InlineData("English [sNs]", SubtitleKind.Partial)]
    [InlineData("English Full + Signs & Songs", SubtitleKind.Full)]
    [InlineData("English Full + SnS", SubtitleKind.Full)]
    [InlineData("English Signs & Songs", SubtitleKind.Partial)]
    [InlineData("English Forced", SubtitleKind.Partial)]
    [InlineData("English強制", SubtitleKind.Partial)]
    [InlineData("English non-forced; forced", SubtitleKind.Partial)]
    [InlineData("English SDH", SubtitleKind.Sdh)]
    [InlineData("English non-forced SDH", SubtitleKind.Sdh)]
    [InlineData("English reinforced", SubtitleKind.Full)]
    [InlineData("English SnSonic", SubtitleKind.Full)]
    [InlineData("English xs+s", SubtitleKind.Full)]
    [InlineData("English nonforcedly", SubtitleKind.Full)]
    public void LabelsHaveBoundaryAwareCoverage(string title, SubtitleKind kind)
        => Assert.Equal(kind, Classifier.Classify(Sub(3, "eng", title)).SubtitleKind);

    [Theory]
    [InlineData("English non-forced")]
    [InlineData("English not forced")]
    [InlineData("English non forcé")]
    public void ExplicitContainerForcedFlagSurvivesANegatedTitle(string title)
        => Assert.Equal(SubtitleKind.Partial, Classifier.Classify(Sub(3, "eng", title, isForced: true)).SubtitleKind);

    [Fact]
    public void ExplicitDialogueStillOverridesAForcedFlagWithoutPositivePartialWords()
        => Assert.Equal(SubtitleKind.Full, Classifier.Classify(Sub(3, "eng", "Full dialogue non-forced", isForced: true)).SubtitleKind);

    [Theory]
    [InlineData("S+S")]
    [InlineData("SnS")]
    [InlineData("s + s")]
    public void SavedSignsAndSongsKeywordAlsoRecognizesItsAliases(string title)
    {
        var keywords = KeywordTables.Default with { PartialWords = new[] { "s&s", "forced" } };
        var classifier = new TrackClassifier(LanguageTable.Default, keywords);
        Assert.Equal(SubtitleKind.Partial, classifier.Classify(Sub(3, "eng", title)).SubtitleKind);
        var disabled = keywords with { PartialWords = new[] { "forced" } };
        Assert.Equal(SubtitleKind.Full, new TrackClassifier(LanguageTable.Default, disabled).Classify(Sub(3, "eng", title)).SubtitleKind);
    }

    [Fact]
    public void AFullTranscriptIsChosenAheadOfSignsAliases()
    {
        var decision = Decide(new[] { Audio(1, "jpn"), Sub(3, "eng", "S+S"), Sub(4, "eng", "non-forced") }, Starter("en"));
        Assert.Equal(4, decision.SubtitleIndex);
    }

    [Fact]
    public void SignsOnlySelectionDoesNotUseANegatedForcedTranscript()
    {
        var decision = Decide(new[] { Audio(1, "eng"), Sub(3, "eng", "non-forced"), Sub(4, "eng", "SnS") }, Starter("en"));
        Assert.Equal(4, decision.SubtitleIndex);
    }
}
