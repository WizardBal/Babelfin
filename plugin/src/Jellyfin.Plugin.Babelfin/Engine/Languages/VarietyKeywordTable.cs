using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Babelfin.Engine.Languages;

/// <summary>
/// One title or file-name keyword that refines a generic language tag (see <see cref="LanguageTable.ResolveWithHints"/>).
/// Latin-script keywords match whole words; CJK keywords match as substrings.
/// </summary>
/// <param name="Keyword">The text to look for ("Cantonese", "粵語", "VFQ").</param>
/// <param name="AppliesToBase">The base language it refines ("zh", "pt", "es", "fr"), or "*" for any.</param>
/// <param name="Variety">The variety it sets, or <c>null</c>.</param>
/// <param name="Script">The script it sets, or <c>null</c>.</param>
/// <param name="Region">The region it sets, or <c>null</c>.</param>
public sealed record VarietyKeyword(string Keyword, string AppliesToBase, string? Variety, string? Script, string? Region);

/// <summary>The editable list of variety, script and region keywords.</summary>
/// <param name="Keywords">The keywords.</param>
public sealed record VarietyKeywordTable(IReadOnlyList<VarietyKeyword> Keywords)
{
    /// <summary>
    /// Gets the built-in keywords: Cantonese/粵語/粤语 → yue, Mandarin/國語/国语/普通话 → cmn, Simplified/简体/简中/chs → Hans,
    /// Traditional/繁體/繁中/cht → Hant, Brasil/Brazil/BR → pt-BR, Portugal → pt-PT, Latino/LatAm/Latin America → es-419,
    /// Castellano/España/Castilian → es-ES, VFQ/Québec/Quebec → fr-CA, VFF/France → fr-FR, Latin/Cyrillic for Serbian and Uzbek.
    /// </summary>
    public static VarietyKeywordTable Default { get; } = new(Array.AsReadOnly(new[]
    {
        // Chinese spoken varieties.
        new VarietyKeyword("Cantonese", "zh", "yue", null, null),
        new VarietyKeyword("粵語", "zh", "yue", null, null),
        new VarietyKeyword("粤语", "zh", "yue", null, null),
        new VarietyKeyword("廣東話", "zh", "yue", null, null),
        new VarietyKeyword("广东话", "zh", "yue", null, null),
        new VarietyKeyword("Mandarin", "zh", "cmn", null, null),
        new VarietyKeyword("國語", "zh", "cmn", null, null),
        new VarietyKeyword("国语", "zh", "cmn", null, null),
        new VarietyKeyword("普通话", "zh", "cmn", null, null),
        new VarietyKeyword("普通話", "zh", "cmn", null, null),
        new VarietyKeyword("華語", "zh", "cmn", null, null),
        new VarietyKeyword("华语", "zh", "cmn", null, null),
        new VarietyKeyword("Hokkien", "zh", "nan", null, null),
        new VarietyKeyword("閩南語", "zh", "nan", null, null),
        new VarietyKeyword("闽南语", "zh", "nan", null, null),
        new VarietyKeyword("Hakka", "zh", "hak", null, null),
        new VarietyKeyword("客家話", "zh", "hak", null, null),

        // Chinese scripts.
        new VarietyKeyword("Simplified", "zh", null, "Hans", null),
        new VarietyKeyword("简体", "zh", null, "Hans", null),
        new VarietyKeyword("簡體", "zh", null, "Hans", null),
        new VarietyKeyword("简中", "zh", null, "Hans", null),
        new VarietyKeyword("簡中", "zh", null, "Hans", null),
        new VarietyKeyword("chs", "zh", null, "Hans", null),
        new VarietyKeyword("Traditional", "zh", null, "Hant", null),
        new VarietyKeyword("繁體", "zh", null, "Hant", null),
        new VarietyKeyword("繁体", "zh", null, "Hant", null),
        new VarietyKeyword("繁中", "zh", null, "Hant", null),
        new VarietyKeyword("cht", "zh", null, "Hant", null),

        // Portuguese.
        new VarietyKeyword("Brasil", "pt", null, null, "BR"),
        new VarietyKeyword("Brazil", "pt", null, null, "BR"),
        new VarietyKeyword("Brazilian", "pt", null, null, "BR"),
        new VarietyKeyword("Brasileiro", "pt", null, null, "BR"),
        new VarietyKeyword("BR", "pt", null, null, "BR"),
        new VarietyKeyword("Portugal", "pt", null, null, "PT"),
        new VarietyKeyword("Europeu", "pt", null, null, "PT"),

        // Spanish.
        new VarietyKeyword("Latino", "es", null, null, "419"),
        new VarietyKeyword("LatAm", "es", null, null, "419"),
        new VarietyKeyword("Latin America", "es", null, null, "419"),
        new VarietyKeyword("Latinoamérica", "es", null, null, "419"),
        new VarietyKeyword("Latinoamericano", "es", null, null, "419"),
        new VarietyKeyword("Castellano", "es", null, null, "ES"),
        new VarietyKeyword("España", "es", null, null, "ES"),
        new VarietyKeyword("Castilian", "es", null, null, "ES"),
        new VarietyKeyword("Latin", "es", null, null, "419"),
        new VarietyKeyword("Spain", "es", null, null, "ES"),

        // French.
        new VarietyKeyword("VFQ", "fr", null, null, "CA"),
        new VarietyKeyword("Québec", "fr", null, null, "CA"),
        new VarietyKeyword("Quebec", "fr", null, null, "CA"),
        new VarietyKeyword("Québécois", "fr", null, null, "CA"),
        new VarietyKeyword("Canada", "fr", null, null, "CA"),
        new VarietyKeyword("VFF", "fr", null, null, "FR"),
        new VarietyKeyword("France", "fr", null, null, "FR"),

        // Serbian and Uzbek scripts.
        new VarietyKeyword("Latin", "sr", null, "Latn", null),
        new VarietyKeyword("Latinica", "sr", null, "Latn", null),
        new VarietyKeyword("Cyrillic", "sr", null, "Cyrl", null),
        new VarietyKeyword("Ćirilica", "sr", null, "Cyrl", null),
        new VarietyKeyword("Ћирилица", "sr", null, "Cyrl", null),
        new VarietyKeyword("Latin", "uz", null, "Latn", null),
        new VarietyKeyword("Cyrillic", "uz", null, "Cyrl", null),
    }));
}
