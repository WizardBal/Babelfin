using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace Jellyfin.Plugin.Babelfin.Engine.Languages;

/// <summary>
/// The language table: ISO 639-1/2B/2T/3 codes, BCP-47 style tags, English names, track tags (chs, pob, spa-419…)
/// and the variety keywords. Immutable and thread-safe once built.
/// </summary>
public sealed class LanguageTable
{
    // File-name segments that are flags in Jellyfin's external subtitle naming, never languages (".hi." is hearing impaired, not Hindi).
    private static readonly FrozenSet<string> _fileNameFlags = FrozenSet.ToFrozenSet(
        new[] { "hi", "cc", "sdh", "forced", "foreign", "default", "full", "sign", "signs" },
        StringComparer.Ordinal);

    // UN M49 region 419: Central America, the Caribbean and South America. US/CA are outside this group.
    // https://unstats.un.org/unsd/methodology/m49/
    private static readonly FrozenSet<string> _latinAmericanRegions = FrozenSet.ToFrozenSet(
        new[]
        {
            "BZ", "CR", "GT", "HN", "MX", "NI", "PA", "SV",
            "AG", "AI", "AW", "BB", "BL", "BQ", "BS", "CU", "CW", "DM", "DO", "GD", "GP", "HT", "JM",
            "KN", "KY", "LC", "MF", "MQ", "MS", "PR", "SX", "TC", "TT", "VC", "VG", "VI",
            "AR", "BO", "BR", "BV", "CL", "CO", "EC", "FK", "GF", "GS", "GY", "PE", "PY", "SR", "UY", "VE",
            "005", "013", "029",
        },
        StringComparer.Ordinal);

    private readonly FrozenDictionary<string, string> _aliases;
    private readonly FrozenDictionary<string, LanguageTag> _codeIndex;
    private readonly FrozenDictionary<string, LanguageEntry> _byCode;
    private readonly IReadOnlyList<PreparedVarietyKeyword> _keywords;

    /// <summary>Initializes a new instance of the <see cref="LanguageTable"/> class.</summary>
    /// <param name="entries">The language rows. The first row claiming a code wins.</param>
    /// <param name="aliases">Raw spellings to canonical codes. They win over ISO codes and names.</param>
    /// <param name="keywords">Title and file-name keywords that refine generic tags.</param>
    public LanguageTable(IEnumerable<LanguageEntry> entries, AliasTable aliases, VarietyKeywordTable keywords)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(keywords);

        var list = new List<LanguageEntry>(entries);

