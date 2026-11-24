namespace Aldanma.Api.Features.Disputes;

public static class DisputeEndpoints
{
    public static void MapDisputeEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/disputes", async (DisputeRequest? req, DisputeService disputes, IHumanCheck human, ClientAddress client, HttpContext ctx, CancellationToken ct) =>
        {
            if (req is null) return Results.BadRequest(new ErrorResponse("Boş itiraz."));
            if (!await human.VerifyAsync(req.HumanToken, client.Of(ctx), ct)) return Results.BadRequest(new ErrorResponse(ErrorResponse.HumanCheckFailed));
            var (ok, error) = await disputes.SubmitAsync(req, ct);
            return ok ? Results.Accepted() : Results.BadRequest(new ErrorResponse(error!));
        }).RequireRateLimiting(RateLimits.Dispute);
    }
}
