# Deploying SecureDocs to Azure App Service with New Relic

This guide puts the demo into your own Azure subscription: two Linux App Service apps (portal and API), an Azure SQL
database, and the New Relic .NET agent running in both apps.

**The New Relic agent is already inside the packages.** `api.zip` and `portal.zip` each contain a `newrelic/` folder with
the Linux agent and the demo's `newrelic.config`. You don't need to install a site extension or anything on the server.
You only set a few environment variables (step 4 below), and the agent loads when the app starts.

## What's in this folder (`azure/`)

```
DEPLOY-AZURE.md            this guide
api.zip                    prebuilt API (Linux x64, .NET 10) with the New Relic agent
portal.zip                 prebuilt portal (Linux x64, .NET 10) with the New Relic agent
main.bicep                 infrastructure template (used by Option A)
deploy-cloudshell.sh       one-command deploy script (Option A); finds a working region/tier by itself
find-working-region.sh     optional: reports which regions/tiers your subscription can use
```
The package's `source/` folder has the full source code, needed only if you want to change or rebuild the app.
To try the app on your Mac without Azure, see `local/LOCAL-RUN.md`. That's a separate, fully local option.

## Before you start

| You need | Where to get it |
|---|---|
| An Azure subscription where you can create resource groups | Azure Portal |
| A **New Relic license key** (type *INGEST - LICENSE*, about 40 characters) | New Relic → your user menu → **API Keys** → copy the key with type *INGEST - LICENSE*. Don't use a *User* key (starts with `NRAK-`). |
| A SQL admin password | Make one up: 8+ characters with upper case, lower case, a digit and a symbol. Avoid `'`, `"` and `;`. |

The other machine doesn't need .NET or the Azure CLI. Everything below runs in the browser, using the Azure Portal and Azure Cloud Shell.

**Approximate cost:** an App Service B1 plan (about $13/month) plus Azure SQL S2 (about $75/month), and the optional SQL monitoring container (about $20/month). S0 (about $15/month) works for light use but saturates under Auto traffic. Delete the resource group when you're done (see the end of this guide).

---

## Option A: automated (recommended, about 10 minutes)

1. Sign in to https://portal.azure.com and open **Cloud Shell** (the `>_` icon in the top bar). Choose **Bash**.
   - The first time, it asks to create storage. Accept the defaults, or pick "No storage account required".
2. In Cloud Shell click **Manage files → Upload** and upload the files from this `azure/` folder:
   `api.zip`, `portal.zip`, `main.bicep`, `deploy-cloudshell.sh` (and optionally `find-working-region.sh`).
3. Run (replace the values):
   ```bash
   export NEW_RELIC_LICENSE_KEY='your-ingest-license-key'
   export SQL_ADMIN_PASSWORD='<choose-a-strong-password>'
   bash deploy-cloudshell.sh
   ```
4. When it finishes, the script prints the **Portal URL**. Open it, sign in (pick any organization), and go to **Verify** below.

**The script picks regions for you.** Many subscriptions (new or Free Trial personal accounts, company sandboxes) have
0 App Service quota for some tiers, and some regions (often East US 2) aren't accepting new Azure SQL servers. So the script
first **tries** creating a small App Service plan in several regions and tiers (B1 → S1 → P0v3 → P0v4 → P1v3 → F1) and
an empty SQL server in several regions, deleting each test right away. It then deploys to the first combination that works.
The web apps and SQL can end up in different regions, which is fine for the demo.

To choose them yourself instead, set any of these before running:
```bash
export RG='rg-securedocs-demo'       # resource group (default rg-securedocs-demo)
export LOCATION='centralus'          # region for the web apps
export APP_SERVICE_SKU='B1'          # App Service tier
export SQL_LOCATION='westus3'        # region for Azure SQL
```
To only check what your subscription allows, without deploying, run `bash find-working-region.sh`.

The script creates the same resources as Option B and applies every setting from the tables in Option B, including the
New Relic variables and a random fault-control key.

> If the script fails with a message about `DOTNETCORE|10.0` not being available, see Troubleshooting.

