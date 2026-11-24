using System.Text;
using System.Text.RegularExpressions;

namespace Aldanma.Core.Messages;

/// <summary>
/// Message analysis engine. Line-by-line counterpart of web/engine.js; both engines must give
/// the same result for the same rule set (see ParityTests).
/// The engine is stateless, thread-safe and makes no network calls.
/// </summary>
public sealed class MessageEngine
{
    private static readonly Dictionary<char, string> FoldMap = new()
    {
        ['ç'] = "c", ['Ç'] = "c", ['ğ'] = "g", ['Ğ'] = "g", ['ı'] = "i", ['İ'] = "i", ['ö'] = "o", ['Ö'] = "o",
        ['ş'] = "s", ['Ş'] = "s", ['ü'] = "u", ['Ü'] = "u", ['â'] = "a", ['Â'] = "a", ['î'] = "i", ['Î'] = "i",
        ['û'] = "u", ['Û'] = "u", ['–'] = "-", ['—'] = "-", ['₺'] = " tl ",
    };

    private const string Punctuation = ",;:!?()\"'`";
    private const string TurkishLetters = "çÇğĞıİöÖşŞüÜâÂîÎûÛ";
    private const string TurkishUpper = "ÇĞİÖŞÜ";
    private const string TurkishLower = "çğıöşü";
    private const string TrimEnd = ".,;:!?)]}>\"'»”’";
    private const string TrimStart = "([{<\"'«“‘";

    private static readonly Dictionary<char, char> DigitLetter = new()
    {
        ['0'] = 'o', ['1'] = 'l', ['3'] = 'e', ['4'] = 'a', ['5'] = 's', ['7'] = 't',
    };

    private readonly RuleSet _r;
    private readonly Regex _mobileRx = RuleSet.Compile(@"(^|[^0-9])(\+?90 ?|0)?5\d{2} ?\d{3} ?\d{2} ?\d{2}($|[^0-9])");
    private readonly Regex _phoneRx = RuleSet.Compile(@"(^|[^0-9])(0 ?850|0 ?5\d{2}|444|\+90|0 ?[2-4]\d{2}) ?\d{3} ?\d{2,4}");
    private readonly List<(string Label, string Domain, RuleSet.Institution Institution)> _officialLabels = [];
    private readonly Dictionary<string, RuleSet.Institution> _institutionById;

