using SecureDocs.Api.Services;
using NewRelic.Api.Agent;

namespace SecureDocs.Api.Workers;

/// <summary>Keeps the simulated manufacturing floor running: processes queued documents every few seconds.</summary>
public class ProductionFloorWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<ProductionFloorWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Production:AutoRun", true)) return;
        var interval = TimeSpan.FromSeconds(config.GetValue("Production:IntervalSeconds", 10));
        await DatabaseInitializer.Ready.WaitAsync(stoppingToken);
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunShiftAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Production floor run failed");
            }
            await Task.Delay(interval, stoppingToken);
        }
    }

    [Transaction(Web = false)]
    async Task RunShiftAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var production = scope.ServiceProvider.GetRequiredService<ProductionService>();
        // Snapshot first so the backlog is still reported when the run itself fails or the floor is stalled.
        await production.SnapshotBacklogAsync(ct);
        await production.RunAsync(null, config.GetValue("Production:BatchSize", 300), ct);
    }
}
