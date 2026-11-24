using Microsoft.Extensions.Options;

namespace Aldanma.Api.Features.Blocklist;

/// <summary>
/// Downloads the malicious URL list published by USOM (the national CERT of Türkiye) on a schedule.
/// This seeds the database so lookups are useful from the first day, before any community report exists.
/// </summary>
public sealed class UsomFeedService(IServiceScopeFactory scopes, IHttpClientFactory httpFactory, IOptions<AldanmaOptions> options, AldanmaMetrics metrics, ILogger<UsomFeedService> log)
    : BackgroundService
{
    public const string Source = "usom";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value.Usom;
        if (!o.Enabled) return;
        // Let the app finish starting (and migrations run) before the first download.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, o.IntervalHours)));
        do
        {
            try
            {
                var http = httpFactory.CreateClient(nameof(UsomFeedService));
                var content = await http.GetStringAsync(o.Url, stoppingToken);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BlocklistImporter>().ImportAsync(Source, content, stoppingToken);
                metrics.BlocklistImport(Source, succeeded: true);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                metrics.BlocklistImport(Source, succeeded: false);
                log.LogWarning(e, "USOM import failed; will retry on the next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
