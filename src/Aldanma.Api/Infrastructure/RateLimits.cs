using System.Globalization;
using System.Threading.RateLimiting;

namespace Aldanma.Api.Infrastructure;

/// <summary>
/// Two layers. A global limiter covers every request: a per-client token bucket (so one client cannot flood any
/// endpoint) chained with a server-wide concurrency cap (so a flood from many clients cannot exhaust database
/// connections). On top, the named per-endpoint fixed windows below guard the expensive or abusable actions.
/// Clients are keyed by <see cref="ClientAddress.LimitKey"/>: IPv4 per address, IPv6 per /64.
/// </summary>
public static class RateLimits
{
    public const string Check = "check";
    public const string Report = "report";
    public const string Dispute = "dispute";
    public const string Admin = "admin";
    public const string Feedback = "feedback";
    public const string Submission = "submission";

    public static IServiceCollection AddAldanmaRateLimits(this IServiceCollection services, LimitOptions limits) => services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.GlobalLimiter = PartitionedRateLimiter.CreateChained(PerClientBucket(limits), ServerWide(limits));
        o.AddPolicy(Check, ctx => PerClient(ctx, 30, TimeSpan.FromMinutes(1)));
        o.AddPolicy(Report, ctx => PerClient(ctx, 10, TimeSpan.FromHours(1)));
        o.AddPolicy(Dispute, ctx => PerClient(ctx, 3, TimeSpan.FromHours(1)));
        o.AddPolicy(Feedback, ctx => PerClient(ctx, 30, TimeSpan.FromHours(1)));
        o.AddPolicy(Submission, ctx => PerClient(ctx, 5, TimeSpan.FromHours(1)));
        // Slows down guessing of the admin key; real moderation needs far fewer requests.
        o.AddPolicy(Admin, ctx => PerClient(ctx, 60, TimeSpan.FromMinutes(1)));
        o.OnRejected = async (ctx, ct) =>
        {
            if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                ctx.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            ctx.HttpContext.RequestServices.GetRequiredService<AldanmaMetrics>().RateLimited(ctx.HttpContext.GetEndpoint()?.DisplayName);
            await ctx.HttpContext.Response.WriteAsJsonAsync(new ErrorResponse(ErrorResponse.TooManyRequests), ct);
        };
    });

    /// <summary>Health probes are never limited: the platform must see a live instance even while it sheds load.</summary>
    private static bool IsHealthProbe(HttpContext ctx) => ctx.Request.Path.StartsWithSegments("/health");

    private static PartitionedRateLimiter<HttpContext> PerClientBucket(LimitOptions l) => PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        IsHealthProbe(ctx)
            ? RateLimitPartition.GetNoLimiter("health")
            : RateLimitPartition.GetTokenBucketLimiter(Key(ctx), _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = l.PerClientBurst,
                // Refilled every 10 s, which spreads the per-minute rate evenly instead of in one lump.
                TokensPerPeriod = Math.Max(1, l.PerClientPerMinute / 6),
                ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    private static PartitionedRateLimiter<HttpContext> ServerWide(LimitOptions l) => PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        IsHealthProbe(ctx)
            ? RateLimitPartition.GetNoLimiter("health")
            : RateLimitPartition.GetConcurrencyLimiter("server", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = l.MaxConcurrentRequests,
                QueueLimit = l.ConcurrentQueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

    private static RateLimitPartition<string> PerClient(HttpContext ctx, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(Key(ctx), _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window });

    private static string Key(HttpContext ctx) => ctx.RequestServices.GetRequiredService<ClientAddress>().LimitKey(ctx);
}
