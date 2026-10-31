using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

public sealed class RememberedAudioSafetyTests
{
    [Theory]
    [InlineData("Director commentary")]
    [InlineData("Audio description")]
    public void RememberedSoundtrack_DoesNotMatchADifferentAudioPurpose(string title)
    {
        var remembered = TrackDescriptor.From(Classify(Audio(1, "en", "Stereo", codec: "aac", channels: 2)));
        Assert.Equal(0, remembered.Score(Classify(Audio(2, "en", title, codec: "aac", channels: 2))));
    }

    [Theory]
    [InlineData("Director commentary")]
    [InlineData("Audio description")]
    public void ExplicitlyRememberedAudioPurpose_StillMatchesThatPurpose(string title)
    {
        var remembered = TrackDescriptor.From(Classify(Audio(1, "en", title, codec: "aac", channels: 2)));
        Assert.True(remembered.Score(Classify(Audio(9, "en", title, codec: "flac", channels: 2))) >= TrackDescriptor.MatchThreshold);
    }

    [Fact]
    public void LegacyDescriptorWithNoKind_DoesNotSelectCommentary()
    {
        var remembered = new TrackDescriptor("en", null, null, "Stereo", "aac", 2);
        Assert.Equal(0, remembered.Score(Classify(Audio(1, "en", "Commentary", codec: "aac", channels: 2))));
    }

    [Fact]
    public void MissingRememberedSoundtrack_FallsBackToTheLanguageChoices()
    {
        var remembered = TrackDescriptor.From(Classify(Audio(1, "en", "Stereo", codec: "aac", channels: 2)));
        var d = Decide(new[] { Audio(1, "en", "Commentary", codec: "aac", channels: 2), Audio(2, "ja", "Stereo", codec: "aac", channels: 2) },
            Starter("en") with { AudioChain = new[] { Lang("ja") } }, manual: new ManualOverride(remembered, false, null));
        Assert.Equal(2, d.AudioIndex);
        Assert.True(d.Has("audio.manual.nomatch"));
    }
}
