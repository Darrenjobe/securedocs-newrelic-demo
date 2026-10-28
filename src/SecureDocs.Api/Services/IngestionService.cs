using System.Diagnostics;
using System.Text.RegularExpressions;
using SecureDocs.Api.Data;
using SecureDocs.Api.Infrastructure;
using SecureDocs.Shared;
using NewRelic.Api.Agent;

namespace SecureDocs.Api.Services;

/// <summary>
/// Common ingestion pipeline for every channel (REST API, FTP drop, portal form):
/// validate → verify addresses (downstream HTTP call) → persist batch + documents → queue for production.
/// </summary>
public partial class IngestionService(
    SecureDocsDb db, FaultState faults, AddressVerificationClient addresses, SqlFaultQueries sqlFaults,
    ILogger<IngestionService> logger)
{
    [Trace]
    public async Task<SubmitBatchResponse> IngestAsync(Client client, string source, string? reference,
        List<DocumentRequest> documents, string actor, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        reference = string.IsNullOrWhiteSpace(reference) ? $"{client.Code}-{DateTime.UtcNow:yyyyMMddHHmmss}" : reference.Trim();

        Telemetry.Client(client.Code);
        Telemetry.Attr("ingestion.source", source);
        Telemetry.Attr("batch.reference", reference);
        Telemetry.Attr("batch.documentCount", documents.Count);

        if (faults.RollError())
        {
            var bad = documents.Count > 0 ? Phi.Mask(documents[Random.Shared.Next(documents.Count)].MemberId) : "n/a";
            throw new PhiValidationException(
                $"Eligibility checksum mismatch for member {bad} in batch {reference}: record rejected by HIPAA validation rules");
        }

        var (valid, warnings) = Validate(documents);
        if (valid.Count == 0)
            throw new PhiValidationException($"Batch {reference} contained no valid documents ({warnings.Count} rejected)");

        if (faults.Current.DatabaseErrors)
        {
            // "Duplicate check" against a legacy archive table that no longer exists.
            await sqlFaults.QueryArchivedPrintJobsAsync(client.Id, ct);
        }

        var verify = await addresses.VerifyAsync(valid, null, ct);
        if (verify.Undeliverable > 0) warnings.Add($"{verify.Undeliverable} address(es) flagged by NCOA/CASS verification");
        Telemetry.Attr("address.checked", verify.Checked);
        Telemetry.Attr("address.undeliverable", verify.Undeliverable);

        var now = DateTime.UtcNow;
        var batch = new Batch
        {
            ClientId = client.Id, Reference = reference, Source = source, Status = BatchStatus.Received,
            DocumentCount = valid.Count, ReceivedUtc = now
        };
        db.Batches.Add(batch);
        await db.SaveChangesAsync(ct);

        db.Documents.AddRange(valid.Select(d => new Document
        {
            BatchId = batch.Id, ClientId = client.Id, DocumentType = d.DocumentType, Channel = d.Channel,
            RecipientName = d.RecipientName.Trim(), AddressLine1 = d.AddressLine1.Trim(), City = d.City.Trim(),
            State = d.State.Trim().ToUpperInvariant(), PostalCode = d.PostalCode.Trim(), MemberId = d.MemberId.Trim(),
            Email = d.Email, Phone = d.Phone, Amount = d.Amount, Status = DocumentStatus.Queued, CreatedUtc = now
        }));
        db.AuditLogs.Add(new AuditLog
        {
            ClientId = client.Id, Actor = actor, Action = $"Ingest.{source}", Utc = now,
            Detail = $"Batch {reference}: {valid.Count} accepted, {documents.Count - valid.Count} rejected"
        });
        await db.SaveChangesAsync(ct);

        Telemetry.Attr("batch.id", batch.Id);
        // Payment value entering the plant (a batch total only; never per-person amounts).
        var amountTotal = (double)valid.Sum(d => d.Amount ?? 0m);
        Telemetry.Attr("batch.amountTotal", amountTotal);
        Telemetry.Event("DocumentBatchIngested", new Dictionary<string, object>
        {
            ["clientCode"] = client.Code, ["clientTier"] = ClientProfiles.TierOf(client.Code),
            ["source"] = source, ["batchId"] = batch.Id,
            ["accepted"] = valid.Count, ["rejected"] = documents.Count - valid.Count,
            ["addressesChecked"] = verify.Checked, ["undeliverable"] = verify.Undeliverable,
            ["printCount"] = valid.Count(d => d.Channel == Channels.Print),
            ["digitalCount"] = valid.Count(d => d.Channel != Channels.Print),
            ["amountTotal"] = amountTotal,
            ["durationMs"] = sw.ElapsedMilliseconds
        });
        logger.LogInformation("Ingested batch {BatchId} ({Reference}) for {Client} via {Source}: {Accepted} accepted, {Rejected} rejected",
            batch.Id, reference, client.Code, source, valid.Count, documents.Count - valid.Count);

        return new SubmitBatchResponse(batch.Id, reference, valid.Count, documents.Count - valid.Count, batch.Status, warnings);
    }

    [Trace]
    static (List<DocumentRequest> Valid, List<string> Warnings) Validate(List<DocumentRequest> docs)
    {
        var valid = new List<DocumentRequest>();
        var warnings = new List<string>();
        for (var i = 0; i < docs.Count; i++)
        {
            var d = docs[i];
            string? problem =
                !DocumentTypes.All.Contains(d.DocumentType) ? $"unknown document type '{d.DocumentType}'" :
                !Channels.All.Contains(d.Channel) ? $"unknown channel '{d.Channel}'" :
                string.IsNullOrWhiteSpace(d.RecipientName) ? "missing recipient name" :
                string.IsNullOrWhiteSpace(d.MemberId) ? "missing member id" :
                d.Channel == Channels.Print && (string.IsNullOrWhiteSpace(d.AddressLine1) || !ZipRegex().IsMatch(d.PostalCode ?? ""))
                    ? "invalid mailing address" :
                d.Channel == Channels.Email && string.IsNullOrWhiteSpace(d.Email) ? "email channel without email address" :
                d.Channel == Channels.Sms && string.IsNullOrWhiteSpace(d.Phone) ? "SMS channel without phone number" :
                null;
            if (problem is null) valid.Add(d);
            else warnings.Add($"Row {i + 1}: {problem}");
        }
        return (valid, warnings);
    }

    [GeneratedRegex(@"^\d{5}(-\d{4})?$")]
    private static partial Regex ZipRegex();
}
