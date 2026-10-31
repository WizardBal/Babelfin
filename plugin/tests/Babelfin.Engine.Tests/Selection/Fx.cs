using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Xunit;

namespace Babelfin.Engine.Tests.Selection;

/// <summary>Fixture helpers with invented titles and representative track layouts.</summary>
internal static class Fx
{
    /// <summary>Every trace code the engine may emit. Every decision made through <see cref="Decide"/> is checked against it.</summary>
    public static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
    {
        "profile.language.unknown", "audio.original.known", "audio.none", "audio.unlabelled.assumed", "audio.unlabelled.unknown",
        "audio.manual", "audio.manual.nomatch", "audio.step.matched", "audio.step.skipped", "audio.fallback", "audio.videodefault",
        "audio.understood", "audio.notunderstood", "audio.unknown",
        "subs.want", "subs.off", "subs.videodefault", "subs.burnedin", "subs.manual", "subs.manual.off", "subs.manual.nomatch", "subs.unlabelled.ignored",
        "subs.matched", "subs.skipped.partial", "subs.skipped.full", "subs.skipped.sdh", "subs.skipped.image", "subs.skipped.language",
        "subs.nothing", "subs.fallback",
    };

    public static readonly LanguageTable Languages = LanguageTable.Default;

    public static readonly TrackClassifier Classifier = new(LanguageTable.Default, KeywordTables.Default);

    public static readonly TrackSelector Selector = new(Languages, Classifier);

    public static Track Audio(int index, string? lang, string? title = null, bool isDefault = false, string? codec = null, int? channels = null, bool isOriginal = false, bool isLossless = false, string? comment = null) =>
        new(index, TrackType.Audio, lang, title, comment, null, codec, channels, isDefault, false, false, isOriginal, false, SubtitleFormat.Unknown, isLossless);

    public static Track Sub(int index, string? lang, string? title = null, bool isDefault = false, bool isForced = false, bool hi = false, bool external = false, SubtitleFormat format = SubtitleFormat.Unknown, string? codec = null, string? fileName = null) =>
        new(index, TrackType.Subtitle, lang, title, null, fileName, codec, null, isDefault, isForced, hi, false, external, format);

    public static IReadOnlyList<AudioStep> StarterChain { get; } = new[]
    {
        new AudioStep(AudioStepKind.MyLanguages),
        new AudioStep(AudioStepKind.OriginalLanguage),
        new AudioStep(AudioStepKind.FileDefault),
    };

    /// <summary>A test profile with ordered audio preferences and explicit subtitle behavior.</summary>
    public static ResolvedProfile Starter(IReadOnlyList<string> understood, IReadOnlyList<string> reads)
    {
        ArgumentNullException.ThrowIfNull(understood);
        ArgumentNullException.ThrowIfNull(reads);
        return new ResolvedProfile(
            Understood: understood,
            Reads: reads,
            UsesAudioDescription: false,
            PrefersSdh: false,
            AudioChain: StarterChain,
            AvoidCommentary: true,
            TieBreaks: ResolvedProfile.DefaultTieBreaks,
            UnlabelledAudio: UnlabelledAudioRule.OriginalWhenOnlyTrack,
            UnlabelledAudioLanguage: null,
            OriginalSource: OriginalLanguageSource.MetadataThenFlag,
            WhenAudioUnderstood: SubtitleWant.PartialOnly,
            WhenAudioNotUnderstood: SubtitleWant.Full,
            UnknownAudioCountsAsUnderstood: false,
            SubtitleLanguages: Array.Empty<string>(),
            Sdh: Preference.Neutral,
            Format: FormatPreference.PreferText,
            Embedded: EmbeddedPreference.Neutral,
            IfNothingMatches: WhenNothingMatches.ShowNothing);
    }

    /// <summary>The test starter with the same understood and read languages.</summary>
    public static ResolvedProfile Starter(params string[] languages) => Starter(languages, languages);

    /// <summary>The "original-audio" library starter: original → library's usual original → file default.</summary>
    public static ResolvedProfile OriginalAudio(params string[] languages) => Starter(languages) with
    {
        AudioChain = new[]
        {
            new AudioStep(AudioStepKind.OriginalLanguage),
            new AudioStep(AudioStepKind.LibraryUsualOriginalLanguage),
            new AudioStep(AudioStepKind.FileDefault),
        },
    };

    public static ResolvedProfile Chain(this ResolvedProfile profile, params AudioStep[] steps) => profile with { AudioChain = steps };

    public static AudioStep Lang(string language, string? titleContains = null) => new(AudioStepKind.Language, language, titleContains);

    public static AudioStep Step(AudioStepKind kind) => new(kind);

    public static ItemFacts Item(string? original = null, string? library = null, params string[] burned) => new(original, library, burned);

    /// <summary>Decides, then checks invariants that must hold for every decision: known codes, at least two trace lines, a clean explanation.</summary>
    public static Decision Decide(IReadOnlyList<Track> tracks, ResolvedProfile profile, ItemFacts? item = null, DeviceCapabilities? device = null, ManualOverride? manual = null)
    {
        var decision = Selector.Decide(tracks, item ?? ItemFacts.None, profile, device, manual);
        Check(decision);
        return decision;
    }

