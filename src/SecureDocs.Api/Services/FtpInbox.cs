using SecureDocs.Api.Data;
using SecureDocs.Shared;

namespace SecureDocs.Api.Services;

/// <summary>
/// The landing zone for client file transfers. Files arrive via the portal's "Secure File Transfer"
/// page, the /api/ftp/upload endpoint, or the FTP poller, and are picked up by <see cref="Workers.FtpIngestionWorker"/>.
/// On App Service this lives under /home (persistent, shared storage).
/// </summary>
public class FtpInbox
{
    public string Root { get; }

    public FtpInbox(IConfiguration config, IWebHostEnvironment env)
    {
        var configured = config["Ftp:InboxPath"];
        Root = !string.IsNullOrWhiteSpace(configured) ? configured
            : Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME") is { Length: > 0 }
                ? "/home/data/securedocs/ftp-inbox"
                : Path.Combine(env.ContentRootPath, "App_Data", "ftp-inbox");
        Directory.CreateDirectory(Root);
    }

    public async Task<FtpTransfer> SaveAsync(SecureDocsDb db, Client client, string fileName, Stream content,
        string origin, CancellationToken ct)
    {
        var safeName = Path.GetFileName(fileName);
        var dir = Path.Combine(Root, client.Code);
        Directory.CreateDirectory(dir);
        // Unique per upload: two clients (or two parallel uploads) can send the same file name in the same millisecond.
        var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}_{safeName}");
        await using (var fs = File.Create(path)) await content.CopyToAsync(fs, ct);

        var transfer = new FtpTransfer
        {
            ClientId = client.Id, FileName = safeName, StoredPath = path, SizeBytes = new FileInfo(path).Length,
            Origin = origin, Status = "Pending", UploadedUtc = DateTime.UtcNow
        };
        db.FtpTransfers.Add(transfer);
        db.AuditLogs.Add(new AuditLog
        {
            ClientId = client.Id, Actor = origin, Action = "FileTransfer.Received", Utc = DateTime.UtcNow,
            Detail = $"{safeName} ({transfer.SizeBytes} bytes)"
        });
        await db.SaveChangesAsync(ct);
        return transfer;
    }

    public static List<DocumentRequest> ParseCsv(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return [];
        var header = lines[0].Split(',').Select(h => h.Trim()).ToList();
        int Col(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        var idx = new
        {
            Type = Col("DocumentType"), Channel = Col("Channel"), Name = Col("RecipientName"), Addr = Col("AddressLine1"),
            City = Col("City"), State = Col("State"), Zip = Col("PostalCode"), Member = Col("MemberId"),
            Email = Col("Email"), Phone = Col("Phone"), Amount = Col("Amount")
        };
        if (idx.Type < 0 || idx.Name < 0)
            throw new FormatException("CSV header must include at least DocumentType and RecipientName columns");

        string Get(string[] f, int i) => i >= 0 && i < f.Length ? f[i].Trim().Trim('"') : "";
        return lines.Skip(1).Select(l => l.Split(',')).Select(f => new DocumentRequest
        {
            DocumentType = Get(f, idx.Type), Channel = Get(f, idx.Channel), RecipientName = Get(f, idx.Name),
            AddressLine1 = Get(f, idx.Addr), City = Get(f, idx.City), State = Get(f, idx.State),
            PostalCode = Get(f, idx.Zip), MemberId = Get(f, idx.Member),
            Email = NullIfEmpty(Get(f, idx.Email)), Phone = NullIfEmpty(Get(f, idx.Phone)),
            Amount = decimal.TryParse(Get(f, idx.Amount), System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : null
        }).ToList();
    }

    static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