    public MessageEngine(RuleSet rules)
    {
        _r = rules;
        _institutionById = rules.Institutions.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var institution in rules.Institutions)
        {
            foreach (var d in institution.Domains)
            {
                var label = d[..d.IndexOf('.')];
                if (label.Length >= 5) _officialLabels.Add((label, d, institution));
            }
        }
    }

    /// <summary>Engine using the embedded rule set.</summary>
    public static MessageEngine Default() => new(RuleSet.Embedded());

    public RuleSet Rules => _r;
    public string RulesVersion => _r.Version;

    private static bool IsSpace(int c) => c is 32 or 9 or 10 or 13 or 160 or 11 or 12;

    /// <summary>Lower-cases the text, folds Turkish letters to ASCII and turns everything else into spaces.</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        var prevSpace = true;
        for (var i = 0; i < text.Length; i++)
        {
            int c = text[i];
            string e;
            if (c >= 65 && c <= 90) e = ((char)(c + 32)).ToString();
            else if (IsSpace(c)) e = " ";
            else if (c == 46)
            {
                int n = i + 1 < text.Length ? text[i + 1] : 32;
                var alnum = (n >= 48 && n <= 57) || (n >= 65 && n <= 90) || (n >= 97 && n <= 122);
                e = alnum ? "." : " ";
            }
            else if (Punctuation.Contains(text[i])) e = " ";
            else if (c >= 33 && c <= 126) e = text[i].ToString();
            else if (c == 0x307) e = "";
            else if (FoldMap.TryGetValue(text[i], out var folded)) e = folded;
            else e = " ";
            foreach (var ch in e)
            {
                if (ch == ' ')
                {
                    if (!prevSpace) sb.Append(' ');
                    prevSpace = true;
                }
                else
                {
                    sb.Append(ch);
                    prevSpace = false;
                }
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    private static string AsciiLower(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(ch >= 'A' && ch <= 'Z' ? (char)(ch + 32) : ch);
        return sb.ToString();
    }

    private static bool AllDigits(string s) => s.Length > 0 && s.All(ch => ch >= '0' && ch <= '9');
    private static bool AllLetters(string s) => s.Length > 0 && s.All(ch => ch >= 'a' && ch <= 'z');

    public static int Levenshtein(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (var j = 0; j <= m; j++) prev[j] = j;
        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var d = prev[j] + 1;
                if (curr[j - 1] + 1 < d) d = curr[j - 1] + 1;
                if (prev[j - 1] + cost < d) d = prev[j - 1] + cost;
                curr[j] = d;
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }

    private static string Fill(string template, params (string Name, string Value)[] values)
    {
        var s = template;
        foreach (var (name, value) in values) s = s.Replace("{" + name + "}", value, StringComparison.Ordinal);
        return s;
    }

    private static bool HasSuffix(string host, string suffix) =>
        host == suffix || (host.Length > suffix.Length && host.EndsWith("." + suffix, StringComparison.Ordinal));

    /// <summary>Registrable domain of a host, honouring Turkish second-level suffixes (com.tr, gov.tr...).</summary>
    public string RegisteredDomain(string host)
    {
        var parts = host.Split('.');
        var n = 2;
        foreach (var s in _r.SecondLevelSuffixes)
        {
            if (HasSuffix(host, s) && host != s) { n = s.Split('.').Length + 1; break; }
        }
        return parts.Length <= n ? host : string.Join('.', parts[^n..]);
    }

    /// <summary>A link as found in text, before inspection.</summary>
    public sealed record ParsedLink(string Raw, string Scheme, string Host, string Path, bool Ip, string Tld, bool Special);

    private sealed class InspectedLink
    {
        public required string Raw, Host, RegisteredDomain;
        public bool Official, Channel;
        public List<string> Signals = [];
        public string Token = "", Institution = "", OfficialDomain = "", Tld = "";
    }

    /// <summary>Tries to read a link from a single word; returns null when the word is not a link.</summary>
    public ParsedLink? ParseLink(string word)
    {
        var s = word;
        while (s.Length > 0 && TrimStart.Contains(s[0])) s = s[1..];
        var lower = AsciiLower(s);
        var scheme = "";
        var i = lower.IndexOf("https://", StringComparison.Ordinal);
        var j = lower.IndexOf("http://", StringComparison.Ordinal);
        if (i >= 0 && (j < 0 || i <= j)) { scheme = "https"; s = s[(i + 8)..]; }
        else if (j >= 0) { scheme = "http"; s = s[(j + 7)..]; }
        else
        {
            var w = lower.IndexOf("www.", StringComparison.Ordinal);
            if (w > 0) s = s[w..];
            else
            {
                // Glued forms such as "Takip:site.com/x"
                var dot = s.IndexOf('.');
                var cut = -1;
                for (var q = 0; q < s.Length && (dot < 0 || q < dot); q++)
                {
                    if (":=(".Contains(s[q])) cut = q;
                }
                if (cut >= 0) s = s[(cut + 1)..];
            }
        }
        while (s.Length > 0 && TrimEnd.Contains(s[^1])) s = s[..^1];
        if (s.Length < 4) return null;
        // Turkish suffix: "turkiye.gov.tr'den"
        var lastDot = s.LastIndexOf('.');
        for (var q = lastDot + 1; q < s.Length; q++)
        {
            if (s[q] == '\'' || s[q] == '’') { s = s[..q]; break; }
        }
        var authorityEnd = s.Length;
        for (var q = 0; q < s.Length; q++)
        {
            if (s[q] == '/' || s[q] == '?' || s[q] == '#') { authorityEnd = q; break; }
        }
        var host = AsciiLower(s[..authorityEnd]);
        var path = AsciiLower(s[authorityEnd..]);
        var at = host.LastIndexOf('@');
        if (at >= 0)
        {
            if (scheme == "") return null; // e-mail address
            host = host[(at + 1)..];
        }
        var colon = host.IndexOf(':');
        if (colon >= 0) host = host[..colon];
        while (host.Length > 0 && host[^1] == '.') host = host[..^1];
        var hasWww = host.StartsWith("www.", StringComparison.Ordinal);
        if (hasWww) host = host[4..];
        var labels = host.Split('.');
        if (labels.Length < 2) return null;
        var special = false;
        foreach (var label in labels)
        {
            if (label.Length == 0) return null;
            if (label.StartsWith("xn--", StringComparison.Ordinal)) special = true;
            foreach (var ch in label)
            {
                int c = ch;
                var alnum = (c >= 97 && c <= 122) || (c >= 48 && c <= 57) || c == 45;
                if (c > 127)
                {
                    if (c >= 0x2000 && c <= 0x2BFF) return null; // punctuation and symbols
                    special = true;
                }
                else if (!alnum) return null;
            }
        }
        var ip = false;
        if (labels.Length == 4)
        {
            ip = true;
            foreach (var label in labels) if (!AllDigits(label) || label.Length > 3) ip = false;
        }
        var tld = labels[^1];
        if (!ip)
        {
            var validTld = (AllLetters(tld) && tld.Length >= 2 && tld.Length <= 24) || tld.StartsWith("xn--", StringComparison.Ordinal);
            if (!validTld) return null;
            if (scheme == "" && !hasWww && !_r.BareTlds.Contains(tld)) return null;
            if (scheme == "" && !hasWww && labels[^2].Length < 2) return null;
        }
        return new ParsedLink((scheme != "" ? scheme + "://" : "") + (hasWww ? "www." : "") + host + path, scheme, host, path, ip, ip ? "" : tld, special);
    }

    /// <summary>Extracts every link in a text, in order, using the same rules as the analysis.</summary>
    public IReadOnlyList<ParsedLink> ExtractLinks(string? text)
    {
        var result = new List<ParsedLink>();
        text ??= "";
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var space = i == text.Length || IsSpace(text[i]);
            if (!space) { if (start < 0) start = i; continue; }
            if (start < 0) continue;
            var word = text[start..i];
            var b = word.IndexOf('.') > 0 ? ParseLink(word) : null;
            if (b is not null) result.Add(b);
            start = -1;
        }
        return result;
    }

    /// <summary>Returns the official domain and institution name when the host belongs to a known official domain.</summary>
    public (string Domain, string? InstitutionName)? FindOfficial(string host)
    {
        var r = FindOfficialInternal(host);
        return r is null ? null : (r.Value.Domain, r.Value.Institution?.Name);
    }

    private (RuleSet.Institution? Institution, string Domain)? FindOfficialInternal(string host)
    {
        foreach (var institution in _r.Institutions)
        {
            foreach (var d in institution.Domains) if (HasSuffix(host, d)) return (institution, d);
        }
        foreach (var s in _r.PublicSuffixes) if (HasSuffix(host, s) && host != s) return (null, s);
        foreach (var d in _r.KnownDomains) if (HasSuffix(host, d)) return (null, d);
        return null;
    }

    private (RuleSet.Institution Institution, string Token)? FindImpersonation(string host)
    {
        var parts = host.Replace('.', '-').Split('-');
        var noHyphen = host.Replace("-", "", StringComparison.Ordinal);
        foreach (var institution in _r.Institutions)
        {
            foreach (var t in institution.Tokens)
            {
                if (t.Length >= 5)
                {
                    if (host.Contains(t, StringComparison.Ordinal) || noHyphen.Contains(t, StringComparison.Ordinal)) return (institution, t);
                }
                else
                {
                    foreach (var p in parts)
                    {
                        if (p == t || p.StartsWith(t, StringComparison.Ordinal) || (t.Length == 4 && p.Length > 4 && p.EndsWith(t, StringComparison.Ordinal)))
                            return (institution, t);
                    }
                }
            }
        }
        return null;
    }

    private (string Domain, RuleSet.Institution Institution)? FindLookalike(string registered)
    {
        var first = registered[..registered.IndexOf('.')];
        var sb = new StringBuilder(first.Length);
        foreach (var ch in first) sb.Append(DigitLetter.TryGetValue(ch, out var y) ? y : ch);
        var plain = sb.ToString();
        var noHyphen = plain.Replace("-", "", StringComparison.Ordinal);
        foreach (var r in _officialLabels)
        {
            var limit = r.Label.Length >= 9 ? 2 : 1;
            foreach (var candidate in new[] { plain, noHyphen })
            {
                if (candidate.Length < 4) continue;
                if (candidate == r.Label) return (r.Domain, r.Institution);
                var diff = candidate.Length - r.Label.Length;
                if (diff > limit || diff < -limit) continue;
                if (Levenshtein(candidate, r.Label) <= limit) return (r.Domain, r.Institution);
            }
        }
        return null;
    }

    private InspectedLink InspectLink(ParsedLink b)
    {
        var s = new InspectedLink { Raw = b.Raw, Host = b.Host, RegisteredDomain = b.Ip ? b.Host : RegisteredDomain(b.Host) };
        if (b.Ip)
        {
            s.Signals.Add("ip-address");
            if (b.Scheme == "http") s.Signals.Add("unencrypted");
            if (b.Path.Contains(".apk", StringComparison.Ordinal)) s.Signals.Add("apk-file");
            return s;
        }
        var official = b.Special ? null : FindOfficialInternal(b.Host);
        if (official is not null)
        {
            s.Official = true;
            s.Signals.Add("official-domain");
            return s;
        }
        if (Array.IndexOf(_r.ChannelDomains, b.Host) >= 0) { s.Channel = true; return s; }
        var strong = false;
        if (Array.IndexOf(_r.Shorteners, b.Host) >= 0 || Array.IndexOf(_r.Shorteners, s.RegisteredDomain) >= 0)
        {
            s.Signals.Add("url-shortener");
            strong = true;
        }
        else
        {
            // A host with Turkish letters ("türkiye.gov.tr") is folded and compared with official domains.
            var sb = new StringBuilder(b.Host.Length);
            foreach (var ch in b.Host) sb.Append(FoldMap.TryGetValue(ch, out var y) && y.Length == 1 ? y : ch.ToString());
            var folded = sb.ToString();
            var foldedOfficial = b.Special ? FindOfficialInternal(folded) : null;
            var lookalike = foldedOfficial is { Institution: not null } fo ? (fo.Domain, fo.Institution!) : FindLookalike(RegisteredDomain(folded));
            if (lookalike is not null)
            {
                s.Signals.Add("lookalike-spelling");
                s.OfficialDomain = lookalike.Value.Domain;
                s.Institution = lookalike.Value.Institution.Name;
                strong = true;
            }
            else
            {
                var imp = FindImpersonation(b.Host);
                if (imp is not null)
                {
                    s.Signals.Add("impersonating-domain");
                    s.Token = imp.Value.Token;
                    s.Institution = imp.Value.Institution.Name;
                    strong = true;
                }
            }
            if (_r.SuspiciousTlds.Contains(b.Tld)) { s.Signals.Add("suspicious-tld"); s.Tld = b.Tld; strong = true; }
            int hyphens = 0, digits = 0;
            foreach (var ch in b.Host)
            {
                if (ch == '-') hyphens++;
                if (ch >= '0' && ch <= '9') digits++;
            }
            if (hyphens >= 2 || digits >= 4 || b.Host.Split('.').Length >= 5) s.Signals.Add("complex-address");
        }
        if (b.Special) { s.Signals.Add("special-characters"); strong = true; }
        if (b.Path.Contains(".apk", StringComparison.Ordinal)) { s.Signals.Add("apk-file"); strong = true; }
        if (b.Scheme == "http") s.Signals.Add("unencrypted");
        if (!strong) s.Signals.Add("unknown-domain");
        return s;
    }

    /// <summary>Analyses a message.</summary>
    /// <param name="text">Message text.</param>
    /// <param name="sender">One of the <see cref="Sender"/> values.</param>
    public MessageAnalysis Analyze(string? text, string? sender = null)
    {
        text ??= "";
        sender = string.IsNullOrEmpty(sender) ? Sender.Unknown : sender;
        var personal = sender is Sender.Mobile or Sender.Foreign or Sender.WhatsApp;

        // 1. Extract links
        var links = new List<InspectedLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var withoutLinksSb = new StringBuilder(text.Length);
        var pos = 0;
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var space = i == text.Length || IsSpace(text[i]);
            if (!space) { if (start < 0) start = i; continue; }
            if (start < 0) continue;
            var word = text[start..i];
            var b = word.IndexOf('.') > 0 ? ParseLink(word) : null;
            if (b is not null)
            {
                withoutLinksSb.Append(text, pos, start - pos).Append(' ');
                pos = i;
                var key = b.Host + b.Path;
                if (!seen.Contains(key) && links.Count < 10)
                {
                    seen.Add(key);
                    links.Add(InspectLink(b));
                }
            }
            start = -1;
        }
        withoutLinksSb.Append(text, pos, text.Length - pos);
        var withoutLinks = withoutLinksSb.ToString();
        var plain = Fold(withoutLinks);

        var signals = new List<Signal>();
        var score = 0;
        void Add(string id, string type, string name, int weight, string reason)
        {
            signals.Add(new Signal(id, type, name, weight, reason));
            score += weight;
        }

        // 2. Language signals
        var lang = new HashSet<string>(StringComparer.Ordinal);
        var langHits = new List<RuleSet.LanguageSignal>();
        foreach (var d in _r.LanguageSignals)
        {
            if (d.Patterns.Any(r => r.IsMatch(plain))) { lang.Add(d.Id); langHits.Add(d); }
        }
        if (links.Any(b => b.Channel) && !lang.Contains("channel-switch"))
        {
            var d = _r.LanguageSignals.FirstOrDefault(x => x.Id == "channel-switch");
            if (d is not null) { lang.Add(d.Id); langHits.Add(d); }
        }

        var riskyLinks = links.Where(b => !b.Official && !b.Channel).ToList();
        // Intent classes (broad meaning classes); their combinations are evaluated in step 7b.
        var features = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in _r.IntentClasses) if (n.Patterns.Any(r => r.IsMatch(plain))) features.Add(n.Id);

        var asksAction = features.Contains("callback") || riskyLinks.Count > 0 || links.Any(b => b.Channel) || lang.Contains("sensitive-info") || lang.Contains("money-request")
            || _phoneRx.IsMatch(plain) || personal;

        // 3. Categories
        var matched = new List<(RuleSet.Category K, int Effective, bool Strong)>();
        foreach (var k in _r.Categories)
        {
            int effective = 0;
            var strong = false;
            if (k.SenderCondition is not null && Array.IndexOf(k.SenderCondition, sender) < 0) continue;
            if (k.Strong.Any(r => r.IsMatch(plain)))
            {
                // A message written in an institution's name that asks for nothing counts as a notice.
                if ((k.Institutional || k.RequiresAction) && !asksAction) effective = k.Weight * 5 / 10;
                else { effective = k.Weight; strong = true; }
            }
            else
            {
                var weak = k.Weak.Count(r => r.IsMatch(plain));
                if (weak >= 2 && (riskyLinks.Count > 0 || lang.Contains("sensitive-info") || lang.Contains("money-request")))
                    effective = k.Weight * 6 / 10;
            }
            if (effective > 0) matched.Add((k, effective, strong));
        }
        // OrderByDescending is stable: file order is kept on ties (same as the JS sort).
        var ranked = matched.OrderByDescending(e => e.Effective).Take(3).ToList();
        for (var i = 0; i < ranked.Count; i++)
        {
            var e = ranked[i];
            Add(e.K.Id, "category", e.K.Name, i == 0 ? e.Effective : 5, e.K.Description);
        }

        foreach (var d in langHits) Add(d.Id, "language", d.Name, d.Weight, d.Description);

        // 4. Writing
        bool cyrillic = false, accent = false;
        int letters = 0, upper = 0;
        foreach (var ch in withoutLinks)
        {
            int c = ch;
            if (c >= 0x370 && c <= 0x4ff) cyrillic = true;
            else if (c >= 0xc0 && c <= 0x24f && c != 0xd7 && c != 0xf7 && !TurkishLetters.Contains(ch)) accent = true;
            if ((c >= 65 && c <= 90) || TurkishUpper.Contains(ch)) { letters++; upper++; }
            else if ((c >= 97 && c <= 122) || TurkishLower.Contains(ch)) letters++;
        }
        if (_r.DigitLettersPattern.IsMatch(plain)) Add("digit-letters", "writing", _r.DigitLetters.Name, _r.DigitLetters.Weight, _r.DigitLetters.Description);
        if (cyrillic) Add("mixed-alphabet", "writing", _r.MixedAlphabet.Name, _r.MixedAlphabet.Weight, _r.MixedAlphabet.Description);
        if (accent) Add("foreign-accents", "writing", _r.ForeignAccents.Name, _r.ForeignAccents.Weight, _r.ForeignAccents.Description);
        if ((letters >= 20 && upper * 100 >= letters * 70) || withoutLinks.Contains("!!", StringComparison.Ordinal))
            Add("shouting", "writing", _r.Shouting.Name, _r.Shouting.Weight, _r.Shouting.Description);

        // 5. Claimed institution
        var textInstitutions = _r.Institutions.Where(k => k.Keywords.Any(r => r.IsMatch(plain))).ToList();
        var claimed = textInstitutions.Where(k => k.Mismatch).ToList();
        var strongInstitutional = ranked.Where(e => e.Strong && e.K.Institutional).ToList();
        var claimedName = "";
        if (claimed.Count > 0) claimedName = claimed[0].Name;
        else if (strongInstitutional.Count > 0)
        {
            var first = strongInstitutional[0].K;
            claimedName = first.Institutions.Length > 0 ? _institutionById[first.Institutions[0]].Name : first.Name;
        }
        var hasClaim = claimedName != "";

        // 6. Link signals (each kind counts once)
        foreach (var (id, t) in _r.LinkSignals)
        {
            switch (id)
            {
                case "institution-link-mismatch":
                    if (hasClaim && riskyLinks.Count > 0)
                        Add(id, "link", t.Name, t.Weight, Fill(t.Description, ("institution", claimedName), ("host", riskyLinks[0].Host)));
                    continue;
                case "link-and-payment":
                    if (riskyLinks.Count > 0 && (lang.Contains("payment-language") || lang.Contains("money-request") || lang.Contains("sensitive-info") || lang.Contains("info-request")))
                        Add(id, "link", t.Name, t.Weight, Fill(t.Description, ("host", riskyLinks[0].Host)));
                    continue;
                case "link-and-reward":
                    if (riskyLinks.Count > 0 && lang.Contains("reward"))
                        Add(id, "link", t.Name, t.Weight, Fill(t.Description, ("host", riskyLinks[0].Host)));
                    continue;
                case "official-domain":
                    if (links.Count > 0 && riskyLinks.Count == 0 && links.Any(b => b.Official) && !lang.Contains("sensitive-info"))
                        Add(id, "link", t.Name, t.Weight, Fill(t.Description, ("host", links.First(b => b.Official).Host)));
                    continue;
            }
            var found = links.FirstOrDefault(x => x.Signals.Contains(id));
            if (found is not null)
            {
                Add(id, "link", t.Name, t.Weight, Fill(t.Description,
                    ("host", found.Host), ("token", found.Token), ("institution", found.Institution), ("official", found.OfficialDomain), ("tld", found.Tld)));
            }
        }

        // 7. Sender
        void AddSender(string id)
        {
            var g = _r.SenderSignals[id];
            Add(id, "sender", g.Name, g.Weight, g.Description);
        }
        if (sender == Sender.Mobile && hasClaim) AddSender("mobile-institution");
        if (sender == Sender.Foreign) AddSender(hasClaim ? "foreign-institution" : "foreign-number");
        if (sender == Sender.WhatsApp && hasClaim) AddSender("whatsapp-institution");
        if (hasClaim && _mobileRx.IsMatch(plain)) AddSender("mobile-in-text");

        // 7b. Intent layer: broad meaning classes and their combinations
        foreach (var id in langHits.Select(d => d.Id)) features.Add("lang:" + id);
        if (riskyLinks.Count > 0) features.Add("link-risky");
        if (links.Any(b => b.Official)) features.Add("link-official");
        if (links.Count == 0) features.Add("link-none");
        if (personal) features.Add("channel-personal");
        if (sender == Sender.Header) features.Add("channel-header");
        int intentTop = 0, intentCount = 0;
        foreach (var c in _r.Combinations)
        {
            if (!c.All.All(features.Contains)) continue;
            if (c.Any.Length > 0 && !c.Any.Any(features.Contains)) continue;
            if (c.None.Any(features.Contains)) continue;
            signals.Add(new Signal("intent:" + c.Id, "intent", c.Name, c.Score, c.Description));
            if (c.Score > intentTop) intentTop = c.Score;
            intentCount++;
        }
        var intentScore = 0;
        if (intentCount > 0)
        {
            var extra = (intentCount - 1) * 4;
            intentScore = intentTop + (extra > 12 ? 12 : extra);
        }
        if (intentScore > score) score = intentScore;

        score = Math.Clamp(score, 0, 100);
        var level = _r.LevelOf(score);

        // 8. What to do and the official verification path
        var whatToDo = new List<string>();
        void AddStep(string a) { if (!whatToDo.Contains(a)) whatToDo.Add(a); }
        if (ranked.Count > 0) foreach (var a in ranked[0].K.WhatToDo) AddStep(a);
        foreach (var a in _r.BasicRules) AddStep(a);

        var verification = new List<VerificationPath>();
        void AddVerification(string title, string vText, IReadOnlyList<OfficialLink> vLinks)
        {
            if (verification.Count < 3 && !verification.Any(d => d.Text == vText)) verification.Add(new VerificationPath(title, vText, vLinks));
        }
        foreach (var e in ranked.Take(2)) AddVerification(e.K.Name, e.K.VerificationText, e.K.VerificationLinks);
        var categoryInstitutions = new HashSet<string>(ranked.SelectMany(e => e.K.Institutions), StringComparer.Ordinal);
        foreach (var k in textInstitutions)
        {
            if (!categoryInstitutions.Contains(k.Id) || ranked.Count == 0) AddVerification(k.Name, k.Verification, []);
        }

        return new MessageAnalysis(
            score,
            level,
            [.. ranked.Select(e => new NamedItem(e.K.Id, e.K.Name))],
            [.. textInstitutions.Select(k => new NamedItem(k.Id, k.Name))],
            signals,
            [.. links.Select(b => new LinkResult(b.Raw, b.Host, b.RegisteredDomain, b.Official, b.Signals))],
            whatToDo,
            verification,
            level == RiskLevel.NoSigns ? [] : _r.IfClicked,
            level == RiskLevel.NoSigns ? _r.NoSignsWarning : _r.NotConclusive,
            _r.Version);
    }
}
