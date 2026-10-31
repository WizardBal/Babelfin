namespace Jellyfin.Plugin.Babelfin.Engine.Languages;

/// <summary>One row of the language table.</summary>
/// <param name="TwoLetter">ISO 639-1 code of the base language ("pt"), or <c>null</c> when it has none. Variety, script and region rows repeat their base's code.</param>
/// <param name="ThreeLetterT">ISO 639-2/T code ("por"); for a variety row its ISO 639-3 code ("yue").</param>
/// <param name="ThreeLetterB">ISO 639-2/B code when it differs from T ("chi"), else <c>null</c>.</param>
/// <param name="EnglishName">English name ("Portuguese (Brazil)"). It also resolves as an OMDb-style name.</param>
/// <param name="Variety">Spoken variety (ISO 639-3) this row stands for, or <c>null</c>.</param>
/// <param name="Script">Script (ISO 15924) this row stands for, or <c>null</c>.</param>
/// <param name="Region">Region this row stands for, or <c>null</c>.</param>
public sealed record LanguageEntry(
    string? TwoLetter,
    string ThreeLetterT,
    string? ThreeLetterB,
    string EnglishName,
    string? Variety = null,
    string? Script = null,
    string? Region = null)
{
    /// <summary>Gets a value indicating whether the row is a plain language (no variety, script or region).</summary>
    internal bool IsGeneric => Variety is null && Script is null && Region is null;

    /// <summary>Gets a code built from the three-letter code that always parses back to this row ("por-BR", "yue", "zho-Hant-HK").</summary>
    internal string StableCode =>
        (Variety ?? ThreeLetterT) + (Script is null ? string.Empty : "-" + Script) + (Region is null ? string.Empty : "-" + Region);
}
