using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SecureDocs.Shared;

/// <summary>
/// Records a New Relic deployment marker (Change tracking) on this app whenever a Fault Control switch is turned on
/// or off, so the APM charts show "a release went out here" right where a fault starts and where it's fixed.
/// The notes are deliberately (ironically) upbeat: turning a fault on looks like an "improvement" release, turning
/// it off looks like the hotfix or rollback. The changelog and attributes say each marker was simulated.
///
/// Uses NerdGraph's changeTrackingCreateDeployment, so it needs a New Relic User API key (NewRelic:UserApiKey).
/// Without one it does nothing (local runs). Sending is fire-and-forget, so toggling stays instant.
/// </summary>
public sealed class ChangeMarkers
{
    /// <summary>Per switch: a version slug, the release note when it's turned on, and the fix note when turned off.</summary>
    public static readonly IReadOnlyDictionary<string, (string Slug, string On, string Off)> Releases =
        new Dictionary<string, (string, string, string)>
        {
            ["ErrorRate"] = ("hipaa-rules",
                "Hardened HIPAA eligibility validation: stricter checksum rules and smarter SLA aggregation",
                "Hotfix: relax the eligibility checksum rule that was rejecting valid batches"),
            ["DatabaseErrors"] = ("dup-check",
                "Improved duplicate detection: check the print-job archive before printing",
                "Hotfix: remove the dependency on the dropped PrintJobArchive_2019 table"),
            ["SlowTraces"] = ("cass-provider",
                "Performance: switch address verification to a faster CASS/NCOA provider",
                "Rollback: revert to the previous address verification provider (latency regression)"),
            ["SlowQueries"] = ("search-opt",
                "Optimized query: faster case-insensitive document search and compliance aging report",
                "Fixing query slowness: restore the index-friendly search predicate"),
            ["NPlusOneQueries"] = ("qa-lazyload",
                "Optimized batch detail: lazy-load QA results only when they're needed",
                "Fix: load QA results in a single query (N+1 regression)"),
            ["QaMismatches"] = ("ocr-v4",
                "Improved accuracy: new OCR model v4.2 for the QA cameras",
                "Rollback: QA cameras back to OCR model v4.1 (misreads)"),
            ["ProductionStalled"] = ("shift-sched",
                "Efficiency: new, smarter shift scheduler for the production floor",
                "Hotfix: production scheduler stuck waiting on the shift calendar"),
            ["PortalErrors"] = ("member-widget",
                "Adding new features: member summary widget on every page",
                "Hotfix: null member widget model crashing portal pages"),
        };

