# From zero to full observability: how SecureDocs was instrumented with New Relic

SecureDocs is a .NET 10 document-processing platform: a client portal and an API on Azure App Service, Azure SQL as
the database, and data intake by API and secure file transfer. This guide lists **every step** we took to get it to
full observability. Use it to take your own app to the same level.

## The short version

| Layer | What you get | Effort | Code changes |
|---|---|---|---|
| 1. APM agent | Every request, SQL statement, outbound call and error; distributed traces; service map; logs in context | ~30 min | **None** (1 package + app settings) |
| 2. Browser monitoring | Real-user page loads, Core Web Vitals, JavaScript errors | ~5 min | **None** (one config line) |
| 3. Business context | Client, tier and batch on every trace; business events (SLA met, QA yield, money in flight) | ~½–1 day | About 55 one-line calls plus a 40-line helper |
| 4. Users impacted | Errors Inbox shows how many users and clients each error hit | ~1 hour | 1 middleware + 1 browser line |
| 5. Database visibility | Database health, slow queries, waits, execution plans, blocking | ~1–2 hours | **None** (read-only login + small container) |
| 6. Synthetic monitoring | 24×7 outside-in checks of the portal, the API and file transfer | ~½ day | **None** (separate scripts) |
| 7. Alerts | 19 conditions on technical and business signals | ~2 hours | **None** |
| 8. Dashboards | Business KPIs, database health, slow query investigation | ~½ day | **None** |
| 9. Pathpoint | 2 business-flow maps with KPIs, touchpoints and live status | ~2–3 hours | **None** |
| 10. Service levels | 16 SLIs/SLOs for the apps, the database, synthetics and business KPIs | ~1–2 hours | **None** |

**The takeaway:** layers 1, 2 and 5 give you full technical coverage with **no code changes**. Only layers 3 and 4
touch code, with small additive one-line calls; no existing logic was changed. Everything else is configuration
in New Relic. We scripted it so it can be repeated, but all of it can also be done in the UI.

> **Effort estimates** are for an engineer who knows the app, and include testing. Your times will depend on how
> many services you have and how much business context you want to add.

---

## Prerequisites
- A New Relic account, its **license key** (for agents) and a **User API key** (only for scripted setup).
- Your app (here: ASP.NET Core on Linux App Service) and permission to change its app settings.
- Read access to your database to create a monitoring login.

---

## 1. APM: automatic instrumentation (no code)

**Add the agent package** to each web project:
```xml
<PackageReference Include="NewRelic.Agent" Version="10.55.0" />
```

**Add these app settings** on each App Service (or as environment variables anywhere else):
```
CORECLR_ENABLE_PROFILING = 1
CORECLR_PROFILER         = {36032161-FFC0-4B61-B559-F6C5D41BAE5A}
CORECLR_PROFILER_PATH    = /home/site/wwwroot/newrelic/libNewRelicProfiler.so
CORECLR_NEWRELIC_HOME    = /home/site/wwwroot/newrelic
NEW_RELIC_HOME           = /home/site/wwwroot/newrelic
NEW_RELIC_LICENSE_KEY    = <your license key>
NEW_RELIC_APP_NAME       = SecureDocs-API          (one name per service)
```
On Windows App Service you can use the **New Relic site extension** instead of the package and profiler settings.

**Optional: tune `newrelic.config`.** We shipped one config with both apps (copied on build and publish) to:
- Turn on distributed tracing, logs in context and code-level metrics.
- Capture transaction traces over 1 s, with **obfuscated SQL and explain plans** for slow queries.
- **Protect sensitive data:** never send SQL parameter values, cookies or auth headers. SecureDocs handles PHI, so
  this mattered.
- Ignore expected status codes (401, 404) in error rates.

**What you get with no code:**
- Every web request, with response time, throughput and error rate.
- Every SQL statement, with timing, call counts, slow-query traces and plans.
- Outbound HTTP calls, and **distributed traces** across portal → API → downstream service → database.
- Errors with stack traces in **Errors Inbox**.
- A service map, and app logs linked to traces and errors.

**Optional, one attribute:** background jobs (workers, queues) are not web requests. To trace them as transactions,
add `[Transaction(Web = false)]` to the job method (package `NewRelic.Agent.Api`). We did this for the file
pickup and production workers.

## 2. Browser monitoring (no code)
1. Enable browser monitoring for the portal app in New Relic (**APM → your app → Browser**).
2. Turn on automatic injection in `newrelic.config`:
   ```xml
   <browserMonitoring autoInstrument="true" />
   ```

You get real-user page loads, Core Web Vitals (LCP, INP, CLS), JavaScript errors and AJAX timing, linked to the
backend traces.

## 3. Business context: custom attributes and events (small code changes)

This turns technical data into business data: which **client** is affected, whether we met the **SLA**, how much
**money** is in flight.

**Add the API package:**
```xml
<PackageReference Include="NewRelic.Agent.Api" Version="10.55.0" />
```

