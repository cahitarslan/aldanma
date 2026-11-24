using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aldanma.Api.Features.Blocklist;

/// <summary>
/// Keeps the malicious address list published by USOM (the national CERT of Türkiye) in the database, so lookups
/// are useful from the first day, before any community report exists.
/// <para>
/// The list lives in PostgreSQL, so a restart or redeploy keeps it. Only the very first import (and a periodic full
/// resync, which also catches removals) reads the whole list, about 50 pages; every other run asks only for entries
/// added since the last import, usually one page.
/// </para>
/// <para>
/// Each page is saved as soon as it arrives, and where the run stands is kept in blocklist_import_runs. A run that
/// stops (a USOM outage, a redeploy) resumes from that page on the next start instead of starting over. Removals
/// ("USOM dropped these") are applied only after a full run has read every page.
/// </para>
/// Progress is logged and shown in /api/meta (blocklistImport). A failed run is logged as an error and counted in
/// aldanma.blocklist.imports; everything saved so far stays.
/// </summary>
public sealed class UsomFeedService(
    IServiceScopeFactory scopes,
    BlocklistImportStatus status,
    IOptions<AldanmaOptions> options,
    AldanmaMetrics metrics,
    TimeProvider time,
    ILogger<UsomFeedService> log) : BackgroundService
{
    public const string Source = "usom";

    /// <summary>An unfinished run older than this is dropped and planned afresh, so a resume never picks up a stale list.</summary>
    public static readonly TimeSpan MaxResumeAge = TimeSpan.FromDays(2);

    /// <summary>After a failed run, try again this soon rather than after the full interval; the next run resumes.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value.Usom;
        if (!o.Enabled)
        {
            log.LogInformation("USOM import is disabled");
            return;
        }
        // Let the app finish starting (and migrations run) before the first download.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var ok = await RunOnceAsync(stoppingToken);
            await Task.Delay(ok ? TimeSpan.FromHours(Math.Max(1, o.IntervalHours)) : RetryAfterFailure, time, stoppingToken);
        }
    }

    /// <returns>False when the run failed; what it saved stays and the next run resumes from there.</returns>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var o = options.Value.Usom;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var importer = scope.ServiceProvider.GetRequiredService<BlocklistImporter>();
            var run = await ResumeOrPlanAsync(db, importer, o, ct);
            var full = run.Mode == BlocklistImportStatus.Full;

            status.Start(run.Mode);
            log.LogInformation("USOM import {Action}: {Mode}{Since}, from list type {Type} page {Page}",
                run.NextPage > 1 || run.TypeIndex > 0 ? "resumed" : "started", full ? "full list" : "refresh",
                run.Since is { } d ? $" since {d:yyyy-MM-dd}" : "", run.TypeIndex, run.NextPage);

            try
            {
                await scope.ServiceProvider.GetRequiredService<UsomClient>().ReadAsync(run.Since, new UsomPosition(run.TypeIndex, run.NextPage), status,
                    async (entries, next) =>
                    {
                        await importer.UpsertAsync(Source, entries, run.StartedAt, ct);
                        (run.TypeIndex, run.NextPage, run.UpdatedAt) = (next.TypeIndex, next.Page, time.GetUtcNow());
                        await db.SaveChangesAsync(ct);
                    }, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Fail("download failed", e);
                return false;
            }

            if (full) await importer.RemoveUnseenAsync(Source, run.StartedAt, ct);
            db.BlocklistImportRuns.Remove(run);
            await db.SaveChangesAsync(ct);
            status.Succeed();
            metrics.BlocklistImport(Source, succeeded: true);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Fail("import failed", e);
            return false;
        }
    }

    /// <summary>The unfinished run, if there is a recent one; otherwise a new run as <see cref="BlocklistImporter.PlanAsync"/> decides.</summary>
    private async Task<BlocklistImportRun> ResumeOrPlanAsync(AppDbContext db, BlocklistImporter importer, UsomOptions o, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var open = await db.BlocklistImportRuns.FirstOrDefaultAsync(r => r.Source == Source, ct);
        if (open is not null && now - open.StartedAt <= MaxResumeAge) return open;
        if (open is not null)
        {
            log.LogWarning("USOM import from {Started:u} never finished and is too old to resume; starting a new one", open.StartedAt);
            db.BlocklistImportRuns.Remove(open);
            await db.SaveChangesAsync(ct);
        }

        var plan = await importer.PlanAsync(Source, TimeSpan.FromDays(o.FullSyncDays), ct);
        var run = new BlocklistImportRun
        {
            Source = Source,
            Mode = plan.Full ? BlocklistImportStatus.Full : BlocklistImportStatus.Incremental,
            StartedAt = now,
            Since = plan.Since,
            UpdatedAt = now,
        };
        db.BlocklistImportRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private void Fail(string publicReason, Exception e)
    {
        status.Fail(publicReason);
        metrics.BlocklistImport(Source, succeeded: false);
        log.LogError(e, "USOM import failed ({Reason}); what was saved stays, and the next run in {Minutes} minutes resumes from the last saved page",
            publicReason, RetryAfterFailure.TotalMinutes);
    }
}