        var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in aliases.RawToCode)
        {
            var key = NormalizeKey(pair.Key);
            if (key.Length > 0)
            {
                aliasMap.TryAdd(key, (pair.Value ?? string.Empty).Trim());
            }
        }

        // A two-letter code an alias redirects to another language is "shadowed": that language is then known by its three-letter code.
        var shadowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in aliasMap)
        {
            if (pair.Key.Length == 2 && IsAsciiLetters(pair.Key))
            {
                var target = NormalizeKey(pair.Value);
                var dash = target.IndexOf('-', StringComparison.Ordinal);
                var first = dash < 0 ? target : target.Substring(0, dash);
                if (!string.Equals(first, pair.Key, StringComparison.Ordinal))
                {
                    shadowed.Add(pair.Key);
                }
            }
        }

        var rows = new List<(LanguageEntry Entry, LanguageTag Tag)>(list.Count);
        foreach (var e in list)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.ThreeLetterT))
            {
                continue;
            }

            var two = e.TwoLetter?.ToLowerInvariant();
            string baseCode;
            if (two is not null && (e.Variety is not null || !shadowed.Contains(two)))
            {
                baseCode = two;
            }
            else
            {
                baseCode = e.ThreeLetterT.ToLowerInvariant();
            }

            rows.Add((e, new LanguageTag(baseCode, e.Variety, e.Script, e.Region)));
        }

        var codeIndex = new Dictionary<string, LanguageTag>(StringComparer.Ordinal);
        foreach (var (e, tag) in rows)
        {
            if (!e.IsGeneric)
            {
                continue;
            }

            var two = e.TwoLetter?.ToLowerInvariant();
            if (two is not null && !shadowed.Contains(two))
            {
                codeIndex.TryAdd(two, tag);
            }

            codeIndex.TryAdd(e.ThreeLetterT.ToLowerInvariant(), tag);
            if (e.ThreeLetterB is not null)
            {
                codeIndex.TryAdd(e.ThreeLetterB.ToLowerInvariant(), tag);
            }
        }

        foreach (var (e, tag) in rows)
        {
            if (!e.IsGeneric)
            {
                // Varieties ("yue") and rows whose own code no plain row claims (Jellyfin's "frc" = fr-CA).
                codeIndex.TryAdd(e.ThreeLetterT.ToLowerInvariant(), tag);
            }
        }

        var byCode = new Dictionary<string, LanguageEntry>(StringComparer.OrdinalIgnoreCase);
        var codes = new List<string>();
        foreach (var (e, tag) in rows)
        {
            if (byCode.TryAdd(tag.Code, e))
            {
                codes.Add(tag.Code);
            }
        }

        // Names of this table's own rows (a custom table gets its names resolvable too); explicit aliases keep priority.
        foreach (var pair in AliasTable.DeriveNames(list, Array.Empty<KeyValuePair<string, string>>(), codeIndex.ContainsKey))
        {
            aliasMap.TryAdd(pair.Key, pair.Value);
        }

        var prepared = new List<PreparedVarietyKeyword>();
        foreach (var k in keywords.Keywords)
        {
            if (k is null || string.IsNullOrWhiteSpace(k.Keyword))
            {
                continue;
            }

            var p = new PreparedVarietyKeyword(new PreparedKeyword(k.Keyword), k);
            if (p.Keyword.Normalized.Length > 0)
            {
                prepared.Add(p);
            }
        }

        _aliases = aliasMap.ToFrozenDictionary(StringComparer.Ordinal);
        _codeIndex = codeIndex.ToFrozenDictionary(StringComparer.Ordinal);
        _byCode = byCode.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _keywords = prepared.AsReadOnly();
        Codes = new ReadOnlyCollection<string>(codes);
    }

    /// <summary>Gets the built-in table: ISO 639 data from Jellyfin plus Babelfin's additions, <see cref="AliasTable.Default"/> and <see cref="VarietyKeywordTable.Default"/>.</summary>
    public static LanguageTable Default { get; } = new(IsoLanguages.All, AliasTable.Default, VarietyKeywordTable.Default);

    /// <summary>Gets every canonical code the table knows ("ja", "zh-Hans", "yue", "pt-BR"…), in table order, for the UI and API.</summary>
    public IReadOnlyList<string> Codes { get; }

    /// <summary>
    /// Resolves a raw language string. Accepted: ISO 639-1 ("ja"), 639-2/T and /B ("jpn", "chi", "zho"), 639-3 varieties ("cmn", "yue"),
    /// tags such as "xx-YY", "xx-Scrp", "xx-Scrp-YY", "xxx-YY" and "zh-yue" (any case, '_' read as '-'; unknown variant subtags are ignored),
    /// aliases ("chs", "pob", "iw") and English names ("Japanese", "Spanish; Castilian").
    /// "und", "mul", "zxx", "mis", "unknown", empty and white space resolve to <c>null</c>, as does anything unknown.
    /// File-name noise such as ".forced" is not stripped here; see <see cref="ResolveWithHints"/>.
    /// </summary>
    /// <param name="raw">The raw string.</param>
    /// <returns>The tag, or <c>null</c>.</returns>
    public LanguageTag? Resolve(string? raw)
    {
        var key = NormalizeKey(raw);
        if (key.Length == 0)
        {
            return null;
        }

        if (_aliases.TryGetValue(key, out var aliased))
        {
            return aliased.Length == 0 ? null : Parse(NormalizeKey(aliased));
        }

        var tag = Parse(key);
        if (tag is not null)
        {
            // A second look with the canonical spelling: "zho-TW" → "zh-tw" → zh-Hant, "spa-MX" → "es-mx" → es-419.
            var canonical = tag.Code.ToLowerInvariant();
            if (!string.Equals(canonical, key, StringComparison.Ordinal) && _aliases.TryGetValue(canonical, out aliased))
            {
                return aliased.Length == 0 ? null : Parse(NormalizeKey(aliased)) ?? tag;
            }

            return tag;
        }

        if (key.Contains(';', StringComparison.Ordinal))
        {
            foreach (var part in key.Split(';'))
            {
                var partTag = Resolve(part);
                if (partTag is not null)
                {
                    return partTag;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a metadata original-language value. TMDb uses "cn" for Cantonese; ordinary track tags retain their own aliases.
    /// </summary>
    /// <param name="raw">The title's original language from metadata.</param>
    /// <returns>The language tag, or null when unknown.</returns>
    public LanguageTag? ResolveMetadataOriginalLanguage(string? raw) =>
        Resolve(NormalizeKey(raw) == "cn" ? "yue" : raw);

    /// <summary>
    /// Resolves <paramref name="raw"/>, then refines it from the track title and file name.
    /// <list type="bullet">
    /// <item>When <paramref name="raw"/> resolves to nothing, the language comes from the file name's language segment
    /// ("Movie.pt-BR.srt", "movie.chs.srt", "movie.en.forced.srt"; flag segments such as ".hi." and ".forced." are skipped),
    /// else from an English name in the title (und audio titled "Japanese" becomes ja).</item>
    /// <item>A file-name language with the same base fills axes the tag leaves generic ("por" + "Movie.pt-BR.srt" → pt-BR).</item>
    /// <item>Keywords from the <see cref="VarietyKeywordTable"/> found in the title or file name fill the variety, script or region
    /// when the tag is generic on that axis and the keyword applies to the tag's base: "Cantonese" on chi → yue, "Brasil" on por → pt-BR,
    /// "VFQ" on fre → fr-CA. They never override an explicit value (yue + "Mandarin" stays yue), never apply to another base
    /// ("Brasil" on jpn changes nothing), and two keywords that disagree on one axis leave it generic.</item>
    /// </list>
    /// </summary>
    /// <param name="raw">The raw language string.</param>
    /// <param name="title">The track's raw title.</param>
    /// <param name="fileName">The external file name or path.</param>
    /// <returns>The tag, or <c>null</c>.</returns>
    public LanguageTag? ResolveWithHints(string? raw, string? title, string? fileName)
    {
        var tag = Resolve(raw);
        var file = TextMatcher.FileNameOnly(fileName);
        var fileTag = FromFileName(file);
        tag ??= fileTag ?? FromTitle(title);
        if (tag is null)
        {
            return null;
        }

        var variety = tag.Variety;
        var script = tag.Script;
        var region = tag.Region;

        if (fileTag is not null && string.Equals(fileTag.Base, tag.Base, StringComparison.OrdinalIgnoreCase))
        {
            variety ??= fileTag.Variety;
            script ??= fileTag.Script;
            region ??= fileTag.Region;
        }

        if (variety is null || script is null || region is null)
        {
            var texts = new[] { TextMatcher.Normalize(title), TextMatcher.Normalize(file) };
            string? hintVariety = null, hintScript = null, hintRegion = null;
            bool varietyConflict = false, scriptConflict = false, regionConflict = false;
            foreach (var k in _keywords)
            {
                var appliesTo = k.Source.AppliesToBase;
                if (appliesTo != "*" && !string.Equals(appliesTo, tag.Base, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TextMatcher.Contains(texts[0], k.Keyword) && !TextMatcher.Contains(texts[1], k.Keyword))
                {
                    continue;
                }

                Collect(k.Source.Variety, ref hintVariety, ref varietyConflict);
                Collect(k.Source.Script, ref hintScript, ref scriptConflict);
                Collect(k.Source.Region, ref hintRegion, ref regionConflict);
            }

            if (variety is null && !varietyConflict)
            {
                variety = hintVariety;
            }

            if (script is null && !scriptConflict)
            {
                script = hintScript;
            }

            if (region is null && !regionConflict)
            {
                region = hintRegion;
            }
        }

        return variety == tag.Variety && script == tag.Script && region == tag.Region
            ? tag
            : new LanguageTag(tag.Base, variety, script, region);
    }

    /// <summary>
    /// How well <paramref name="actual"/> (a track) satisfies <paramref name="wanted"/>. Each axis (variety, script, region)
    /// is compared on its own and the worst result wins:
    /// <list type="table">
    /// <listheader><term>Case</term><description>Result</description></listheader>
    /// <item><term>different Base</term><description>None (ja vs en)</description></item>
    /// <item><term>wanted generic on the axis</term><description>Exact (zh vs cmn; en vs en-US; zh-Hant vs zh-Hant-HK)</description></item>
    /// <item><term>wanted specific, actual equal</term><description>Exact (yue vs yue)</description></item>
    /// <item><term>wanted specific, actual generic</term><description>Generic (yue vs zh; zh-Hant vs zh; pt-BR vs pt; zh-Hant-HK vs zh-Hant; en-US vs en)</description></item>
    /// <item><term>wanted specific, actual different</term><description>None (yue vs cmn; zh-Hant vs zh-Hans; pt-BR vs pt-PT; es-419 vs es-ES)</description></item>
    /// </list>
    /// </summary>
    /// <param name="wanted">The wanted language.</param>
    /// <param name="actual">The track's language.</param>
    /// <returns>The match quality.</returns>
    public MatchQuality Match(LanguageTag wanted, LanguageTag actual)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(actual);
        if (!string.Equals(wanted.Base, actual.Base, StringComparison.OrdinalIgnoreCase))
        {
            return MatchQuality.None;
        }

        var q = Axis(wanted.Variety, actual.Variety);
        q = Min(q, Axis(wanted.Script, actual.Script));
        q = Min(q, Axis(wanted.Region, actual.Region));
        return q;
    }

    /// <summary>
    /// The English name of a tag ("Japanese", "Chinese (Cantonese)", "Portuguese (Brazil)"). A tag the table has no row for
    /// is named after its nearest row plus the extra subtags ("French (BE)", "Chinese (Traditional, MO)"); an unknown base gives <c>null</c>.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The English name, or <c>null</c>.</returns>
    public string? EnglishName(LanguageTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (_byCode.TryGetValue(tag.Code, out var exact))
        {
            return exact.EnglishName;
        }

        // Nearest row: drop region, then script, then variety.
        var candidates = new[]
        {
            new LanguageTag(tag.Base, tag.Variety, tag.Script, null),
            new LanguageTag(tag.Base, tag.Variety, null, tag.Region),
            new LanguageTag(tag.Base, tag.Variety, null, null),
            new LanguageTag(tag.Base, null, tag.Script, tag.Region),
            new LanguageTag(tag.Base, null, tag.Script, null),
            new LanguageTag(tag.Base, null, null, tag.Region),
            new LanguageTag(tag.Base, null, null, null),
        };

        foreach (var candidate in candidates)
        {
            if (!_byCode.TryGetValue(candidate.Code, out var near))
            {
                continue;
            }

            var extra = new List<string>();
            if (tag.Variety is not null && candidate.Variety is null)
            {
                extra.Add(tag.Variety);
            }

            if (tag.Script is not null && candidate.Script is null)
            {
                extra.Add(tag.Script);
            }

            if (tag.Region is not null && candidate.Region is null)
            {
                extra.Add(tag.Region);
            }

            var name = near.EnglishName;
            var joined = string.Join(", ", extra);
            return name.EndsWith(')')
                ? name.Substring(0, name.Length - 1) + ", " + joined + ")"
                : name + " (" + joined + ")";
        }

        return null;
    }

    /// <summary>Normalises a lookup key: trimmed, lower-case, NFC, '_' as '-', runs of white space as one space.</summary>
    /// <param name="raw">The raw key.</param>
    /// <returns>The key; empty for <c>null</c> or white space.</returns>
    internal static string NormalizeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var s = raw.Trim();
        if (!s.IsNormalized(NormalizationForm.FormC))
        {
            s = s.Normalize(NormalizationForm.FormC);
        }

        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c == '_' ? '-' : char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    private static MatchQuality Axis(string? wanted, string? actual)
    {
        if (wanted is null)
        {
            return MatchQuality.Exact;
        }

        if (actual is null)
        {
            return MatchQuality.Generic;
        }

        return string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase) ? MatchQuality.Exact : MatchQuality.None;
    }

    private static MatchQuality Min(MatchQuality a, MatchQuality b) => a < b ? a : b;

    private static void Collect(string? value, ref string? hint, ref bool conflict)
    {
        if (value is null)
        {
            return;
        }

        if (hint is null)
        {
            hint = value;
        }
        else if (!string.Equals(hint, value, StringComparison.OrdinalIgnoreCase))
        {
            conflict = true;
        }
    }

    private static bool IsAsciiLetters(string s)
    {
        foreach (var c in s)
        {
            if (c is < 'a' or > 'z')
            {
                return false;
            }
        }

        return s.Length > 0;
    }

    private static bool IsDigits(string s)
    {
        foreach (var c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return s.Length > 0;
    }

    // Structural parse of a normalised key (no alias lookup).
    private LanguageTag? Parse(string key)
    {
        if (key.Length == 0)
        {
            return null;
        }

        var parts = key.Split('-');
        var first = parts[0];
        if (first.Length is < 2 or > 3 || !IsAsciiLetters(first) || !_codeIndex.TryGetValue(first, out var start))
        {
            return null;
        }

        var variety = start.Variety;
        var script = start.Script;
        var region = start.Region;
        for (var i = 1; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 3 && IsAsciiLetters(p))
            {
                if (variety is null
                    && _codeIndex.TryGetValue(p, out var v)
                    && v.Variety is not null
                    && string.Equals(v.Base, start.Base, StringComparison.Ordinal))
                {
                    variety = v.Variety;
                    continue;
                }

                break;
            }

            if (p.Length == 4 && IsAsciiLetters(p))
            {
                script ??= char.ToUpperInvariant(p[0]) + p.Substring(1);
                continue;
            }

            if ((p.Length == 2 && IsAsciiLetters(p)) || (p.Length == 3 && IsDigits(p)))
            {
                region ??= p.ToUpperInvariant();
                continue;
            }

            // Variants, extensions and private-use subtags are ignored.
            break;
        }

        if (start.Base == "es" && region is not null && _latinAmericanRegions.Contains(region))
        {
            region = "419";
        }

        return new LanguageTag(start.Base, variety, script, region);
    }

    private LanguageTag? FromFileName(string? file)
    {
        if (string.IsNullOrEmpty(file))
        {
            return null;
        }

        var segments = file.Split('.');
        for (var i = segments.Length - 2; i >= 1; i--)
        {
            var seg = NormalizeKey(segments[i]);
            if (seg.Length == 0 || _fileNameFlags.Contains(seg))
            {
                continue;
            }

            var tag = Resolve(seg);
            if (tag is not null)
            {
                return tag;
            }
        }

        return null;
    }

    private LanguageTag? FromTitle(string? title)
    {
        var key = NormalizeKey(title);
        if (key.Length == 0)
        {
            return null;
        }

        // Only names (four letters or more), never bare codes: a title "AD" or "Hi" must not become a language.
        if (key.Length >= 4 && _aliases.TryGetValue(key, out var whole) && whole.Length > 0)
        {
            return Parse(NormalizeKey(whole));
        }

        var start = -1;
        for (var i = 0; i <= key.Length; i++)
        {
            var isLetter = i < key.Length && char.IsLetter(key[i]) && !TextMatcher.IsCjk(key[i]);
            if (isLetter)
            {
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (start >= 0 && i - start >= 4)
            {
                var word = key.Substring(start, i - start);
                if (_aliases.TryGetValue(word, out var code) && code.Length > 0)
                {
                    var tag = Parse(NormalizeKey(code));
                    if (tag is not null)
                    {
                        return tag;
                    }
                }
            }

            start = -1;
        }

        return null;
    }

    private sealed record PreparedVarietyKeyword(PreparedKeyword Keyword, VarietyKeyword Source);
}
