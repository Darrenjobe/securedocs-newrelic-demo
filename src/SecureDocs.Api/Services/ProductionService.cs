using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SecureDocs.Api.Data;
using SecureDocs.Api.Infrastructure;
using SecureDocs.Shared;
using Microsoft.EntityFrameworkCore;
using NewRelic.Api.Agent;

namespace SecureDocs.Api.Services;

/// <summary>
/// Simulates the manufacturing floor: compose each unique document, print (or send email/SMS),
/// then have a QA camera read the printed piece back and compare it with the source record.
/// A piece that fails QA is re-queued once for a reprint; if the reprint also fails it is pulled (QaFailed).
///
/// Business telemetry (custom events, all PHI-free):
///   QaInspection       one per camera read: passed, mismatchType, reprint, camera, document type, client/tier
///   BatchCompleted     one per finished batch: cycle time vs SLA, QA failures, reprints, value, print/digital mix
///   ProductionBacklog  one per client per production cycle: queue depth, oldest item, at-risk/overdue, value in flight
/// </summary>
public class ProductionService(SecureDocsDb db, FaultState faults, ILogger<ProductionService> logger)
{
    static readonly string[] Cameras = ["CAM-01", "CAM-02", "CAM-03", "CAM-04", "CAM-05", "CAM-06", "CAM-07", "CAM-08"];

