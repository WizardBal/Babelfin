using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>remembered manual changes as descriptors.</summary>
public class ManualOverrideTests
{
    private static TrackDescriptor Describe(System.Collections.Generic.IReadOnlyList<Jellyfin.Plugin.Babelfin.Engine.Classification.Track> tracks, int index) =>
        TrackDescriptor.From(Classify(tracks.Single(t => t.Index == index)));

    [Fact]
    public void AudioDescriptor_MatchesAfterIndexShift()
    {
        var remembered = Describe(Slv(), 2);

        var d = Decide(Shift(Slv(), 1), Starter("en"), manual: new ManualOverride(remembered, false, null));

        Assert.Equal(3, d.AudioIndex);
        Assert.True(d.Has("audio.manual"));
    }

    [Fact]
    public void SubtitleDescriptor_MatchesAfterIndexShift()
    {
        var remembered = Describe(ClockworkOrchard(), 4);

        var d = Decide(Shift(ClockworkOrchard(), 1), Starter("en"), manual: new ManualOverride(null, false, remembered));

        Assert.Equal(5, d.SubtitleIndex);
        Assert.True(d.Has("subs.manual"));
    }

    [Fact]
    public void AudioDescriptor_MatchesAfterReRipWithDifferentCodec()
    {
        var before = new[] { Audio(1, "eng", "Stereo", codec: "aac", channels: 2), Audio(2, "jpn", "Original", codec: "aac", channels: 2) };
        var after = new[] { Audio(1, "eng", "Stereo", codec: "flac", channels: 2), Audio(2, "jpn", "Original", codec: "flac", channels: 2) };
        var remembered = Describe(before, 2);

        var d = Decide(after, Starter("en"), manual: new ManualOverride(remembered, false, null));

        Assert.True(remembered.Score(Classify(after[1])) >= TrackDescriptor.MatchThreshold);
        Assert.Equal(2, d.AudioIndex);
    }

    [Fact]
    public void Descriptor_MatchingNothing_FallsBackToChainWithTrace()
    {
        var remembered = new TrackDescriptor("ko", null, "normal", "Korean Dub", "aac", 2);
        var tracks = new[] { Audio(1, "eng"), Audio(2, "jpn") };

        var d = Decide(tracks, Starter("en"), manual: new ManualOverride(remembered, false, null));

        Assert.Equal(1, d.AudioIndex);
        Assert.True(d.Has("audio.manual.nomatch"));
    }

    [Fact]
    public void SubtitleDescriptor_MatchingNothing_FallsBackWithTrace()
    {
        var remembered = new TrackDescriptor("ko", null, "full", "Korean", null, null);

        var d = Decide(ClockworkOrchard(), OriginalAudio("en"), Item(original: "ja"), manual: new ManualOverride(null, false, remembered));

        Assert.Equal(4, d.SubtitleIndex);
        Assert.True(d.Has("subs.manual.nomatch"));
    }

    [Fact]
    public void SubtitlesOff_IsRemembered()
    {
        var d = Decide(ClockworkOrchard(), OriginalAudio("en"), Item(original: "ja"), manual: new ManualOverride(null, true, null));

        Assert.Null(d.SubtitleIndex);
        Assert.True(d.SubtitlesWanted);
        Assert.True(d.Has("subs.manual.off"));
        Assert.Contains("you turned them off", d.Explain(), System.StringComparison.Ordinal);
    }

    // a remembered partial track is honoured even though the profile wants full subtitles.
    [Fact]
    public void RememberedPartialTrack_BeatsProfileWant()
    {
        var remembered = Describe(ClockworkOrchard(), 3);

        var d = Decide(ClockworkOrchard(), OriginalAudio("en"), Item(original: "ja"), manual: new ManualOverride(null, false, remembered));

        Assert.Equal(3, d.SubtitleIndex);
    }

    // a remembered subtitle track is honoured even when the profile has subtitles off.
    [Fact]
    public void RememberedSubtitle_HonouredWhenProfileOff()
    {
        var remembered = Describe(Slv(), 4);

        var d = Decide(Slv(), Starter("en") with { WhenAudioUnderstood = SubtitleWant.Off }, manual: new ManualOverride(null, false, remembered));

        Assert.Equal(4, d.SubtitleIndex);
    }

    // equal scores → lowest index.
    [Fact]
    public void Descriptor_TiedScores_LowestIndexWins()
    {
        var remembered = Describe(Slv(), 5);

        var d = Decide(Slv(), Starter("en"), manual: new ManualOverride(null, false, remembered));

        Assert.Equal(4, d.SubtitleIndex);
    }

    // the remembered audio wins over the chain.
    [Fact]
    public void RememberedAudio_BeatsChain()
    {
        var remembered = Describe(Slv(), 2);

        var d = Decide(Slv(), Starter("en"), manual: new ManualOverride(remembered, false, null));

        Assert.Equal(2, d.AudioIndex);
        Assert.False(d.AudioUnderstood);
        Assert.Equal(4, d.SubtitleIndex);
    }

