using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Aldanma.Api.Infrastructure;

/// <summary>
/// The /api/admin route group, protected by the X-Admin-Key header. Without a configured key, or with a wrong one,
/// every admin route answers 404 so its existence is not revealed. Features add their own moderation routes to it.
/// </summary>
public static class AdminAccess
{
    public static RouteGroupBuilder MapAdminGroup(this IEndpointRouteBuilder api) =>
        api.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
        {
            var key = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<AldanmaOptions>>().Value.AdminKey;
            var given = ctx.HttpContext.Request.Headers["X-Admin-Key"].ToString();
            if (string.IsNullOrEmpty(key) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key)))
                return Results.NotFound();
            return await next(ctx);
        }).RequireRateLimiting(RateLimits.Admin);
}
