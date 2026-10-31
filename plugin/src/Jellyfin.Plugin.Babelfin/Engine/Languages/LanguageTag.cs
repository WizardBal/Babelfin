using System.Text;

namespace Jellyfin.Plugin.Babelfin.Engine.Languages;

/// <summary>
/// A normalised language. Audio compares spoken varieties; subtitles compare written languages and scripts.
/// Build one with <see cref="LanguageTable.Resolve"/>; "und" and the other "no language" codes never become a tag (the result is <c>null</c>).
/// </summary>
/// <param name="Base">ISO 639-1 when one exists, else 639-2/T, else 639-3 ("ja", "zh", "pt"). Varieties keep their macrolanguage here ("cmn" and "yue" have Base "zh").</param>
/// <param name="Variety">Spoken variety as ISO 639-3 ("cmn", "yue", "nan", "hak", "wuu", "gan"); <c>null</c> = generic.</param>
/// <param name="Script">ISO 15924 script ("Hans", "Hant", "Latn", "Cyrl"); <c>null</c> = generic.</param>
/// <param name="Region">ISO 3166 or UN M.49 region ("BR", "PT", "419", "ES", "CA", "FR", "US", "GB"); <c>null</c> = generic.</param>
public sealed record LanguageTag(string Base, string? Variety, string? Script, string? Region)
{
    /// <summary>
    /// Gets the canonical code: "zh", "zh-Hans", "yue", "pt-BR", "es-419", "fr-CA", "sr-Latn", "zh-Hant-HK".
    /// The variety takes the place of the base when set ("yue", "cmn-Hans"); script and region follow, joined with '-'.
    /// </summary>
    public string Code
    {
        get
        {
            var sb = new StringBuilder(Variety ?? Base);
            if (Script is not null)
            {
                sb.Append('-').Append(Script);
            }

            if (Region is not null)
            {
                sb.Append('-').Append(Region);
            }

            return sb.ToString();
        }
    }

    /// <summary>Gets a value indicating whether the tag is generic on every axis (no variety, script or region).</summary>
    public bool IsGeneric => Variety is null && Script is null && Region is null;

    /// <summary>Returns <see cref="Code"/>.</summary>
    /// <returns>The canonical code.</returns>
    public override string ToString() => Code;
}

/// <summary>How well a track's language satisfies a wanted language (see <see cref="LanguageTable.Match"/>).</summary>
public enum MatchQuality
{
    /// <summary>Not the wanted language (different base, or a different variety, script or region).</summary>
    None = 0,

    /// <summary>Same base, but the track's tag is less specific than what was wanted (track "chi" when "yue" was wanted). Ranks below <see cref="Exact"/>.</summary>
    Generic = 1,

    /// <summary>Equal on every axis the wanted tag specifies (a generic wanted tag is satisfied exactly by any track sharing its base).</summary>
    Exact = 2,
}
