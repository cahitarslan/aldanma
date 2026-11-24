using Microsoft.Extensions.Options;

namespace Aldanma.Api.Features.Blocklist;

/// <summary>
/// Downloads the malicious address list published by USOM (the national CERT of Türkiye) on a schedule.
/// This seeds the database so lookups are useful from the first day, before any community report exists.
/// A failed run is logged as an error and counted in aldanma.blocklist.imports; the previous list stays in place.
/// </summary>
public sealed class UsomFeedService(IServiceScopeFactory scopes, IOptions<AldanmaOptions> options, AldanmaMetrics metrics, ILogger<UsomFeedService> log)
    : BackgroundService
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
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var entries = await scope.ServiceProvider.GetRequiredService<UsomClient>().FetchAllAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<BlocklistImporter>().ImportAsync(Source, entries, stoppingToken);
                metrics.BlocklistImport(Source, succeeded: true);
            }
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                metrics.BlocklistImport(Source, succeeded: false);
                log.LogError(e, "USOM import failed; the previous list stays in place and the next try is in {Hours} hours", o.IntervalHours);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
