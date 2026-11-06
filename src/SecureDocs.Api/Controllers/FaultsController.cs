using SecureDocs.Api.Data;
using SecureDocs.Api.Infrastructure;
using SecureDocs.Api.Services;
using SecureDocs.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SecureDocs.Api.Controllers;

/// <summary>Fault-injection controls used by the portal's Fault Control panel.</summary>
[ApiController]
[Route("api/faults")]
public class FaultsController(FaultState faults, SecureDocsDb db, SqlFaultQueries sqlFaults,
    AddressVerificationClient addresses, ChangeMarkers markers, IConfiguration config) : ControllerBase
{
    /// <summary>If Faults:ControlKey is set, changing faults requires a matching X-Fault-Key header.</summary>
    bool Authorized() =>
        string.IsNullOrEmpty(config["Faults:ControlKey"]) || Request.Headers["X-Fault-Key"] == config["Faults:ControlKey"];

    [HttpGet]
    public ApiFaultState Get() => faults.Current;

    [HttpPut]
    public ActionResult<ApiFaultState> Put([FromBody] ApiFaultState state)
    {
        if (!Authorized()) return Unauthorized();
        var before = faults.Current;
        faults.Set(state);
        // Each switch that changes gets a deployment marker on this app: on = the "release" that introduced the
        // fault, off = the hotfix. Reset all faults comes through here too, so it records the fixes.
        foreach (var (toggle, on) in ChangeMarkers.Changes(before, faults.Current)) markers.Record(toggle, on);
        return faults.Current;
    }

    /// <summary>One-shot triggers: exception | db-error | slow-query | slow-trace</summary>
    [HttpPost("trigger/{kind}")]
    public async Task<IActionResult> Trigger(string kind, CancellationToken ct)
    {
        if (!Authorized()) return Unauthorized();
        Telemetry.Attr("fault.trigger", kind);
        var client = await db.Clients.AsNoTracking().OrderBy(c => c.Id).FirstAsync(ct);
        switch (kind)
        {
            case "exception":
                throw new PhiValidationException(
                    $"Manual fault: member record failed HIPAA field validation (client {client.Code}, field DOB out of range)");
            case "db-error":
                await sqlFaults.QueryArchivedPrintJobsAsync(client.Id, ct);
                return Ok();
            case "slow-query":
                var rows = await sqlFaults.SlowDocumentSearchAsync(client.Id, SyntheticData.RandomLastName(), ct);
                return Ok(new { kind, rows = rows.Count });
            case "slow-trace":
                var docs = SyntheticData.GenerateDocuments(5).Select(d => { d.Channel = Channels.Print; return d; });
                var r = await addresses.VerifyAsync(docs, faults.Current.SlowTraceMs, ct);
                return Ok(new { kind, r.ElapsedMs });
            case "clear-backlog":
                // Demo reset: finish everything waiting in the production queue without emitting business events,
                // so dashboards and Pathpoint return to a healthy baseline.
                var now = DateTime.UtcNow;
                var clearedDocs = await db.Documents.Where(d => d.Status == DocumentStatus.Queued)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(d => d.Status, d => d.Channel == Channels.Print ? DocumentStatus.QaPassed : DocumentStatus.Delivered)
                        .SetProperty(d => d.CompletedUtc, now), ct);
                var batches = await db.Batches.Where(b => b.Status == BatchStatus.Received || b.Status == BatchStatus.InProduction)
                    .ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BatchStatus.Completed).SetProperty(b => b.CompletedUtc, now), ct);
                Telemetry.Attr("backlog.clearedDocuments", clearedDocs);
                return Ok(new { kind, clearedDocuments = clearedDocs, completedBatches = batches });
            default:
                return BadRequest($"Unknown fault '{kind}'");
        }
    }
}

[ApiController]
public class HealthController(SecureDocsDb db) : ControllerBase
{
    [HttpGet("/health")]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        if (!Workers.DatabaseInitializer.IsReady)
            return StatusCode(503, new { status = "Initializing", detail = Workers.DatabaseInitializer.Status });
        try
        {
            var docs = await db.Documents.CountAsync(ct);
            return Ok(new { status = "Healthy", database = db.Database.ProviderName, documents = docs });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { status = "Unhealthy", error = ex.Message });
        }
    }
}
