using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Aldanma.Api.Data;
using Aldanma.Api.Infrastructure;

namespace Aldanma.Tests;

public class LimitKeyTests
{
    [Theory]
    [InlineData("198.51.100.7", "198.51.100.7")]           // IPv4: per address (CGNAT makes /24 too coarse)
    [InlineData("::ffff:198.51.100.7", "198.51.100.7")]    // IPv4-mapped IPv6 counts as the IPv4 address
    [InlineData("2001:db8:1:2::1", "20010DB800010002/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "20010DB800010002/64")]
    public void Clients_are_keyed_by_IPv4_address_or_IPv6_slash_64(string address, string key) =>
        Assert.Equal(key, ClientAddress.LimitKeyOf(address));
}

/// <summary>Global flood limits, with a tiny bucket (3 requests, then 1 per 10 s) so they trip quickly.</summary>
public class GlobalLimitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private WebApplicationFactory<Program> Tight() => factory.WithWebHostBuilder(b => b
        .UseSetting("Aldanma:Limits:PerClientBurst", "3")
        .UseSetting("Aldanma:Limits:PerClientPerMinute", "6"));

    private static HttpClient From(WebApplicationFactory<Program> app, string ip)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add("CF-Connecting-IP", ip);
        return c;
    }

    [DatabaseFact]
    public async Task Every_endpoint_shares_a_per_client_bucket_and_429_carries_Retry_After()
    {
        await using var app = Tight();
        var client = From(app, "198.51.100.40");
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/meta")).StatusCode);

        var limited = await client.GetAsync("/api/meta");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero, "429 must say when to retry");
        Assert.Contains("Çok sık", await limited.Content.ReadAsStringAsync());

        // A neighbour in the same IPv4 /24 is a different client (mobile CGNAT).
        Assert.Equal(HttpStatusCode.OK, (await From(app, "198.51.100.41").GetAsync("/api/meta")).StatusCode);
        // Health probes are never limited.
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [DatabaseFact]
    public async Task Rotating_addresses_inside_one_IPv6_slash_64_does_not_escape_the_limit()
    {
        await using var app = Tight();
        for (var i = 1; i <= 3; i++) Assert.Equal(HttpStatusCode.OK, (await From(app, $"2001:db8:7:7::{i}").GetAsync("/api/meta")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await From(app, "2001:db8:7:7:abcd::99").GetAsync("/api/meta")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await From(app, "2001:db8:7:8::1").GetAsync("/api/meta")).StatusCode);
    }

    [DatabaseFact]
    public async Task Meta_is_answered_from_the_output_cache()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("CF-Connecting-IP", "198.51.100.50");
        var first = await client.GetAsync("/api/meta");
        var second = await client.GetAsync("/api/meta");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Null(first.Headers.Age);
        Assert.NotNull(second.Headers.Age);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private RdapClient Rdap(AsyncServiceScope scope, CountingHandler handler, IOptions<AldanmaOptions> o, RdapThrottle throttle) => new(
        new HttpClient(handler), scope.ServiceProvider.GetRequiredService<AppDbContext>(), o, new RdapBudget(o), throttle,
        scope.ServiceProvider.GetRequiredService<AldanmaMetrics>(), TimeProvider.System, NullLogger<RdapClient>.Instance);

    [DatabaseFact]
    public async Task One_request_triggers_at_most_three_new_RDAP_fetches_and_a_busy_server_skips_them()
    {
        var o = Options.Create(new AldanmaOptions { Rdap = new RdapOptions { BaseUrl = "https://rdap.invalid/domain/", MaxFetchesPerRequest = 3, MaxConcurrentFetches = 1 } });
        await using var scope = factory.Services.CreateAsyncScope();

        var handler = new CountingHandler();
        var rdap = Rdap(scope, handler, o, new RdapThrottle(o));
        foreach (var d in new[] { "a-cap.example", "b-cap.example", "c-cap.example", "d-cap.example", "e-cap.example" })
            await rdap.GetAsync(d, CancellationToken.None);
        Assert.Equal(3, handler.Calls);

        // All server-wide slots taken: the lookup gives up on the age instead of queueing.
        var busy = new RdapThrottle(o);
        await busy.Gate.WaitAsync();
        var idle = new CountingHandler();
        Assert.Null(await Rdap(scope, idle, o, busy).GetAsync("f-busy.example", CancellationToken.None));
        Assert.Equal(0, idle.Calls);
    }
}
