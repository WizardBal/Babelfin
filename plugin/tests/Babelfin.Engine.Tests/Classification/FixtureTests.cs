using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Xunit;
using static Babelfin.Engine.Tests.Classification.Tracks;

namespace Babelfin.Engine.Tests.Classification;

/// <summary>Representative stream layouts with invented titles and release tags.</summary>
public class FixtureTests
{
    [Fact]
    public void Skyward_Lantern_Voyage_forced_default_is_partial_and_the_others_are_full()
    {
        var tracks = new[]
        {
            Audio(1, "eng", "Surround", isDefault: true),
            Audio(2, "jpn", "Surround"),
            Sub(3, "eng", null, isDefault: true, forced: true),
            Sub(4, "eng", null),
            Sub(5, "eng", null),
            Sub(6, "eng", null),
        };

        var c = Classifier.ClassifyAll(tracks);
        Assert.All(c.Take(2), a => Assert.Equal(AudioKind.Normal, a.AudioKind));
        Assert.Equal("en", c[0].Language?.Code);
        Assert.Equal("ja", c[1].Language?.Code);
        Assert.Equal(SubtitleKind.Partial, c[2].SubtitleKind);
        Assert.All(c.Skip(3), s => Assert.Equal(SubtitleKind.Full, s.SubtitleKind));
    }

    [Fact]
    public void Clockwork_Orchard_signs_songs_is_partial_although_not_flagged()
    {
        var signs = Classifier.Classify(Sub(3, "eng", "Signs/Songs [PebbleForge]", isDefault: true));
        var full = Classifier.Classify(Sub(4, "eng", "English (CloudSpool)"));
        Assert.Equal(SubtitleKind.Partial, signs.SubtitleKind);
        Assert.Equal(SubtitleKind.Full, full.SubtitleKind);
    }

    [Fact]
    public void Moonlit_Harbor_signs_and_songs_is_partial_and_dialogue_is_full()
    {
        Assert.Equal(SubtitleKind.Partial, Classifier.Classify(Sub(3, "eng", "Signs & Songs", isDefault: true)).SubtitleKind);
        Assert.Equal(SubtitleKind.Full, Classifier.Classify(Sub(4, "eng", "Dialogue")).SubtitleKind);
    }

    [Fact]
    public void Starlight_Survey_pgs_tracks()
    {
        Assert.Equal(SubtitleKind.Full, Classifier.Classify(Sub(3, "eng", "[Full] (PGS)", format: SubtitleFormat.Image)).SubtitleKind);
        Assert.Equal(SubtitleKind.Partial, Classifier.Classify(Sub(4, "eng", "[Signs/Songs] (PGS)", format: SubtitleFormat.Image)).SubtitleKind);
    }

    [Fact]
    public void The_Glass_Relay_sdh_track()
    {
        var c = Classifier.ClassifyAll(new[]
        {
            Audio(1, null, null),
            Sub(2, "eng", null, isDefault: true, forced: true),
            Sub(3, "eng", null),
            Sub(4, "eng", "SDH"),
            Sub(5, "jpn", null),
        });

        Assert.Null(c[0].Language);
        Assert.Equal(SubtitleKind.Partial, c[1].SubtitleKind);
        Assert.Equal(SubtitleKind.Full, c[2].SubtitleKind);
        Assert.Equal(SubtitleKind.Sdh, c[3].SubtitleKind);
        Assert.Equal(SubtitleKind.Full, c[4].SubtitleKind);
        Assert.Equal("ja", c[4].Language?.Code);
    }
}
