using Microsoft.EntityFrameworkCore;

namespace Aldanma.Api.Data;

/// <summary>Maps identifiers to their database rows and aggregates reports. Never returns individual reports.</summary>
public sealed class IdentifierStore(AppDbContext db, KeyedHasher hasher)
{
    public static bool IsPublic(string kind) => kind is IdentifierKind.Domain or IdentifierKind.Url;

    public Task<IdentifierRecord?> FindAsync(Identifier id, CancellationToken ct)
    {
        var hash = hasher.Identifier(id.Kind, id.Value);
        return db.Identifiers.FirstOrDefaultAsync(x => x.Kind == id.Kind && x.KeyHash == hash, ct);
    }

    public async Task<IdentifierRecord> GetOrCreateAsync(Identifier id, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await FindAsync(id, ct);
        if (existing is not null) return existing;
        var record = new IdentifierRecord
        {
            Kind = id.Kind,
            KeyHash = hasher.Identifier(id.Kind, id.Value),
            PublicValue = IsPublic(id.Kind) ? id.Value : null,
            Masked = id.Masked,
            CreatedAt = now,
        };
        db.Identifiers.Add(record);
        return record;
    }

    public async Task<ReportSummary> SummaryAsync(IdentifierRecord? record, CancellationToken ct)
    {
        if (record is null || record.Hidden) return ReportSummary.None;
        var reports = db.ReportIdentifiers
            .Where(ri => ri.IdentifierId == record.Id && ri.Report.Status == ReportStatus.Active)
            .Select(ri => ri.Report);

        var rows = await reports
            .Select(r => new { r.ReporterHash, r.CreatedAt, r.Category, r.Channel })
            .ToListAsync(ct);
        var disputed = await db.Disputes.AnyAsync(d => d.IdentifierId == record.Id && d.Status == DisputeStatus.Open, ct);
        if (rows.Count == 0) return ReportSummary.None with { Disputed = disputed };

        var coReported = await db.ReportIdentifiers
            .Where(ri => ri.IdentifierId != record.Id && !ri.Identifier.Hidden && ri.Report.Status == ReportStatus.Active
                && ri.Report.Identifiers.Any(x => x.IdentifierId == record.Id))
            .Select(ri => ri.IdentifierId)
            .Distinct()
            .CountAsync(ct);

        return new ReportSummary(
            rows.Select(r => Convert.ToHexString(r.ReporterHash)).Distinct().Count(),
            rows.Count,
            rows.Min(r => r.CreatedAt),
            rows.Max(r => r.CreatedAt),
            rows.GroupBy(r => r.Category).ToDictionary(g => g.Key, g => g.Count()),
            rows.GroupBy(r => r.Channel).ToDictionary(g => g.Key, g => g.Count()),
            coReported,
            disputed);
    }

    public Task<List<string>> BlocklistsAsync(Identifier id, CancellationToken ct)
    {
        if (!IsPublic(id.Kind)) return Task.FromResult(new List<string>());
        return db.BlocklistEntries
            .Where(b => b.Kind == id.Kind && b.Value == id.Value && b.RemovedAt == null)
            .Select(b => b.Source)
            .Distinct()
            .ToListAsync(ct);
    }
}
