using System.Diagnostics.Metrics;

namespace Aldanma.Api.Infrastructure;

/// <summary>
/// Business metrics, exported through OpenTelemetry. Tags hold only coarse categories (kind, level, category),
/// never an identifier, so metrics cannot reveal what was looked up or reported.
/// </summary>
public sealed class AldanmaMetrics
{
    public const string MeterName = "Aldanma";

    private readonly Counter<long> _lookups;
    private readonly Counter<long> _reports;
    private readonly Counter<long> _disputes;
    private readonly Counter<long> _rateLimited;
    private readonly Counter<long> _blocklistImports;
    private readonly Counter<long> _feedback;
    private readonly Counter<long> _submissions;
    private readonly Counter<long> _rdapSkipped;
    private readonly Counter<long> _notifications;

    public AldanmaMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _lookups = meter.CreateCounter<long>("aldanma.lookups", description: "Identifiers scored, by kind and resulting level.");
        _reports = meter.CreateCounter<long>("aldanma.reports", description: "Community reports accepted, by category.");
        _disputes = meter.CreateCounter<long>("aldanma.disputes", description: "Disputes received.");
        _rateLimited = meter.CreateCounter<long>("aldanma.rate_limited", description: "Requests rejected by a rate limit, by endpoint.");
        _blocklistImports = meter.CreateCounter<long>("aldanma.blocklist.imports", description: "Block list import runs, by source and outcome.");
        _feedback = meter.CreateCounter<long>("aldanma.feedback", description: "Was-this-right answers, by level shown and verdict.");
        _submissions = meter.CreateCounter<long>("aldanma.submissions", description: "Messages sent for review, by claim.");
        _notifications = meter.CreateCounter<long>("aldanma.notifications", description: "Moderation e-mails, by outcome.");
        _rdapSkipped = meter.CreateCounter<long>("aldanma.rdap.skipped", description: "Domain-age fetches skipped, by reason (per-request cap or server busy).");
    }

    public void Lookup(string kind, string level) => _lookups.Add(1, new("kind", kind), new("level", level));
    public void ReportAccepted(string category) => _reports.Add(1, new KeyValuePair<string, object?>("category", category));
    public void DisputeReceived() => _disputes.Add(1);
    public void RateLimited(string? endpoint) => _rateLimited.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint ?? "unknown"));
    public void FeedbackReceived(string level, bool correct) => _feedback.Add(1, new("level", level), new("correct", correct));
    public void RdapSkipped(string reason) => _rdapSkipped.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public void SubmissionReceived(string claim) => _submissions.Add(1, new KeyValuePair<string, object?>("claim", claim));
    public void NotificationSent(bool succeeded) =>
        _notifications.Add(1, new KeyValuePair<string, object?>("outcome", succeeded ? "success" : "failure"));
    public void BlocklistImport(string source, bool succeeded) =>
        _blocklistImports.Add(1, new("source", source), new("outcome", succeeded ? "success" : "failure"));
}
