using Microsoft.EntityFrameworkCore;

namespace SecureDocs.Api.Data;

public class Client
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Industry { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

public class Batch
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string Reference { get; set; } = "";
    public string Source { get; set; } = "";
    public string Status { get; set; } = "";
    public int DocumentCount { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

public class Document
{
    public long Id { get; set; }
    public int BatchId { get; set; }
    public int ClientId { get; set; }
    public string DocumentType { get; set; } = "";
    public string Channel { get; set; } = "";
    public string RecipientName { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string MemberId { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public decimal? Amount { get; set; }
    public string Status { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

/// <summary>One QA camera read of a printed document: does the printed name/ZIP match the source data?</summary>
public class QaInspection
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    public int ClientId { get; set; }
    public string CameraId { get; set; } = "";
    public string ExpectedName { get; set; } = "";
    public string ScannedName { get; set; } = "";
    public string ExpectedPostal { get; set; } = "";
    public string ScannedPostal { get; set; } = "";
    public bool Passed { get; set; }
    public DateTime InspectedUtc { get; set; }
}

public class FtpTransfer
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string FileName { get; set; } = "";
    public string StoredPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Origin { get; set; } = "";
    public string Status { get; set; } = "";
    public int RecordCount { get; set; }
    public int? BatchId { get; set; }
    public DateTime UploadedUtc { get; set; }
    public DateTime? ProcessedUtc { get; set; }
    public string? Error { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime Utc { get; set; }
}

public class SecureDocsDb(DbContextOptions<SecureDocsDb> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<QaInspection> QaInspections => Set<QaInspection>();
    public DbSet<FtpTransfer> FtpTransfers => Set<FtpTransfer>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Client>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Industry).HasMaxLength(64);
            e.Property(x => x.ApiKey).HasMaxLength(64);
        });

        b.Entity<Batch>(e =>
        {
            e.HasIndex(x => new { x.ClientId, x.ReceivedUtc });
            e.Property(x => x.Reference).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(32);
        });

        b.Entity<Document>(e =>
        {
            // Intentionally NO index on RecipientName / PostalCode / AddressLine1 so
            // free-text searches scan the table — great for slow-query demos.
            e.HasIndex(x => x.BatchId);
            e.HasIndex(x => new { x.ClientId, x.Status });
            e.Property(x => x.DocumentType).HasMaxLength(32);
            e.Property(x => x.Channel).HasMaxLength(16);
            e.Property(x => x.RecipientName).HasMaxLength(128);
            e.Property(x => x.AddressLine1).HasMaxLength(128);
            e.Property(x => x.City).HasMaxLength(64);
            e.Property(x => x.State).HasMaxLength(2);
            e.Property(x => x.PostalCode).HasMaxLength(10);
            e.Property(x => x.MemberId).HasMaxLength(32);
            e.Property(x => x.Email).HasMaxLength(128);
            e.Property(x => x.Phone).HasMaxLength(32);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Status).HasMaxLength(16);
        });

        b.Entity<QaInspection>(e =>
        {
            e.HasIndex(x => x.DocumentId);
            e.HasIndex(x => new { x.ClientId, x.InspectedUtc });
            e.Property(x => x.CameraId).HasMaxLength(16);
            e.Property(x => x.ExpectedName).HasMaxLength(128);
            e.Property(x => x.ScannedName).HasMaxLength(128);
            e.Property(x => x.ExpectedPostal).HasMaxLength(10);
            e.Property(x => x.ScannedPostal).HasMaxLength(10);
        });

        b.Entity<FtpTransfer>(e =>
        {
            e.HasIndex(x => new { x.ClientId, x.UploadedUtc });
            e.HasIndex(x => x.Status);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.StoredPath).HasMaxLength(512);
            e.Property(x => x.Origin).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(16);
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => new { x.ClientId, x.Utc });
            e.Property(x => x.Actor).HasMaxLength(64);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(512);
        });
    }
}
