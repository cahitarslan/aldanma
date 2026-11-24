using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aldanma.Api.Features.Notifications;

public enum ModerationEvent { Report, Dispute, Submission }

public interface IModerationNotifier
{
    /// <summary>Records that something new waits for review. Never blocks and never throws.</summary>
    void Notify(ModerationEvent e);
}

/// <summary>
/// The queue between the request handlers and <see cref="ModerationMailService"/>. Bounded, and full means dropped:
/// a flood of reports must slow nothing down, and one mail already says "there is a lot waiting".
/// </summary>
public sealed class ModerationNotifier(IOptions<AldanmaOptions> options) : IModerationNotifier
{
    private readonly Channel<ModerationEvent> _queue = Channel.CreateBounded<ModerationEvent>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public bool Enabled { get; } = options.Value.Notify.IsEnabled;

    public ChannelReader<ModerationEvent> Reader => _queue.Reader;

    public void Notify(ModerationEvent e)
    {
        if (Enabled) _queue.Writer.TryWrite(e);
    }
}

/// <summary>
/// Turns moderation events into short e-mails for the site owner. Events are batched (one mail for a burst) and
/// throttled (at most one mail per <see cref="NotifyOptions.MinIntervalSeconds"/>), so a flood cannot fill the inbox
/// or use up the mail quota. A mail carries only counts: never a phone number, IBAN, address or message text.
/// </summary>
public sealed class ModerationMailService(
    ModerationNotifier notifier,
    IServiceScopeFactory scopes,
    IOptions<AldanmaOptions> options,
    AldanmaMetrics metrics,
    TimeProvider time,
    ILogger<ModerationMailService> log) : BackgroundService
{
    /// <summary>A mail that keeps failing is given up after this many tries; the next event starts over.</summary>
    public const int MaxAttempts = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!notifier.Enabled)
        {
            log.LogInformation("Moderation e-mails are off (Aldanma:Notify is not configured)");
            return;
        }

        var o = options.Value.Notify;
        var news = new Dictionary<ModerationEvent, int>();
        var lastAttempt = DateTimeOffset.MinValue;
        var attempts = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (news.Count == 0)
            {
                if (!await notifier.Reader.WaitToReadAsync(stoppingToken)) return;
                // Give a burst a moment to arrive, so it becomes one mail.
                await Task.Delay(TimeSpan.FromSeconds(o.BatchDelaySeconds), time, stoppingToken);
            }
            var wait = lastAttempt + TimeSpan.FromSeconds(o.MinIntervalSeconds) - time.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, time, stoppingToken);

            while (notifier.Reader.TryRead(out var e)) news[e] = news.GetValueOrDefault(e) + 1;
            lastAttempt = time.GetUtcNow();
            attempts++;
            if (await TrySendAsync(news, stoppingToken) || attempts >= MaxAttempts)
            {
                if (attempts >= MaxAttempts && news.Count > 0) log.LogError("Moderation e-mail given up after {Attempts} tries", attempts);
                news.Clear();
                attempts = 0;
            }
        }
    }

    private async Task<bool> TrySendAsync(Dictionary<ModerationEvent, int> news, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var pending = await PendingAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), ct);
            var sent = await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(Compose(news, pending), ct);
            metrics.NotificationSent(sent);
            if (sent) log.LogInformation("Moderation e-mail sent: {Reports} reports, {Disputes} disputes, {Submissions} submissions",
                news.GetValueOrDefault(ModerationEvent.Report), news.GetValueOrDefault(ModerationEvent.Dispute), news.GetValueOrDefault(ModerationEvent.Submission));
            return sent;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            metrics.NotificationSent(false);
            log.LogError(e, "Moderation e-mail could not be prepared");
            return false;
        }
    }

    public sealed record Pending(int OpenDisputes, int OpenSubmissions, int ReportsLastDay);

    private async Task<Pending> PendingAsync(AppDbContext db, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-1);
        return new Pending(
            await db.Disputes.CountAsync(d => d.Status == DisputeStatus.Open, ct),
            await db.Submissions.CountAsync(s => s.Status == SubmissionStatus.Open, ct),
            await db.Reports.CountAsync(r => r.Status == ReportStatus.Active && r.CreatedAt >= since, ct));
    }

    /// <summary>Counts only, in Turkish for the site owner.</summary>
    public static Email Compose(IReadOnlyDictionary<ModerationEvent, int> news, Pending pending)
    {
        var parts = new List<string>();
        if (news.GetValueOrDefault(ModerationEvent.Dispute) is > 0 and var d) parts.Add($"{d} yeni itiraz");
        if (news.GetValueOrDefault(ModerationEvent.Report) is > 0 and var r) parts.Add($"{r} yeni ihbar");
        if (news.GetValueOrDefault(ModerationEvent.Submission) is > 0 and var s) parts.Add($"{s} yeni mesaj");
        var subject = "Aldanma: " + (parts.Count > 0 ? string.Join(", ", parts) : "bekleyen kayıtlar");
        var text = $"""
            Aldanma'ya yeni kayıt geldi: {(parts.Count > 0 ? string.Join(", ", parts) : "-")}.

            Bekleyenler:
            - Açık itiraz: {pending.OpenDisputes}
            - İncelenmeyi bekleyen mesaj: {pending.OpenSubmissions}
            - Son 24 saatteki ihbar: {pending.ReportsLastDay}

            Ayrıntılar yönetim adreslerinde (X-Admin-Key ile): /api/admin/disputes, /api/admin/submissions, /api/admin/reports/recent
            Bu mailde gizlilik için numara, IBAN, adres ya da mesaj metni bulunmaz.
            """;
        return new Email(subject, text);
    }
}
