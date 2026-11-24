namespace Aldanma.Core.Identifiers;

/// <summary>Kinds of identifiers that can be looked up and reported.</summary>
public static class IdentifierKind
{
    public const string Phone = "phone";
    public const string Iban = "iban";
    /// <summary>A registrable domain or a host on a shared hosting platform.</summary>
    public const string Domain = "domain";
    /// <summary>A path-specific address on a shortener or path-based platform (bit.ly/abc, sites.google.com/view/x).</summary>
    public const string Url = "url";

    public static readonly IReadOnlyList<string> All = [Phone, Iban, Domain, Url];
}

/// <summary>
/// A normalised identifier. <see cref="Value"/> is the canonical form used as the lookup key;
/// <see cref="Display"/> is the human-friendly form of the same value.
/// </summary>
public sealed record Identifier(string Kind, string Value, string Display)
{
    /// <summary>True for a phone short code such as 2828 or 112.</summary>
    public bool IsShortCode => Kind == IdentifierKind.Phone && Value.StartsWith(PhoneNumber.ShortCodePrefix, StringComparison.Ordinal);

    /// <summary>Host part of a domain or URL identifier; null for phones and IBANs.</summary>
    public string? Host => Kind is IdentifierKind.Domain or IdentifierKind.Url ? Value.Split('/')[0] : null;

    /// <summary>Masked form used whenever the identifier is shown to someone who did not type it.</summary>
    public string Masked => Kind switch
    {
        IdentifierKind.Phone => MaskPhone(Value),
        IdentifierKind.Iban => Value.Length > 8 ? Value[..4] + " **** **** " + Value[^4..] : Value,
        _ => Value,
    };

    private static string MaskPhone(string e164)
    {
        if (e164.Length < 7) return e164;
        return e164[..^7] + " *** " + e164[^4..^2] + " " + e164[^2..];
    }
}
