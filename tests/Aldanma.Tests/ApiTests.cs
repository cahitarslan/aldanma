using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Aldanma.Api.Features.Blocklist;
using Aldanma.Core.Scoring;

namespace Aldanma.Tests;

public class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Iban = "TR85 0001 0000 0000 0012 3456 78";

    private HttpClient ClientFrom(string ip)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("CF-Connecting-IP", ip);
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>())!;

    private async Task<JsonElement> Lookup(params string[] items)
    {
        var r = await ClientFrom("198.51.100.1").PostAsJsonAsync("/api/lookup", new { items });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("identifiers");
    }

    private Task<HttpResponseMessage> Report(string ip, string category, params string[] inputs) =>
        ClientFrom(ip).PostAsJsonAsync("/api/reports", new { inputs, category, channel = "whatsapp", loss = "1k-10k", occurredOn = "2026-11" });

    [DatabaseFact]
    public async Task Check_analyses_a_message_and_scores_the_identifiers_inside_it()
    {
        var r = await ClientFrom("198.51.100.2").PostAsJsonAsync("/api/check", new
        {
            text = "Hakkınızda icra takibi başlatılmıştır. Dosyanızı kapatmak için: uyap-dosyasorgu.com/icra ya da 0532 123 45 67",
            sender = "mobile",
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Json(r);
        Assert.Equal("text", body.GetProperty("mode").GetString());
        Assert.Equal("high", body.GetProperty("level").GetString());
        Assert.Equal("fake-enforcement", body.GetProperty("message").GetProperty("categories")[0].GetProperty("id").GetString());
        var values = body.GetProperty("identifiers").EnumerateArray().Select(i => i.GetProperty("value").GetString()).ToList();
        Assert.Equal(["uyap-dosyasorgu.com", "+905321234567"], values);
    }

    [DatabaseFact]
    public async Task Reports_from_distinct_people_raise_the_level_and_repeats_are_ignored()
    {
        Assert.Equal("no-signs", (await Lookup(Iban))[0].GetProperty("level").GetString());

        Assert.Equal(HttpStatusCode.Created, (await Report("198.18.1.1", "deposit", Iban)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Report("198.18.1.1", "deposit", Iban)).StatusCode);
        Assert.Equal("suspicious", (await Lookup(Iban))[0].GetProperty("level").GetString());

        Assert.Equal(HttpStatusCode.Created, (await Report("198.18.2.1", "deposit", Iban)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Report("198.18.3.1", "fake-seller", Iban)).StatusCode);
        var a = (await Lookup(Iban))[0];
        Assert.Equal("high", a.GetProperty("level").GetString());
        var reports = a.GetProperty("reports");
        Assert.Equal(3, reports.GetProperty("reporterCount").GetInt32());
        Assert.Equal("deposit", reports.GetProperty("categories")[0].GetProperty("id").GetString());
        Assert.Equal(2, reports.GetProperty("categories")[0].GetProperty("count").GetInt32());
    }

    [DatabaseFact]
    public async Task Identifiers_reported_together_are_linked()
    {
        var r = await Report("198.18.10.1", "impersonation", "0533 999 88 77", "TR76 0006 2000 0000 0098 7654 32", "https://ptt-kargo-odeme.xyz/a");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var a = (await Lookup("05339998877"))[0];
        Assert.Equal(2, a.GetProperty("reports").GetProperty("coReportedCount").GetInt32());
        Assert.Contains(a.GetProperty("signals").EnumerateArray(), s => s.GetProperty("id").GetString() == "co-reported");
    }

    [DatabaseFact]
    public async Task Official_domains_cannot_be_reported()
    {
        var r = await Report("198.18.20.1", "phishing", "https://www.turkiye.gov.tr");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var a = (await Lookup("turkiye.gov.tr"))[0];
        Assert.True(a.GetProperty("official").GetBoolean());
        Assert.False(a.GetProperty("reportable").GetBoolean());
    }

    [DatabaseFact]
    public async Task Phone_numbers_and_ibans_are_never_stored_in_plain_text()
    {
        await Report("198.18.30.1", "relative", "0544 111 22 33", "TR26 0099 9000 0000 0011 1111 11");
        var rows = await factory.WithDb(db => db.Identifiers.AsNoTracking().ToListAsync());
        Assert.NotEmpty(rows);
        foreach (var row in rows.Where(r => r.Kind is "phone" or "iban"))
        {
            Assert.Null(row.PublicValue);
            Assert.DoesNotContain("1112233", row.Masked.Replace(" ", ""));
            Assert.DoesNotContain("00000000111111", row.Masked.Replace(" ", ""));
        }
    }

    [DatabaseFact]
    public async Task Usom_entries_make_a_domain_high_risk()
    {
        await factory.WithDb(async db =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var importer = scope.ServiceProvider.GetRequiredService<BlocklistImporter>();
            var result = await importer.ImportAsync("usom", "kargo-takip-ptt.xyz\nhttps://www.odeme-sayfasi.top/pay\n# comment\nnot a domain\n", default);
            Assert.Equal(2, result.Total);
            Assert.Equal(1, result.Skipped);
            return 0;
        });
        var a = (await Lookup("http://kargo-takip-ptt.xyz/takip"))[0];
        Assert.Equal("high", a.GetProperty("level").GetString());
        Assert.Contains(a.GetProperty("signals").EnumerateArray(), s => s.GetProperty("id").GetString() == "blocklist-usom");
    }

    [DatabaseFact]
    public async Task New_domains_are_flagged_by_age()
    {
        factory.Registrations.Known["firsat-telefon.com"] = new DomainRegistration(DateTimeOffset.UtcNow.AddDays(-4), "Example Registrar");
        var a = (await Lookup("firsat-telefon.com"))[0];
        Assert.Contains(a.GetProperty("signals").EnumerateArray(), s => s.GetProperty("id").GetString() == "domain-new");
    }

    [DatabaseFact]
    public async Task Accepted_dispute_hides_the_identifier()
    {
        const string phone = "0555 444 33 22";
        await Report("198.18.40.1", "fake-seller", phone);
        await Report("198.18.41.1", "fake-seller", phone);
        Assert.NotEqual("no-signs", (await Lookup(phone))[0].GetProperty("level").GetString());

        var d = await ClientFrom("198.18.42.1").PostAsJsonAsync("/api/disputes", new
        {
            input = phone, contact = "owner@example.com", reason = "Bu numara benim iş hattım, yanlışlıkla ihbar edilmiş olmalı.",
        });
        Assert.Equal(HttpStatusCode.Accepted, d.StatusCode);
        Assert.True((await Lookup(phone))[0].GetProperty("reports").GetProperty("disputed").GetBoolean());

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", "test-admin");
        var open = await Json(await admin.GetAsync("/api/admin/disputes"));
        var id = open.EnumerateArray().First().GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/disputes/{id}", new { accept = true })).StatusCode);
        var after = (await Lookup(phone))[0];
        Assert.Equal("no-signs", after.GetProperty("level").GetString());
        Assert.Equal(0, after.GetProperty("reports").GetProperty("reporterCount").GetInt32());
    }

    [DatabaseFact]
    public async Task A_report_lists_each_accepted_number_even_when_their_masked_forms_match()
    {
        // Both numbers mask to "+90532 *** 45 67".
        var r = await Report("198.18.30.1", "job", "0532 111 45 67", "0532 222 45 67");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var accepted = (await Json(r)).GetProperty("accepted").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(2, accepted.Count);
        Assert.Equal(2, accepted.Distinct().Count());
    }

    [DatabaseFact]
    public async Task Responses_carry_security_headers_and_malformed_json_gets_the_error_shape()
    {
        var content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");
        var r = await ClientFrom("198.51.100.20").PostAsync("/api/check", content);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        var body = await Json(r);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }

    [DatabaseFact]
    public async Task Health_endpoints_answer()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [DatabaseFact]
    public async Task Reporters_on_the_same_network_count_once()
    {
        const string phone = "0532 444 55 66";
        Assert.Equal(HttpStatusCode.Created, (await Report("198.18.200.1", "job", phone)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Report("198.18.200.2", "job", phone)).StatusCode);
        Assert.Equal(1, (await Lookup(phone))[0].GetProperty("reports").GetProperty("reporterCount").GetInt32());
    }

    [DatabaseFact]
    public async Task A_reporter_cannot_name_more_than_the_daily_limit()
    {
        HttpStatusCode last = default;
        for (var i = 0; i < 5; i++)
        {
            var batch = Enumerable.Range(0, 5).Select(j => $"0533 {i}{j}0 00 00").ToArray();
            last = (await Report("198.18.201.1", "other", batch)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.BadRequest, last);
    }

    [DatabaseFact]
    public async Task Feedback_takes_rule_ids_only()
    {
        var client = ClientFrom("198.51.100.30");
        var ok = await client.PostAsJsonAsync("/api/feedback", new
        {
            correct = false, level = "no-signs", score = 10, rulesVersion = "0.5.0",
            kinds = new[] { "phone" }, categories = Array.Empty<string>(), signals = new[] { "phone-mobile", "intent:money-request" },
        });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        var smuggled = await client.PostAsJsonAsync("/api/feedback", new
        {
            correct = true, level = "high", score = 80, rulesVersion = "0.5.0", signals = new[] { "Ahmet Yılmaz 0532 123 45 67" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, smuggled.StatusCode);
        Assert.Equal(1, await factory.WithDb(db => db.Feedback.CountAsync()));
    }

    [DatabaseFact]
    public async Task A_submitted_message_is_stored_with_numbers_masked_and_needs_consent()
    {
        var client = ClientFrom("198.51.100.31");
        var body = new
        {
            text = "Kaporayı " + Iban + " hesabına yatırın, sonra 0532 123 45 67 numarasını arayın.",
            sender = "whatsapp", claim = "missed", level = "no-signs", rulesVersion = "0.5.0", consent = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/submissions", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/submissions", body with { consent = true })).StatusCode);

        var stored = await factory.WithDb(db => db.Submissions.SingleAsync());
        Assert.DoesNotContain("123 45 67", stored.Text);
        Assert.DoesNotContain("3456 78", stored.Text);
        Assert.Contains("TR85 **** ****", stored.Text);

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", "test-admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/submissions/{stored.Id}", new { accept = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/submissions/{stored.Id}", new { accept = true, label = "scam" })).StatusCode);
        var accepted = await Json(await admin.GetAsync("/api/admin/submissions?status=accepted"));
        Assert.Equal("scam", accepted[0].GetProperty("label").GetString());
    }

    [DatabaseFact]
    public async Task Admin_endpoints_are_hidden_without_the_key()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/api/admin/disputes")).StatusCode);
    }

    [DatabaseFact]
    public async Task Invalid_requests_get_Turkish_errors()
    {
        var r = await ClientFrom("198.51.100.9").PostAsJsonAsync("/api/check", new { text = "" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("yazmadın", (await Json(r)).GetProperty("error").GetString());
        var bad = await Report("198.51.100.9", "not-a-category", Iban);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
