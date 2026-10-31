using System;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;
using static Babelfin.Engine.Tests.Selection.Fx;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>"original language" as one chain step, with a source setting and the library's usual original language.</summary>
public class OriginalLanguageTests
{
    private static readonly Jellyfin.Plugin.Babelfin.Engine.Classification.Track[] FlaggedJapanese =
    {
        Audio(1, "eng", isDefault: true),
        Audio(2, "jpn", isOriginal: true),
    };

    private static ResolvedProfile OriginalFirst(OriginalLanguageSource source) =>
        Starter("de").Chain(Step(AudioStepKind.OriginalLanguage), Step(AudioStepKind.FirstTrack)) with { OriginalSource = source };

    [Fact]
    public void Metadata_UsedWhenNoFlag()
    {
        Assert.Equal(2, Decide(Slv(), OriginalFirst(OriginalLanguageSource.MetadataThenFlag), Item(original: "ja")).AudioIndex);
    }

    [Fact]
    public void Flag_UsedWhenNoMetadata()
    {
        Assert.Equal(2, Decide(FlaggedJapanese, OriginalFirst(OriginalLanguageSource.MetadataThenFlag), Item()).AudioIndex);
    }

    [Fact]
    public void MetadataThenFlag_MetadataWinsOnConflict()
    {
        var d = Decide(FlaggedJapanese, OriginalFirst(OriginalLanguageSource.MetadataThenFlag), Item(original: "en"));

        Assert.Equal(1, d.AudioIndex);
        Assert.Equal("metadata", d.Trace.Single(l => l.Code == "audio.original.known").Args!["from"]);
    }

    [Fact]
    public void FlagThenMetadata_FlagWinsOnConflict()
    {
        var d = Decide(FlaggedJapanese, OriginalFirst(OriginalLanguageSource.FlagThenMetadata), Item(original: "en"));

        Assert.Equal(2, d.AudioIndex);
        Assert.Contains("flagged original", d.Trace.Single(l => l.Code == "audio.original.known").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagThenMetadata_NoFlag_UsesMetadata()
    {
        Assert.Equal(2, Decide(Slv(), OriginalFirst(OriginalLanguageSource.FlagThenMetadata), Item(original: "ja")).AudioIndex);
    }

    // OMDb-style name in metadata.
    [Fact]
    public void MetadataName_English_Resolves()
    {
        var tracks = new[] { Audio(1, "jpn", isDefault: true), Audio(2, "eng") };

        Assert.Equal(2, Decide(tracks, OriginalFirst(OriginalLanguageSource.MetadataThenFlag), Item(original: "English")).AudioIndex);
    }

    [Fact]
    public void OriginalUnknown_StepSkippedWithTrace()
    {
        var d = Decide(Slv(), OriginalFirst(OriginalLanguageSource.MetadataThenFlag), Item());

        Assert.Equal(1, d.AudioIndex);
        var skipped = Assert.Single(d.Trace, l => l.Code == "audio.step.skipped");
        Assert.Contains("original language is unknown", skipped.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OriginalUnknown_LibraryUsualStepCoversIt()
    {
        var d = Decide(Slv(), OriginalAudio("en"), Item(library: "ja"));

        Assert.Equal(2, d.AudioIndex);
        Assert.Equal(nameof(AudioStepKind.LibraryUsualOriginalLanguage), d.Trace.Single(l => l.Code == "audio.step.matched").Args!["kind"]);
    }

    [Fact]
    public void UnrecognisedMetadata_IgnoredWithTrace()
    {
        var d = Decide(Slv(), OriginalFirst(OriginalLanguageSource.MetadataThenFlag), Item(original: "Notalanguageatall"));

        Assert.True(d.Has("profile.language.unknown"));
        Assert.Equal(1, d.AudioIndex);
    }

    // the original track exists but only as commentary → the step skips.
    [Fact]
    public void OriginalOnlyCommentary_StepSkipped()
    {
        var tracks = new[] { Audio(1, "eng", isDefault: true), Audio(2, "jpn", "Commentary") };

        var d = Decide(tracks, OriginalFirst(OriginalLanguageSource.MetadataThenFlag), Item(original: "ja"));

        Assert.Equal(1, d.AudioIndex);
        Assert.Contains("commentary", d.Trace.Single(l => l.Code == "audio.step.skipped").Text, StringComparison.Ordinal);
    }

    // TitleContains also applies to the original step.
    [Fact]
    public void OriginalStep_WithTitleContains()
    {
        var tracks = new[] { Audio(1, "jpn", "Stereo"), Audio(2, "jpn", "Remastered 5.1") };
        var profile = Starter("en").Chain(new AudioStep(AudioStepKind.OriginalLanguage, TitleContains: "remastered"));

        Assert.Equal(2, Decide(tracks, profile, Item(original: "ja")).AudioIndex);
    }
}
