using SecureDocs.Api.Data;
using SecureDocs.Api.Infrastructure;
using SecureDocs.Api.Services;
using SecureDocs.Shared;
using Microsoft.EntityFrameworkCore;
using NewRelic.Api.Agent;

namespace SecureDocs.Api.Workers;

/// <summary>
/// Picks up pending file transfers from the inbox and runs them through the ingestion pipeline.
/// Each pickup is a New Relic non-web ("OtherTransaction") transaction.
/// </summary>
public class FtpIngestionWorker(IServiceScopeFactory scopes, ILogger<FtpIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await DatabaseInitializer.Ready.WaitAsync(stoppingToken);
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessPendingAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "FTP ingestion sweep failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    [Transaction(Web = false)]
    async Task ProcessPendingAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureDocsDb>();
        var ingestion = scope.ServiceProvider.GetRequiredService<IngestionService>();

        var pending = await db.FtpTransfers.Where(t => t.Status == "Pending").OrderBy(t => t.Id).Take(5).ToListAsync(ct);
        foreach (var t in pending)
        {
            var client = await db.Clients.FirstAsync(c => c.Id == t.ClientId, ct);
            t.Status = "Processing";
            await db.SaveChangesAsync(ct);
            // File pickup delay: how long the file sat in the inbox before processing started.
            var queueWaitMs = (long)(DateTime.UtcNow - DateTime.SpecifyKind(t.UploadedUtc, DateTimeKind.Utc)).TotalMilliseconds;
            Telemetry.Client(client.Code);
            // The file came from the client's transfer account: a rejected file impacts that client.
            Telemetry.User(t.Origin == "Synthetic Upload" ? "synthetic-monitor" : $"{client.Code}:file-transfer");
            Telemetry.Attr("ftp.queueWaitMs", queueWaitMs);
            Telemetry.Attr("ftp.fileSizeBytes", t.SizeBytes);
            try
            {
                var docs = FtpInbox.ParseCsv(await File.ReadAllTextAsync(t.StoredPath, ct));
                t.RecordCount = docs.Count;
                var source = t.Origin == "Synthetic Upload" ? IngestionSources.Synthetic : IngestionSources.Ftp;
                var result = await ingestion.IngestAsync(client, source,
                    Path.GetFileNameWithoutExtension(t.FileName), docs, $"ftp:{client.Code}", ct);
                t.BatchId = result.BatchId;
                t.Status = "Processed";
            }
            catch (Exception ex)
            {
                // The ingestion transaction failed, but keep the worker alive and surface the error.
                db.ChangeTracker.Clear();
                db.Attach(t);
                t.Status = "Failed";
                t.Error = $"{ex.GetType().Name}: {ex.Message}";
                Telemetry.NoticeError(ex, new Dictionary<string, object> { ["ftp.transferId"] = t.Id, ["client.code"] = client.Code });
                logger.LogError(ex, "Failed to ingest file {File} for {Client}", t.FileName, client.Code);
            }
            t.ProcessedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            Telemetry.Event("FileTransferProcessed", new Dictionary<string, object>
            {
                ["clientCode"] = client.Code, ["clientTier"] = ClientProfiles.TierOf(client.Code),
                ["origin"] = t.Origin, ["status"] = t.Status, ["records"] = t.RecordCount,
                ["sizeBytes"] = t.SizeBytes, ["queueWaitMs"] = queueWaitMs,
                ["processingMs"] = (long)(t.ProcessedUtc.Value - DateTime.SpecifyKind(t.UploadedUtc, DateTimeKind.Utc)).TotalMilliseconds - queueWaitMs
            });
        }
    }
}