    [Trace]
    public async Task<ProductionRunResult> RunAsync(string? clientCode, int maxDocuments, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        maxDocuments = Math.Clamp(maxDocuments, 1, 500);
        Telemetry.Client(clientCode);

        if (faults.Current.ProductionStalled)
        {
            Telemetry.Attr("production.stalled", true);
            logger.LogWarning("Production floor is stalled (fault injected); no documents processed");
            return new ProductionRunResult(0, 0, 0, 0, 0, sw.ElapsedMilliseconds, Stalled: true);
        }

        var clientCodes = await db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        int? clientId = clientCode is null ? null : clientCodes.FirstOrDefault(kv => kv.Value == clientCode).Key;

        var queued = await db.Documents
            .Where(d => d.Status == DocumentStatus.Queued && (clientId == null || d.ClientId == clientId))
            .OrderBy(d => d.Id)
            .Take(maxDocuments)
            .ToListAsync(ct);

        Telemetry.Attr("production.batchSize", queued.Count);
        if (queued.Count == 0) return new ProductionRunResult(0, 0, 0, 0, 0, sw.ElapsedMilliseconds);

        if (faults.RollError())
            throw new CompositionEngineException(
                $"Composition engine failed to render template '{queued[0].DocumentType}-v7' (font resource MICR-E13B not found)");

        // Pieces that were already inspected before are reprints.
        var ids = queued.Where(d => d.Channel == Channels.Print).Select(d => d.Id).ToList();
        var priorReads = await db.QaInspections.Where(q => ids.Contains(q.DocumentId))
            .GroupBy(q => q.DocumentId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        int printed = 0, delivered = 0, passed = 0, failed = 0, reprintsQueued = 0, reprints = 0;
        var now = DateTime.UtcNow;
        foreach (var doc in queued)
        {
            Compose(doc);
            var code = clientCodes.GetValueOrDefault(doc.ClientId, "");
            if (doc.Channel != Channels.Print)
            {
                delivered++;
                doc.Status = DocumentStatus.Delivered;
                doc.CompletedUtc = now;
                continue;
            }

            printed++;
            var isReprint = priorReads.ContainsKey(doc.Id);
            if (isReprint) reprints++;
            var inspection = Inspect(doc, now);
            db.QaInspections.Add(inspection);
            var mismatchType = MismatchType(inspection);

            if (inspection.Passed)
            {
                passed++;
                doc.Status = DocumentStatus.QaPassed;
                doc.CompletedUtc = now;
            }
            else
            {
                failed++;
                if (isReprint)
                {
                    doc.Status = DocumentStatus.QaFailed;   // second failure: pulled for manual review
                    doc.CompletedUtc = now;
                }
                else
                {
                    doc.Status = DocumentStatus.Queued;     // first failure: re-queued for a reprint
                    reprintsQueued++;
                }
                // Business error: the request succeeds, but a possible PHI misprint lands in Errors Inbox.
                Telemetry.NoticeError(
                    new QaMismatchException($"QA camera {inspection.CameraId} detected {mismatchType} mismatch on {doc.DocumentType}; " +
                                            (isReprint ? "reprint also failed, piece pulled for manual review" : "piece diverted for reprint")),
                    new Dictionary<string, object>
                    {
                        ["documentId"] = doc.Id, ["batchId"] = doc.BatchId, ["cameraId"] = inspection.CameraId,
                        ["documentType"] = doc.DocumentType, ["mismatchType"] = mismatchType, ["reprint"] = isReprint,
                        ["client.code"] = code, ["client.tier"] = ClientProfiles.TierOf(code)
                    });
            }

            Telemetry.Event("QaInspection", new Dictionary<string, object>
            {
                ["clientId"] = doc.ClientId, ["clientCode"] = code, ["clientTier"] = ClientProfiles.TierOf(code),
                ["documentType"] = doc.DocumentType, ["cameraId"] = inspection.CameraId,
                ["passed"] = inspection.Passed, ["mismatchType"] = mismatchType, ["reprint"] = isReprint
            });
        }
        await db.SaveChangesAsync(ct);

        await CompleteFinishedBatchesAsync(queued.Select(d => d.BatchId).Distinct().ToList(), clientCodes, ct);

        Telemetry.Attr("production.printed", printed);
        Telemetry.Attr("production.qaFailures", failed);
        Telemetry.Attr("production.reprintsQueued", reprintsQueued);
        Telemetry.Metric("Custom/Production/QaFailures", failed);
        Telemetry.Metric("Custom/Production/DocumentsProcessed", queued.Count);
        Telemetry.Metric("Custom/Production/Reprints", reprints);
        logger.LogInformation("Production run processed {Count} documents: {Printed} printed ({Reprints} reprints), {Delivered} digital, {Failed} QA failures",
            queued.Count, printed, reprints, delivered, failed);
        return new ProductionRunResult(queued.Count, printed, delivered, passed, failed, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Records the state of the production queue per client: depth, oldest item, documents at risk of missing
    /// the SLA, documents already past it, and the payment value still in flight.
    /// </summary>
    [Trace]
    public async Task SnapshotBacklogAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var clients = await db.Clients.AsNoTracking().Select(c => new { c.Id, c.Code }).ToListAsync(ct);
        var queued = await db.Documents.AsNoTracking()
            .Where(d => d.Status == DocumentStatus.Queued)
            .Select(d => new { d.ClientId, d.CreatedUtc, d.Amount })
            .ToListAsync(ct);

        int totalQueued = 0, totalAtRisk = 0, totalOverdue = 0;
        double oldestOverall = 0, valueOverall = 0;
        foreach (var c in clients)
        {
            var sla = ClientProfiles.SlaMinutesOf(c.Code);
            var mine = queued.Where(q => q.ClientId == c.Id)
                .Select(q => new { Age = (now - DateTime.SpecifyKind(q.CreatedUtc, DateTimeKind.Utc)).TotalMinutes, q.Amount })
                .ToList();
            var oldest = mine.Count == 0 ? 0 : Math.Round(mine.Max(m => m.Age), 1);
            var atRisk = mine.Count(m => m.Age >= sla * ClientProfiles.AtRiskShareOfSla && m.Age < sla);
            var overdue = mine.Count(m => m.Age >= sla);
            var value = (double)mine.Sum(m => m.Amount ?? 0m);

            Telemetry.Event("ProductionBacklog", new Dictionary<string, object>
            {
                ["clientCode"] = c.Code, ["clientTier"] = ClientProfiles.TierOf(c.Code), ["slaMinutes"] = sla,
                ["queuedDocuments"] = mine.Count, ["oldestQueuedMinutes"] = oldest,
                ["atRiskCount"] = atRisk, ["overdueCount"] = overdue, ["valueInFlight"] = value,
                ["stalled"] = faults.Current.ProductionStalled
            });
            totalQueued += mine.Count; totalAtRisk += atRisk; totalOverdue += overdue;
            oldestOverall = Math.Max(oldestOverall, oldest); valueOverall += value;
        }

        Telemetry.Attr("production.queueDepth", totalQueued);
        Telemetry.Attr("production.oldestQueuedMinutes", oldestOverall);
        Telemetry.Attr("production.atRiskCount", totalAtRisk);
        Telemetry.Attr("production.overdueCount", totalOverdue);
        Telemetry.Attr("production.valueInFlight", valueOverall);
    }

    /// <summary>Renders the personalised document (hash stands in for real composition work).</summary>
    [Trace]
    static void Compose(Document doc)
    {
        var payload = Encoding.UTF8.GetBytes($"{doc.Id}|{doc.RecipientName}|{doc.AddressLine1}|{doc.MemberId}|{doc.Amount}");
        for (var i = 0; i < 200; i++) payload = SHA256.HashData(payload);
    }

    [Trace]
    QaInspection Inspect(Document doc, DateTime now)
    {
        var misread = faults.Current.QaMismatches ? Random.Shared.Next(100) < 35 : Random.Shared.Next(1000) < 5;
        // A misread garbles the name, the ZIP, or both.
        var kind = misread ? Random.Shared.Next(3) : -1;
        var scannedName = kind is 0 or 2 ? SyntheticData.RandomName() : doc.RecipientName;
        var scannedPostal = kind is 1 or 2 ? "00000" : doc.PostalCode;
        return new QaInspection
        {
            DocumentId = doc.Id, ClientId = doc.ClientId, CameraId = Cameras[Random.Shared.Next(Cameras.Length)],
            ExpectedName = doc.RecipientName, ScannedName = scannedName,
            ExpectedPostal = doc.PostalCode, ScannedPostal = scannedPostal,
            Passed = scannedName == doc.RecipientName && scannedPostal == doc.PostalCode,
            InspectedUtc = now
        };
    }

    static string MismatchType(QaInspection i) =>
        (i.ScannedName != i.ExpectedName, i.ScannedPostal != i.ExpectedPostal) switch
        {
            (true, true) => "name_and_postal",
            (true, false) => "name",
            (false, true) => "postal",
            _ => "none"
        };

    /// <summary>Marks batches complete when nothing is left in the queue and records a BatchCompleted event (SLA attainment).</summary>
    async Task CompleteFinishedBatchesAsync(List<int> batchIds, Dictionary<int, string> clientCodes, CancellationToken ct)
    {
        var batches = await db.Batches.Where(b => batchIds.Contains(b.Id)).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var b in batches)
        {
            var docs = await db.Documents.AsNoTracking().Where(d => d.BatchId == b.Id)
                .Select(d => new { d.Id, d.Channel, d.Status, d.Amount }).ToListAsync(ct);
            if (docs.Any(d => d.Status == DocumentStatus.Queued)) { b.Status = BatchStatus.InProduction; continue; }

            var docIds = docs.Select(d => d.Id).ToList();
            var reads = await db.QaInspections.Where(q => docIds.Contains(q.DocumentId)).Select(q => q.DocumentId).ToListAsync(ct);
            var reprints = reads.Count - reads.Distinct().Count();
            var qaFailures = docs.Count(d => d.Status == DocumentStatus.QaFailed);

            b.Status = qaFailures > 0 ? BatchStatus.CompletedWithExceptions : BatchStatus.Completed;
            b.CompletedUtc = now;

            var code = clientCodes.GetValueOrDefault(b.ClientId, "");
            var sla = ClientProfiles.SlaMinutesOf(code);
            var cycleMinutes = Math.Round((now - DateTime.SpecifyKind(b.ReceivedUtc, DateTimeKind.Utc)).TotalMinutes, 2);
            var slaMet = cycleMinutes <= sla;
            var amount = (double)docs.Sum(d => d.Amount ?? 0m);

            Telemetry.Event("BatchCompleted", new Dictionary<string, object>
            {
                ["clientCode"] = code, ["clientTier"] = ClientProfiles.TierOf(code), ["batchId"] = b.Id, ["source"] = b.Source,
                ["documents"] = docs.Count, ["printCount"] = docs.Count(d => d.Channel == Channels.Print),
                ["digitalCount"] = docs.Count(d => d.Channel != Channels.Print),
                ["qaFailures"] = qaFailures, ["reprints"] = reprints, ["amountTotal"] = amount,
                ["cycleTimeMinutes"] = cycleMinutes, ["slaMinutes"] = sla, ["slaMet"] = slaMet,
                ["status"] = b.Status
            });
            if (!slaMet)
            {
                Telemetry.NoticeError(
                    new SlaBreachException($"Batch {b.Reference} for {code} ({ClientProfiles.TierOf(code)}) completed in {cycleMinutes:0.#} min, SLA is {sla} min"),
                    new Dictionary<string, object>
                    {
                        ["batchId"] = b.Id, ["client.code"] = code, ["client.tier"] = ClientProfiles.TierOf(code),
                        ["cycleTimeMinutes"] = cycleMinutes, ["slaMinutes"] = sla
                    });
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