    public static void Check(Decision decision)
    {
        Assert.True(decision.Trace.Count >= 2, "every decision has at least two trace lines");
        foreach (var line in decision.Trace)
        {
            Assert.Contains(line.Code, KnownCodes);
            Assert.False(string.IsNullOrWhiteSpace(line.Text));
        }

        var explain = decision.Explain();
        Assert.DoesNotContain("null", explain, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Audio: ", explain, StringComparison.Ordinal);
        Assert.Contains("Subtitles: ", explain, StringComparison.Ordinal);
        if (decision.AudioIndex is { } a)
        {
            Assert.Contains($"#{a}", explain, StringComparison.Ordinal);
        }

        if (decision.SubtitleIndex is { } s)
        {
            Assert.Contains($"#{s}", explain, StringComparison.Ordinal);
        }
    }

    public static bool Has(this Decision decision, string code) => decision.Trace.Any(l => l.Code == code);

    public static bool Has(this Decision decision, string code, int trackIndex) => decision.Trace.Any(l => l.Code == code && l.TrackIndex == trackIndex);

    public static ClassifiedTrack Classify(Track track) => Classifier.Classify(track);

    public static Track[] Shift(IEnumerable<Track> tracks, int by) => tracks.Select(t => t with { Index = t.Index + by }).ToArray();

    // ---------- representative track layouts with invented titles and tags ----------

    /// <summary>Skyward Lantern Voyage S00E01: 1 eng default, 2 jpn, 3 eng forced default, 4-6 eng full.</summary>
    public static Track[] Slv() => new[]
    {
        Audio(1, "eng", "Surround", isDefault: true),
        Audio(2, "jpn", "Surround"),
        Sub(3, "eng", isDefault: true, isForced: true),
        Sub(4, "eng"),
        Sub(5, "eng"),
        Sub(6, "eng"),
    };

    /// <summary>Amber Courier S01E13: 1 eng default, 2 jpn, 3 eng forced default.</summary>
    public static Track[] AmberCourier() => new[]
    {
        Audio(1, "eng", isDefault: true),
        Audio(2, "jpn"),
        Sub(3, "eng", isDefault: true, isForced: true),
    };

    /// <summary>Clockwork Orchard S00E01: 3 is "Signs/Songs [PebbleForge]" flagged default but not forced.</summary>
    public static Track[] ClockworkOrchard() => new[]
    {
        Audio(1, "eng", isDefault: true),
        Audio(2, "jpn"),
        Sub(3, "eng", "Signs/Songs [PebbleForge]", isDefault: true),
        Sub(4, "eng", "English (CloudSpool)"),
    };

    /// <summary>Moonlit Harbor S00E09: 3 "Signs &amp; Songs" default (not forced), 4 "Dialogue".</summary>
    public static Track[] MoonlitHarbor() => new[]
    {
        Audio(1, "eng", isDefault: true),
        Audio(2, "jpn"),
        Sub(3, "eng", "Signs & Songs", isDefault: true),
        Sub(4, "eng", "Dialogue"),
    };

    /// <summary>Copper Dune Crossing: 3 eng "Dialogue".</summary>
    public static Track[] CopperDune() => new[]
    {
        Audio(1, "eng", isDefault: true),
        Audio(2, "jpn"),
        Sub(3, "eng", "Dialogue"),
    };

    /// <summary>Starlight Survey: Crimson Beacon: 1 jpn default, 2 eng, 3 "[Full] (PGS)", 4 "[Signs/Songs] (PGS)".</summary>
    public static Track[] StarlightSurvey() => new[]
    {
        Audio(1, "jpn", isDefault: true),
        Audio(2, "eng"),
        Sub(3, "eng", "[Full] (PGS)", format: SubtitleFormat.Image, codec: "pgssub"),
        Sub(4, "eng", "[Signs/Songs] (PGS)", format: SubtitleFormat.Image, codec: "pgssub"),
    };

    /// <summary>Unlabelled-audio fixture: 1 audio with no language, 2 eng forced default, 3 eng full, 4 eng "SDH", 5 jpn.</summary>
    public static Track[] GlassRelay() => new[]
    {
        Audio(1, null),
        Sub(2, "eng", isDefault: true, isForced: true),
        Sub(3, "eng"),
        Sub(4, "eng", "SDH"),
        Sub(5, "jpn"),
    };

    /// <summary>Single unlabelled-audio fixture: one "und" audio track.</summary>
    public static Track[] SingleUnlabelledAudio() => new[] { Audio(1, "und") };

    /// <summary>Multiple-codec fixture: 1 eng dts default, 2 eng ac3, 3 eng DVDSUB.</summary>
    public static Track[] MidnightCircuit() => new[]
    {
        Audio(1, "eng", isDefault: true, codec: "dts", channels: 6),
        Audio(2, "eng", codec: "ac3", channels: 6),
        Sub(3, "eng", format: SubtitleFormat.Image, codec: "dvdsub"),
    };
}
