using System.Numerics;
using System.Text;

namespace Aldanma.Core.Identifiers;

/// <summary>IBAN parsing and ISO 13616 mod-97 validation.</summary>
public static class Iban
{
    /// <summary>Strips spaces and dashes and upper-cases. Returns null when the shape is not IBAN-like.</summary>
    public static string? Normalize(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (ch is ' ' or '-' or ' ' or '\t') continue;
            if (ch is >= 'a' and <= 'z') sb.Append((char)(ch - 32));
            else if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9') sb.Append(ch);
            else return null;
        }
        var s = sb.ToString();
        if (s.Length < 15 || s.Length > 34) return null;
        if (!char.IsAsciiLetterUpper(s[0]) || !char.IsAsciiLetterUpper(s[1]) || !char.IsAsciiDigit(s[2]) || !char.IsAsciiDigit(s[3])) return null;
        return s;
    }

    /// <summary>True when the length matches the country and the mod-97 check passes.</summary>
    public static bool IsValid(string normalized, IReadOnlyDictionary<string, int> lengths)
    {
        var country = normalized[..2];
        if (lengths.TryGetValue(country, out var expected) && expected != normalized.Length) return false;
        var rearranged = normalized[4..] + normalized[..4];
        var digits = new StringBuilder(rearranged.Length * 2);
        foreach (var ch in rearranged)
        {
            if (char.IsAsciiDigit(ch)) digits.Append(ch);
            else digits.Append(ch - 'A' + 10);
        }
        return BigInteger.Parse(digits.ToString()) % 97 == 1;
    }

    /// <summary>Five-digit bank code of a Turkish IBAN (TRkk BBBBB R ...).</summary>
    public static string? TurkishBankCode(string normalized) =>
        normalized.StartsWith("TR", StringComparison.Ordinal) && normalized.Length == 26 ? normalized[4..9] : null;

    /// <summary>Groups of four for display: "TR33 0006 1005 ...".</summary>
    public static string Format(string normalized)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < normalized.Length; i += 4)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(normalized, i, Math.Min(4, normalized.Length - i));
        }
        return sb.ToString();
    }
}
