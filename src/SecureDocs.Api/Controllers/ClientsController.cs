using SecureDocs.Api.Data;
using SecureDocs.Api.Infrastructure;
using SecureDocs.Api.Services;
using SecureDocs.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SecureDocs.Api.Controllers;

[ApiController]
[Route("api/clients")]
public class ClientsController(SecureDocsDb db, FaultState faults, SqlFaultQueries sqlFaults) : ControllerBase
{
    [HttpGet]
    public async Task<List<ClientDto>> GetClients(CancellationToken ct) =>
        await db.Clients.OrderBy(c => c.Name)
            .Select(c => new ClientDto(c.Id, c.Code, c.Name, c.Industry, c.ApiKey))
            .ToListAsync(ct);

    [HttpGet("{code}/dashboard")]
    public async Task<ActionResult<DashboardDto>> GetDashboard(string code, CancellationToken ct)
    {
        var client = await FindClient(code, ct);
        if (client is null) return NotFound();

        if (faults.RollError())
            throw new DashboardAggregationException(
                $"SLA aggregation failed for {client.Code}: production calendar for plant 'SAC-2' returned no shifts");
        if (faults.Current.DatabaseErrors)
            await sqlFaults.QueryArchivedPrintJobsAsync(client.Id, ct);
        if (faults.Current.SlowQueries)
            await sqlFaults.SlowComplianceAgingReportAsync(client.Id, ct);

        var since = DateTime.UtcNow.AddHours(-24);
        var docs = db.Documents.Where(d => d.ClientId == client.Id);

        var totalBatches = await db.Batches.CountAsync(b => b.ClientId == client.Id, ct);
        var totalDocs = await docs.CountAsync(ct);
        var last24 = await docs.CountAsync(d => d.CreatedUtc >= since, ct);
        var queued = await docs.CountAsync(d => d.Status == DocumentStatus.Queued, ct);
        var qaTotal = await db.QaInspections.CountAsync(q => q.ClientId == client.Id, ct);
        var qaFail = await db.QaInspections.CountAsync(q => q.ClientId == client.Id && !q.Passed, ct);

        var byType = await docs.GroupBy(d => d.DocumentType).Select(g => new CountItem(g.Key, g.Count())).ToListAsync(ct);
        var byChannel = await docs.GroupBy(d => d.Channel).Select(g => new CountItem(g.Key, g.Count())).ToListAsync(ct);
        var byStatus = await docs.GroupBy(d => d.Status).Select(g => new CountItem(g.Key, g.Count())).ToListAsync(ct);
        var recent = await db.Batches.Where(b => b.ClientId == client.Id)
            .OrderByDescending(b => b.ReceivedUtc).Take(8)
            .Select(b => new BatchSummaryDto(b.Id, b.Reference, b.Source, b.Status, b.DocumentCount, b.ReceivedUtc, b.CompletedUtc))
            .ToListAsync(ct);

        var passRate = qaTotal == 0 ? 100 : Math.Round(100.0 * (qaTotal - qaFail) / qaTotal, 2);
        return new DashboardDto(client.Code, client.Name, totalBatches, totalDocs, last24, queued, qaTotal, qaFail, passRate,
            byType.OrderByDescending(x => x.Count).ToList(), byChannel.OrderByDescending(x => x.Count).ToList(),
            byStatus.OrderByDescending(x => x.Count).ToList(), recent);
    }

    [HttpGet("{code}/batches")]
    public async Task<ActionResult<List<BatchSummaryDto>>> GetBatches(string code, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var client = await FindClient(code, ct);
        if (client is null) return NotFound();
        return await db.Batches.Where(b => b.ClientId == client.Id)
            .OrderByDescending(b => b.ReceivedUtc).Take(Math.Clamp(take, 1, 500))
            .Select(b => new BatchSummaryDto(b.Id, b.Reference, b.Source, b.Status, b.DocumentCount, b.ReceivedUtc, b.CompletedUtc))
            .ToListAsync(ct);
    }