    // Descriptor match-score weights.
    [Fact]
    public void Score_WeightsFavorLanguagePurposeAndTrackAttributes()
    {
        var track = Classify(Audio(1, "jpn", "Surround", codec: "aac", channels: 6));
        var descriptor = TrackDescriptor.From(track);

        Assert.Equal(10, descriptor.Score(track));
        Assert.Equal(9, descriptor.Score(Classify(Audio(4, "jpn", "Surround", codec: "flac", channels: 6))));
        Assert.Equal(8, descriptor.Score(Classify(Audio(4, "jpn", "Surround", codec: "flac", channels: 2))));
        Assert.Equal(6, descriptor.Score(Classify(Audio(4, "jpn", "Stereo", codec: "ac3", channels: 2))));
    }

    // the same language but a different kind and title doesn't count as the same track.
    [Fact]
    public void Score_LanguageOnly_IsBelowThreshold()
    {
        var descriptor = TrackDescriptor.From(Classify(Sub(3, "eng", "Signs & Songs")));

        Assert.True(descriptor.Score(Classify(Sub(4, "eng", "Dialogue"))) < TrackDescriptor.MatchThreshold);
    }

    // A remembered track must be in the same language.
    [Fact]
    public void RememberedJapaneseSurround_NeverMatchesEnglishSurround_WhenNoJapaneseExists()
    {
        var before = new[] { Audio(1, "eng", "Surround", isDefault: true, codec: "aac", channels: 6), Audio(2, "jpn", "Surround", codec: "aac", channels: 6) };
        var dubOnly = new[] { Audio(1, "eng", "Surround", isDefault: true, codec: "aac", channels: 6), Audio(2, "eng", "Commentary", codec: "aac", channels: 2) };
        var remembered = Describe(before, 2);

        var d = Decide(dubOnly, OriginalAudio("en") with { AudioChain = new[] { Step(AudioStepKind.FirstTrack) } }, manual: new ManualOverride(remembered, false, null));

        Assert.Equal(0, remembered.Score(Classify(dubOnly[0])));
        Assert.Equal(1, d.AudioIndex);
        Assert.True(d.Has("audio.manual.nomatch"));
        Assert.False(d.Has("audio.manual"));
        Assert.True(d.Has("audio.step.matched"));
    }

    // The same-language rule still matches after an index shift.
    [Fact]
    public void RememberedJapaneseSurround_StillMatchesAfterIndexShift()
    {
        var before = new[] { Audio(1, "eng", "Surround", isDefault: true, codec: "aac", channels: 6), Audio(2, "jpn", "Surround", codec: "aac", channels: 6) };
        var remembered = Describe(before, 2);

        var d = Decide(Shift(before, 2), Starter("en"), manual: new ManualOverride(remembered, false, null));

        Assert.Equal(4, d.AudioIndex);
        Assert.True(d.Has("audio.manual", 4));
    }

    // A different or unknown language scores 0 whatever the other fields.
    [Fact]
    public void Score_DifferentOrUnknownLanguage_IsZero()
    {
        var descriptor = TrackDescriptor.From(Classify(Sub(3, "eng", "Dialogue", codec: "ass")));

        Assert.Equal(0, descriptor.Score(Classify(Sub(7, "spa", "Dialogue", codec: "ass"))));
        Assert.Equal(0, descriptor.Score(Classify(Sub(7, null, "Dialogue", codec: "ass"))));
        Assert.True(descriptor.Score(Classify(Sub(7, "eng", "Dialogue", codec: "ass"))) >= TrackDescriptor.MatchThreshold);
    }

    // a remembered unlabelled track is still scored on its other fields.
    [Fact]
    public void Score_UnlabelledDescriptor_ScoredOnOtherFields()
    {
        var descriptor = TrackDescriptor.From(Classify(Audio(1, null, "Main", codec: "aac", channels: 2)));

        Assert.Null(descriptor.Language);
        Assert.Equal(10, descriptor.Score(Classify(Audio(5, null, "Main", codec: "aac", channels: 2))));
    }

    // From() captures kind words.
    [Fact]
    public void From_CapturesKindWords()
    {
        Assert.Equal("partial", TrackDescriptor.From(Classify(Sub(3, "eng", isForced: true))).Kind);
        Assert.Equal("sdh", TrackDescriptor.From(Classify(Sub(3, "eng", hi: true))).Kind);
        Assert.Equal("full", TrackDescriptor.From(Classify(Sub(3, "eng"))).Kind);
        Assert.Equal("commentary", TrackDescriptor.From(Classify(Audio(1, "eng", "Commentary"))).Kind);
        Assert.Equal("normal", TrackDescriptor.From(Classify(Audio(1, "eng"))).Kind);
    }
}
