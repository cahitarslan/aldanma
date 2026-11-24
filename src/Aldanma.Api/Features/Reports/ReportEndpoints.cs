namespace Aldanma.Api.Features.Reports;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/reports", async (ReportRequest? req, ReportService reports, IHumanCheck human, ClientAddress client, HttpContext ctx, CancellationToken ct) =>
        {
            if (req is null) return Results.BadRequest(new ErrorResponse("Boş ihbar."));
            var ip = client.Of(ctx);
            if (!await human.VerifyAsync(req.HumanToken, ip, ct)) return Results.BadRequest(new ErrorResponse(ErrorResponse.HumanCheckFailed));
            var outcome = await reports.SubmitAsync(req, ip, ct);
            return outcome.Ok
                ? Results.Created((string?)null, new { reportId = outcome.ReportId, accepted = outcome.Accepted, skipped = outcome.Skipped })
                : Results.BadRequest(new { error = outcome.Error, skipped = outcome.Skipped });
        }).RequireRateLimiting(RateLimits.Report);
    }
}
