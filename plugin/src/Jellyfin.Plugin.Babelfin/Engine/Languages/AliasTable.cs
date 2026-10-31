using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.Babelfin.Engine.Languages;

/// <summary>
/// Raw spellings found on tracks, in files and in metadata, mapped to a canonical code. Keys are matched
/// case-insensitively with '_' read as '-'. A value is any code <see cref="LanguageTable.Resolve"/> understands structurally
/// ("zh-Hans", "pt-BR", "jpn"); an empty value means "not a language" (und, mul, zxx, mis…), which resolves to <c>null</c>.
/// Explicit aliases win over ISO codes: "sc" and "tw" mean Simplified and Traditional Chinese on tracks, so Sardinian
/// and Twi are then known by their three-letter codes ("srd", "twi").
/// </summary>
/// <param name="RawToCode">Raw spelling to canonical code.</param>
public sealed record AliasTable(IReadOnlyDictionary<string, string> RawToCode)
{
    // Declared before Default: static initialisers run in textual order.

    /// <summary>Gets the hand-written aliases (before the generated English names are added).</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> HandWritten { get; } = Array.AsReadOnly(new[]
    {
        // Chinese written forms and region shorthands.
        Pair("chs", "zh-Hans"),
        Pair("cht", "zh-Hant"),
        Pair("sc", "zh-Hans"),
        Pair("tc", "zh-Hant"),
        Pair("zhs", "zh-Hans"),
        Pair("zht", "zh-Hant"),
        Pair("zh-cn", "zh-Hans"),
        Pair("zh-sg", "zh-Hans"),
        Pair("zh-my", "zh-Hans"),
        Pair("zh-tw", "zh-Hant"),
        Pair("zh-hk", "zh-Hant-HK"),
        Pair("zh-mo", "zh-Hant-MO"),
        Pair("cn", "zh"),
        Pair("tw", "zh-Hant"),
        Pair("hk", "zh-Hant"),
        Pair("zh-min-nan", "nan"),
        Pair("zh-guoyu", "cmn"),

        // Portuguese and Spanish track tags.
        Pair("pob", "pt-BR"),
        Pair("pb", "pt-BR"),
        Pair("ptb", "pt-BR"),
        Pair("ptp", "pt-PT"),
        Pair("spa-419", "es-419"),
        Pair("es-la", "es-419"),
        Pair("es-mx", "es-419"),

        // Deprecated and mistaken codes (B codes also come from the table; listed for clarity).
        Pair("iw", "he"),
        Pair("in", "id"),
        Pair("ji", "yi"),
        Pair("jw", "jv"),
        Pair("mo", "ro"),
        Pair("nb", "nob"),
        Pair("nn", "nno"),
        Pair("scc", "sr"),
        Pair("scr", "hr"),
        Pair("jp", "ja"),
        Pair("jap", "ja"),
        Pair("gre", "el"),
        Pair("ger", "de"),
        Pair("fre", "fr"),
        Pair("dut", "nl"),

        // Variety and form names people and metadata use.
        Pair("mandarin", "cmn"),
        Pair("cantonese", "yue"),
        Pair("hokkien", "nan"),
        Pair("min nan", "nan"),
        Pair("hakka", "hak"),
        Pair("shanghainese", "wuu"),
        Pair("putonghua", "cmn"),
        Pair("guoyu", "cmn"),
        Pair("simplified chinese", "zh-Hans"),
        Pair("traditional chinese", "zh-Hant"),
        Pair("brazilian portuguese", "pt-BR"),
        Pair("european portuguese", "pt-PT"),
        Pair("latin american spanish", "es-419"),
        Pair("castilian spanish", "es-ES"),
        Pair("canadian french", "fr-CA"),

        // Not a language.
        Pair("und", string.Empty),
        Pair("mul", string.Empty),
        Pair("zxx", string.Empty),
        Pair("mis", string.Empty),
        Pair("unknown", string.Empty),
        Pair("unk", string.Empty),
        Pair("none", string.Empty),
        Pair("undefined", string.Empty),
        Pair("undetermined", string.Empty),
        Pair("n/a", string.Empty),
        Pair("multiple languages", string.Empty),
        Pair("no linguistic content", string.Empty),
        Pair("uncoded languages", string.Empty),
    });

    /// <summary>
    /// Gets the built-in alias table: common track tags (chs, cht, sc, tc, pob, spa-419…), deprecated ISO codes
    /// (iw, in, ji, jw, mo), common mistakes (jp, jap), variety names (Mandarin, Cantonese), the "no language" codes,
    /// and every English name of the built-in language table (also the part before " (" or ",", and each name on either side of ';').
    /// "es-MX" maps to es-419 (Latin American Spanish), not to a Mexico-only region, because that is what such tracks are.
    /// "Latino" is not an alias: it is a title keyword (see <see cref="VarietyKeywordTable"/>).
    /// </summary>
    public static AliasTable Default { get; } = BuildDefault();

    /// <summary>
    /// Derives name aliases from entries, in priority order (first claim wins; keys that are ISO codes are skipped):
    /// full English names of plain languages with a two-letter code, then of other plain languages, then of variety,
    /// script and region rows, then the alternate names; then the same again with the part before " (" and before ",".
    /// Values are the entry's three-letter-based code, which parses back to the entry whatever the aliases say.
    /// </summary>
    /// <param name="entries">The language entries.</param>
    /// <param name="alternates">Further (name, three-letter code) pairs.</param>
    /// <param name="isCode">Whether a lower-case key is an ISO code of some entry.</param>
    /// <returns>Lower-case name to code, in insertion order.</returns>
    internal static IReadOnlyList<KeyValuePair<string, string>> DeriveNames(
        IReadOnlyList<LanguageEntry> entries,
        IReadOnlyList<KeyValuePair<string, string>> alternates,
        Func<string, bool> isCode)
    {
        var ordered = new List<KeyValuePair<string, string>>();
        foreach (var e in entries)
        {
            if (e.IsGeneric && e.TwoLetter is not null)
            {
                ordered.Add(Pair(e.EnglishName, e.StableCode));
            }
        }

        foreach (var e in entries)
        {
            if (e.IsGeneric && e.TwoLetter is null)
            {
                ordered.Add(Pair(e.EnglishName, e.StableCode));
            }
        }

        foreach (var e in entries)
        {
            if (!e.IsGeneric)
            {
                ordered.Add(Pair(e.EnglishName, e.StableCode));
            }
        }

        ordered.AddRange(alternates);

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<KeyValuePair<string, string>>();
        void Claim(string name, string code)
        {
            var key = LanguageTable.NormalizeKey(name);
            if (key.Length > 0 && !isCode(key) && claimed.Add(key))
            {
                result.Add(Pair(key, code));
            }
        }

        foreach (var pair in ordered)
        {
            Claim(pair.Key, pair.Value);
        }

        foreach (var pair in ordered)
        {
            var name = pair.Key;
            var paren = name.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0)
            {
                name = name.Substring(0, paren);
                Claim(name, pair.Value);
            }

            var comma = name.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0)
            {
                Claim(name.Substring(0, comma), pair.Value);
            }
        }

        return result;
    }

    private static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);

    private static AliasTable BuildDefault()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in HandWritten)
        {
            map.TryAdd(LanguageTable.NormalizeKey(pair.Key), pair.Value);
        }

        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in IsoLanguages.All)
        {
            if (e.TwoLetter is not null)
            {
                codes.Add(e.TwoLetter);
            }

            codes.Add(e.ThreeLetterT);
            if (e.ThreeLetterB is not null)
            {
                codes.Add(e.ThreeLetterB);
            }
        }

        foreach (var pair in DeriveNames(IsoLanguages.All, IsoLanguages.AllAlternateNames, codes.Contains))
        {
            map.TryAdd(pair.Key, pair.Value);
        }

        return new AliasTable(new ReadOnlyDictionary<string, string>(map));
    }
}
