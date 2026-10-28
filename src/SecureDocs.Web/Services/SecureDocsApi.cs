using System.Net.Http.Headers;
using System.Text.Json;
using SecureDocs.Shared;
using Microsoft.Extensions.Caching.Memory;

namespace SecureDocs.Web.Services;

public class ApiException(int status, string title, string? detail)
    : Exception($"SecureDocs API returned {status} {title}: {detail}")
{
    public int Status { get; } = status;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public string UserMessage => $"{Title}: {Detail ?? "the request failed"} (HTTP {Status})";
}

/// <summary>
/// Typed client for the SecureDocs API. Every call is an outbound HTTP request, which New Relic
/// links to the API's transaction through W3C trace-context headers (distributed tracing).
/// </summary>
public class SecureDocsApi(HttpClient http, IMemoryCache cache, IConfiguration config)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<List<ClientDto>> GetClientsAsync(CancellationToken ct = default) =>
        (await cache.GetOrCreateAsync("clients", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await Get<List<ClientDto>>("api/clients", ct);
        }))!;

    public async Task<ClientDto?> GetClientAsync(string code, CancellationToken ct = default) =>
        (await GetClientsAsync(ct)).FirstOrDefault(c => c.Code == code);

    public Task<DashboardDto> GetDashboardAsync(string code, CancellationToken ct = default) =>
        Get<DashboardDto>($"api/clients/{code}/dashboard", ct);

    public Task<List<BatchSummaryDto>> GetBatchesAsync(string code, int take = 50, CancellationToken ct = default) =>
        Get<List<BatchSummaryDto>>($"api/clients/{code}/batches?take={take}", ct);

    public Task<BatchDetailDto> GetBatchAsync(int id, CancellationToken ct = default) =>
        Get<BatchDetailDto>($"api/batches/{id}", ct);

    public Task<List<DocumentDto>> SearchAsync(string code, string q, CancellationToken ct = default) =>
        Get<List<DocumentDto>>($"api/clients/{code}/documents/search?q={Uri.EscapeDataString(q)}", ct);

    public Task<List<QaInspectionDto>> GetQaAsync(string code, bool failedOnly, CancellationToken ct = default) =>
        Get<List<QaInspectionDto>>($"api/clients/{code}/qa?failedOnly={failedOnly}", ct);

    public Task<List<FtpTransferDto>> GetTransfersAsync(string code, CancellationToken ct = default) =>
        Get<List<FtpTransferDto>>($"api/clients/{code}/ftp-transfers", ct);

    public Task<List<VolumeReportRow>> GetVolumeReportAsync(string code, CancellationToken ct = default) =>
        Get<List<VolumeReportRow>>($"api/clients/{code}/reports/volume", ct);

    public async Task<SubmitBatchResponse> SubmitBatchAsync(string code, string? reference, List<DocumentRequest> docs,
        CancellationToken ct = default)
    {
        var client = await GetClientAsync(code, ct) ?? throw new ApiException(404, "UnknownClient", code);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/batches")
        {
            Content = JsonContent.Create(new SubmitBatchRequest { ClientCode = code, Reference = reference, Documents = docs })
        };
        req.Headers.Add("X-Api-Key", client.ApiKey);
        req.Headers.Add("X-Ingestion-Source", IngestionSources.Portal);
        return await Send<SubmitBatchResponse>(req, ct);
    }

    public async Task<FtpTransferDto> UploadFileAsync(string code, string fileName, Stream content, CancellationToken ct = default)
    {
        var client = await GetClientAsync(code, ct) ?? throw new ApiException(404, "UnknownClient", code);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(code), "clientCode");
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", fileName);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/ftp/upload") { Content = form };
        req.Headers.Add("X-Api-Key", client.ApiKey);
        req.Headers.Add("X-Ingestion-Source", IngestionSources.Portal);
        return await Send<FtpTransferDto>(req, ct);
    }

    public Task<ProductionRunResult> RunProductionAsync(string code, int max, CancellationToken ct = default) =>
        Send<ProductionRunResult>(new HttpRequestMessage(HttpMethod.Post, "api/production/run")
        {
            Content = JsonContent.Create(new ProductionRunRequest(code, max))
        }, ct);

    public Task<ApiFaultState> GetFaultsAsync(CancellationToken ct = default) => Get<ApiFaultState>("api/faults", ct);

    public Task<ApiFaultState> SetFaultsAsync(ApiFaultState state, CancellationToken ct = default) =>
        Send<ApiFaultState>(WithFaultKey(new HttpRequestMessage(HttpMethod.Put, "api/faults") { Content = JsonContent.Create(state) }), ct);

    public async Task<string> TriggerAsync(string kind, CancellationToken ct = default)
    {
        using var req = WithFaultKey(new HttpRequestMessage(HttpMethod.Post, $"api/faults/trigger/{kind}"));
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw ToApiException(resp, body);
        return body;
    }

    HttpRequestMessage WithFaultKey(HttpRequestMessage req)
    {
        if (config["Faults:ControlKey"] is { Length: > 0 } key) req.Headers.Add("X-Fault-Key", key);
        return req;
    }

    Task<T> Get<T>(string url, CancellationToken ct) => Send<T>(new HttpRequestMessage(HttpMethod.Get, url), ct);

    async Task<T> Send<T>(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        {
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw ToApiException(resp, body);
            return JsonSerializer.Deserialize<T>(body, Json)!;
        }
    }

    static ApiException ToApiException(HttpResponseMessage resp, string body)
    {
        string title = resp.ReasonPhrase ?? "Error", detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("title", out var t)) title = t.GetString() ?? title;
                if (doc.RootElement.TryGetProperty("detail", out var d)) detail = d.GetString() ?? detail;
            }
        }
        catch (JsonException) { }
        return new ApiException((int)resp.StatusCode, title, detail);
    }
}
