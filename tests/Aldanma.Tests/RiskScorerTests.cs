using Aldanma.Core.Identifiers;
using Aldanma.Core.Messages;
using Aldanma.Core.Scoring;

namespace Aldanma.Tests;

public class RiskScorerTests
{
    private static readonly RiskScorer Scorer = RiskScorer.Default();
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static ReportSummary Reports(int reporters, int daysAgo = 3) => new(
        reporters, reporters, Now.AddDays(-60), Now.AddDays(-daysAgo),
        new Dictionary<string, int> { ["deposit"] = reporters }, new Dictionary<string, int> { ["whatsapp"] = reporters }, 0, false);

    [Fact]
    public void Unknown_mobile_number_without_reports_shows_no_signs()
    {
        var a = Scorer.Assess(new Identifier(IdentifierKind.Phone, "+905321234567", ""), IdentifierFacts.Empty, Now);
        Assert.Equal(RiskLevel.NoSigns, a.Level);
        Assert.Contains(a.Signals, s => s.Id == "phone-mobile");
    }

    [Theory]
    [InlineData(1, RiskLevel.Suspicious)]
    [InlineData(3, RiskLevel.High)]
    [InlineData(12, RiskLevel.High)]
    public void Community_reports_raise_the_score(int reporters, string level)
    {
        var a = Scorer.Assess(new Identifier(IdentifierKind.Iban, "TR850001000000000012345678", ""), IdentifierFacts.Empty with { Reports = Reports(reporters) }, Now);
        Assert.Equal(level, a.Level);
        Assert.Equal("Ziraat Bankası", a.Details["bank"]);
        Assert.Contains(a.Signals, s => s.Id == "reported" && s.Reason.StartsWith($"{reporters} kişi", StringComparison.Ordinal));
    }

    [Fact]
    public void Usom_listing_is_high_risk_on_its_own()
    {
        var a = Scorer.Assess(new Identifier(IdentifierKind.Domain, "kargo-takip-ptt.xyz", ""), IdentifierFacts.Empty with { Blocklists = ["usom"] }, Now);
        Assert.Equal(RiskLevel.High, a.Level);
    }

    [Fact]
    public void A_domain_registered_last_week_is_flagged()
    {
        var facts = IdentifierFacts.Empty with { Registration = new DomainRegistration(Now.AddDays(-6), "Some Registrar") };
        var a = Scorer.Assess(new Identifier(IdentifierKind.Domain, "harika-kampanya.com", ""), facts, Now);
        Assert.Contains(a.Signals, s => s.Id == "domain-new" && s.Reason.Contains("6 gün", StringComparison.Ordinal));
        Assert.NotEqual(RiskLevel.NoSigns, a.Level);
    }

    [Fact]
    public void Lookalike_domain_gets_link_signals_from_the_message_engine()
    {
        var a = Scorer.Assess(new Identifier(IdentifierKind.Domain, "yap1kredi.com.tr", ""), IdentifierFacts.Empty, Now);
        Assert.Contains(a.Signals, s => s.Id == "lookalike-spelling");
    }

    [Fact]
    public void Official_domains_ignore_reports_and_cannot_be_reported()
    {
        var a = Scorer.Assess(new Identifier(IdentifierKind.Domain, "turkiye.gov.tr", ""), IdentifierFacts.Empty with { Reports = Reports(20) }, Now);
        Assert.True(a.Official);
        Assert.False(a.Reportable);
        Assert.Equal(RiskLevel.NoSigns, a.Level);
        Assert.Equal(0, a.Reports.ReporterCount);
    }

    [Fact]
    public void Reports_from_fewer_than_three_networks_alone_never_reach_high_risk()
    {
        var phone = InputParser.Default().ParseSingle("0532 123 45 67")!;
        var two = Scorer.Assess(phone, IdentifierFacts.Empty with { Reports = Reports(2) with { CoReportedCount = 4 } }, Now);
        Assert.NotEqual(RiskLevel.High, two.Level);
        Assert.Equal(RiskLevel.High, Scorer.Assess(phone, IdentifierFacts.Empty with { Reports = Reports(3) }, Now).Level);
    }

    [Theory]
    [InlineData("2828", false)]
    [InlineData("bit.ly", false)]
    [InlineData("bit.ly/abc123", true)]
    [InlineData("0532 123 45 67", true)]
    [InlineData("yap1kredi.com.tr", true)]
    public void Short_codes_and_bare_shorteners_cannot_be_reported(string input, bool reportable)
    {
        var id = InputParser.Default().ParseSingle(input)!;
        Assert.Equal(reportable, Scorer.IsReportable(id));
        Assert.Equal(reportable, Scorer.Assess(id, IdentifierFacts.Empty, Now).Reportable);
    }

    [Fact]
    public void Foreign_and_premium_numbers_carry_weight()
    {
        Assert.Contains(Scorer.Assess(new Identifier(IdentifierKind.Phone, "+2348031234567", ""), IdentifierFacts.Empty, Now).Signals,
            s => s.Id == "phone-foreign" && s.Reason.Contains("Nijerya", StringComparison.Ordinal));
        Assert.Contains(Scorer.Assess(new Identifier(IdentifierKind.Phone, "+909001112233", ""), IdentifierFacts.Empty, Now).Signals, s => s.Id == "phone-premium");
    }

    [Fact]
    public void Invalid_iban_is_explained()
    {
        var a = Scorer.Assess(new Identifier(IdentifierKind.Iban, "TR860001000000000012345678", ""), IdentifierFacts.Empty, Now);
        Assert.Equal("false", a.Details["valid"]);
        Assert.Contains(a.Signals, s => s.Id == "iban-invalid");
    }
}