---

## Option B: manual setup in the Azure Portal (about 30 minutes)

> **On a company subscription?** It probably has Azure Policy rules that block some settings: allowed tiers and regions,
> required tags, SQL password sign-in, public SQL access, Cloud Shell. Read
> [Company subscription guardrails](#company-subscription-guardrails-option-b) first. It says which steps change for each rule.

Use the same region for everything.

### 1. Resource group
**Create a resource → Resource group.** Name it `rg-securedocs-demo` and pick a region.

### 2. Azure SQL database
**Create a resource → SQL Database**
- **Basics**
  - Resource group: `rg-securedocs-demo`. Database name: `SecureDocs`.
  - Server: **Create new**. Choose a globally unique name (e.g. `securedocs-sql-<yourinitials>`) and the same region.
    For Authentication method choose **Use SQL authentication**, with admin login `securedocsadmin` and your password.
  - Want to use SQL elastic pool: **No**.
  - Compute + storage: **Configure database → Standard S2 (50 DTU)**. S0 is cheaper but runs at about 90% CPU once Auto traffic and SQL monitoring are on.
  - Backup storage redundancy: **Locally-redundant** (cheapest).
- **Networking**: Connectivity method **Public endpoint**, and turn on **Allow Azure services and resources to access this server**.
  - Optional: also turn on *Add current client IP address* if you want to query the database from your machine.
- **Review + create**, then **Create**.

After it's created, open the database and go to **Settings → Connection strings → ADO.NET (SQL authentication)**.
Copy it and replace `{your_password}` with your real password. It looks like this:
```
Server=tcp:securedocs-sql-xx.database.windows.net,1433;Initial Catalog=SecureDocs;Persist Security Info=False;User ID=securedocsadmin;Password=YOUR_PASSWORD;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;
```
You don't need to create any tables. The API creates the schema and seeds about 9,000 synthetic documents on its first start.

### 3. Two web apps (API first, then portal)
**Create a resource → Web App.** Do this twice: once with the API name, once with the portal name.

| Field | API app | Portal app |
|---|---|---|
| Name | `securedocs-api-<unique>` | `securedocs-portal-<unique>` |
| Publish | Code | Code |
| Runtime stack | **.NET 10 (LTS)** | **.NET 10 (LTS)** |
| Operating System | **Linux** | **Linux** |
| Region | same as SQL | same |
| Linux plan | Create new, e.g. `securedocs-plan` | **reuse** `securedocs-plan` |
| Pricing plan | **Basic B1** (or higher) | same plan |
| Monitoring tab → Application Insights | **No** | **No** |

Turn **Application Insights off**. Its auto-instrumentation can conflict with the New Relic profiler.

> **Error "Operation cannot be completed without additional quota … Current Limit (B1 VMs): 0"?**
> Your subscription isn't allowed any Basic (B1) App Service plans in that region. This is common on new, trial and
> company subscriptions. See [App Service quota error](#app-service-quota-error-current-limit-b1-vms-0) below for fixes.

Write down each app's **Default domain**, e.g. `securedocs-api-xyz.azurewebsites.net`.

### 4. Configure the API app
Open the API web app.

**Settings → Environment variables → App settings.** Add each row (**+ Add**), then click **Apply** at the bottom:

| Name | Value |
|---|---|
| `CORECLR_ENABLE_PROFILING` | `1` |
| `CORECLR_PROFILER` | `{36032161-FFC0-4B61-B559-F6C5D41BAE5A}` |
| `CORECLR_PROFILER_PATH` | `/home/site/wwwroot/newrelic/libNewRelicProfiler.so` |
| `CORECLR_NEWRELIC_HOME` | `/home/site/wwwroot/newrelic` |
| `NEW_RELIC_HOME` | `/home/site/wwwroot/newrelic` |
| `NEWRELIC_LOG_DIRECTORY` | `/home/LogFiles/NewRelic` |
| `NEW_RELIC_LICENSE_KEY` | *your ingest license key* |
| `NEW_RELIC_APP_NAME` | `SecureDocs-API` |
| `NEW_RELIC_DISTRIBUTED_TRACING_ENABLED` | `true` |
| `Database__Provider` | `SqlServer` |
| `Faults__ControlKey` | any random text, e.g. a GUID. **The portal must use the same value.** |
| `Ftp__InboxPath` | `/home/data/securedocs/ftp-inbox` |

**Same page, Connection strings tab → + Add**:
- Name `SecureDocs`
- Value: the ADO.NET connection string from step 2
- Type **SQLAzure**

Click **Apply**.

**Settings → Configuration → General settings**:
- Startup Command: `dotnet SecureDocs.Api.dll`
- Always on: **On**
- HTTPS Only: **On**
- FTP state: **Disabled**

Click **Save**.

**Monitoring → Health check**: Enable, path `/health`, then **Save**.

### 5. Configure the portal app
Open the portal web app and add these under **Settings → Environment variables → App settings**:

| Name | Value |
|---|---|
| `CORECLR_ENABLE_PROFILING` | `1` |
| `CORECLR_PROFILER` | `{36032161-FFC0-4B61-B559-F6C5D41BAE5A}` |
| `CORECLR_PROFILER_PATH` | `/home/site/wwwroot/newrelic/libNewRelicProfiler.so` |
| `CORECLR_NEWRELIC_HOME` | `/home/site/wwwroot/newrelic` |
| `NEW_RELIC_HOME` | `/home/site/wwwroot/newrelic` |
| `NEWRELIC_LOG_DIRECTORY` | `/home/LogFiles/NewRelic` |
| `NEW_RELIC_LICENSE_KEY` | *your ingest license key* |
| `NEW_RELIC_APP_NAME` | `SecureDocs-Portal` |
| `NEW_RELIC_DISTRIBUTED_TRACING_ENABLED` | `true` |
| `Api__BaseUrl` | `https://<API default domain>` (no trailing slash) |
| `Api__PublicUrl` | `https://<API default domain>` |
| `Faults__ControlKey` | **same value as the API** |

**General settings**:
- Startup Command: `dotnet SecureDocs.Web.dll`
- Always on: **On**
- HTTPS Only: **On**
- FTP state: **Disabled**

Click **Save**. Then set **Health check** to `/health`.

### 6. Upload the code
Open **Cloud Shell (Bash)**. Use **Manage files → Upload** to upload `api.zip` and `portal.zip`, then run:
```bash
RG=rg-securedocs-demo
az webapp deploy -g $RG -n securedocs-api-<unique>    --src-path api.zip    --type zip
az webapp deploy -g $RG -n securedocs-portal-<unique> --src-path portal.zip --type zip
```
Deploy the API first. Its first start takes 1–3 minutes because it creates and seeds the database.

> Cloud Shell blocked or not allowed? See [Uploading the code without Cloud Shell](#g6-uploading-the-code-without-cloud-shell).

---

## Company subscription guardrails (Option B)

Company subscriptions are usually protected by **Azure Policy** and quotas. When a policy blocks you, the portal shows
**"RequestDisallowedByPolicy"** or **"Resource ... was disallowed by policy"** on the *Review + create* page, along with the
**policy name**. That name tells you which section below applies.

**Find your rules before you start:** in the Azure Portal open **Policy → Assignments** and filter to your subscription
(or management group). Read the assigned policies and initiatives, especially ones about *allowed locations*,
*allowed SKUs*, *tags*, *SQL*, *App Service* and *network*. If you can't see them, ask your cloud/platform team:
*"Which regions, App Service tiers and SQL settings are allowed in subscription X?"*

### G1. Allowed regions and tiers
- Use a region from the policy's *allowed locations* list, for **both** SQL and the web apps.
- If B1 is blocked or has 0 quota, pick another tier your company allows (often **P0v3/P1v3** or **S1**), or request quota
  (see [App Service quota error](#app-service-quota-error-current-limit-b1-vms-0)).
- Some companies require an existing, shared App Service plan. If so, select that plan in step 3 instead of creating one.

### G2. Required tags
If a policy requires tags (e.g. `CostCenter`, `Owner`, `Environment`, `Application`), fill them in on the **Tags** tab of
every create page: resource group, SQL server and database, App Service plan, and both web apps. The policy message lists
the tag names it expects.

### G3. SQL without passwords (Entra-only authentication)
Very common policy: *"Azure SQL Database should have Microsoft Entra-only authentication enabled"*. SQL logins with
passwords are not allowed, so the API signs in with its **managed identity** instead. The app supports this already.
Only the setup changes:

1. **Step 2 (SQL):** under *Authentication method* choose **Use Microsoft Entra-only authentication**. Click
   **Set admin** and pick yourself, or a group you're in.
2. **Step 3:** after creating the API web app, open it → **Settings → Identity → System assigned → Status: On → Save**.
3. Give that identity access to the database. Open the database → **Query editor (preview)** and sign in with
   *Microsoft Entra authentication*. Then run this, replacing the name with your **exact API web app name**:
   ```sql
   CREATE USER [securedocs-api-<unique>] FROM EXTERNAL PROVIDER;
   ALTER ROLE db_owner ADD MEMBER [securedocs-api-<unique>];
   ```
   `db_owner` is needed because the API creates its tables on first start. If your DBA won't grant `db_owner`, use
   `db_ddladmin`, `db_datareader` and `db_datawriter` instead.
   - If Query editor is blocked, any Entra-authenticated SQL tool works (SSMS, Azure Data Studio, VS Code mssql extension).
4. **Step 4:** use this connection string instead of the password one (name `SecureDocs`, type **SQLAzure**):
   ```
   Server=tcp:<your-sql-server>.database.windows.net,1433;Initial Catalog=SecureDocs;Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;
   ```
   (Use your actual database name. In step 2 the guide calls it `SecureDocs`.)
5. Slow-query explain plans in New Relic also work with this setup, because `db_owner` includes the SHOWPLAN permission.

### G4. SQL public access not allowed (private endpoint)
Policy example: *"Public network access on Azure SQL Database should be disabled"*. When this applies, you can't turn on
**Allow Azure services**. Instead the API reaches SQL privately:

1. Create (or ask for) a **virtual network** with two subnets: one for the private endpoint (e.g. `snet-pe`), one
   **delegated to `Microsoft.Web/serverFarms`** for App Service (e.g. `snet-app`, /27 or larger).
   Many companies have a standard "landing zone" VNet for this. Ask which one to use.
2. **SQL server → Networking → Private access → + Create a private endpoint.** Put it in `snet-pe` and accept
   **Integrate with private DNS zone** (`privatelink.database.windows.net`).
3. **API web app → Settings → Networking → Outbound traffic → Virtual network integration** and choose `snet-app`.
   (VNet integration needs Basic tier or higher.)
4. The connection string doesn't change. Keep the normal `<server>.database.windows.net` name, and the private DNS zone
   makes it resolve to the private IP address.

### G5. Outbound internet is restricted (New Relic can't report)
If the apps route traffic through a company firewall (VNet integration with *Route All*), the New Relic agent needs
outbound **HTTPS (443)** to `collector.newrelic.com` and `*.nr-data.net`, or `collector.eu01.nr-data.net` for EU
accounts. Ask the network team to allow these. Symptom: the apps work but nothing appears in New Relic, and the agent
log in `/home/LogFiles/NewRelic` shows connection errors.

### G6. Uploading the code without Cloud Shell
Use whichever of these your company allows. All of them use your normal Entra sign-in:

| Method | How |
|---|---|
| **Azure CLI on your machine** | Install the Azure CLI, run `az login`, then run the two `az webapp deploy` commands from step 6, in the folder holding the zips. |
| **VS Code** | Install the *Azure App Service* extension and sign in. **Extract** `api.zip` into a folder, right-click the API app in the Azure panel → **Deploy to Web App…** → pick that folder. Do the same for `portal.zip`. |
| **Visual Studio (Windows)** | Open `../source/SecureDocs.sln`, right-click **SecureDocs.Api** → **Publish** → Azure → Azure App Service (Linux) → pick the existing app → Publish. Repeat for **SecureDocs.Web**. This builds from source, so it needs the .NET 10 SDK. |
| **Kudu zip deploy** (only if *SCM Basic Auth Publishing Credentials* is allowed) | Web app → **Development Tools → Advanced Tools → Go**. Then from a terminal: `curl -X POST -u '<user>:<pass>' --data-binary @api.zip https://<app>.scm.azurewebsites.net/api/zipdeploy`. The user and password come from **Download publish profile** on the app's Overview page. Many companies disable this. |

However you deploy, the `newrelic/` folder must end up at the top of `/home/site/wwwroot`.
Check with **SSH**: `ls /home/site/wwwroot/newrelic/libNewRelicProfiler.so`.

### G7. Other policies you may see
| Policy | What to do |
|---|---|
| *App Service apps should only be accessible over HTTPS* / *latest TLS* / *FTPS only or disabled* | Already covered: HTTPS Only On, TLS 1.2, FTP Disabled. |
| *App Service apps should use managed identity* | Turn on **Identity → System assigned** on both apps (harmless). |
| *Application Insights should be enabled* | Allowed, but keep the App Insights **auto-instrumentation agent off** (*Application Insights → Instrument your application → Disable*). Only one profiler can run, and it must be New Relic. |
| *App Service apps should disable public network access* | The portal wouldn't be reachable from the internet. Ask for an exception for the **portal** app, or demo from inside the company network. |
| *Diagnostic settings required* | Add a diagnostic setting to your company's Log Analytics workspace as instructed. It doesn't affect the demo. |

---

## Verify

1. **API:** open `https://<API domain>/health`. You should see `{"status":"Healthy","database":"Microsoft.EntityFrameworkCore.SqlServer","documents":9000+}`.
   - If it's still starting, wait a minute and refresh.
2. **Portal:** open `https://<portal domain>`, pick an organization and click **Sign in**. The dashboard should show numbers.
3. **New Relic agent loaded:**
   - In the Azure Portal, open each app's **Monitoring → Log stream**. After a restart you should see the app start without profiler errors.
   - For a direct check, open **Development Tools → SSH** on the app and run `ls /home/LogFiles/NewRelic`. You should see `newrelic_agent_*.log` files. Run `tail -50` on the newest one and look for `Reporting to:` with your account URL.
4. **New Relic UI:** go to **APM & Services**. **SecureDocs-Portal** and **SecureDocs-API** appear 2–5 minutes after the first requests.
5. **Generate data:** in the portal click **Fault Control** (bottom-right) and turn on **Auto traffic**. Leave it on for a few minutes.

## Business KPI dashboard
The apps also send business KPIs (client tier, SLA attainment, documents at risk, payment value in flight, file pickup
delay, QA yield, possible PHI misprints, reprints). Import the ready-made dashboard: **New Relic → Dashboards → Import
dashboard**, then paste `dashboard-securedocs.json` (in this folder). Details, KPI definitions and alert ideas are in
`BUSINESS-KPIS.md`. Generate the dashboard file for your account first:
`python3 make_dashboard.py <ACCOUNT_ID>`.

## Synthetic monitors and Pathpoint flows
`synthetics/` has 4 monitor scripts, and `pathpoint/` has 2 business-flow definitions with their KPIs and touchpoints
(read `pathpoint/README.md` first). To create everything in your account at once, use a New Relic **User** API key:
```bash
export NEW_RELIC_USER_KEY=NRAK-...
python3 newrelic_setup.py --account <ACCOUNT_ID> --portal-url <portal URL> --api-url <api URL>
```

## Running the demo

| Fault Control setting | Show in New Relic |
|---|---|
| **Slow address service** (e.g. 5000 ms) | **Distributed tracing**: a slow trace that runs Portal → API → address service → Azure SQL |
| **Slow SQL** | **SecureDocs-API → Databases**, then the slow query list with obfuscated SQL and explain plan; **Transactions → traces** |
| **N+1 queries**, then open a batch | A transaction trace with hundreds of SQL calls |
| **Stall production floor** | **SecureDocs - Business KPIs** dashboard: documents at risk, then past SLA, payment value in flight. Late batches raise `SlaBreachException` in Errors Inbox |
| **API error rate**, **Database errors**, **Portal errors**, **QA camera mismatches** | **Errors Inbox**: separate error groups with stack traces, attributes (`client.code`, `batch.id`), the linked trace and logs |
| **Reset all faults** | Response time and error rate return to normal |

Useful NRQL queries:
```sql
SELECT count(*) FROM QaInspection FACET passed, cameraId SINCE 1 hour ago
SELECT sum(accepted) FROM DocumentBatchIngested FACET source, clientCode TIMESERIES
SELECT count(*) FROM TransactionError FACET error.class, appName SINCE 30 minutes ago
```

**Optional: Azure SQL server-side metrics** (DTU, CPU, deadlocks). APM covers query time from the app's side. To also see server-side metrics in New Relic, go to **Infrastructure → Azure → Link an Azure account**. Create the service principal it asks for (Reader role on the subscription), then turn on **Azure SQL Database**.

## App Service quota error ("Current Limit (B1 VMs): 0")

You see this when you create the web app or plan:
```
Operation cannot be completed without additional quota.
Current Limit (B1 VMs): 0   Current Usage: 0   Amount required for this deployment (B1 VMs): 1
```
Quotas are set **per pricing tier and per region**, so you can work around it in any of these ways. Try them in this order:

> **Quick way to find a tier that works:** create just the plan first (**Create a resource → App Service Plan**, Linux),
> trying P0v3, S1, P1v3 or another region until one is accepted. Then in step 3, select that **existing plan** when
> creating both web apps. That's faster than filling in the whole web app form for each attempt.
> You can also look up your limits in **Quotas → App Service**, filtered to your subscription and region.
> On a company subscription, ask your cloud team which App Service plan tiers and regions are approved, or whether a shared
> App Service plan already exists that you can use.

0. **Check which tiers you're allowed first.** In the portal open **Quotas → App Service**, pick your subscription,
   and download the CSV. Look for rows with a *Current Limit* above 0. On many company sandbox subscriptions only
   **Premium v4** (`P0v4`, `P1v4` …) has quota and everything else is 0. If so, use **P0v4**, the smallest Premium v4
   size. It's plenty for this demo, with both apps sharing one plan. In the pricing list it's under **Premium v4**. Keep **Zone redundancy
   off**, because zone-redundant sizes usually have 0 quota.
   - If P0v4 isn't offered in the list, create the plan in a **new resource group**. Some newer tiers only run on newer
     hardware that older resource groups can't use.
   - Option A script: `export APP_SERVICE_SKU='P0v4'`.
1. **Pick a different pricing tier (fastest).** On the *Create Web App* page under **Pricing plan**, click
   **Explore pricing plans** and choose **Standard S1** or **Premium v3 P0v3**. Each tier has its own quota and they are
   often available when B1 isn't. Both run the demo well, but cost more than B1:
   roughly $70/month for S1 and $60–80/month for P0v3, depending on region.
   - Option A script: `export APP_SERVICE_SKU='P0v3'` (or `S1`) before running it.
2. **Pick a different region.** Try another nearby region (e.g. `eastus`, `centralus`, `westus2`, `westus3`).
   **If your SQL database already exists, keep it.** The web apps can be in a different region from SQL. It only adds a few
   milliseconds per query, which is fine for the demo.
3. **Ask Microsoft to raise the quota** (free, usually approved within minutes to a few hours):
   - In the Azure Portal search for **Quotas** and open **App Service**. Filter to your subscription and region, find
     **B1 VMs** (or *Basic*), click the pencil/**Request increase** icon, and request at least **1**.
   - If App Service isn't listed there, go to **Help + support → Create a support request**. Choose Issue type **Service and subscription
     limits (quotas)**, Quota type **App Service**, then enter the region, the tier (Basic B1) and the new limit (1 or more).
4. **Free tier F1 (last resort).** It deploys, but the demo runs poorly. F1 has no *Always On*, so the background
   production floor and FTP ingestion stop when the site is idle. It also has a daily CPU limit (60 minutes) that *Auto traffic*
   uses up quickly. If you use it, leave **Always on** off (F1 doesn't allow it) and generate traffic only while presenting.

**Personal accounts:** a 0 limit for B1 usually means a **Free Trial** subscription (or one created very recently).
Upgrading to Pay-As-You-Go (*Subscriptions → your subscription → Upgrade*) keeps your remaining free credit and lifts
most App Service limits. The Option A script also falls back to the Free tier (F1) if nothing else is allowed.

**Free trial / Azure for Students subscriptions** often have 0 quota for every paid App Service tier. Upgrading to
pay-as-you-go (*Subscriptions → your subscription → Upgrade*) is usually the quickest fix.

## Troubleshooting

| Symptom | Fix |
|---|---|
| **"Location '…' is not accepting creation of new … SQL Database servers"** (`RegionDoesNotAllowProvisioning`) | That region is full for new SQL servers. Use another region for SQL (`SQL_LOCATION=centralus` for Option A; in Option B pick another region when creating the SQL server). Web apps and SQL can be in different regions. |
| **"Operation cannot be completed without additional quota" / "Current Limit (B1 VMs): 0"** | See [App Service quota error](#app-service-quota-error-current-limit-b1-vms-0): check your quota CSV for a tier with a limit above 0 (often **P0v4** on company sandboxes), or request a quota increase. |
| **"Container did not respond to startup probe on port 8080 within … 230s"** | Fixed in this version: the API now starts answering immediately and creates demo data in the background (`/health` shows `Initializing` until it's done). If you still see this, check **Log stream** for a startup error, or add the app setting `WEBSITES_CONTAINER_START_TIME_LIMIT` = `600`. |
| **.NET 10 isn't in the Runtime stack list**, or the deploy fails with `DOTNETCORE\|10.0` | Your region doesn't offer .NET 10 on Linux yet. Pick a different region. To keep the region, rebuild from `../source/` for the newest .NET version offered: change `<TargetFramework>` in the three `.csproj` files and update package versions to match. |
| API `/health` returns 503, or the app won't start | Open **Log stream**. Usually it's the connection string: wrong password, the name isn't exactly `SecureDocs`, or the type isn't SQLAzure. Also check that SQL **Networking → Allow Azure services** is on. |
| Portal shows "The SecureDocs API is unreachable" | `Api__BaseUrl` is wrong. It must be `https://<api-domain>`, with no path and no trailing slash. |
| Fault toggles don't stick, or the panel shows a 401 | `Faults__ControlKey` is different on the two apps. |
| No data in New Relic | (1) The key must be the **INGEST - LICENSE** key, not a User key (`NRAK-…`). (2) Check the setting names for typos. (3) **Restart** the app after changing settings. (4) Read `/home/LogFiles/NewRelic/*.log` over SSH. |
| App fails to start right after adding the `CORECLR_*` settings | Make sure `CORECLR_PROFILER_PATH` points to `/home/site/wwwroot/newrelic/libNewRelicProfiler.so`, and that the zip was deployed. The `newrelic/` folder must be at the root of `wwwroot`. |
| Want to start over with fresh data | In the Azure Portal open the database → **Query editor**, then drop the tables, or delete and recreate the database. The API reseeds on its next start. |

## Clean up
Delete the resource group, which removes everything: **Resource groups → rg-securedocs-demo → Delete resource group**.
From Cloud Shell: `az group delete -n rg-securedocs-demo`.

## Security notes
- This is a demo with **synthetic data only**. Sign-in is simulated, and the API's `/api/clients` endpoint exposes the demo API keys. Don't load real PHI.
- The fault endpoints need `Faults__ControlKey`, so set it to a random value. To lock the API down further, use **Networking → Access restrictions** on the API app to allow only the portal app's outbound IP addresses (listed in the portal app's **Properties**).
