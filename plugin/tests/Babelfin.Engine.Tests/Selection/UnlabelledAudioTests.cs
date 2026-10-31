using System;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>untagged audio ("und", empty).</summary>
public class UnlabelledAudioTests
{
    [Fact]
    public void SingleUndTrack_OriginalJa_AssumedJapanese_NotUnderstood()
    {
        var tracks = new[] { Audio(1, "und"), Sub(2, "eng") };

        var d = Decide(tracks, Starter("en"), Item(original: "ja"));

        Assert.Equal(1, d.AudioIndex);
        Assert.False(d.AudioUnderstood);
        Assert.Equal(2, d.SubtitleIndex);
        var assumed = Assert.Single(d.Trace, l => l.Code == "audio.unlabelled.assumed");
        Assert.Contains("assumed", assumed.Text, StringComparison.Ordinal);
        Assert.Equal("ja", assumed.Args!["language"]);
    }

    [Fact]
    public void SingleUndTrack_OriginalJa_JapaneseSpeaker_Understood()
    {
        var tracks = new[] { Audio(1, "und"), Sub(2, "eng") };
        var profile = Fx.Starter(new[] { "ja", "en" }, new[] { "en" });

        var d = Decide(tracks, profile, Item(original: "ja"));

        Assert.True(d.AudioUnderstood);
        Assert.Null(d.SubtitleIndex);
    }

    [Fact]
    public void AssumedLanguage_ShowsInExplain()
    {
        var d = Decide(new[] { Audio(1, null), Sub(2, "eng") }, Starter("en"), Item(original: "ja"));

        Assert.Contains("(assumed)", d.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void UndAmongSeveralTracks_OriginalWhenOnlyTrack_StaysUnknown()
    {
        var tracks = new[] { Audio(1, "und"), Audio(2, "eng"), Sub(3, "eng") };

        var d = Decide(tracks, Starter("en").Chain(Step(AudioStepKind.OriginalLanguage), Step(AudioStepKind.FirstTrack)), Item(original: "ja"));

        Assert.True(d.Has("audio.unlabelled.unknown", 1));
        Assert.Equal(1, d.AudioIndex);
        Assert.False(d.AudioUnderstood);
        Assert.Equal(3, d.SubtitleIndex);
    }

    [Fact]
    public void UnlabelledRuleOriginal_AppliesAmongSeveralTracks()
    {
        var tracks = new[] { Audio(1, "und"), Audio(2, "eng") };

        var d = Decide(tracks, OriginalAudio("en") with { UnlabelledAudio = UnlabelledAudioRule.OriginalLanguage }, Item(original: "ja"));

        Assert.Equal(1, d.AudioIndex);
        Assert.True(d.Has("audio.unlabelled.assumed", 1));
    }

    [Fact]
    public void SpecificLanguageRule_AssumesThatLanguage()
    {
        var tracks = new[] { Audio(1, null), Sub(2, "eng") };
        var profile = Starter("de", "en") with { UnlabelledAudio = UnlabelledAudioRule.SpecificLanguage, UnlabelledAudioLanguage = "de" };

        var d = Decide(tracks, profile);

        Assert.Equal(1, d.AudioIndex);
        Assert.True(d.AudioUnderstood);
        Assert.Equal("de", d.Trace.Single(l => l.Code == "audio.unlabelled.assumed").Args!["language"]);
    }

    [Fact]
    public void LibraryUsualRule_AssumesLibraryLanguage()
    {
        var tracks = new[] { Audio(1, ""), Sub(2, "eng") };

        var d = Decide(tracks, Starter("en") with { UnlabelledAudio = UnlabelledAudioRule.LibraryUsualOriginalLanguage }, Item(library: "ja"));

        Assert.Equal("ja", d.Trace.Single(l => l.Code == "audio.unlabelled.assumed").Args!["language"]);
        Assert.Equal(2, d.SubtitleIndex);
    }

    [Fact]
    public void UnknownRule_TreatedAsNotUnderstood_FullSubs()
    {
        var tracks = new[] { Audio(1, "und"), Sub(2, "eng", isForced: true), Sub(3, "eng") };

        var d = Decide(tracks, Starter("en") with { UnlabelledAudio = UnlabelledAudioRule.Unknown }, Item(original: "en"));

        Assert.False(d.AudioUnderstood);
        Assert.True(d.Has("audio.unlabelled.unknown", 1));
        Assert.Equal(3, d.SubtitleIndex);
    }

    // A single "und" audio track with English original-language metadata is treated as English.
    [Fact]
    public void SingleUnlabelledAudio_OriginalEnglish_AssumedEnglishUnderstood()
    {
        var d = Decide(SingleUnlabelledAudio(), Starter("en"), Item(original: "en"));

        Assert.Equal(1, d.AudioIndex);
        Assert.True(d.AudioUnderstood);
        Assert.Null(d.SubtitleIndex);
    }

    // an "und" track whose title names the language resolves from the title (not unlabelled).
    [Fact]
    public void UndTrackTitledJapanese_ResolvesFromTitle()
    {
        var tracks = new[] { Audio(1, "und", "Japanese"), Audio(2, "eng") };

        var d = Decide(tracks, OriginalAudio("en"), Item(original: "ja"));

        Assert.Equal(1, d.AudioIndex);
        Assert.False(d.Has("audio.unlabelled.assumed"));
    }

    // OriginalLanguage rule with an unknown original → unknown.
    [Fact]
    public void OriginalRule_OriginalUnknown_StaysUnknown()
    {
        var d = Decide(new[] { Audio(1, "und") }, Starter("en") with { UnlabelledAudio = UnlabelledAudioRule.OriginalLanguage });

        Assert.True(d.Has("audio.unlabelled.unknown", 1));
        Assert.True(d.Has("audio.unknown"));
    }
}
