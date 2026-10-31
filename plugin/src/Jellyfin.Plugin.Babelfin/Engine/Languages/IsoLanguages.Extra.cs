using System.Collections.Generic;

namespace Jellyfin.Plugin.Babelfin.Engine.Languages;

/// <summary>The built-in ISO 639 language data (hand-written part: entries Jellyfin's table lacks).</summary>
internal static partial class IsoLanguages
{
    /// <summary>
    /// Hand-added entries: Chinese spoken varieties (ISO 639-3), script forms, a few regional forms,
    /// and languages missing from Jellyfin's table.
    /// </summary>
    internal static readonly LanguageEntry[] ExtraEntries =
    {
        // Chinese spoken varieties: Base "zh", the 639-3 code in ThreeLetterT and Variety.
        new LanguageEntry("zh", "cmn", null, "Chinese (Mandarin)", Variety: "cmn"),
        new LanguageEntry("zh", "yue", null, "Chinese (Cantonese)", Variety: "yue"),
        new LanguageEntry("zh", "nan", null, "Chinese (Min Nan)", Variety: "nan"),
        new LanguageEntry("zh", "hak", null, "Chinese (Hakka)", Variety: "hak"),
        new LanguageEntry("zh", "wuu", null, "Chinese (Wu)", Variety: "wuu"),
        new LanguageEntry("zh", "gan", null, "Chinese (Gan)", Variety: "gan"),

        // Scripts.
        new LanguageEntry("sr", "srp", null, "Serbian (Latin)", Script: "Latn"),
        new LanguageEntry("sr", "srp", null, "Serbian (Cyrillic)", Script: "Cyrl"),
        new LanguageEntry("uz", "uzb", null, "Uzbek (Latin)", Script: "Latn"),
        new LanguageEntry("uz", "uzb", null, "Uzbek (Cyrillic)", Script: "Cyrl"),
        new LanguageEntry("pa", "pan", null, "Panjabi (Shahmukhi)", Script: "Arab"),
        new LanguageEntry("pa", "pan", null, "Panjabi (Gurmukhi)", Script: "Guru"),

        // Regions the table lacks but people pick.
        new LanguageEntry("es", "spa", null, "Spanish (Spain)", Region: "ES"),
        new LanguageEntry("fr", "fra", "fre", "French (France)", Region: "FR"),
        new LanguageEntry("en", "eng", null, "English (United States)", Region: "US"),
        new LanguageEntry("en", "eng", null, "English (United Kingdom)", Region: "GB"),

        // Languages missing from Jellyfin's ISO 639-2 table (Filipino and Scots are already in it).
        new LanguageEntry(null, "cnr", null, "Montenegrin"),
        // Keep the macrolanguage distinct: an ambiguous sh tag does not establish Serbian, Croatian or Bosnian.
        new LanguageEntry("sh", "hbs", null, "Serbo-Croatian"),
    };

    /// <summary>Gets every built-in entry: the generated table followed by <see cref="ExtraEntries"/>.</summary>
    internal static IReadOnlyList<LanguageEntry> All => Holder.All;

    /// <summary>Gets every built-in alternate English name as (name, code) pairs.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> AllAlternateNames => Holder.Alternates;

    // A nested holder so the combined lists are built after both partial files' fields are initialised.
    private static class Holder
    {
        internal static readonly IReadOnlyList<LanguageEntry> All = BuildAll();

        internal static readonly IReadOnlyList<KeyValuePair<string, string>> Alternates = BuildAlternates();

        private static IReadOnlyList<LanguageEntry> BuildAll()
        {
            var list = new List<LanguageEntry>(Entries.Length + ExtraEntries.Length);
            foreach (var entry in Entries)
            {
                // ISO/IANA place Bokmal and Nynorsk under Norwegian; keep their distinct variety axes.
                list.Add(entry.ThreeLetterT is "nob" or "nno"
                    ? entry with { TwoLetter = "no", Variety = entry.ThreeLetterT }
                    : entry);
            }
            list.AddRange(ExtraEntries);
            return list.AsReadOnly();
        }

        private static IReadOnlyList<KeyValuePair<string, string>> BuildAlternates()
        {
            var list = new List<KeyValuePair<string, string>>(AlternateNames.Length);
            foreach (var pair in AlternateNames)
            {
                list.Add(new KeyValuePair<string, string>(pair[0], pair[1]));
            }

            return list.AsReadOnly();
        }
    }
}
