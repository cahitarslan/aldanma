using Aldanma.Api.Features.Notifications;

namespace Aldanma.Api.Features.Reports;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/reports", async (ReportRequest? req, ReportService reports, IHumanCheck human, IModerationNotifier notifier, ClientAddress client, HttpContext ctx, CancellationToken ct) =>
        {
            if (req is null) return Results.BadRequest(new ErrorResponse("Boş ihbar."));
            // Validate before the human check: a Turnstile token is single-use, so a form with a mistake must not spend it.
            if (reports.Validate(req) is { } invalid) return Results.BadRequest(new { error = invalid.Error, skipped = invalid.Skipped });
            var ip = client.Of(ctx);
            if (!await human.VerifyAsync(req.HumanToken, ip, ct)) return Results.BadRequest(new ErrorResponse(ErrorResponse.HumanCheckFailed));
            var outcome = await reports.SubmitAsync(req, ip, ct);
            if (outcome.Ok) notifier.Notify(ModerationEvent.Report);
            return outcome.Ok
                ? Results.Created((string?)null, new { reportId = outcome.ReportId, accepted = outcome.Accepted, skipped = outcome.Skipped })
                : Results.BadRequest(new { error = outcome.Error, skipped = outcome.Skipped });
        }).RequireRateLimiting(RateLimits.Report);
    }
}