    [HttpGet("{code}/documents/search")]
    public async Task<ActionResult<List<DocumentDto>>> Search(string code, [FromQuery] string q, CancellationToken ct)
    {
        var client = await FindClient(code, ct);
        if (client is null) return NotFound();
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2) return BadRequest("Search term must be at least 2 characters");
        Telemetry.Attr("search.slowPath", faults.Current.SlowQueries);

        if (faults.Current.SlowQueries)
            return await sqlFaults.SlowDocumentSearchAsync(client.Id, q.Trim(), ct);

        var term = q.Trim();
        var rows = await db.Documents
            .Where(d => d.ClientId == client.Id && (d.RecipientName.Contains(term) || d.MemberId == term))
            .OrderByDescending(d => d.CreatedUtc).Take(50)
            .Select(d => new
            {
                d.Id, d.BatchId, d.DocumentType, d.Channel, d.RecipientName, d.MemberId, d.City, d.State, d.PostalCode,
                d.Status, d.CreatedUtc,
                Qa = db.QaInspections.Where(x => x.DocumentId == d.Id).OrderByDescending(x => x.Id)
                    .Select(x => x.Passed ? "Pass" : "Fail").FirstOrDefault()
            }).ToListAsync(ct);
        return rows.Select(d => new DocumentDto(d.Id, d.BatchId, d.DocumentType, d.Channel, d.RecipientName,
            Phi.Mask(d.MemberId), d.City, d.State, d.PostalCode, d.Status, d.Qa, d.CreatedUtc)).ToList();
    }

    [HttpGet("{code}/qa")]
    public async Task<ActionResult<List<QaInspectionDto>>> GetQa(string code, [FromQuery] bool failedOnly = false, CancellationToken ct = default)
    {
        var client = await FindClient(code, ct);
        if (client is null) return NotFound();
        return await db.QaInspections.Where(q => q.ClientId == client.Id && (!failedOnly || !q.Passed))
            .OrderByDescending(q => q.Id).Take(100)
            .Select(q => new QaInspectionDto(q.Id, q.DocumentId, q.CameraId, q.ExpectedName, q.ScannedName,
                q.ExpectedPostal, q.ScannedPostal, q.Passed, q.InspectedUtc))
            .ToListAsync(ct);
    }

    [HttpGet("{code}/ftp-transfers")]
    public async Task<ActionResult<List<FtpTransferDto>>> GetTransfers(string code, CancellationToken ct)
    {
        var client = await FindClient(code, ct);
        if (client is null) return NotFound();
        return await db.FtpTransfers.Where(t => t.ClientId == client.Id)
            .OrderByDescending(t => t.Id).Take(50)
            .Select(t => new FtpTransferDto(t.Id, t.FileName, t.SizeBytes, t.Origin, t.Status, t.RecordCount, t.BatchId,
                t.UploadedUtc, t.ProcessedUtc, t.Error))
            .ToListAsync(ct);
    }

    [HttpGet("{code}/reports/volume")]
    public async Task<ActionResult<List<VolumeReportRow>>> VolumeReport(string code, CancellationToken ct)
    {
        var client = await FindClient(code, ct);
        if (client is null) return NotFound();
        if (faults.Current.SlowQueries)
            await sqlFaults.SlowComplianceAgingReportAsync(client.Id, ct);
        var rows = await db.Documents.Where(d => d.ClientId == client.Id)
            .GroupBy(d => new { d.DocumentType, d.Channel })
            .Select(g => new VolumeReportRow(g.Key.DocumentType, g.Key.Channel, g.Count()))
            .ToListAsync(ct);
        return rows.OrderBy(r => r.DocumentType).ThenBy(r => r.Channel).ToList();
    }

    async Task<Client?> FindClient(string code, CancellationToken ct)
    {
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code, ct);
        Telemetry.Client(client?.Code);
        return client;
    }
}
