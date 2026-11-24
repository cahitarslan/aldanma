using System.Text;

namespace Aldanma.Core.Identifiers;

/// <summary>Kind of a phone number from a Turkish point of view.</summary>
public enum PhoneType
{
    Mobile,
    Landline,
    NonGeographic,
    TollFree,
    Premium,
    BusinessShort,
    ShortCode,
    Foreign,
}

public sealed record PhoneInfo(string E164, PhoneType Type, string Display, string? CountryCode, string? AreaCode);

/// <summary>Normalises phone numbers to E.164 (+90...) and classifies them. No network calls.</summary>
public static class PhoneNumber
{
    /// <summary>Short codes (3-6 digits) have no E.164 form, so they are keyed as "sc:" + digits.</summary>
    public const string ShortCodePrefix = "sc:";

    /// <summary>Parses a phone number written in any common Turkish or international form. Returns null when it is not a phone number.</summary>
    public static PhoneInfo? Parse(string input)
    {
        var digits = new StringBuilder();
        var plus = false;
        foreach (var ch in input.Trim())
        {
            if (char.IsAsciiDigit(ch)) digits.Append(ch);
            else if (ch == '+' && digits.Length == 0 && !plus) plus = true;
            else if (ch is ' ' or '-' or '(' or ')' or '.' or '/' or ' ') continue;
            else return null;
        }
        var d = digits.ToString();
        if (d.Length == 0) return null;

        if (!plus && d.StartsWith("00", StringComparison.Ordinal)) { plus = true; d = d[2..]; }

        if (plus)
        {
            if (d.StartsWith("90", StringComparison.Ordinal)) return National(d[2..]);
            if (d.Length < 7 || d.Length > 15) return null;
            var cc = CountryCode(d);
            return new PhoneInfo("+" + d, PhoneType.Foreign, "+" + cc + " " + GroupDigits(d[cc.Length..]), cc, null);
        }

        // Short codes and 444 lines
        if (d.Length is >= 3 and <= 6 && !d.StartsWith('0')) return new PhoneInfo(ShortCodePrefix + d, PhoneType.ShortCode, d, null, null);
        if (d.Length == 7 && d.StartsWith("444", StringComparison.Ordinal))
            return new PhoneInfo("+90" + d, PhoneType.BusinessShort, "444 " + d[3] + " " + d[4..], null, null);

        if (d.StartsWith('0')) d = d[1..];
        return National(d);
    }

    private static PhoneInfo? National(string n)
    {
        if (n.Length == 7 && n.StartsWith("444", StringComparison.Ordinal))
            return new PhoneInfo("+90" + n, PhoneType.BusinessShort, "444 " + n[3] + " " + n[4..], null, null);
        if (n.Length != 10) return null;
        var display = "0" + n[..3] + " " + n[3..6] + " " + n[6..8] + " " + n[8..];
        var type = n[0] switch
        {
            '5' => PhoneType.Mobile,
            '2' or '3' or '4' => PhoneType.Landline,
            '8' when n.StartsWith("850", StringComparison.Ordinal) => PhoneType.NonGeographic,
            '8' when n.StartsWith("800", StringComparison.Ordinal) => PhoneType.TollFree,
            '9' when n.StartsWith("900", StringComparison.Ordinal) => PhoneType.Premium,
            _ => (PhoneType?)null,
        };
        if (type is null) return null;
        return new PhoneInfo("+90" + n, type.Value, display, "90", type == PhoneType.Landline ? n[..3] : null);
    }

    // "8031234567" -> "803 123 4567": groups of three, the last group takes the remainder.
    private static string GroupDigits(string rest)
    {
        var parts = new List<string>();
        var i = 0;
        while (rest.Length - i > 4) { parts.Add(rest.Substring(i, 3)); i += 3; }
        parts.Add(rest[i..]);
        return string.Join(' ', parts);
    }

    // ITU country codes are prefix-free: try 1, 2 and 3 digit prefixes against the known zones.
    private static string CountryCode(string d)
    {
        if (d[0] is '1' or '7') return d[..1];
        string[] twoDigit = ["20", "27", "30", "31", "32", "33", "34", "36", "39", "40", "41", "43", "44", "45", "46", "47", "48", "49",
            "51", "52", "53", "54", "55", "56", "57", "58", "60", "61", "62", "63", "64", "65", "66", "81", "82", "84", "86", "90", "91", "92", "93", "94", "95", "98"];
        var two = d[..2];
        return Array.IndexOf(twoDigit, two) >= 0 ? two : d[..Math.Min(3, d.Length)];
    }
}
