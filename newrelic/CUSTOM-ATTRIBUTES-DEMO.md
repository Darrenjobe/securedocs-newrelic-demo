# Business context on top of auto-instrumentation: how it was done

**The point:** the New Relic .NET agent instruments everything automatically: web requests, SQL, HTTP calls, errors
and distributed tracing, with **zero code changes**. Adding *business* context took **one NuGet package, one small
helper, and one line per business moment.**

## Step 0: Auto-instrumentation (no code)
```xml
<!-- SecureDocs.Api.csproj -->
<PackageReference Include="NewRelic.Agent" Version="10.55.0" />
```
Plus a few App Service settings (`CORECLR_ENABLE_PROFILING=1`, profiler path, license key, app name).
This gives you traces, SQL, errors and the service map.

## Step 1: Add the agent API (one package)
```xml
<PackageReference Include="NewRelic.Agent.Api" Version="10.55.0" />
```

## Step 2: One line per business moment
**Tag the current request** with who it's for and what it's doing. Its trace, errors and SQL all inherit it:
```csharp
var tx = NewRelic.Api.Agent.NewRelic.GetAgent().CurrentTransaction;
tx.AddCustomAttribute("client.code", "PCHP");          // which client
tx.AddCustomAttribute("client.tier", "Enterprise");    // how important
tx.AddCustomAttribute("batch.documentCount", 250);     // how big
```

**Record a business outcome** as an event you can chart, alert on and put in Pathpoint:
```csharp
NewRelic.Api.Agent.NewRelic.RecordCustomEvent("BatchCompleted", new Dictionary<string, object> {
    ["clientCode"] = "PCHP", ["cycleTimeMinutes"] = 12.4, ["slaMet"] = true, ["amountTotal"] = 5478.16
});
```

**Flag a business failure** that isn't a crash, such as a QA camera catching a possible PHI misprint:
```csharp
NewRelic.Api.Agent.NewRelic.NoticeError(new QaMismatchException("Name mismatch on CAM-03"),
    new Dictionary<string, object> { ["cameraId"] = "CAM-03", ["documentType"] = "EOB" });
```

**Say who the user is**, so Errors Inbox shows **users impacted** for every error group:
```csharp
NewRelic.Api.Agent.NewRelic.GetAgent().CurrentTransaction.SetUserId("SMG:jordan.ops");  // sets enduser.id
```
```js
newrelic.setUserId("SMG:jordan.ops");   // browser agent: front-end errors count users too
```
The portal sets it on every request (client + portal user, never PHI) and forwards it to the API in a header, so an
API error behind a portal click counts against the person who clicked. Client-system API calls are attributed to
`<client>:api-integration`, and rejected client files to `<client>:file-transfer`.

In the app these are wrapped in a small helper (about 40 lines) (`Telemetry.Client(code)`, `Telemetry.Event(...)`), so a call site
is a single line. In total: **55 one-line calls in 12 files**, and none of the existing logic changed.

## Step 3: Use it, with no extra setup
The attributes appear on transactions, traces and errors straight away:
```sql
-- Which clients feel the slowdown right now?
SELECT percentile(duration, 95), percentage(count(*), WHERE error IS true)
FROM Transaction WHERE appName = 'SecureDocs-API' FACET client.code, client.tier SINCE 30 minutes ago

-- Are we meeting SLA per client?
SELECT percentage(count(*), WHERE slaMet IS true) FROM BatchCompleted FACET clientCode SINCE 1 day ago
```
The same data drives the **Business KPIs dashboard**, **alert conditions** (e.g. "Enterprise documents past SLA")
and the **Pathpoint** flow steps.

## Rules we followed
- **No PHI.** Only client codes, counts, types, camera IDs and money *totals*, never names, addresses or member IDs.
- **Low variety for facets.** Facet on `clientCode`, `tier` or `documentType`; keep IDs such as `batchId` for lookup.
- **Safe without the agent.** The API calls do nothing when the agent isn't attached (for example, running locally on a Mac).

## Where to show it in the code
| What | File |
|---|---|
| The helper (about 40 lines) | `src/SecureDocs.Shared/Telemetry.cs` |
| Tagging a request (client, tier, batch) | `src/SecureDocs.Api/Services/IngestionService.cs` lines 25–28 |
| Business event `DocumentBatchIngested` | `src/SecureDocs.Api/Services/IngestionService.cs` line 79 |
| Business event `BatchCompleted` (SLA) | `src/SecureDocs.Api/Services/ProductionService.cs` line 241 |
| Business error (PHI misprint) | `src/SecureDocs.Api/Services/ProductionService.cs` (`NoticeError` in the QA step) |
| Users impacted (`SetUserId`) | `src/SecureDocs.Web/Program.cs` (portal middleware), `src/SecureDocs.Api/Program.cs` (forwarded header), `Pages/Shared/_Layout.cshtml` (browser) |
| Background jobs as transactions | `[Transaction(Web = false)]` in `src/SecureDocs.Api/Workers/*.cs` |
