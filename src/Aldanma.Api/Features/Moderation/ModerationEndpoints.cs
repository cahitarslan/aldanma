using Microsoft.EntityFrameworkCore;

namespace Aldanma.Api.Features.Moderation;

/// <summary>Moderation of reports and disputes. Mapped on the protected admin group (see AdminAccess).</summary>
public static class ModerationEndpoints
{
    public sealed record ResolveDisputeRequest(bool Accept);

    private const string LogCategory = "Aldanma.Admin";

    public static void MapModerationEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/disputes", async (AppDbContext db, CancellationToken ct) =>
            await db.Disputes.Where(d => d.Status == DisputeStatus.Open).OrderBy(d => d.CreatedAt)
                .Select(d => new { d.Id, d.CreatedAt, d.Contact, d.Reason, identifier = new { d.Identifier.Id, d.Identifier.Kind, d.Identifier.Masked, d.Identifier.PublicValue } })
                .ToListAsync(ct));

        // Accepting a dispute hides the identifier: it then scores as if it had never been reported.
        admin.MapPost("/disputes/{id:guid}", async (Guid id, ResolveDisputeRequest req, AppDbContext db, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var dispute = await db.Disputes.Include(d => d.Identifier).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (dispute is null) return Results.NotFound();
            dispute.Status = req.Accept ? DisputeStatus.Accepted : DisputeStatus.Rejected;
            if (req.Accept) dispute.Identifier.Hidden = true;
            await db.SaveChangesAsync(ct);
            loggers.CreateLogger(LogCategory).LogInformation("Admin resolved dispute {DisputeId}: {Status}", id, dispute.Status);
            return Results.NoContent();
        });

        admin.MapPost("/reports/{id:guid}/reject", async (Guid id, AppDbContext db, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var n = await db.Reports.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReportStatus.Rejected), ct);
            if (n == 0) return Results.NotFound();
            loggers.CreateLogger(LogCategory).LogInformation("Admin rejected report {ReportId}", id);
            return Results.NoContent();
        });

        admin.MapGet("/reports/recent", async (AppDbContext db, CancellationToken ct) =>
            await db.Reports.OrderByDescending(r => r.CreatedAt).Take(100)
                .Select(r => new { r.Id, r.CreatedAt, r.Category, r.Channel, r.Loss, r.Status,
                    identifiers = r.Identifiers.Select(i => new { i.Identifier.Kind, i.Identifier.Masked, i.Identifier.PublicValue }) })
                .ToListAsync(ct));
    }
}