    /// <summary>API fault switches that changed between two states: (switch, true = turned on / false = turned off).</summary>
    public static IEnumerable<(string Toggle, bool On)> Changes(ApiFaultState before, ApiFaultState after)
    {
        (string, bool, bool)[] switches =
        [
            ("ErrorRate", before.ErrorRatePercent > 0, after.ErrorRatePercent > 0),
            ("DatabaseErrors", before.DatabaseErrors, after.DatabaseErrors),
            ("SlowTraces", before.SlowTraces, after.SlowTraces),
            ("SlowQueries", before.SlowQueries, after.SlowQueries),
            ("NPlusOneQueries", before.NPlusOneQueries, after.NPlusOneQueries),
            ("QaMismatches", before.QaMismatches, after.QaMismatches),
            ("ProductionStalled", before.ProductionStalled, after.ProductionStalled),
        ];
        foreach (var (name, was, now) in switches)
            if (was != now) yield return (name, now);
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    readonly string? _apiKey;
    readonly long? _accountId;
    readonly string _appName;
    readonly Uri _endpoint;
    readonly Action<string> _log;
    string? _entityGuid;

    public ChangeMarkers(string? userApiKey, long? accountId, string appName, string? graphqlUrl, Action<string> log)
    {
        _apiKey = string.IsNullOrWhiteSpace(userApiKey) ? null : userApiKey.Trim();
        _accountId = accountId;
        _appName = appName;
        _endpoint = new Uri(string.IsNullOrWhiteSpace(graphqlUrl) ? "https://api.newrelic.com/graphql" : graphqlUrl);
        _log = log;
    }

    public bool Enabled => _apiKey is not null;

    /// <summary>Queues a deployment marker for a switch (a key of <see cref="Releases"/>) being turned on or off.</summary>
    public void Record(string toggle, bool on)
    {
        if (!Enabled || !Releases.TryGetValue(toggle, out var release)) return;
        var slug = on ? release.Slug : release.Slug + "-fix";
        var note = on ? release.On : release.Off;
        _ = Task.Run(async () =>
        {
            try { await SendAsync(toggle, on, slug, note); }
            catch (Exception ex) { _log($"Change marker for '{toggle}' failed: {ex.Message}"); }
        });
    }

    async Task SendAsync(string toggle, bool on, string slug, string description)
    {
        var guid = _entityGuid ??= await FindEntityGuidAsync();
        if (guid is null) { _log($"Change marker skipped: APM app '{_appName}' not found in New Relic"); return; }

        var now = DateTimeOffset.UtcNow;
        var data = await GraphQlAsync(
            """
            mutation($d: ChangeTrackingDeploymentInput!) {
              changeTrackingCreateDeployment(deployment: $d) { deploymentId }
            }
            """,
            new JsonObject
            {
                ["d"] = new JsonObject
                {
                    ["entityGuid"] = guid,
                    ["version"] = $"{now:yyyy.MM.dd.HHmm}-{slug}",
                    ["description"] = description,
                    ["changelog"] = $"Simulated release from SecureDocs Fault Control: '{toggle}' switched {(on ? "on" : "off")}.",
                    ["deploymentType"] = "ROLLING",
                    ["user"] = "release-pipeline (Fault Control demo)",
                    ["timestamp"] = now.ToUnixTimeMilliseconds(),
                    ["customAttributes"] = new JsonObject
                    {
                        ["faultToggle"] = toggle, ["faultState"] = on ? "on" : "off", ["simulated"] = true
                    },
                },
            });
        _log($"Change marker recorded on {_appName} for '{toggle}' {(on ? "on" : "off")} " +
             $"(deployment {data?["changeTrackingCreateDeployment"]?["deploymentId"]})");
    }

    async Task<string?> FindEntityGuidAsync()
    {
        var filter = $"domain = 'APM' AND type = 'APPLICATION' AND name = '{_appName.Replace("'", "")}'" +
                     (_accountId is { } id ? $" AND accountId = {id}" : "");
        var data = await GraphQlAsync(
            "query($q: String!) { actor { entitySearch(query: $q) { results { entities { guid } } } } }",
            new JsonObject { ["q"] = filter });
        var entities = data?["actor"]?["entitySearch"]?["results"]?["entities"]?.AsArray();
        return entities is { Count: > 0 } ? entities[0]?["guid"]?.GetValue<string>() : null;
    }

    async Task<JsonNode?> GraphQlAsync(string query, JsonObject variables)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(new JsonObject { ["query"] = query, ["variables"] = variables })
        };
        req.Headers.Add("API-Key", _apiKey);
        using var res = await Http.SendAsync(req);
        var body = await res.Content.ReadFromJsonAsync<JsonNode>();
        res.EnsureSuccessStatusCode();
        if (body?["errors"] is JsonArray { Count: > 0 } errors)
            throw new InvalidOperationException(string.Join("; ", errors.Select(e => e?["message"]?.GetValue<string>())));
        return body?["data"];
    }

    /// <summary>Builds an instance from the app's configuration values.</summary>
    public static ChangeMarkers FromConfig(Func<string, string?> config, string defaultAppName, Action<string> log) =>
        new(config("NewRelic:UserApiKey"),
            long.TryParse(config("NewRelic:AccountId"), out var id) ? id : null,
            config("NEW_RELIC_APP_NAME") is { Length: > 0 } name ? name : defaultAppName,
            config("NewRelic:GraphQlUrl"),
            log);
}
