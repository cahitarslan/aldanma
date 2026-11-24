using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Aldanma.Api.Features.Blocklist;

namespace Aldanma.Api.Features.Meta;

public sealed record MetaResponse(
    string RulesVersion, int ReportCount, int ReportedIdentifierCount, int BlocklistCount, DateTimeOffset? BlocklistUpdatedAt,
    BlocklistImportSnapshot? BlocklistImport = null);

/// <summary>
/// Public counters for the home page, read on every visit. The response is output-cached for 60 s (per Origin, for
/// CORS), so a flood of page loads is answered from memory; the counts themselves are recomputed every 5 minutes.
/// blocklistImport is the live (in-memory) progress of the block list download, so it costs no query.
/// </summary>
public static class MetaEndpoints
{
    public static void MapMetaEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/meta", async (AppDbContext db, MessageEngine engine, IMemoryCache cache, BlocklistImportStatus import, CancellationToken ct) =>
            Results.Ok((await cache.GetOrCreateAsync("meta", async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                var reportCount = await db.Reports.CountAsync(r => r.Status == ReportStatus.Active, ct);
                var identifierCount = await db.Identifiers.CountAsync(i => !i.Hidden && i.Reports.Any(r => r.Report.Status == ReportStatus.Active), ct);
                var blocklist = await db.BlocklistEntries.CountAsync(b => b.RemovedAt == null, ct);
                var updated = await db.BlocklistEntries.Where(b => b.Source == UsomFeedService.Source).MaxAsync(b => (DateTimeOffset?)b.LastSeenAt, ct);
                return new MetaResponse(engine.RulesVersion, reportCount, identifierCount, blocklist, updated);
            }))! with { BlocklistImport = import.Snapshot() }))
            .CacheOutput(p => p.Expire(TimeSpan.FromSeconds(60)).SetVaryByHeader("Origin"));
    }
}
