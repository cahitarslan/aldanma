using Microsoft.EntityFrameworkCore;

namespace Aldanma.Api.Features.Feedback;

public static class FeedbackEndpoints
{
    public static void MapFeedbackEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/feedback", async (FeedbackRequest? req, FeedbackService feedback, CancellationToken ct) =>
        {
            if (req is null) return Results.BadRequest(new ErrorResponse("Boş geri bildirim."));
            var error = await feedback.SubmitAsync(req, ct);
            return error is null ? Results.Accepted() : Results.BadRequest(new ErrorResponse(error));
        }).RequireRateLimiting(RateLimits.Feedback);
    }

    /// <summary>Export for measurement: how often each level is judged right, per rule version.</summary>
    public static void MapFeedbackAdminEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/feedback", async (AppDbContext db, DateTimeOffset? since, CancellationToken ct) =>
            await db.Feedback.Where(f => since == null || f.CreatedAt >= since).OrderBy(f => f.CreatedAt).Take(10_000)
                .Select(f => new { f.CreatedAt, f.Correct, f.Level, f.Score, f.RulesVersion, f.Kinds, f.Categories, f.Signals })
                .ToListAsync(ct));
    }
}
