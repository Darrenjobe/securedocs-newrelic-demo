using SecureDocs.Api.Infrastructure;
using SecureDocs.Shared;
using NewRelic.Api.Agent;

namespace SecureDocs.Api.Services;

/// <summary>
/// Calls the address-verification service (hosted inside this API at /api/address/verify) over HTTP,
/// so it shows up as an external call / extra span in the distributed trace.
/// </summary>
public class AddressVerificationClient(HttpClient http, ILogger<AddressVerificationClient> logger)
{
    [Trace]
    public async Task<AddressVerifyResponse> VerifyAsync(IEnumerable<DocumentRequest> docs, int? forceDelayMs, CancellationToken ct)
    {
        var lines = docs.Where(d => d.Channel == Channels.Print)
            .Select(d => new AddressLine(d.AddressLine1, d.City, d.State, d.PostalCode))
            .ToList();
        if (lines.Count == 0) return new AddressVerifyResponse(0, 0, 0, 0);

        var url = forceDelayMs is > 0 ? $"api/address/verify?delayMs={forceDelayMs}" : "api/address/verify";
        try
        {
            var resp = await http.PostAsJsonAsync(url, new AddressVerifyRequest(lines), ct);
            resp.EnsureSuccessStatusCode();
            return (await resp.Content.ReadFromJsonAsync<AddressVerifyResponse>(ct))!;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Address verification service call failed");
            throw new AddressServiceException("Address verification service unavailable", ex);
        }
    }
}