**Tag the current request.** Its trace, SQL and errors all inherit the attributes:
```csharp
var tx = NewRelic.Api.Agent.NewRelic.GetAgent().CurrentTransaction;
tx.AddCustomAttribute("client.code", "PCHP");
tx.AddCustomAttribute("client.tier", "Enterprise");
tx.AddCustomAttribute("batch.documentCount", 250);
```

**Record business outcomes** as events you can chart, alert on and use in Pathpoint and service levels:
```csharp
NewRelic.Api.Agent.NewRelic.RecordCustomEvent("BatchCompleted", new Dictionary<string, object> {
    ["clientCode"] = "PCHP", ["cycleTimeMinutes"] = 12.4, ["slaMet"] = true, ["amountTotal"] = 5478.16
});
```

**Report business failures** that aren't crashes (here, a QA camera catching a possible misprint):
```csharp
NewRelic.Api.Agent.NewRelic.NoticeError(new QaMismatchException("Name mismatch on CAM-03"),
    new Dictionary<string, object> { ["cameraId"] = "CAM-03", ["documentType"] = "EOB" });
```

**What we added in SecureDocs:**
- **Attributes:** client code and tier, batch size, channel and document type.
- **Events:** `DocumentBatchIngested`, `FileTransferProcessed` (file pickup delay), `ProductionBacklog` (queue depth,
  oldest item, documents at risk), `QaInspection` (pass/fail, mismatch type, reprints) and `BatchCompleted` (SLA met,
  cycle time, payment value).

**How we kept it small:** we wrapped these calls in a ~40-line helper (`Telemetry.Client(code)`,
`Telemetry.Event(...)`), so each call site is one line. In total: about 55 one-line calls in 12 files.

**Rules to follow:**
- **No sensitive data:** send codes, counts, types and totals, never names, addresses or member IDs.
- **Facet on low-variety values** (client, tier, document type). Keep IDs such as `batchId` for lookups only.
- **The API is safe without the agent:** the calls do nothing when the agent isn't attached (for example, locally).

## 4. Users impacted in Errors Inbox (small code change)

Errors Inbox counts **users impacted** per error group from the `enduser.id` attribute.

**Back end:** one line in a request middleware:
```csharp
NewRelic.Api.Agent.NewRelic.GetAgent().CurrentTransaction.SetUserId(userId);   // sets enduser.id
```

**Browser:** one line on the page:
```js
newrelic.setUserId(userId);
```

**Across services:** the portal forwards the user to the API in a request header, and the API calls `SetUserId`
with it. An API error behind a portal click then counts against the person who clicked. Calls with no person
behind them get a stable label: `<client>:api-integration`, `<client>:file-transfer`, or `synthetic-monitor`.

Use an opaque identifier (we used `client:username`). Avoid emails or other personal data where privacy rules apply.

## 5. Database visibility (no code)

There are two complementary views:

| View | Source | What it shows |
|---|---|---|
| **From the app** | APM agent (step 1), automatic | Each query's time and calls, DB time per request, slow-query traces with plans, the clients affected |
| **From the database** | SQL Server integration (`nri-mssql`) | Connections, throughput, waits, locks, deadlocks, buffer pool, I/O, **top slow queries, execution plans, blocking sessions** |

**Setup for Azure SQL Database:**
1. **Create a read-only monitoring login.** It can read performance views and cannot change data:
   ```sql
   -- In master
   CREATE LOGIN [newrelic] WITH PASSWORD = '<password>';
   CREATE USER [newrelic] FOR LOGIN [newrelic];
   ALTER SERVER ROLE ##MS_ServerStateReader## ADD MEMBER [newrelic];
   ALTER SERVER ROLE ##MS_DefinitionReader## ADD MEMBER [newrelic];
   -- In your application database
   CREATE USER [newrelic] FOR LOGIN [newrelic];
   GRANT VIEW DATABASE STATE TO [newrelic];
   GRANT VIEW DEFINITION TO [newrelic];
   ```
2. **Run the integration.** Azure SQL is a managed service with no host to install an agent on, so run the New
   Relic infrastructure agent with `nri-mssql` (image `newrelic/infrastructure-bundle`) in a small container next to
   it. We used Azure Container Instances (0.5 vCPU, 1 GB, about $0.71/day). For SQL Server on a VM or on-premises,
   install the infrastructure agent on the server instead.
3. **Configure two integration instances** (needed on Azure SQL Database):
   - **Metrics:** connect with no database in the connection string; the integration connects to each database
     itself.
   - **Query monitoring:** connect to the application database (`EXTRA_CONNECTION_URL_ARGS: database=<YourDb>`) with
     `ENABLE_QUERY_MONITORING: true`, `INVENTORY: true` (skip duplicate metrics) and `TIMEOUT: 120` (collecting plans
     can be slow on a busy database).

   Use TLS with certificate verification (`ENABLE_SSL: true`, `CERTIFICATE_LOCATION` pointing to a `.pem` CA bundle).

**Notes:**
- **Database 360** does not support Azure SQL Database at the time of writing. The integration above is the
  supported option, and it covers query performance, waits, plans and blocking.
- **Azure platform metrics** (DTU/CPU %, storage) come from the **Azure Monitor integration**. It needs an Azure
  service principal with *Reader* access, and the New Relic *cloud integrations* permission.

