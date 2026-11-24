using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aldanma.Api.Features.Notifications;

namespace Aldanma.Tests;

public class NotificationComposeTests
{
    [Fact]
    public void A_mail_names_what_arrived_and_what_waits()
    {
        var news = new ModerationMailService.News(Reports: 2, Disputes: 1, Submissions: 0);
        var mail = ModerationMailService.Compose(news, new ModerationMailService.Pending(OpenDisputes: 3, OpenSubmissions: 0, ReportsLastDay: 5));

        Assert.Equal("Aldanma: 1 yeni itiraz, 2 yeni ihbar", mail.Subject);
        Assert.Contains("Açık itiraz: 3", mail.Text);
        Assert.Contains("Son 24 saatteki ihbar: 5", mail.Text);
    }
}

/// <summary>Moderation mails through the real request pipeline, with the mail provider replaced by a recorder.</summary>
public class NotificationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed class RecordingSender : IEmailSender
    {
        public ConcurrentQueue<Email> Sent { get; } = new();
        public Task<bool> SendAsync(Email email, CancellationToken ct)
        {
            Sent.Enqueue(email);
            return Task.FromResult(true);
        }
    }

    private (WebApplicationFactory<Program> App, RecordingSender Sender) WithMail(int minIntervalSeconds)
    {
        var sender = new RecordingSender();
        var app = factory.WithWebHostBuilder(b => b
            .UseSetting("Aldanma:Notify:ResendApiKey", "re_test")
            .UseSetting("Aldanma:Notify:From", "Aldanma <aldanma@mail.example.com>")
            .UseSetting("Aldanma:Notify:To", "owner@example.com")
            .UseSetting("Aldanma:Notify:BatchDelaySeconds", "8")
            .UseSetting("Aldanma:Notify:MinIntervalSeconds", minIntervalSeconds.ToString())
            .ConfigureServices(s =>
            {
                s.RemoveAll<IEmailSender>();
                s.AddSingleton<IEmailSender>(sender);
                // ApiFactory removes every hosted service (USOM download); this test needs the mail worker back.
                s.AddHostedService<ModerationMailService>();
            }));
        return (app, sender);
    }

    private static HttpClient From(WebApplicationFactory<Program> app, string ip)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add("CF-Connecting-IP", ip);
        return c;
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int seconds = 10)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    [DatabaseFact]
    public async Task A_burst_becomes_one_mail_with_counts_and_no_identifiers()
    {
        var (app, sender) = WithMail(minIntervalSeconds: 3600);
        await using var _ = app;

        const string phone = "0532 888 77 66";
        var report = await From(app, "198.18.80.1").PostAsJsonAsync("/api/reports", new { inputs = new[] { phone }, category = "fake-seller", channel = "whatsapp" });
        Assert.Equal(HttpStatusCode.Created, report.StatusCode);
        var dispute = await From(app, "198.18.80.2").PostAsJsonAsync("/api/disputes", new
        {
            input = phone, contact = "owner@example.com", reason = "Bu numara benim iş hattım, yanlışlıkla ihbar edilmiş olmalı.",
        });
        Assert.Equal(HttpStatusCode.Accepted, dispute.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await From(app, "198.18.80.3").PostAsJsonAsync("/api/reports", new { inputs = new[] { "0532 888 77 67" }, category = "job", channel = "sms" })).StatusCode);

        // The batch window is generous so a slow test machine still gets all three requests in before the mail.
        Assert.True(await WaitFor(() => !sender.Sent.IsEmpty, seconds: 20), "a mail should be sent");
        await Task.Delay(2000); // anything more would be a second mail inside the throttle window
        var mail = Assert.Single(sender.Sent);
        Assert.Equal("Aldanma: 1 yeni itiraz, 2 yeni ihbar", mail.Subject);
        Assert.Contains("Açık itiraz: 1", mail.Text);
        Assert.DoesNotContain("888", mail.Subject + mail.Text);
        Assert.DoesNotContain("owner@example.com", mail.Text);
    }

    [DatabaseFact]
    public async Task Without_a_key_nothing_is_queued_and_requests_still_work()
    {
        await using var app = factory.WithWebHostBuilder(_ => { });
        var r = await From(app, "198.18.81.1").PostAsJsonAsync("/api/reports", new { inputs = new[] { "0532 888 11 22" }, category = "job", channel = "sms" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.False(app.Services.GetRequiredService<ModerationNotifier>().Enabled);
    }
}

/// <summary>
/// A redeploy while a mail waits for the throttle used to lose it, because the counts lived in memory.
/// Its own class, so it has its own database and its own notification state.
/// </summary>
public class NotificationRestartTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed class RecordingSender : IEmailSender
    {
        public ConcurrentQueue<Email> Sent { get; } = new();
        public Task<bool> SendAsync(Email email, CancellationToken ct)
        {
            Sent.Enqueue(email);
            return Task.FromResult(true);
        }
    }

    private WebApplicationFactory<Program> WithMail(RecordingSender sender, int minIntervalSeconds) =>
        factory.WithWebHostBuilder(b => b
            .UseSetting("Aldanma:Notify:ResendApiKey", "re_test")
            .UseSetting("Aldanma:Notify:From", "Aldanma <aldanma@mail.example.com>")
            .UseSetting("Aldanma:Notify:To", "owner@example.com")
            .UseSetting("Aldanma:Notify:BatchDelaySeconds", "1")
            .UseSetting("Aldanma:Notify:MinIntervalSeconds", minIntervalSeconds.ToString())
            .ConfigureServices(s =>
            {
                s.RemoveAll<IEmailSender>();
                s.AddSingleton<IEmailSender>(sender);
                s.AddHostedService<ModerationMailService>();
            }));

    private static HttpClient From(WebApplicationFactory<Program> app, string ip)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add("CF-Connecting-IP", ip);
        return c;
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int seconds = 15)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    [DatabaseFact]
    public async Task A_mail_waiting_for_its_turn_survives_a_restart_and_nothing_new_sends_nothing()
    {
        const string phone = "0532 777 12 34";
        var before = new RecordingSender();
        await using (var app = WithMail(before, minIntervalSeconds: 3600))
        {
            Assert.Equal(HttpStatusCode.Created, (await From(app, "198.18.90.1").PostAsJsonAsync("/api/reports", new { inputs = new[] { phone }, category = "job", channel = "sms" })).StatusCode);
            Assert.True(await WaitFor(() => !before.Sent.IsEmpty), "the first report is mailed");
            Assert.Equal("Aldanma: 1 yeni ihbar", Assert.Single(before.Sent).Subject);

            // Inside the hour-long throttle: this dispute's mail waits... and the app is then redeployed.
            var dispute = await From(app, "198.18.90.2").PostAsJsonAsync("/api/disputes", new
            {
                input = phone, contact = "owner@example.com", reason = "Bu numara benim iş hattım, yanlışlıkla ihbar edilmiş olmalı.",
            });
            Assert.Equal(HttpStatusCode.Accepted, dispute.StatusCode);
            await Task.Delay(1500);
            Assert.Single(before.Sent);
        }

        // After the restart (the throttle has passed by now), the waiting dispute is mailed, and only it.
        var after = new RecordingSender();
        await using (var app = WithMail(after, minIntervalSeconds: 0))
        {
            _ = app.Services; // starts the host and its mail worker
            Assert.True(await WaitFor(() => !after.Sent.IsEmpty), "the waiting dispute is mailed after the restart");
            await Task.Delay(2500);
            Assert.Equal("Aldanma: 1 yeni itiraz", Assert.Single(after.Sent).Subject);
        }
    }
}
