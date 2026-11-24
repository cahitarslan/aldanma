using Microsoft.EntityFrameworkCore;

namespace Aldanma.Api.Features.Reports;

public sealed record ReportRequest(string[]? Inputs, string? Category, string? Channel, string? Loss, string? OccurredOn, string? HumanToken);

public sealed record ReportOutcome(bool Ok, string? Error, Guid? ReportId, IReadOnlyList<string> Accepted, IReadOnlyList<string> Skipped);

/// <summary>Validates and stores community reports.</summary>
public sealed class ReportService(
    AppDbContext db,
    IdentifierStore store,
    KeyedHasher hasher,
    InputParser parser,
    RiskScorer scorer,
    IdentifierRules rules,
    AldanmaMetrics metrics,
    TimeProvider time,
    ILogger<ReportService> log)
{
    public const int MaxIdentifiersPerReport = 5;

    /// <summary>
    /// One reporter (network) may name at most this many identifiers a day. Real victims report a handful;
    /// someone trying to smear many numbers or sites at once hits this first.
    /// </summary>
    public const int MaxIdentifiersPerReporterPerDay = 20;

    private sealed record Checked(string? Error, DateOnly? Occurred, List<Identifier> Identifiers, List<string> Skipped);

    /// <summary>
    /// Every check that does not need the human check. The endpoint runs it first, so a form with a mistake in it
    /// does not use up the single-use Turnstile token. Null when the report may go on.
    /// </summary>
    public ReportOutcome? Validate(ReportRequest request)
    {
        var c = Check(request, time.GetUtcNow());
        return c.Error is null ? null : new ReportOutcome(false, c.Error, null, [], c.Skipped);
    }

    private Checked Check(ReportRequest request, DateTimeOffset now)
    {
        static Checked Fail(string error) => new(error, null, [], []);

        if (request.Category is null || !rules.ReportCategories.ContainsKey(request.Category)) return Fail("Geçersiz dolandırıcılık türü.");
        if (request.Channel is null || !rules.ReportChannels.ContainsKey(request.Channel)) return Fail("Geçersiz kanal.");
        if (!rules.LossBands.ContainsKey(request.Loss ?? "none")) return Fail("Geçersiz kayıp aralığı.");
        DateOnly? occurred = null;
        if (!string.IsNullOrEmpty(request.OccurredOn))
        {
            if (!DateOnly.TryParseExact(request.OccurredOn + "-01", "yyyy-MM-dd", out var d) || d > DateOnly.FromDateTime(now.UtcDateTime) || d.Year < 2000)
                return Fail("Geçersiz tarih.");
            occurred = d;
        }

        var identifiers = new List<Identifier>();
        var skipped = new List<string>();
        foreach (var raw in (request.Inputs ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Take(MaxIdentifiersPerReport))
        {
            var id = parser.ParseSingle(raw);
            if (id is null) { skipped.Add(raw.Length > 40 ? raw[..40] : raw); continue; }
            if (!scorer.IsReportable(id)) { skipped.Add(id.Display); continue; }
            if (!identifiers.Contains(id)) identifiers.Add(id);
        }
        return identifiers.Count == 0
            ? new Checked("İhbar edilecek geçerli bir numara, IBAN ya da adres bulunamadı.", null, [], skipped)
            : new Checked(null, occurred, identifiers, skipped);
    }

    public async Task<ReportOutcome> SubmitAsync(ReportRequest request, string clientAddress, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var (error, occurred, identifiers, skipped) = Check(request, now);
        if (error is not null) return new ReportOutcome(false, error, null, [], skipped);
        var loss = request.Loss ?? "none";

        var reporter = hasher.Reporter(clientAddress);
        var records = new List<(Identifier Id, IdentifierRecord Record)>();
        foreach (var id in identifiers) records.Add((id, await store.GetOrCreateAsync(id, now, ct)));

        // The same person reporting the same identifier again within a day adds nothing.
        var since = now.AddDays(-1);
        var existingIds = records.Where(r => r.Record.Id != 0).Select(r => r.Record.Id).ToList();
        var duplicates = existingIds.Count == 0 ? [] : await db.ReportIdentifiers
            .Where(ri => existingIds.Contains(ri.IdentifierId) && ri.Report.ReporterHash == reporter && ri.Report.CreatedAt >= since)
            .Select(ri => ri.IdentifierId)
            .ToListAsync(ct);
        var fresh = records.Where(r => !duplicates.Contains(r.Record.Id)).ToList();
        if (fresh.Count == 0) return new ReportOutcome(false, "Bunu son 24 saat içinde zaten ihbar ettin. Teşekkürler.", null, [], skipped);

        var reportedToday = await db.ReportIdentifiers.CountAsync(ri => ri.Report.ReporterHash == reporter && ri.Report.CreatedAt >= since, ct);
        if (reportedToday + fresh.Count > MaxIdentifiersPerReporterPerDay)
        {
            log.LogWarning("Reporter hit the daily identifier limit ({Limit})", MaxIdentifiersPerReporterPerDay);
            return new ReportOutcome(false, "Bugün için ihbar sınırına ulaştın. Yarın tekrar deneyebilirsin.", null, [], skipped);
        }

        var report = new Report
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = now,
            Category = request.Category!, // checked above
            Channel = request.Channel!,
            Loss = loss,
            OccurredOn = occurred,
            ReporterHash = reporter,
        };
        foreach (var (_, record) in fresh) report.Identifiers.Add(new ReportIdentifier { Report = report, Identifier = record });
        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);
        metrics.ReportAccepted(report.Category);
        // Only the report id and coarse facts are logged: never an identifier or the reporter.
        log.LogInformation("Report {ReportId} accepted: {Category}, {IdentifierCount} identifiers", report.Id, report.Category, fresh.Count);

        return new ReportOutcome(true, null, report.Id, [.. fresh.Select(f => f.Id.Display)], skipped);
    }
}
