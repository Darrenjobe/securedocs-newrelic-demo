using SecureDocs.Api.Data;

namespace SecureDocs.Api.Workers;

/// <summary>
/// Creates the schema and seeds demo data in the background, so the API starts listening immediately
/// (App Service stops containers that don't answer within ~230 s, and seeding Azure SQL can take longer).
/// Other workers wait on <see cref="Ready"/> before touching the database.
/// </summary>
public class DatabaseInitializer(IServiceScopeFactory scopes, IConfiguration config, ILogger<DatabaseInitializer> logger)
    : BackgroundService
{
    static readonly TaskCompletionSource ReadySource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the schema exists and seeding has finished.</summary>
    public static Task Ready => ReadySource.Task;
    public static bool IsReady => ReadySource.Task.IsCompleted;
    public static string Status { get; private set; } = "Starting";

    /// <summary>
    /// Covering indexes for the dashboard's per-client summaries (counts and group-bys). Added with IF NOT EXISTS so
    /// databases created by earlier versions get them too. (The free-text search columns stay unindexed on purpose,
    /// so the slow-query demo still scans.)
    /// </summary>
    static async Task EnsurePerformanceIndexesAsync(SecureDocsDb db, CancellationToken ct)
    {
        if (!Microsoft.EntityFrameworkCore.SqlServerDatabaseFacadeExtensions.IsSqlServer(db.Database)) return;
        await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database, """
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Documents_Client_Summary')
                CREATE INDEX IX_Documents_Client_Summary ON Documents (ClientId)
                INCLUDE (DocumentType, Channel, Status, CreatedUtc);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_QaInspections_Client_Passed')
                CREATE INDEX IX_QaInspections_Client_Passed ON QaInspections (ClientId, Passed);
            """, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SecureDocsDb>();
                Status = "Creating schema";
                await db.Database.EnsureCreatedAsync(stoppingToken);
                await EnsurePerformanceIndexesAsync(db, stoppingToken);
                Status = "Seeding demo data";
                await Seeder.SeedAsync(db, config.GetValue("Seed:BatchesPerClient", 60), logger, stoppingToken);
                Status = "Ready";
                ReadySource.TrySetResult();
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                Status = $"Database not ready: {ex.GetType().Name} (attempt {attempt}), retrying";
                logger.LogWarning(ex, "Database initialization failed (attempt {Attempt}), retrying...", attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, attempt * 5)), stoppingToken);
            }
        }
    }
}
