namespace Aldanma.Core.Scoring;

/// <summary>Aggregated community reports for one identifier. Individual reports are never exposed.</summary>
public sealed record ReportSummary(
    int ReporterCount,
    int ReportCount,
    DateTimeOffset? FirstReportedAt,
    DateTimeOffset? LastReportedAt,
    IReadOnlyDictionary<string, int> Categories,
    IReadOnlyDictionary<string, int> Channels,
    int CoReportedCount,
    bool Disputed)
{
    public static readonly ReportSummary None = new(0, 0, null, null, new Dictionary<string, int>(), new Dictionary<string, int>(), 0, false);
}

/// <summary>Registration data for a domain, from RDAP.</summary>
public sealed record DomainRegistration(DateTimeOffset? CreatedAt, string? Registrar);

/// <summary>Everything the server knows about an identifier besides its shape.</summary>
public sealed record IdentifierFacts(
    ReportSummary Reports,
    IReadOnlyList<string> Blocklists,
    DomainRegistration? Registration)
{
    public static readonly IdentifierFacts Empty = new(ReportSummary.None, [], null);
}
