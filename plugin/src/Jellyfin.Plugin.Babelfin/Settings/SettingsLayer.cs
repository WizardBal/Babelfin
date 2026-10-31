using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Engine.Selection;

namespace Jellyfin.Plugin.Babelfin.Settings;

/// <summary>What to do with titles in a language the person doesn't understand.</summary>
public enum ForeignTitlesRule
{
    /// <summary>A dub in my language when the file has one (the starter default): my languages → the original → the file's default.</summary>
    DubInMyLanguage,

    /// <summary>The original with subtitles: the original → my languages → the file's default.</summary>
    OriginalWithSubtitles,
}

/// <summary>How long a manual track change is remembered.</summary>
public enum MemoryScope
{
    /// <summary>The rest of this show (default).</summary>
    RestOfShow,

    /// <summary>This season.</summary>
    Season,

    /// <summary>This title only.</summary>
    TitleOnly,

    /// <summary>Don't remember.</summary>
    DontRemember,
}

/// <summary>One step of an audio chain as stored (mutable twin of <see cref="AudioStep"/> for XML and JSON).</summary>
public class AudioStepSetting
{
    /// <summary>Initializes a new instance of the <see cref="AudioStepSetting"/> class.</summary>
    public AudioStepSetting()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AudioStepSetting"/> class.</summary>
    /// <param name="kind">The step kind.</param>
    /// <param name="language">The language for <see cref="AudioStepKind.Language"/>.</param>
    /// <param name="titleContains">Plain text the raw track title must contain (never a regex).</param>
    public AudioStepSetting(AudioStepKind kind, string? language = null, string? titleContains = null)
    {
        Kind = kind;
        Language = language;
        TitleContains = titleContains;
    }

    /// <summary>Gets or sets the step kind.</summary>
    public AudioStepKind Kind { get; set; }

    /// <summary>Gets or sets the language ("ja", "yue", "pt-BR"; any string the language table resolves) for <see cref="AudioStepKind.Language"/>.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets plain text the raw track title must contain (case-insensitive "contains", never a regular expression).</summary>
    public string? TitleContains { get; set; }

    /// <summary>Converts to the engine's step.</summary>
    /// <returns>The engine step.</returns>
    public AudioStep ToEngine() => new(Kind, string.IsNullOrWhiteSpace(Language) ? null : Language.Trim(), string.IsNullOrWhiteSpace(TitleContains) ? null : TitleContains);

    /// <summary>Copies the step.</summary>
    /// <returns>A copy.</returns>
    public AudioStepSetting Clone() => new(Kind, Language, TitleContains);
}

/// <summary>
/// One layer of settings: every field is optional ("inherit"). A layer is used for the admin's everyday
/// defaults, a library default, a smart rule's settings, a title default, a person's everyday settings, their library and
/// title overrides, and the admin's per-person locks. The same class serialises to XML (plugin configuration) and JSON (per-person files).
/// A <c>null</c> field inherits; a set field (an empty array included) is applied by the precedence in <see cref="SettingsFields"/>.
/// List fields are arrays on purpose: XmlSerializer leaves an absent array null but creates an absent <c>List&lt;T&gt;</c> as empty, which would turn "inherit" into "set to nothing".
/// </summary>
public class SettingsLayer
{
    /// <summary>Gets or sets the spoken languages the person understands, ordered.</summary>
    public string[]? Understood { get; set; }

    /// <summary>Gets or sets the written languages the person reads, ordered (the default subtitle languages).</summary>
    public string[]? Reads { get; set; }

    /// <summary>Gets or sets a value indicating whether audio-description tracks are preferred.</summary>
    public bool? UsesAudioDescription { get; set; }

    /// <summary>Gets or sets a value indicating whether SDH is preferred (and subtitles shown even when the audio is understood).</summary>
    public bool? PrefersSdh { get; set; }

    /// <summary>Gets or sets the ordered audio steps. When set, it replaces the starter chain built from <see cref="ForeignTitles"/>.</summary>
    public AudioStepSetting[]? AudioChain { get; set; }

    /// <summary>Gets or sets what happens with titles in a language the person doesn't understand; it shapes the starter audio chain when <see cref="AudioChain"/> is unset.</summary>
    public ForeignTitlesRule? ForeignTitles { get; set; }

    /// <summary>Gets or sets a value indicating whether commentary tracks are skipped unless nothing else exists.</summary>
    public bool? AvoidCommentary { get; set; }

    /// <summary>Gets or sets the same-step audio tie-breaks, in order.</summary>
    public TieBreak[]? TieBreaks { get; set; }

    /// <summary>Gets or sets what unlabelled audio is assumed to be (one field together with <see cref="UnlabelledAudioLanguage"/>).</summary>
    public UnlabelledAudioRule? UnlabelledAudio { get; set; }

    /// <summary>Gets or sets the language for <see cref="UnlabelledAudioRule.SpecificLanguage"/>.</summary>
    public string? UnlabelledAudioLanguage { get; set; }

