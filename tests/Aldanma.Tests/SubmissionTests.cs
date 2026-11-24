using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace Aldanma.Tests;

/// <summary>Review-queue submissions in a database of their own, so counts are exact.</summary>
public class SubmissionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Iban = "TR85 0001 0000 0000 0012 3456 78";

    [DatabaseFact]
    public async Task A_confirmed_result_can_be_shared_masked_with_consent_and_unknown_claims_are_rejected()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("CF-Connecting-IP", "198.51.100.32");
        var body = new
        {
            text = "PTT: Kargonuz adrese teslim edilemedi. Ücreti " + Iban + " hesabına yatırın, bilgi 0532 123 45 67.",
            sender = "header", claim = "confirmed", level = "high", rulesVersion = "0.5.0", consent = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/submissions", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/submissions", body with { consent = true, claim = "spam" })).StatusCode);
        Assert.Equal(0, await factory.WithDb(db => db.Submissions.CountAsync()));

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/submissions", body with { consent = true })).StatusCode);
        var stored = await factory.WithDb(db => db.Submissions.SingleAsync());
        Assert.Equal("confirmed", stored.Claim);
        Assert.DoesNotContain("123 45 67", stored.Text);
        Assert.DoesNotContain("3456 78", stored.Text);
        Assert.Contains("TR85 **** ****", stored.Text);
    }
}
