using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Regression;

public class RecognitionClassification
{
    [Theory]
    [InlineData("Full Signs & Songs", true, SubtitleKind.Partial)]
    [InlineData("Full Signs & Songs", false, SubtitleKind.Full)]
    [InlineData("Dialogue + Forced", true, SubtitleKind.Partial)]
    [InlineData("Dialogue + Forced", false, SubtitleKind.Full)]
    [InlineData("Full", true, SubtitleKind.Full)]
    public void ForcedFlagDecidesWhenFullAndPartialWordsConflict(string title, bool forced, SubtitleKind expected)
    {
        Assert.Equal(expected, Classify(Sub(3, "en", title, isForced: forced)).SubtitleKind);
    }

    [Fact]
    public void ConflictingWordsAcrossTitleCommentAndSuffixRespectForcedAndSdhFlags()
    {
        var track = Sub(3, "en", "Full", isForced: true, hi: true, fileName: ".en.forced.srt");
        Assert.Equal(SubtitleKind.Partial, Classify(track).SubtitleKind);
        Assert.Equal(SubtitleKind.Sdh, Classify(track with { IsForced = false }).SubtitleKind);
        Assert.Equal(SubtitleKind.Partial, Classify(track with { FileName = null, Comment = "Signs & Songs" }).SubtitleKind);
    }

    [Theory]
    [InlineData("en", "Hi-Res", SubtitleKind.Full)]
    [InlineData("en", "English Hi-Res", SubtitleKind.Full)]
    [InlineData("en", "Hi Res Audio", SubtitleKind.Full)]
    [InlineData("en", "Hi_Res", SubtitleKind.Full)]
    [InlineData("en", "Hi–Res", SubtitleKind.Full)]
    [InlineData("en", "Hi-Resolution", SubtitleKind.Full)]
    [InlineData("en", "Hi-Res [SDH]", SubtitleKind.Sdh)]
    [InlineData("en", "Hi-Res [HI]", SubtitleKind.Sdh)]
    [InlineData("en", "English (HI)", SubtitleKind.Sdh)]
    [InlineData("hin", "Hindi (HI)", SubtitleKind.Sdh)]
    [InlineData("hin", "Hindi [HI]", SubtitleKind.Sdh)]
    [InlineData("hin", "Hindi HI", SubtitleKind.Sdh)]
    [InlineData("hin", "HI", SubtitleKind.Full)]
    [InlineData("hin", "Hindi Hi-Res", SubtitleKind.Full)]
    [InlineData("hin", "Hindi SDH", SubtitleKind.Sdh)]
    public void HiDependsOnContextRatherThanBeingGloballyIgnored(string language, string title, SubtitleKind expected)
    {
        Assert.Equal(expected, Classify(Sub(3, language, title)).SubtitleKind);
    }

    [Theory]
    [InlineData("hi", ".hi.srt", SubtitleKind.Full)]
    [InlineData("hi", ".hin.hi.srt", SubtitleKind.Sdh)]
    [InlineData("hi", ".hi.hi.srt", SubtitleKind.Sdh)]
    [InlineData("hi", ".hi.sdh.srt", SubtitleKind.Sdh)]
    [InlineData("en", ".en.hi.srt", SubtitleKind.Sdh)]
    [InlineData("en", ".en.hi-res.srt", SubtitleKind.Full)]
    [InlineData(null, ".hi.srt", SubtitleKind.Sdh)]
    public void HindiLanguageSuffixAndSeparateHearingImpairedSuffixAreDistinct(string? language, string suffix, SubtitleKind expected)
    {
        Assert.Equal(expected, Classify(Sub(3, language, fileName: suffix)).SubtitleKind);
    }

    [Fact]
    public void CommentAndHearingImpairedFlagStillCarryIndependentEvidence()
    {
        var track = Sub(3, "hi", "Hi-Res", hi: true);
        Assert.Equal(SubtitleKind.Sdh, Classify(track).SubtitleKind);
        Assert.Equal(SubtitleKind.Full, Classify(track with { IsHearingImpaired = false, Comment = "Hi-Res" }).SubtitleKind);
        Assert.Equal(SubtitleKind.Sdh, Classify(track with { IsHearingImpaired = false, Comment = "HI" }).SubtitleKind);
    }
}
