using System.Diagnostics;
using Microsoft.AspNetCore.Http.Timeouts;

namespace Aldanma.Api.Infrastructure;

/// <summary>
/// Every request gets a deadline, so slow clients or a stuck dependency cannot pile up work. Lookups get longer because
/// each may wait for a few RDAP calls (see <see cref="RdapOptions.MaxFetchesPerRequest"/>). A timeout answers 504 with
/// the API's usual error shape.
/// </summary>
public static class RequestTimeouts
{
    public const string Lookup = "lookup";

    public static IServiceCollection AddAldanmaRequestTimeouts(this IServiceCollection services, LimitOptions limits) =>
        services.AddRequestTimeouts(o =>
        {
            o.DefaultPolicy = Policy(limits.RequestTimeoutSeconds);
            o.AddPolicy(Lookup, Policy(limits.LookupTimeoutSeconds));
        });

    private static RequestTimeoutPolicy Policy(int seconds) => new()
    {
        Timeout = TimeSpan.FromSeconds(seconds),
        TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
        WriteTimeoutResponse = ctx => ctx.Response.WriteAsJsonAsync(
            new ErrorResponse(ErrorResponse.Timeout, Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier)),
    };
}
