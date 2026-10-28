# SecureDocs: a fully instrumented New Relic demo on Azure

SecureDocs is a realistic demo app for a healthcare document printing and delivery business. It runs on
**Azure App Service** with **Azure SQL** and is instrumented end to end with **New Relic**:

- APM and distributed tracing
- browser monitoring
- business context (custom attributes and events)
- users impacted in Errors Inbox
- database monitoring with query plans
- synthetics, alerts, dashboards, Pathpoint flows and service levels

A **Fault Control** panel in the app breaks things on demand (errors, slow SQL, slow downstream calls, business
failures), so you can practise or show root-cause analysis.

Use this repo to:
- **Recreate the whole environment** in your own Azure subscription and New Relic account.
- **See how each piece of instrumentation was done**, and apply the same steps to your own app.

> **Demo software.** All data is synthetic, and there is no real patient or member data anywhere. Sign-in is
> simulated and the API is demo-grade, so don't put real data in it. This isn't an official New Relic or Microsoft
> project.

---

## Where to start

| If you want to… | Read |
|---|---|
| Understand what was instrumented and how to do the same with your app | **[newrelic/INSTRUMENTATION-GUIDE.md](newrelic/INSTRUMENTATION-GUIDE.md)** |
| See the code changes behind the business context | [newrelic/CUSTOM-ATTRIBUTES-DEMO.md](newrelic/CUSTOM-ATTRIBUTES-DEMO.md) |
| Run it on your laptop in a few minutes (no Azure, no New Relic) | [Run locally](#run-locally) |
| Deploy it to Azure with New Relic | [Recreate in your environment](#recreate-in-your-environment) |
| Run a live demo (break it, then find the root cause) | [newrelic/DEMO-RUNBOOK.md](newrelic/DEMO-RUNBOOK.md) |

---

## Architecture

```
 Browser ──► SecureDocs-Portal   (App Service, ASP.NET Core Razor Pages, .NET 10)
   │              │  HTTP + W3C trace context → one distributed trace
   │              ▼
   │         SecureDocs-API      (App Service, ASP.NET Core Web API, .NET 10)
   │              ├──► Address verification service (HTTP call, its own span)
   │              ├──► Azure SQL Database (EF Core + raw SQL)
   │              └──► Background jobs: file pickup, FTP poller, production floor + QA cameras
   │
 New Relic synthetics ──► Portal / API / file transfer (24×7 outside-in checks)
 Azure Container Instance (New Relic infrastructure agent + nri-mssql) ──► Azure SQL (read-only monitoring login)
```

**What the business does:**
- **Clients send member data** by API, secure file transfer or the portal.
- The data is **validated** (HIPAA-style field rules plus address verification).
- **Unique documents** are composed and printed: EOBs, ID cards, statements, checks and so on.
- Every printed piece is **checked by a QA camera**.
- Batches must complete **within each client's SLA**: Enterprise or Standard tier.

## What's in the repo

```
src/SecureDocs.Shared     DTOs, synthetic data, the small New Relic helper (Telemetry.cs)
src/SecureDocs.Api        Web API, EF Core (Azure SQL / SQLite), background jobs, fault injection
src/SecureDocs.Web        Client portal (Razor Pages), Fault Control panel, traffic generator
infra/main.bicep          App Service plan (Linux) + 2 web apps + Azure SQL, with New Relic agent settings
deploy.sh                 One-command deploy (needs the .NET 10 SDK and Azure CLI)
deploy-cloudshell.sh      Deploy prebuilt packages from Azure Cloud Shell (finds a region/tier you have quota for)
newrelic/                 Agent config, instrumentation guide, demo runbook, KPIs, service levels, SQL monitoring
synthetics/               3 scripted monitors (API, file transfer, browser journey) + a local test runner
pathpoint/                The 2 business flows: what they model and how to import them
scripts/                  New Relic setup through NerdGraph (monitors, alerts, flows, dashboards, service levels),
                          the SQL monitor deploy, and packaging
local/                    Run everything locally (SQLite, no Azure)
```

---

## Run locally

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The app uses SQLite locally, so you
don't need a database server or Azure.

```bash
local/run-from-source.sh      # builds and starts both apps; the portal opens at http://localhost:5080
```
```bash
local/stop.sh                 # stop (add --reset to delete the local database)
```

On Windows, or to run each project yourself:
```bash
dotnet run --project src/SecureDocs.Api --launch-profile http    # http://localhost:5081
dotnet run --project src/SecureDocs.Web --launch-profile http    # http://localhost:5080
```

**Locally there's no New Relic data.** The .NET agent's profiler isn't attached locally (and doesn't run on macOS),
and the agent API calls simply do nothing. That's useful in itself: it shows the custom instrumentation is safe
without an agent. Set `SECUREDOCS_TELEMETRY_LOG=1` to print the business events to the console instead.

---

## Recreate in your environment

The order below matches the layers in the
[instrumentation guide](newrelic/INSTRUMENTATION-GUIDE.md). Steps 1–2 give you full technical visibility; steps
3–7 add synthetics, alerts and the business views on top.

### Prerequisites
- **Azure:** a subscription where you can create resources, and the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)
  (`az login`).
