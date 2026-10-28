using SecureDocs.Api.Data;
using SecureDocs.Api.Infrastructure;
using SecureDocs.Api.Services;
using SecureDocs.Shared;
using FluentFTP;
using Microsoft.EntityFrameworkCore;
using NewRelic.Api.Agent;

namespace SecureDocs.Api.Workers;

/// <summary>
/// Optional: polls a real FTP/FTPS server for client drops. Expected layout on the server:
///   /{Ftp:RemoteRoot}/{CLIENTCODE}/*.csv   → downloaded into the inbox, then moved to .../processed/
/// Disabled unless Ftp:Host is configured.
/// </summary>
public class FtpPollingWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<FtpPollingWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(config["Ftp:Host"]))
        {
            logger.LogInformation("FTP poller disabled (Ftp:Host not set). Files can still be dropped via the portal or /api/ftp/upload.");
            return;
        }
        await DatabaseInitializer.Ready.WaitAsync(stoppingToken);
        var interval = TimeSpan.FromSeconds(config.GetValue("Ftp:PollSeconds", 30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "FTP poll failed");
            }
            await Task.Delay(interval, stoppingToken);
        }
    }

    [Transaction(Web = false)]
    async Task PollAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureDocsDb>();
        var inbox = scope.ServiceProvider.GetRequiredService<FtpInbox>();
        var root = config["Ftp:RemoteRoot"] ?? "/inbound";

        await using var ftp = new AsyncFtpClient(config["Ftp:Host"], config["Ftp:User"], config["Ftp:Password"],
            config.GetValue("Ftp:Port", 21));
        ftp.Config.EncryptionMode = config.GetValue("Ftp:UseTls", true) ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
        ftp.Config.ValidateAnyCertificate = config.GetValue("Ftp:AcceptAnyCertificate", false);
        await ftp.Connect(ct);

        foreach (var client in await db.Clients.ToListAsync(ct))
        {
            var dir = $"{root.TrimEnd('/')}/{client.Code}";
            if (!await ftp.DirectoryExists(dir, ct)) continue;
            foreach (var item in await ftp.GetListing(dir, ct))
            {
                if (item.Type != FtpObjectType.File || !item.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
                using var ms = new MemoryStream();
                await ftp.DownloadStream(ms, item.FullName, token: ct);
                ms.Position = 0;
                await inbox.SaveAsync(db, client, item.Name, ms, "FTP Poller", ct);
                await ftp.CreateDirectory($"{dir}/processed", ct);
                await ftp.MoveFile(item.FullName, $"{dir}/processed/{DateTime.UtcNow:yyyyMMddHHmmss}_{item.Name}", FtpRemoteExists.Overwrite, ct);
                Telemetry.Attr("ftp.filesDownloaded", 1);
                logger.LogInformation("Downloaded {File} from FTP for {Client}", item.Name, client.Code);
            }
        }
        await ftp.Disconnect(ct);
    }
}
