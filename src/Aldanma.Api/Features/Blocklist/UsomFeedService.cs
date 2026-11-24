using Microsoft.Extensions.Options;

namespace Aldanma.Api.Features.Blocklist;

/// <summary>
/// Keeps the malicious address list published by USOM (the national CERT of Türkiye) in the database, so lookups
/// are useful from the first day, before any community report exists.
/// <para>
/// The list lives in PostgreSQL, so a restart or redeploy keeps it. Only the very first import (and a periodic full
/// resync, which also catches removals) reads the whole list, about 50 pages; every other run asks only for entries
/// added since the last import, usually one page. Progress is logged and shown in /api/meta (blocklistImport).
/// A failed run is logged as an error and counted in aldanma.blocklist.imports; the previous list stays in place.
/// </para>
/// </summary>
public sealed class UsomFeedService(
    IServiceScopeFactory scopes,
    BlocklistImportStatus status,
    IOptions<AldanmaOptions> options,
    AldanmaMetrics metrics,
    ILogger<UsomFeedService> log) : BackgroundService
{
    public const string Source = "usom";

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
        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, o.IntervalHours)));
        do await RunOnceAsync(o, stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(UsomOptions o, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var importer = scope.ServiceProvider.GetRequiredService<BlocklistImporter>();
            var plan = await importer.PlanAsync(Source, TimeSpan.FromDays(o.FullSyncDays), ct);
            status.Start(plan.Full ? BlocklistImportStatus.Full : BlocklistImportStatus.Incremental);
            log.LogInformation("USOM import started: {Mode}{Since}", plan.Full ? "full list" : "refresh", plan.Since is { } d ? $" since {d:yyyy-MM-dd}" : "");

            IReadOnlyList<string> entries;
            try
            {
                entries = await scope.ServiceProvider.GetRequiredService<UsomClient>().FetchAsync(plan.Since, status, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Fail("download failed", e, o);
                return;
            }
            await importer.ImportAsync(Source, entries, plan.Full, ct);
            status.Succeed();
            metrics.BlocklistImport(Source, succeeded: true);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Fail("import failed", e, o);
        }
    }

    private void Fail(string publicReason, Exception e, UsomOptions o)
    {
        status.Fail(publicReason);
        metrics.BlocklistImport(Source, succeeded: false);
        log.LogError(e, "USOM import failed ({Reason}); the previous list stays in place and the next try is in {Hours} hours", publicReason, o.IntervalHours);
    }
}
