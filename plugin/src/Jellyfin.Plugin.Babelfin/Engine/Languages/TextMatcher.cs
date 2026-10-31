using System;
using System.Text;

namespace Jellyfin.Plugin.Babelfin.Engine.Languages;

/// <summary>
/// Keyword matching shared by the language hints and the track classifier.
/// Text and keywords are normalised the same way (lower-case, NFC, '_', '.', '-' and white space become one space,
/// curly apostrophes become '). Latin-script keywords match whole words only: the characters just outside the match
/// must not be letters or digits. Keywords containing CJK characters match as plain substrings.
/// </summary>
internal static class TextMatcher
{
    /// <summary>Normalises text or a keyword for matching.</summary>
    /// <param name="text">The raw text.</param>
    /// <returns>The normalised text; empty for <c>null</c>.</returns>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!text.IsNormalized(NormalizationForm.FormC))
        {
            text = text.Normalize(NormalizationForm.FormC);
        }

        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var raw in text)
        {
            var c = char.ToLowerInvariant(raw);
            if (c is '’' or '‘' or 'ʼ' or '`' or '´')
            {
                c = '\'';
            }

            if (char.IsWhiteSpace(c) || c is '_' or '.' or '-' or '‐' or '‑' or '‒' or '–' or '—')
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Returns the part of a path after the last '/' or '\'.</summary>
    /// <param name="path">A file name or path.</param>
    /// <returns>The file name, or <c>null</c>.</returns>
    internal static string? FileNameOnly(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var cut = path.LastIndexOfAny(new[] { '/', '\\' });
        return cut < 0 ? path : path.Substring(cut + 1);
    }

    /// <summary>Whether a character belongs to a CJK script (Han, kana, Hangul, Bopomofo, CJK punctuation).</summary>
    /// <param name="c">The character.</param>
    /// <returns><c>true</c> for CJK.</returns>
    internal static bool IsCjk(char c) =>
        (c >= '⺀' && c <= '⿟')
        || (c >= '　' && c <= 'ㇿ')
        || (c >= '㐀' && c <= '䶿')
        || (c >= '一' && c <= '鿿')
        || (c >= '가' && c <= '힯')
        || (c >= '豈' && c <= '﫿')
        || (c >= 'ｦ' && c <= 'ﾟ');

    /// <summary>Whether a string contains any CJK character.</summary>
    /// <param name="s">The string.</param>
    /// <returns><c>true</c> when it does.</returns>
    internal static bool ContainsCjk(string s)
    {
        foreach (var c in s)
        {
            if (IsCjk(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a character counts as part of a Latin-script word (letters and digits outside CJK).</summary>
    /// <param name="c">The character.</param>
    /// <returns><c>true</c> for a word character.</returns>
    internal static bool IsWordChar(char c) => char.IsLetterOrDigit(c) && !IsCjk(c);

    /// <summary>Whether normalised text contains a prepared keyword under the matching rules.</summary>
    /// <param name="normalizedText">Text from <see cref="Normalize"/>.</param>
    /// <param name="keyword">The keyword.</param>
    /// <returns><c>true</c> on a match.</returns>
    internal static bool Contains(string normalizedText, PreparedKeyword keyword)
    {
        var k = keyword.Normalized;
        if (k.Length == 0 || normalizedText.Length < k.Length)
        {
            return false;
        }

        if (keyword.IsCjk)
        {
            return normalizedText.Contains(k, StringComparison.Ordinal);
        }

        var checkBefore = IsWordChar(k[0]);
        var checkAfter = IsWordChar(k[k.Length - 1]);
        var start = 0;
        while (start <= normalizedText.Length - k.Length)
        {
            var i = normalizedText.IndexOf(k, start, StringComparison.Ordinal);
            if (i < 0)
            {
                return false;
            }

            var end = i + k.Length;
            var okBefore = !checkBefore || i == 0 || !IsWordChar(normalizedText[i - 1]);
            var okAfter = !checkAfter || end == normalizedText.Length || !IsWordChar(normalizedText[end]);
            if (okBefore && okAfter)
            {
                return true;
            }

            start = i + 1;
        }

        return false;
    }
}

/// <summary>A keyword normalised once for repeated matching.</summary>
internal sealed class PreparedKeyword
{
    /// <summary>Initializes a new instance of the <see cref="PreparedKeyword"/> class.</summary>
    /// <param name="original">The keyword as listed in its table.</param>
    internal PreparedKeyword(string original)
    {
        Original = original;
        Normalized = TextMatcher.Normalize(original);
        IsCjk = TextMatcher.ContainsCjk(Normalized);
    }

    /// <summary>Gets the keyword as listed in its table (used in reasons).</summary>
    internal string Original { get; }

    /// <summary>Gets the normalised keyword.</summary>
    internal string Normalized { get; }

    /// <summary>Gets a value indicating whether the keyword contains CJK characters (substring matching).</summary>
    internal bool IsCjk { get; }
}
