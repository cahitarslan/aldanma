using System.Globalization;
using Aldanma.Core.Identifiers;
using Aldanma.Core.Messages;

namespace Aldanma.Core.Scoring;

/// <summary>
/// Combines what can be read from an identifier itself (phone type, IBAN bank, domain spelling),
/// public block lists, domain age and community reports into a 0-100 score. Pure: all facts are passed in.
/// </summary>
public sealed class RiskScorer(MessageEngine engine, IdentifierRules rules)
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static RiskScorer Default() => new(MessageEngine.Default(), IdentifierRules.Embedded());

    public IdentifierRules Rules => rules;

    public IdentifierAssessment Assess(Identifier id, IdentifierFacts facts, DateTimeOffset now)
    {
        var signals = new List<Signal>();
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        var official = false;

        void Add(string key, params (string Name, string Value)[] values) => signals.Add(SignalFor(key, null, values));

        switch (id.Kind)
        {
            case IdentifierKind.Phone:
                AssessPhone(id, details, Add);
                break;
            case IdentifierKind.Iban:
                AssessIban(id, details, Add);
                break;
            default:
                official = AssessLink(id, details, signals, Add);
                break;
        }

        foreach (var list in facts.Blocklists)
        {
            if (list == "usom") Add("blocklist-usom");
        }

        if (facts.Registration?.CreatedAt is { } created && !official)
        {
            var days = (int)Math.Floor((now - created).TotalDays);
            details["registeredOn"] = created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (days < rules.NewDomainDays) Add("domain-new", ("days", Math.Max(days, 0).ToString(Turkish)));
            else if (days < rules.YoungDomainDays) Add("domain-young", ("days", days.ToString(Turkish)));
            else if (days >= rules.EstablishedDomainYears * 365) Add("domain-established", ("years", (days / 365).ToString(Turkish)));
        }

        var reports = official ? ReportSummary.None : facts.Reports;
        var reportWeight = 0;
        if (reports.ReporterCount > 0)
        {
            foreach (var w in rules.ReportWeights) if (reports.ReporterCount >= w.MinReporters) reportWeight = w.Weight;
            var last = reports.LastReportedAt?.ToString("d MMMM yyyy", Turkish) ?? "-";
            signals.Add(SignalFor("reported", reportWeight, ("count", reports.ReporterCount.ToString(Turkish)), ("last", last)));
            if (reports.LastReportedAt is { } l && now - l <= TimeSpan.FromDays(30)) Add("reported-recently");
            if (reports.CoReportedCount > 0) Add("co-reported", ("count", reports.CoReportedCount.ToString(Turkish)));
        }
        if (reports.Disputed) Add("disputed");

        var score = Math.Clamp(signals.Sum(s => s.Weight), 0, 100);
        return new IdentifierAssessment(id, score, engine.Rules.LevelOf(score), signals, details, reports, official, IsReportable(id));
    }

    /// <summary>
    /// Whether the identifier may be reported. Official domains, shared platforms and short codes cannot be,
    /// so the report database cannot be used to smear them.
    /// </summary>
    public bool IsReportable(Identifier id)
    {
        if (id.IsShortCode) return false;
        if (id.Host is not { } host) return true;
        if (engine.FindOfficial(host) is not null) return false;
        return id.Kind != IdentifierKind.Domain || !(rules.Shorteners.Contains(host) || rules.PathHosts.Contains(host));
    }

    /// <summary>Builds a signal from its rule definition, filling the {placeholders} of its sentence.</summary>
    private Signal SignalFor(string key, int? weight, params (string Name, string Value)[] values)
    {
        var d = rules.Signals[key];
        var text = d.Description;
        foreach (var (name, value) in values) text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        return new Signal(key, "identifier", d.Name, weight ?? d.Weight, text);
    }

    private void AssessPhone(Identifier id, Dictionary<string, string> details, Action<string, (string, string)[]> add)
    {
        var number = id.IsShortCode ? id.Value[PhoneNumber.ShortCodePrefix.Length..] : id.Value;
        var info = PhoneNumber.Parse(number);
        if (info is null) return;
        details["phoneType"] = info.Type.ToString();
        if (id.IsShortCode && rules.EmergencyLines.TryGetValue(number, out var lineName))
        {
            add("phone-emergency", [("name", lineName)]);
            return;
        }
        switch (info.Type)
        {
            case PhoneType.Foreign:
                var country = info.CountryCode is { } cc && rules.Countries.TryGetValue(cc, out var n) ? n : "+" + info.CountryCode;
                details["country"] = country;
                add("phone-foreign", [("country", country)]);
                break;
            case PhoneType.Premium: add("phone-premium", []); break;
            case PhoneType.NonGeographic: add("phone-nongeographic", []); break;
            case PhoneType.TollFree: add("phone-toll-free", []); break;
            case PhoneType.BusinessShort: add("phone-business-short", []); break;
            case PhoneType.Mobile: add("phone-mobile", []); break;
            case PhoneType.ShortCode: add("phone-short-code", []); break;
            case PhoneType.Landline:
                details["areaCode"] = "0" + info.AreaCode;
                add("phone-landline", [("area", "0" + info.AreaCode)]);
                break;
        }
    }

    private void AssessIban(Identifier id, Dictionary<string, string> details, Action<string, (string, string)[]> add)
    {
        if (!Iban.IsValid(id.Value, rules.IbanLengths))
        {
            details["valid"] = "false";
            add("iban-invalid", []);
            return;
        }
        details["valid"] = "true";
        var country = id.Value[..2];
        if (country != "TR")
        {
            var name = rules.IbanCountries.TryGetValue(country, out var c) ? c : country;
            details["country"] = name;
            add("iban-foreign", [("country", name)]);
        }
        else if (Iban.TurkishBankCode(id.Value) is { } code)
        {
            var bank = rules.Banks.TryGetValue(code, out var b) ? b : null;
            details["bankCode"] = code;
            if (bank is not null)
            {
                details["bank"] = bank;
                add("iban-bank", [("bank", bank)]);
            }
        }
        add("iban-personal-payment", []);
    }

    /// <returns>True when the address is a known official domain.</returns>
    private bool AssessLink(Identifier id, Dictionary<string, string> details, List<Signal> signals, Action<string, (string, string)[]> add)
    {
        var host = id.Host!;
        if (engine.FindOfficial(host) is { } off)
        {
            details["official"] = off.Domain;
            if (off.InstitutionName is not null) details["institution"] = off.InstitutionName;
            add("official", [("host", host)]);
            return true;
        }
        // Reuse the message engine's link inspection (lookalike spelling, impersonation, suspicious TLD...).
        var analysis = engine.Analyze("https://" + id.Value);
        foreach (var s in analysis.Signals)
        {
            if (s.Type == "link" && s.Id != "unknown-domain") signals.Add(s);
        }
        return false;
    }
}
