using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aldanma.Api.Features.Notifications;

public enum ModerationEvent { Report, Dispute, Submission }

public interface IModerationNotifier
{
    /// <summary>Wakes the mail worker because something new waits for review. Never blocks and never throws.</summary>
    void Notify(ModerationEvent e);
}

/// <summary>
/// The wake-up line between the request handlers and <see cref="ModerationMailService"/>. Bounded, and full means
/// dropped: the events carry nothing the database does not, so a flood loses nothing and slows nothing down.
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
/// Short e-mails to the site owner about new reports, disputes and submissions (not feedback).
/// <para>
/// The database is the source of truth: a mail covers everything created after the stored
/// <see cref="NotificationState.LastNotifiedAt"/>, and sending advances it. Events from the request handlers only wake
/// the worker early; it also checks on start and every <see cref="PollInterval"/>. So a restart or redeploy while a
/// mail waits for its turn loses nothing (it used to: the counts lived in memory), and a failed send is tried again.
/// </para>
/// Bursts are batched (one mail) and throttled (at most one mail per <see cref="NotifyOptions.MinIntervalSeconds"/>,
/// counted from the stored LastSentAt), so a flood cannot fill the inbox or use up the mail quota.
/// A mail carries only counts: never a phone number, IBAN, address or message text.
/// </summary>
public sealed class ModerationMailService(
    ModerationNotifier notifier,
    IServiceScopeFactory scopes,
    IOptions<AldanmaOptions> options,
    AldanmaMetrics metrics,
    TimeProvider time,
    ILogger<ModerationMailService> log) : BackgroundService
{
    public const string StateKey = "moderation";

    /// <summary>How often the worker looks for unmentioned rows without being woken; bounds the delay after a failure.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The stored state is created before the host starts serving, so nothing created right after the very first
    /// start can predate it and go unmentioned.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (notifier.Enabled) await EnsureStateAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!notifier.Enabled)
        {
            log.LogInformation("Moderation e-mails are off (Aldanma:Notify is not configured)");
            return;
        }

        var o = options.Value.Notify;
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Sleep until an event arrives or the poll interval passes; the first round checks at once (after a restart).
            if (!first) await WaitForEventAsync(PollInterval, stoppingToken);
            first = false;
            // Give a burst a moment to arrive, so it becomes one mail.
            await Task.Delay(TimeSpan.FromSeconds(o.BatchDelaySeconds), time, stoppingToken);
            Drain();
            await SendIfDueAsync(o, stoppingToken);
        }
    }

    private async Task WaitForEventAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await notifier.Reader.WaitToReadAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private void Drain()
    {
        while (notifier.Reader.TryRead(out _)) { }
    }

    /// <summary>The first start records "now", so turning the feature on does not mail about the whole past.</summary>
    private async Task EnsureStateAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.NotificationStates.AnyAsync(s => s.Key == StateKey, ct)) return;
        db.NotificationStates.Add(new NotificationState { Key = StateKey, LastNotifiedAt = time.GetUtcNow() });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* another instance created it first */ }
    }

    public sealed record News(int Reports, int Disputes, int Submissions)
    {
        public bool Any => Reports + Disputes + Submissions > 0;
    }

    public sealed record Pending(int OpenDisputes, int OpenSubmissions, int ReportsLastDay);

    /// <summary>Sends one mail about everything not yet mentioned, if anything is; waits out the throttle first.</summary>
    /// <returns>True when a mail went out.</returns>
    public async Task<bool> SendIfDueAsync(NotifyOptions o, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = await db.NotificationStates.SingleAsync(s => s.Key == StateKey, ct);
            if (state.LastSentAt is { } last)
            {
                var wait = last + TimeSpan.FromSeconds(o.MinIntervalSeconds) - time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, ct);
                    Drain();
                }
            }

            var cutoff = time.GetUtcNow();
            var from = state.LastNotifiedAt;
            var news = new News(
                await db.Reports.CountAsync(r => r.CreatedAt > from && r.CreatedAt <= cutoff, ct),
                await db.Disputes.CountAsync(d => d.CreatedAt > from && d.CreatedAt <= cutoff, ct),
                await db.Submissions.CountAsync(s => s.CreatedAt > from && s.CreatedAt <= cutoff, ct));
            if (!news.Any) return false;

            var sent = await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(Compose(news, await PendingAsync(db, ct)), ct);
            metrics.NotificationSent(sent);
            if (!sent) return false; // the rows stay unmentioned; the next wake-up or poll tries again

            (state.LastNotifiedAt, state.LastSentAt) = (cutoff, time.GetUtcNow());
            await db.SaveChangesAsync(ct);
            log.LogInformation("Moderation e-mail sent: {Reports} reports, {Disputes} disputes, {Submissions} submissions",
                news.Reports, news.Disputes, news.Submissions);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            metrics.NotificationSent(false);
            log.LogError(e, "Moderation e-mail could not be prepared; it will be tried again");
            return false;
        }
    }

    private async Task<Pending> PendingAsync(AppDbContext db, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-1);
        return new Pending(
            await db.Disputes.CountAsync(d => d.Status == DisputeStatus.Open, ct),
            await db.Submissions.CountAsync(s => s.Status == SubmissionStatus.Open, ct),
            await db.Reports.CountAsync(r => r.Status == ReportStatus.Active && r.CreatedAt >= since, ct));
    }

    /// <summary>Counts only, in Turkish for the site owner.</summary>
    public static Email Compose(News news, Pending pending)
    {
        var parts = new List<string>();
        if (news.Disputes > 0) parts.Add($"{news.Disputes} yeni itiraz");
        if (news.Reports > 0) parts.Add($"{news.Reports} yeni ihbar");
        if (news.Submissions > 0) parts.Add($"{news.Submissions} yeni mesaj");
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
