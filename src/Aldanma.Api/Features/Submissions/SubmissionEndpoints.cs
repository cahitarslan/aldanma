using Microsoft.EntityFrameworkCore;

namespace Aldanma.Api.Features.Submissions;

public static class SubmissionEndpoints
{
    public static void MapSubmissionEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/submissions", async (SubmissionRequest? req, SubmissionService submissions, IHumanCheck human, ClientAddress client, HttpContext ctx, CancellationToken ct) =>
        {
            if (req is null) return Results.BadRequest(new ErrorResponse("Boş gönderim."));
            // Validate before the human check: a Turnstile token is single-use, so a form with a mistake must not spend it.
            if (SubmissionService.Validate(req) is { } invalid) return Results.BadRequest(new ErrorResponse(invalid));
            if (!await human.VerifyAsync(req.HumanToken, client.Of(ctx), ct)) return Results.BadRequest(new ErrorResponse(ErrorResponse.HumanCheckFailed));
            var error = await submissions.SubmitAsync(req, ct);
            return error is null ? Results.Accepted() : Results.BadRequest(new ErrorResponse(error));
        }).RequireRateLimiting(RateLimits.Submission);
    }

    /// <summary>The review queue. tools/suggest-rules.mjs reads the accepted, labelled ones.</summary>
    public static void MapSubmissionAdminEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/submissions", async (AppDbContext db, string? status, CancellationToken ct) =>
            await db.Submissions.Where(s => s.Status == (status ?? SubmissionStatus.Open)).OrderBy(s => s.CreatedAt).Take(500)
                .Select(s => new { s.Id, s.CreatedAt, s.Text, s.Sender, s.Claim, s.Level, s.RulesVersion, s.Status, s.Label })
                .ToListAsync(ct));

        admin.MapPost("/submissions/{id:guid}", async (Guid id, ResolveSubmissionRequest req, SubmissionService submissions, CancellationToken ct) =>
        {
            if (!SubmissionService.IsValidResolution(req)) return Results.BadRequest(new ErrorResponse("An accepted submission needs a label: scam or legit."));
            return await submissions.ResolveAsync(id, req, ct) ? Results.NoContent() : Results.NotFound();
        });
    }
}
