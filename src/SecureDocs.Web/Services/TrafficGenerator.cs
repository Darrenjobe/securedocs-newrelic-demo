using SecureDocs.Shared;

namespace SecureDocs.Web.Services;

/// <summary>
/// Simulates portal users by calling this web app's own pages over HTTP, so each simulated click is a
/// real web transaction (Portal → API → SQL) in New Relic. Runs when "Auto traffic" is on or a burst is queued.
/// </summary>
public class TrafficGenerator(IHttpClientFactory httpFactory, PortalFaults faults, SecureDocsApi api,
    ILogger<TrafficGenerator> logger) : BackgroundService
{
    // Simulated client operations staff (each client has these users), so "users impacted" counts are realistic.
    static readonly string[] Users =
    [
        "jordan.ops", "alex.kim", "maria.lopez", "sam.patel", "dana.wright", "chris.nguyen", "taylor.reed", "priya.shah"
    ];

    static readonly (string Method, string Path, int Weight)[] Actions =
    [
        ("GET", "/", 5),
        ("GET", "/Batches", 2),
        ("GET", "/demo/random-batch", 3),
        ("GET", "/Documents?q={name}", 3),
        ("GET", "/Production", 2),
        ("GET", "/Reports", 1),
        ("GET", "/Transfer", 1),
        ("POST", "/demo/submit-sample", 2),
        ("POST", "/demo/upload-sample", 1),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var http = httpFactory.CreateClient("self");
        var totalWeight = Actions.Sum(a => a.Weight);
        List<string> clientCodes = [];

        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = 0;
            // Up to 3 concurrent simulated users per tick.
            var tasks = new List<Task>();
            for (var i = 0; i < 3 && faults.TakeTrafficSlot(); i++)
            {
                if (clientCodes.Count == 0)
                {
                    try { clientCodes = (await api.GetClientsAsync(stoppingToken)).Select(c => c.Code).ToList(); }
                    catch (Exception ex) { logger.LogWarning("Traffic generator can't reach API: {Message}", ex.Message); break; }
                }
                var roll = Random.Shared.Next(totalWeight);
                var action = Actions.First(a => (roll -= a.Weight) < 0);
                var path = action.Path.Replace("{name}", SyntheticData.RandomLastName());
                var client = clientCodes[Random.Shared.Next(clientCodes.Count)];
                tasks.Add(SendAsync(http, action.Method, path, client, stoppingToken));
                sent++;
            }
            await Task.WhenAll(tasks);
            await Task.Delay(sent > 0 ? 400 : 1000, stoppingToken);
        }
    }

    async Task SendAsync(HttpClient http, string method, string path, string client, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path);
            var user = Users[Random.Shared.Next(Users.Length)];
            req.Headers.Add("Cookie", $"{PortalSession.ClientCookie}={client}; {PortalSession.UserCookie}={user}");
            req.Headers.UserAgent.ParseAdd("SecureDocs-TrafficGenerator/1.0");
            using var resp = await http.SendAsync(req, ct);
            logger.LogDebug("Traffic {Method} {Path} as {Client} → {Status}", method, path, client, (int)resp.StatusCode);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Traffic request {Path} failed: {Message}", path, ex.Message);
        }
    }
}

public static class PortalSession
{
    public const string ClientCookie = "sd_client";
    public const string UserCookie = "sd_user";

    /// <summary>Stable end-user ID for telemetry, e.g. "SMG:jordan.ops" (client + portal user name; no PHI).</summary>
    public static string? UserId(HttpContext? ctx)
    {
        var client = ctx?.Request.Cookies[ClientCookie];
        var user = ctx?.Request.Cookies[UserCookie];
        return string.IsNullOrEmpty(client) || string.IsNullOrEmpty(user) ? null : $"{client}:{user}";
    }
}

/// <summary>Passes the signed-in portal user to the API, so API errors count against the user who triggered them.</summary>
public class UserForwardingHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var userId = PortalSession.UserId(accessor.HttpContext);
        if (userId is not null && !request.Headers.Contains(Telemetry.UserHeader))
            request.Headers.Add(Telemetry.UserHeader, userId);
        return base.SendAsync(request, ct);
    }
}
