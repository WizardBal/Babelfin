using System;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>burned-in subtitles; device capabilities; format and embedded preferences.</summary>
public class BurnedInAndDeviceTests
{
    private static readonly ResolvedProfile JapaneseAudio = OriginalAudio("en");

    // Burned-in
    [Fact]
    public void WantedLanguageBurnedIn_NoSubsNoFetch()
    {
        var d = Decide(ClockworkOrchard(), JapaneseAudio, Item("ja", null, "en"));

        Assert.Null(d.SubtitleIndex);
        Assert.True(d.SubtitlesWanted);
        Assert.True(d.Has("subs.burnedin"));
        Assert.Contains("English is burned in", d.Explain(), StringComparison.Ordinal);
    }

    // Burned-in
    [Fact]
    public void DifferentLanguageBurnedIn_Normal()
    {
        var d = Decide(ClockworkOrchard(), JapaneseAudio, Item("ja", null, "ja"));

        Assert.Equal(4, d.SubtitleIndex);
        Assert.False(d.Has("subs.burnedin"));
    }

    // Burned-in: a partial want with the language burned in still shows nothing.
    [Fact]
    public void PartialWant_BurnedIn_StillNone()
    {
        var d = Decide(Slv(), Starter("en"), Item("ja", null, "eng"));

        Assert.Null(d.SubtitleIndex);
    }

    // Burned-in: a backup language burned in counts too.
    [Fact]
    public void BackupLanguageBurnedIn_None()
    {
        var d = Decide(ClockworkOrchard(), JapaneseAudio with { SubtitleLanguages = new[] { "en", "es" } }, Item("ja", null, "spa"));

        Assert.Null(d.SubtitleIndex);
    }

    // Device
    [Fact]
    public void ImageSubsUnsupported_ImageDemoted()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "eng", format: SubtitleFormat.Image), Sub(3, "eng", format: SubtitleFormat.Text) };
        var profile = JapaneseAudio with { Format = FormatPreference.Neutral };

        Assert.Equal(2, Decide(tracks, profile, Item("ja")).SubtitleIndex);
        Assert.Equal(3, Decide(tracks, profile, Item("ja"), new DeviceCapabilities(SupportsImageSubtitles: false)).SubtitleIndex);
    }

    // Device: an unsupported image track is still used when it's the only one (the server burns it in).
    [Fact]
    public void ImageSubsUnsupported_OnlyImage_StillChosen()
    {
        var d = Decide(MidnightCircuit(), Fx.Starter(new[] { "de" }, new[] { "en" }), device: new DeviceCapabilities(SupportsImageSubtitles: false));

        Assert.Equal(3, d.SubtitleIndex);
    }

    // Device
    [Fact]
    public void TextSubsUnsupported_TextDemoted()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "eng", format: SubtitleFormat.Text), Sub(3, "eng", format: SubtitleFormat.Image) };

        var d = Decide(tracks, JapaneseAudio with { Format = FormatPreference.Neutral }, Item("ja"), new DeviceCapabilities(SupportsTextSubtitles: false));

        Assert.Equal(3, d.SubtitleIndex);
    }

    // Format
    [Fact]
    public void TextOnly_DropsImage()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "eng", format: SubtitleFormat.Image), Sub(3, "eng", "Full", format: SubtitleFormat.Text) };

        var d = Decide(tracks, JapaneseAudio with { Format = FormatPreference.TextOnly }, Item("ja"));

        Assert.Equal(3, d.SubtitleIndex);
        Assert.True(d.Has("subs.skipped.image", 2));
    }

    // Format
    [Fact]
    public void PreferText_TextBeforeImage()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "eng", format: SubtitleFormat.Image, isDefault: true), Sub(3, "eng", format: SubtitleFormat.Text) };

        Assert.Equal(3, Decide(tracks, JapaneseAudio, Item("ja")).SubtitleIndex);
    }

    // Format: kind outranks format — a full image track beats a partial text track.
    [Fact]
    public void FullImage_BeatsPartialText()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "eng", "Signs", isDefault: true, format: SubtitleFormat.Text), Sub(3, "eng", format: SubtitleFormat.Image) };

        Assert.Equal(3, Decide(tracks, JapaneseAudio, Item("ja")).SubtitleIndex);
    }

    // Embedded
    [Fact]
    public void PreferExternal_ExternalFirst()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "eng"), Sub(3, "eng", external: true, fileName: "Show.S01E01.en.srt") };

        Assert.Equal(3, Decide(tracks, JapaneseAudio with { Embedded = EmbeddedPreference.PreferExternal }, Item("ja")).SubtitleIndex);
        Assert.Equal(2, Decide(tracks, JapaneseAudio with { Embedded = EmbeddedPreference.Neutral }, Item("ja")).SubtitleIndex);
    }

    // Embedded
    [Fact]
    public void PreferEmbedded_EmbeddedFirst()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, "eng", external: true, fileName: "Show.S01E01.en.srt"), Sub(3, "eng") };

        Assert.Equal(3, Decide(tracks, JapaneseAudio with { Embedded = EmbeddedPreference.PreferEmbedded }, Item("ja")).SubtitleIndex);
    }

    // Device: DirectPlayable picks the direct-playable codec.
    [Fact]
    public void DirectPlayable_PicksPlayableCodec()
    {
        var device = new DeviceCapabilities(CanDirectPlayAudio: t => t.Codec == "ac3");

        Assert.Equal(1, Decide(MidnightCircuit(), Starter("en")).AudioIndex);
        Assert.Equal(2, Decide(MidnightCircuit(), Starter("en"), device: device).AudioIndex);
    }

    // Subtitles with no language are ignored with a trace.
    [Fact]
    public void UnlabelledSubtitle_IgnoredWithTrace()
    {
        var tracks = new[] { Audio(1, "jpn"), Sub(2, null), Sub(3, "eng") };

        var d = Decide(tracks, JapaneseAudio, Item("ja"));

        Assert.Equal(3, d.SubtitleIndex);
        Assert.True(d.Has("subs.unlabelled.ignored", 2));
    }
}