    /// <summary>Gets or sets where the title's original language comes from.</summary>
    public OriginalLanguageSource? OriginalSource { get; set; }

    /// <summary>Gets or sets the subtitles wanted when the chosen audio is understood.</summary>
    public SubtitleWant? WhenAudioUnderstood { get; set; }

    /// <summary>Gets or sets the subtitles wanted when the chosen audio isn't understood.</summary>
    public SubtitleWant? WhenAudioNotUnderstood { get; set; }

    /// <summary>Gets or sets a value indicating whether audio in an unknown language counts as understood.</summary>
    public bool? UnknownAudioCountsAsUnderstood { get; set; }

    /// <summary>Gets or sets the ordered subtitle languages (empty list = the languages the person reads).</summary>
    public string[]? SubtitleLanguages { get; set; }

    /// <summary>Gets or sets the SDH preference.</summary>
    public Preference? Sdh { get; set; }

    /// <summary>Gets or sets the text-versus-image preference.</summary>
    public FormatPreference? Format { get; set; }

    /// <summary>Gets or sets the embedded-versus-external preference.</summary>
    public EmbeddedPreference? Embedded { get; set; }

    /// <summary>Gets or sets what to show when no wanted full track exists.</summary>
    public WhenNothingMatches? IfNothingMatches { get; set; }

    /// <summary>Gets or sets how long a manual track change is remembered.</summary>
    public MemoryScope? Memory { get; set; }

    /// <summary>Gets a value indicating whether no field is set.</summary>
    public bool IsEmpty => !SettingsFields.All.Any(IsSet);

    /// <summary>Whether a field is set in this layer.</summary>
    /// <param name="field">A name from <see cref="SettingsFields"/>.</param>
    /// <returns>True when set.</returns>
    public bool IsSet(string field) => field switch
    {
        SettingsFields.Understood => Understood is not null,
        SettingsFields.Reads => Reads is not null,
        SettingsFields.UsesAudioDescription => UsesAudioDescription.HasValue,
        SettingsFields.PrefersSdh => PrefersSdh.HasValue,
        SettingsFields.AudioChain => AudioChain is not null,
        SettingsFields.ForeignTitles => ForeignTitles.HasValue,
        SettingsFields.AvoidCommentary => AvoidCommentary.HasValue,
        SettingsFields.TieBreaks => TieBreaks is not null,
        SettingsFields.UnlabelledAudio => UnlabelledAudio.HasValue,
        SettingsFields.OriginalSource => OriginalSource.HasValue,
        SettingsFields.WhenAudioUnderstood => WhenAudioUnderstood.HasValue,
        SettingsFields.WhenAudioNotUnderstood => WhenAudioNotUnderstood.HasValue,
        SettingsFields.UnknownAudioCountsAsUnderstood => UnknownAudioCountsAsUnderstood.HasValue,
        SettingsFields.SubtitleLanguages => SubtitleLanguages is not null,
        SettingsFields.Sdh => Sdh.HasValue,
        SettingsFields.Format => Format.HasValue,
        SettingsFields.Embedded => Embedded.HasValue,
        SettingsFields.IfNothingMatches => IfNothingMatches.HasValue,
        SettingsFields.Memory => Memory.HasValue,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown settings field"),
    };

    /// <summary>The names of every field set in this layer, in <see cref="SettingsFields.All"/> order.</summary>
    /// <returns>The field names.</returns>
    public IEnumerable<string> SetFields() => SettingsFields.All.Where(IsSet);

    /// <summary>Copies one field (set or unset) from another layer.</summary>
    /// <param name="source">The layer to copy from.</param>
    /// <param name="field">A name from <see cref="SettingsFields"/>.</param>
    public void CopyField(SettingsLayer source, string field)
    {
        ArgumentNullException.ThrowIfNull(source);
        switch (field)
        {
            case SettingsFields.Understood: Understood = source.Understood?.ToArray(); break;
            case SettingsFields.Reads: Reads = source.Reads?.ToArray(); break;
            case SettingsFields.UsesAudioDescription: UsesAudioDescription = source.UsesAudioDescription; break;
            case SettingsFields.PrefersSdh: PrefersSdh = source.PrefersSdh; break;
            case SettingsFields.AudioChain: AudioChain = source.AudioChain?.Select(s => s.Clone()).ToArray(); break;
            case SettingsFields.ForeignTitles: ForeignTitles = source.ForeignTitles; break;
            case SettingsFields.AvoidCommentary: AvoidCommentary = source.AvoidCommentary; break;
            case SettingsFields.TieBreaks: TieBreaks = source.TieBreaks?.ToArray(); break;
            case SettingsFields.UnlabelledAudio:
                UnlabelledAudio = source.UnlabelledAudio;
                UnlabelledAudioLanguage = source.UnlabelledAudioLanguage;
                break;
            case SettingsFields.OriginalSource: OriginalSource = source.OriginalSource; break;
            case SettingsFields.WhenAudioUnderstood: WhenAudioUnderstood = source.WhenAudioUnderstood; break;
            case SettingsFields.WhenAudioNotUnderstood: WhenAudioNotUnderstood = source.WhenAudioNotUnderstood; break;
            case SettingsFields.UnknownAudioCountsAsUnderstood: UnknownAudioCountsAsUnderstood = source.UnknownAudioCountsAsUnderstood; break;
            case SettingsFields.SubtitleLanguages: SubtitleLanguages = source.SubtitleLanguages?.ToArray(); break;
            case SettingsFields.Sdh: Sdh = source.Sdh; break;
            case SettingsFields.Format: Format = source.Format; break;
            case SettingsFields.Embedded: Embedded = source.Embedded; break;
            case SettingsFields.IfNothingMatches: IfNothingMatches = source.IfNothingMatches; break;
            case SettingsFields.Memory: Memory = source.Memory; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown settings field");
        }
    }

