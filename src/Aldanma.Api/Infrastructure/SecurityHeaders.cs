namespace Aldanma.Api.Infrastructure;

/// <summary>
/// Headers for a JSON-only API: nothing may be framed, sniffed, cached by shared caches or loaded as a document.
/// The static site sets its own, stricter policy (see tools/build-web.mjs).
/// </summary>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use((ctx, next) =>
    {
        // Set when the response starts, so error responses (whose headers are cleared on the way) get them too.
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            h["Referrer-Policy"] = "no-referrer";
            h.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        return next(ctx);
    });
}
