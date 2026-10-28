using SecureDocs.Api.Data;
using SecureDocs.Api.Infrastructure;
using SecureDocs.Api.Services;
using SecureDocs.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SecureDocs.Api.Controllers;

/// <summary>Client data submission over REST. Requires the client's X-Api-Key header.</summary>
[ApiController]
[Route("api/batches")]
public class BatchesController(SecureDocsDb db, IngestionService ingestion, FaultState faults) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SubmitBatchResponse>> Submit([FromBody] SubmitBatchRequest request,
        [FromHeader(Name = "X-Api-Key")] string? apiKey, [FromHeader(Name = "X-Ingestion-Source")] string? source,
        CancellationToken ct)
    {
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Code == request.ClientCode, ct);
        if (client is null) return NotFound($"Unknown client '{request.ClientCode}'");
        if (client.ApiKey != apiKey) return Unauthorized("Invalid or missing X-Api-Key");
        if (request.Documents.Count is 0 or > 5000) return BadRequest("A batch must contain 1-5000 documents");

        AttributeApiCaller(Request, client.Code, source);
        var channel = source switch
        {
            IngestionSources.Portal => IngestionSources.Portal,
            IngestionSources.Synthetic => IngestionSources.Synthetic,
            _ => IngestionSources.Api
        };
        var result = await ingestion.IngestAsync(client, channel, request.Reference, request.Documents, $"{channel.ToLowerInvariant()}:{client.Code}", ct);
        return CreatedAtAction(nameof(Get), new { id = result.BatchId }, result);
    }

    /// <summary>Calls without a forwarded portal user come from a client's system (or our synthetic monitor).</summary>
    internal static void AttributeApiCaller(HttpRequest request, string clientCode, string? source)
    {
        if (!string.IsNullOrEmpty(request.Headers[Telemetry.UserHeader].ToString())) return;   // portal user already set
        Telemetry.User(source == IngestionSources.Synthetic ? "synthetic-monitor" : $"{clientCode}:api-integration");
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<BatchDetailDto>> Get(int id, CancellationToken ct)
    {
        var b = await db.Batches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return NotFound();
        Telemetry.Attr("batch.id", id);
        Telemetry.Client(await db.Clients.Where(c => c.Id == b.ClientId).Select(c => c.Code).FirstOrDefaultAsync(ct));
        Telemetry.Attr("query.nPlusOne", faults.Current.NPlusOneQueries);

        var summary = new BatchSummaryDto(b.Id, b.Reference, b.Source, b.Status, b.DocumentCount, b.ReceivedUtc, b.CompletedUtc);
        var docs = await db.Documents.AsNoTracking().Where(d => d.BatchId == id).OrderBy(d => d.Id).Take(250).ToListAsync(ct);

        Dictionary<long, string> qa;
        if (faults.Current.NPlusOneQueries)
        {
            // Anti-pattern on purpose: one round trip per document.
            qa = new Dictionary<long, string>();
            foreach (var d in docs)
            {
                var insp = await db.QaInspections.AsNoTracking().Where(q => q.DocumentId == d.Id)
                    .OrderByDescending(q => q.Id).FirstOrDefaultAsync(ct);
                if (insp is not null) qa[d.Id] = insp.Passed ? "Pass" : "Fail";
            }
        }
        else
        {
            var ids = docs.Select(d => d.Id).ToList();
            qa = (await db.QaInspections.AsNoTracking().Where(q => ids.Contains(q.DocumentId))
                    .Select(q => new { q.Id, q.DocumentId, q.Passed }).ToListAsync(ct))
                .GroupBy(q => q.DocumentId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Id).First().Passed ? "Pass" : "Fail");
        }

        return new BatchDetailDto(summary, docs.Select(d => new DocumentDto(d.Id, d.BatchId, d.DocumentType, d.Channel,
            d.RecipientName, Phi.Mask(d.MemberId), d.City, d.State, d.PostalCode, d.Status,
            qa.GetValueOrDefault(d.Id), d.CreatedUtc)).ToList());
    }
}

/// <summary>File-transfer endpoint: accepts a CSV and drops it in the FTP inbox for asynchronous ingestion.</summary>
[ApiController]
[Route("api/ftp")]
public class FtpController(SecureDocsDb db, FtpInbox inbox) : ControllerBase
{
    [HttpPost("upload")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<FtpTransferDto>> Upload([FromForm] string clientCode, IFormFile file,
        [FromHeader(Name = "X-Api-Key")] string? apiKey, [FromHeader(Name = "X-Ingestion-Source")] string? source,
        CancellationToken ct)
    {
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Code == clientCode, ct);
        if (client is null) return NotFound($"Unknown client '{clientCode}'");
        if (client.ApiKey != apiKey) return Unauthorized("Invalid or missing X-Api-Key");
        if (file.Length == 0) return BadRequest("Empty file");

        Telemetry.Client(client.Code);
        BatchesController.AttributeApiCaller(Request, client.Code, source);
        Telemetry.Attr("ftp.fileSizeBytes", file.Length);
        await using var stream = file.OpenReadStream();
        var t = await inbox.SaveAsync(db, client, file.FileName, stream,
            source switch
            {
                IngestionSources.Portal => "Portal Upload",
                IngestionSources.Synthetic => "Synthetic Upload",
                _ => "API Upload"
            }, ct);
        return Accepted(new FtpTransferDto(t.Id, t.FileName, t.SizeBytes, t.Origin, t.Status, 0, null, t.UploadedUtc, null, null));
    }
}

[ApiController]
[Route("api/production")]
public class ProductionController(ProductionService production) : ControllerBase
{
    [HttpPost("run")]
    public Task<ProductionRunResult> Run([FromBody] ProductionRunRequest request, CancellationToken ct) =>
        production.RunAsync(request.ClientCode, request.MaxDocuments, ct);
}

/// <summary>Mock CASS/NCOA address verification service. Called over HTTP by the ingestion pipeline.</summary>
[ApiController]
[Route("api/address")]
public class AddressController(FaultState faults) : ControllerBase
{
    [HttpPost("verify")]
    public async Task<AddressVerifyResponse> Verify([FromBody] AddressVerifyRequest request, [FromQuery] int? delayMs,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var delay = delayMs ?? (faults.Current.SlowTraces ? faults.Current.SlowTraceMs : Random.Shared.Next(20, 120));
        Telemetry.Attr("address.count", request.Addresses.Count);
        Telemetry.Attr("address.simulatedLatencyMs", delay);
        await Task.Delay(delay, ct);
        var undeliverable = request.Addresses.Count(_ => Random.Shared.Next(100) < 2);
        return new AddressVerifyResponse(request.Addresses.Count, request.Addresses.Count - undeliverable, undeliverable,
            sw.ElapsedMilliseconds);
    }
}
