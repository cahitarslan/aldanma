using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aldanma.Api.Infrastructure;

namespace Aldanma.Tests;

/// <summary>
/// A Turnstile token is single-use. A form with a mistake in it must be rejected before the human check,
/// otherwise the corrected form fails with "human check failed" until the page is reloaded.
/// </summary>
public class HumanCheckOrderTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed class CountingHumanCheck : IHumanCheck
    {
        public int Calls;
        public Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(true);
        }
    }

    private (WebApplicationFactory<Program> App, CountingHumanCheck Human) Counting()
    {
        var human = new CountingHumanCheck();
        var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IHumanCheck>();
            s.AddSingleton<IHumanCheck>(human);
        }));
        return (app, human);
    }

    private static HttpClient From(WebApplicationFactory<Program> app, string ip)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add("CF-Connecting-IP", ip);
        return c;
    }

    [DatabaseFact]
    public async Task Invalid_forms_do_not_spend_the_human_check()
    {
        var (app, human) = Counting();
        await using var _ = app;
        var client = From(app, "198.18.90.1");

        // Dispute without an e-mail, then with a reason that is too short.
        var noMail = await client.PostAsJsonAsync("/api/disputes", new { input = "0532 777 66 55", contact = "", reason = "Bu numara benim, yanlışlıkla ihbar edilmiş." });
        Assert.Equal(HttpStatusCode.BadRequest, noMail.StatusCode);
        var shortReason = await client.PostAsJsonAsync("/api/disputes", new { input = "0532 777 66 55", contact = "owner@example.com", reason = "test" });
        Assert.Contains("20", await shortReason.Content.ReadAsStringAsync());
        // Report with no category, submission without consent.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/reports", new { inputs = new[] { "0532 777 66 55" }, channel = "whatsapp" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/submissions", new
        {
            text = "Kargonuz bekliyor, ödeme için bu bağlantıya tıklayın lütfen.", claim = "missed", level = "no-signs", rulesVersion = "0.5.0", consent = false,
        })).StatusCode);
        Assert.Equal(0, human.Calls);

        // A valid report goes through the human check once.
        var ok = await client.PostAsJsonAsync("/api/reports", new { inputs = new[] { "0532 777 66 55" }, category = "fake-seller", channel = "whatsapp" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(1, human.Calls);
    }
}
