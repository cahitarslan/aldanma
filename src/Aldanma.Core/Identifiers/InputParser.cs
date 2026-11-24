using System.Text.RegularExpressions;
using Aldanma.Core.Messages;

namespace Aldanma.Core.Identifiers;

/// <summary>What the user pasted, as far as the parser can tell.</summary>
public static class InputMode
{
    public const string Phone = "phone";
    public const string Iban = "iban";
    public const string Link = "link";
    /// <summary>A message, a listing or any other free text. Identifiers inside it are extracted.</summary>
    public const string Text = "text";
}

public sealed record ParsedInput(string Mode, IReadOnlyList<Identifier> Identifiers);

/// <summary>
/// Turns whatever the user pasted (a phone number, an IBAN, a link, or a whole message or listing)
/// into normalised identifiers. Pure and network-free.
/// </summary>
public sealed partial class InputParser(MessageEngine engine, IdentifierRules rules)
{
    public IdentifierRules Rules => rules;

    private const int MaxIdentifiers = 10;

    /// <summary>Longest input accepted where exactly one identifier is expected.</summary>
    public const int MaxSingleInputLength = 300;

    [GeneratedRegex(@"(?<![A-Za-z0-9])TR ?\d{2}(?: ?\d{4}){5} ?\d{2}(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex TurkishIbanRx();

    [GeneratedRegex(@"(?<![0-9+])(?:\+|00)[1-9][0-9 ()\-]{7,18}[0-9](?![0-9])")]
    private static partial Regex InternationalPhoneRx();

    [GeneratedRegex(@"(?<![0-9])(?:0 ?)?\(?(?:5\d{2}|[2-4]\d{2}|850|800|900)\)? ?\d{3}[ \-]?\d{2}[ \-]?\d{2}(?![0-9])")]
    private static partial Regex NationalPhoneRx();

    [GeneratedRegex(@"(?<![0-9])444[ \-]?\d[ \-]?\d{3}(?![0-9])")]
    private static partial Regex BusinessLineRx();

    public static InputParser Default() => new(MessageEngine.Default(), IdentifierRules.Embedded());

    public ParsedInput Parse(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return new ParsedInput(InputMode.Text, []);

        if (!text.Any(char.IsLetter) && PhoneNumber.Parse(text) is { } phone)
            return new ParsedInput(InputMode.Phone, [FromPhone(phone)]);

        if (Iban.Normalize(text) is { } iban && (iban.StartsWith("TR", StringComparison.Ordinal) || !text.Contains(' ') || text.Length <= 42))
            return new ParsedInput(InputMode.Iban, [FromIban(iban)]);

        if (!text.Any(char.IsWhiteSpace))
        {
            var link = engine.ParseLink(text) ?? engine.ParseLink("https://" + text);
            if (link is not null) return new ParsedInput(InputMode.Link, [FromLink(link)]);
        }

        return new ParsedInput(InputMode.Text, ExtractAll(text));
    }

    /// <summary>
    /// Parses input that must hold exactly one phone number, IBAN or link (a lookup item, a report entry, a dispute).
    /// Returns null for free text, for several identifiers, or for input longer than <see cref="MaxSingleInputLength"/>.
    /// </summary>
    public Identifier? ParseSingle(string? input)
    {
        if (input is not { Length: > 0 and <= MaxSingleInputLength }) return null;
        var parsed = Parse(input);
        return parsed.Mode != InputMode.Text && parsed.Identifiers.Count == 1 ? parsed.Identifiers[0] : null;
    }

    /// <summary>All identifiers inside a free text, de-duplicated, in order of appearance.</summary>
    public IReadOnlyList<Identifier> ExtractAll(string text)
    {
        var found = new List<(int Index, Identifier Id)>();
        foreach (Match m in TurkishIbanRx().Matches(text))
        {
            var n = Iban.Normalize(m.Value);
            if (n is not null) found.Add((m.Index, FromIban(n)));
        }
        var masked = TurkishIbanRx().Replace(text, x => new string(' ', x.Length));
        foreach (var rx in new[] { InternationalPhoneRx(), NationalPhoneRx(), BusinessLineRx() })
        {
            foreach (Match m in rx.Matches(masked))
            {
                if (PhoneNumber.Parse(m.Value) is { } p) found.Add((m.Index, FromPhone(p)));
            }
            masked = rx.Replace(masked, x => new string(' ', x.Length));
        }
        var offset = 0;
        foreach (var link in engine.ExtractLinks(text))
        {
            var at = text.IndexOf(link.Host, offset, StringComparison.OrdinalIgnoreCase);
            found.Add((at < 0 ? int.MaxValue : at, FromLink(link)));
            if (at >= 0) offset = at;
        }
        return [.. found.OrderBy(f => f.Index).Select(f => f.Id).DistinctBy(i => (i.Kind, i.Value)).Take(MaxIdentifiers)];
    }

    /// <summary>
    /// Replaces every phone number and IBAN in a text with its masked form, so a message can be kept for review
    /// without the numbers in it. Links stay: they are public and are what a reviewer needs to see.
    /// </summary>
    public static string MaskIdentifiers(string text)
    {
        var masked = TurkishIbanRx().Replace(text, m => Iban.Normalize(m.Value) is { } n ? FromIban(n).Masked : m.Value);
        foreach (var rx in new[] { InternationalPhoneRx(), NationalPhoneRx(), BusinessLineRx() })
            masked = rx.Replace(masked, m => PhoneNumber.Parse(m.Value) is { } p ? FromPhone(p).Masked : m.Value);
        return masked;
    }

    public static Identifier FromPhone(PhoneInfo p) => new(IdentifierKind.Phone, p.E164, p.Display);

    public static Identifier FromIban(string normalized) => new(IdentifierKind.Iban, normalized, Iban.Format(normalized));

    /// <summary>
    /// Reduces a link to the key reports are stored under: the registrable domain, the full host on shared
    /// hosting platforms (so one bad site does not taint the whole platform), or host + path on shorteners.
    /// </summary>
    public Identifier FromLink(MessageEngine.ParsedLink link)
    {
        var host = link.Host;
        if (link.Ip) return new Identifier(IdentifierKind.Domain, host, host);

        if (host == "wa.me")
        {
            // wa.me/905321234567 is just a phone number in disguise.
            var digits = link.Path.Split(['?', '#'])[0].Trim('/');
            if (digits.Length > 0 && digits.All(char.IsAsciiDigit) && PhoneNumber.Parse("+" + digits) is { } waPhone) return FromPhone(waPhone);
        }
        if (rules.Shorteners.Contains(host) || rules.PathHosts.Contains(host))
        {
            var take = rules.PathHosts.Contains(host) ? 2 : 1;
            var pathPart = string.Join('/', link.Path.Split(['?', '#'])[0].Split('/', StringSplitOptions.RemoveEmptyEntries).Take(take));
            if (pathPart.Length > 0)
            {
                var value = host + "/" + pathPart;
                return new Identifier(IdentifierKind.Url, value, value);
            }
            return new Identifier(IdentifierKind.Domain, host, host);
        }

        foreach (var suffix in rules.HostingSuffixes)
        {
            if (host.EndsWith("." + suffix, StringComparison.Ordinal))
            {
                var head = host[..^(suffix.Length + 1)];
                var lastLabel = head[(head.LastIndexOf('.') + 1)..];
                var value = lastLabel + "." + suffix;
                return new Identifier(IdentifierKind.Domain, value, value);
            }
        }
        var registered = engine.RegisteredDomain(host);
        return new Identifier(IdentifierKind.Domain, registered, registered);
    }
}
