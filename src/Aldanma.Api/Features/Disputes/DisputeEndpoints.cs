using Aldanma.Api.Features.Notifications;

namespace Aldanma.Api.Features.Disputes;

public static class DisputeEndpoints
{
    public static void MapDisputeEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/disputes", async (DisputeRequest? req, DisputeService disputes, IHumanCheck human, IModerationNotifier notifier, ClientAddress client, HttpContext ctx, CancellationToken ct) =>
        {
            if (req is null) return Results.BadRequest(new ErrorResponse("Boş itiraz."));
            // Validate before the human check: a Turnstile token is single-use, so a form with a mistake must not spend it.
            if (await disputes.ValidateAsync(req, ct) is { } invalid) return Results.BadRequest(new ErrorResponse(invalid));
            if (!await human.VerifyAsync(req.HumanToken, client.Of(ctx), ct)) return Results.BadRequest(new ErrorResponse(ErrorResponse.HumanCheckFailed));
            var (ok, error) = await disputes.SubmitAsync(req, ct);
            if (ok) notifier.Notify(ModerationEvent.Dispute);
            return ok ? Results.Accepted() : Results.BadRequest(new ErrorResponse(error!));
        }).RequireRateLimiting(RateLimits.Dispute);
    }
}