    /// <summary>Unsets one field.</summary>
    /// <param name="field">A name from <see cref="SettingsFields"/>.</param>
    public void Clear(string field) => CopyField(new SettingsLayer(), field);

    /// <summary>A deep copy.</summary>
    /// <returns>The copy.</returns>
    public SettingsLayer Clone()
    {
        var copy = new SettingsLayer();
        foreach (var field in SettingsFields.All)
        {
            copy.CopyField(this, field);
        }

        return copy;
    }

    /// <summary>Returns the layer with every field of <paramref name="overlay"/> that is set copied over this layer's values (this layer is unchanged).</summary>
    /// <param name="overlay">The layer whose set fields win.</param>
    /// <returns>A new layer.</returns>
    public SettingsLayer With(SettingsLayer overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        var result = Clone();
        foreach (var field in overlay.SetFields())
        {
            result.CopyField(overlay, field);
        }

        return result;
    }
}

/// <summary>The field names of a <see cref="SettingsLayer"/>, used for locks, provenance ("set by…") and precedence.</summary>
public static class SettingsFields
{
    /// <summary>Spoken languages.</summary>
    public const string Understood = "Understood";

    /// <summary>Written languages.</summary>
    public const string Reads = "Reads";

    /// <summary>Audio description accessibility choice.</summary>
    public const string UsesAudioDescription = "UsesAudioDescription";

    /// <summary>SDH accessibility choice.</summary>
    public const string PrefersSdh = "PrefersSdh";

    /// <summary>The audio chain.</summary>
    public const string AudioChain = "AudioChain";

    /// <summary>Titles in a language I don't understand.</summary>
    public const string ForeignTitles = "ForeignTitles";

    /// <summary>Avoid commentary.</summary>
    public const string AvoidCommentary = "AvoidCommentary";

    /// <summary>Tie-breaks.</summary>
    public const string TieBreaks = "TieBreaks";

    /// <summary>Unlabelled audio rule (and its language).</summary>
    public const string UnlabelledAudio = "UnlabelledAudio";

    /// <summary>Original language source.</summary>
    public const string OriginalSource = "OriginalSource";

    /// <summary>Subtitles when the audio is understood.</summary>
    public const string WhenAudioUnderstood = "WhenAudioUnderstood";

    /// <summary>Subtitles when the audio isn't understood.</summary>
    public const string WhenAudioNotUnderstood = "WhenAudioNotUnderstood";

    /// <summary>Unknown audio counts as understood.</summary>
    public const string UnknownAudioCountsAsUnderstood = "UnknownAudioCountsAsUnderstood";

    /// <summary>Subtitle languages.</summary>
    public const string SubtitleLanguages = "SubtitleLanguages";

    /// <summary>SDH preference.</summary>
    public const string Sdh = "Sdh";

    /// <summary>Format preference.</summary>
    public const string Format = "Format";

    /// <summary>Embedded preference.</summary>
    public const string Embedded = "Embedded";

    /// <summary>If nothing matches.</summary>
    public const string IfNothingMatches = "IfNothingMatches";

    /// <summary>Memory scope for manual changes.</summary>
    public const string Memory = "Memory";

    /// <summary>Gets every field name, in display order.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        Understood, Reads, UsesAudioDescription, PrefersSdh,
        AudioChain, ForeignTitles, AvoidCommentary, TieBreaks, UnlabelledAudio, OriginalSource,
        WhenAudioUnderstood, WhenAudioNotUnderstood, UnknownAudioCountsAsUnderstood, SubtitleLanguages, Sdh, Format, Embedded, IfNothingMatches,
        Memory,
    };

    /// <summary>Whether a string names a field.</summary>
    /// <param name="field">The candidate.</param>
    /// <returns>True when it is one of <see cref="All"/>.</returns>
    public static bool IsField(string? field) => field is not null && All.Contains(field, StringComparer.Ordinal);
}
