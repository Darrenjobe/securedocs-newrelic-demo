using SecureDocs.Shared;
using Microsoft.EntityFrameworkCore;

namespace SecureDocs.Api.Data;

public static class Seeder
{
    static readonly (string Code, string Name, string Industry)[] SeedClients =
    [
        ("PCHP", "Pacific Coast Health Plan", "Health Insurance"),
        ("SMG", "Summit Medical Group", "Hospital Network"),
        ("EBA", "Evergreen Benefits Administrators", "Benefits / Payments"),
    ];

    public static async Task SeedAsync(SecureDocsDb db, int batchesPerClient, ILogger logger, CancellationToken ct = default)
    {
        var rng = new Random();
        var now = DateTime.UtcNow;

        foreach (var (code, name, industry) in SeedClients)
        {
            // Resumable: if a previous start was interrupted mid-seed, only the missing batches are added.
            var client = await db.Clients.FirstOrDefaultAsync(c => c.Code == code, ct);
            if (client is null)
            {
                client = new Client
                {
                    Code = code, Name = name, Industry = industry,
                    ApiKey = $"sd_{code.ToLowerInvariant()}_{Guid.NewGuid():N}"[..32],
                    CreatedUtc = now.AddDays(-90)
                };
                db.Clients.Add(client);
                await db.SaveChangesAsync(ct);
            }
            var existing = await db.Batches.CountAsync(b => b.ClientId == client.Id, ct);
            if (existing >= batchesPerClient) continue;
            logger.LogInformation("Seeding {Count} demo batches for {Client}...", batchesPerClient - existing, code);

            db.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var i = existing; i < batchesPerClient; i++)
            {
                var received = now.AddDays(-rng.Next(0, 30)).AddMinutes(-rng.Next(0, 1440));
                var docs = SyntheticData.GenerateDocuments(rng.Next(20, 80), rng);
                var batch = new Batch
                {
                    ClientId = client.Id,
                    Reference = $"{code}-{received:yyyyMMdd}-{i:D4}",
                    Source = rng.Next(3) switch { 0 => IngestionSources.Api, 1 => IngestionSources.Ftp, _ => IngestionSources.Portal },
                    Status = BatchStatus.Completed,
                    DocumentCount = docs.Count,
                    ReceivedUtc = received,
                    CompletedUtc = received.AddHours(rng.Next(2, 20))
                };
                db.Batches.Add(batch);
                await db.SaveChangesAsync(ct);

                var entities = docs.Select(d => new Document
                {
                    BatchId = batch.Id, ClientId = client.Id, DocumentType = d.DocumentType, Channel = d.Channel,
                    RecipientName = d.RecipientName, AddressLine1 = d.AddressLine1, City = d.City, State = d.State,
                    PostalCode = d.PostalCode, MemberId = d.MemberId, Email = d.Email, Phone = d.Phone,
                    Amount = d.Amount, CreatedUtc = received,
                    Status = d.Channel == Channels.Print ? DocumentStatus.QaPassed : DocumentStatus.Delivered,
                    CompletedUtc = batch.CompletedUtc
                }).ToList();
                db.Documents.AddRange(entities);
                await db.SaveChangesAsync(ct);

                var hadFailure = false;
                foreach (var d in entities.Where(d => d.Channel == Channels.Print))
                {
                    var pass = rng.Next(100) >= 2;
                    if (!pass) { d.Status = DocumentStatus.QaFailed; hadFailure = true; db.Entry(d).State = EntityState.Modified; }
                    db.QaInspections.Add(new QaInspection
                    {
                        DocumentId = d.Id, ClientId = client.Id, CameraId = $"CAM-{rng.Next(1, 9):D2}",
                        ExpectedName = d.RecipientName,
                        ScannedName = pass ? d.RecipientName : SyntheticData.RandomName(rng),
                        ExpectedPostal = d.PostalCode, ScannedPostal = d.PostalCode,
                        Passed = pass, InspectedUtc = received.AddHours(1)
                    });
                }
                if (hadFailure) { batch.Status = BatchStatus.CompletedWithExceptions; db.Entry(batch).State = EntityState.Modified; }
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
            db.ChangeTracker.AutoDetectChangesEnabled = true;
        }
        logger.LogInformation("Demo data ready.");
    }
}
