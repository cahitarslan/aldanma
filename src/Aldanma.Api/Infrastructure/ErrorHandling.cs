using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace Aldanma.Api.Infrastructure;

/// <summary>
/// Turns every unhandled exception into the API's single error shape. Malformed requests become 400; anything
/// else is logged with its trace id and returned as a generic 500 that leaks no detail.
/// </summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception exception, CancellationToken ct)
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier;
        if (exception is BadHttpRequestException bad)
        {
            // Unreadable JSON, wrong content type, body too large: the client's fault, not worth an error log.
            log.LogInformation("Rejected malformed request: {Reason}", bad.Message);
            ctx.Response.StatusCode = bad.StatusCode;
            await ctx.Response.WriteAsJsonAsync(new ErrorResponse(ErrorResponse.InvalidRequest, traceId), ct);
            return true;
        }
        if (exception is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer.
            return true;
        }

        log.LogError(exception, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(new ErrorResponse(ErrorResponse.Unexpected, traceId), ct);
        return true;
    }
}