## 6. Synthetic monitoring (no app changes)

Synthetics test the app from outside, around the clock, even with no users.

| Monitor | Type | What it proves |
|---|---|---|
| API health | Ping | The API is up and reports Healthy |
| Client API intake | Scripted API | A client system can submit a batch and read it back within its time budgets |
| Secure file transfer | Scripted API | An uploaded file is picked up and turned into a batch within 45 s |
| Portal journey | Scripted browser | Sign in → dashboard → submit → search works end to end |

**Steps:**
1. Write the scripts. Scripted API monitors use `$http`; scripted browser monitors use `$webDriver`/`$selenium`.
2. Test them locally first (we used a small Node.js runner), then create them in New Relic.
3. Tag synthetic traffic (we send `source = Synthetic`) so business KPIs and service levels can exclude it.

## 7. Alerts (configuration only)

We created **19 NRQL alert conditions** in one policy, covering both technical and business signals:
- **Technical:** synthetic check failures, intake API slow (p95 over 3 s) or erroring (over 5%), portal errors,
  slow downstream calls, database errors, slow database time.
- **Business:** files waiting over 60 s, record rejection rate, production floor stalled, documents at risk of
  missing SLA, Enterprise documents past SLA, QA mismatch spikes, reprints failing again, batches completed after SLA.

**Tips from tuning:**
- Use **count-based** thresholds over a window for noisy ratios (for example, QA mismatches over 10 minutes).
- Set **signal expiration** on sparse event signals, so incidents close when the problem stops.
- Scope latency alerts to **client-facing** requests, so expected heavy background jobs don't trigger them.

## 8. Dashboards (configuration only)

| Dashboard | Built from |
|---|---|
| **Business KPIs:** SLA attainment per client and tier, file pickup delay, queue depth and oldest item, documents at risk, QA yield and mismatch types, reprints, payment value in flight | Custom events (step 3) |
| **Azure SQL health:** connections, throughput, buffer/PLE, blocking, deadlocks, waits, I/O, log growth, plus the app's DB time | SQL integration + APM |
| **Slow query investigation:** *Find* (slowest queries, from both the database and the app) → *Why* (plans, scans, waits, blocking) → *Who is affected* (requests, clients, trace IDs) | SQL integration + APM |

Every widget is a NRQL query. Build dashboards in the UI, or import them as JSON (we kept ours as JSON files so they
can be recreated in any account).

## 9. Pathpoint: business flows (configuration only)

Pathpoint maps the business journey to its technical touchpoints, so non-engineers can see where a flow is broken
and what it costs.

We built two flows:
- **Member data intake:** client connects → sends member data (API or secure file transfer) → validate and verify
  (field validation, address verification) → stored and queued for production. KPIs: records received and
  rejected, files processed, average file pickup delay, undeliverable addresses, payment value received.
- **Unique document production and proof of delivery:** production queue → compose and print → QA camera
  verification (with reprint and re-verify) → deliver and prove. KPIs: documents completed, batches that missed SLA,
  p95 batch cycle time, possible PHI misprints caught, reprints, payment value delivered.

**Steps:**
1. Define the stages and steps of each flow.
2. Attach **touchpoints** to each step: APM services, synthetic monitors and the alert conditions from step 7.
   Their health drives each step's status.
3. Add **KPIs** as NRQL queries on your custom events (each KPI uses a single aggregation).

## 10. Service levels (configuration only)

We created **16 service levels**. The 28-day ones are what you'd report to clients; the 7-day ones are internal
engineering targets.
- **Portal:** availability (99.9%), pages under 2 s (95%), page load (LCP) under 2.5 s (90%).
- **API:** intake availability (99.9%), intake under 3 s (99%), status lookups under 500 ms (99%), dashboard and
  reports under 3 s (95%).
- **Database:** queries under 500 ms (99%), requests without SQL errors (99.9%).
- **Synthetics:** success of each of the 4 monitors.
- **Business:** batches completed within SLA (99%), files processed within 60 s (99%), print accuracy (99%).

Each is a "good events / valid events" pair of NRQL filters, on data you already have from the earlier steps.

---

## Repeating it for your own app

1. **Day 1, no code:** agent + app settings (step 1), browser (step 2) and database integration (step 5). You get
   full technical visibility.
2. **Pick 3–5 business moments** that matter to your customers (an order placed, a file processed, an SLA met), and
   add one attribute or event call for each (steps 3–4).
3. **Turn the data into outcomes:** synthetics for your critical journeys (step 6), then alerts, dashboards, Pathpoint
   and service levels on top (steps 7–10).

## Being upfront about what took real work
- **Business context** needs knowledge of your domain: someone has to decide which moments and attributes matter.
  The code is easy; that decision is the real work.
- **Scripted synthetics** are code that needs writing and maintaining as the UI changes.
- **Azure SQL Database** needs a small always-on container for the database integration, because there is no host
  to install an agent on.
- **Thresholds need tuning** with real traffic. Our first alert thresholds were noisy and took one round of
  adjustment.
- **Permissions:** creating the Azure service principal and linking Azure Monitor need admin rights in both Azure
  and New Relic.