- **New Relic** (a [free account](https://newrelic.com/signup) works):
  - your **account ID**;
  - an **ingest license key** for the agents;
  - a **User API key** (`NRAK-…`) for the setup scripts.
- **Tools:** the .NET 10 SDK, `zip` and Python 3 (scripts use the standard library only). Node.js is optional, only
  to test synthetic scripts locally.

### 1. Deploy the apps with the APM agent
```bash
export NEW_RELIC_LICENSE_KEY=<ingest license key>
export SQL_ADMIN_PASSWORD='<strong password: 8+ chars, upper, lower, digit, symbol>'
./deploy.sh                                # optional: RG=… LOCATION=… SQL_LOCATION=… APP_SERVICE_SKU=…
```
This creates the `rg-securedocs-demo` resource group with:
- a Linux **B1** App Service plan running the portal and the API, with the New Relic .NET agent
  (`NewRelic.Agent` NuGet package and profiler app settings);
- an Azure SQL server and an **S2** database.

On first start, the API creates the schema and seeds about 9,000 synthetic documents. The script prints both URLs.

- **No .NET SDK on your machine, or a locked-down company subscription?** See [DEPLOY-AZURE.md](DEPLOY-AZURE.md).
  It covers deploying prebuilt packages from Azure Cloud Shell (`scripts/package.sh` builds them), quota errors, and
  Azure Policy guardrails.
- **Turn on browser monitoring:** in New Relic, open **APM → SecureDocs-Portal → Browser** and enable it. The agent
  injects the browser script automatically (`browserMonitoring autoInstrument="true"` in
  [newrelic/newrelic.config](newrelic/newrelic.config)).
- **Generate traffic:** open the portal, click **Fault Control** (bottom right) and turn on **Auto traffic**.
  APM, traces, errors and business events start arriving within a minute or two.

### 2. Database monitoring (Azure SQL)
APM already shows every query from the app's side. To add the database's own view (waits, top slow queries,
execution plans, blocking):

1. **Create a read-only monitoring login** with
   [newrelic/mssql/create-monitoring-login.sql](newrelic/mssql/create-monitoring-login.sql). Run it as the SQL admin,
   once in `master` and once in `SecureDocs`.
2. **Run the SQL Server integration in a small container** next to the database:
   ```bash
   export NEW_RELIC_LICENSE_KEY=<ingest license key>
   export SQL_MONITOR_PASSWORD='<the monitoring login password>'
   bash scripts/deploy_sql_monitor.sh
   ```

[newrelic/mssql/README.md](newrelic/mssql/README.md) explains the two-instance configuration that Azure SQL Database
needs, and how to troubleshoot.

### 3. Synthetics, alerts and Pathpoint
```bash
export NEW_RELIC_USER_KEY=NRAK-...
python3 scripts/newrelic_setup.py --account <ACCOUNT_ID> \
  --portal-url https://<your-portal>.azurewebsites.net --api-url https://<your-api>.azurewebsites.net
```
This creates:
- **4 synthetic monitors:** API health ping, client API intake, secure file transfer, and the portal browser journey;
- **19 alert conditions** in the policy *SecureDocs - Business flows*;
- **2 Pathpoint flows**, wired to those signals.

Run it again with `--verify` to see check results and open incidents. To test the synthetic scripts locally first,
see [synthetics/README.md](synthetics/README.md).

### 4. Dashboards
```bash
python3 scripts/make_db_dashboards.py --account <ACCOUNT_ID> --validate --create   # Azure SQL health + Slow query investigation
```
```bash
scripts/render_newrelic_files.sh <ACCOUNT_ID>   # writes importable JSON for all 3 dashboards and both flows (offline)
```
Import `newrelic/dashboard-securedocs.json` (the Business KPIs dashboard) through **Dashboards → Import dashboard**.
The KPIs are explained in [newrelic/BUSINESS-KPIS.md](newrelic/BUSINESS-KPIS.md).

### 5. Service levels
```bash
python3 scripts/newrelic_service_levels.py --account <ACCOUNT_ID> --create    # 16 SLIs/SLOs
python3 scripts/newrelic_service_levels.py --account <ACCOUNT_ID> --check     # current compliance
```
The targets, and the reasoning behind each one, are in [newrelic/SERVICE-LEVELS.md](newrelic/SERVICE-LEVELS.md).

### 6. Optional: Azure platform metrics
Azure Monitor metrics (DTU/CPU %, storage, App Service CPU and memory) come from New Relic's Azure integration.
You need:
- a service principal with the **Reader** role on the resource group;
- the *cloud integrations* permission in New Relic.

Then run `python3 scripts/azure_newrelic_link.py --help`, or link the subscription in the UI under
**Infrastructure → Azure**.

### 7. Run the demo
Follow [newrelic/DEMO-RUNBOOK.md](newrelic/DEMO-RUNBOOK.md). It covers the pre-demo reset, four
break-it-then-find-the-root-cause stories, timings, and how to reset between them.

---

## Cost

At Azure list prices (Central US) the environment costs about **$3.56/day**:

| Resource | Per day |
|---|---|
| Azure SQL S2 | $2.42 |
| SQL monitor container (0.5 vCPU, 1 GB) | $0.71 |
| App Service B1 (both apps) | $0.43 |

Between demos:
- Stop the container: `az container stop -g rg-securedocs-demo -n securedocs-sql-monitor`.
- Scale the database to S0 and turn Auto traffic off.

Those two steps bring it to about $0.91/day.

New Relic usage (data ingest, synthetic checks) is billed separately by New Relic. A free account covers a light
demo; run the synthetics `--period EVERY_HOUR` between demos to stay well within it.

Delete everything when you're done:
```bash
az group delete -n rg-securedocs-demo
```

## Security notes
- **Secrets are never stored in the repo.** Keys and passwords come from environment variables. The Bicep template
  passes them to App Service as app settings.
- **The agent config protects sensitive data.** It never sends SQL parameter values, cookies or auth headers, and SQL
  is obfuscated (see [newrelic/newrelic.config](newrelic/newrelic.config)).
- **Custom attributes carry only codes, counts, types and totals**, never names, addresses or member IDs.
- **The fault endpoints need a generated control key.** Even so, treat a deployed instance as public and
  short-lived.
