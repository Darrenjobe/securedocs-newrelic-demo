# SecureDocs demo runbook: break it, then find the root cause

Use the **Fault Control** panel (the floating button on any portal page) to break the app live. Each toggle causes a
realistic failure in the real code path. New Relic doesn't know about the switch: it finds each root cause from
normal telemetry (traces, query plans, stack traces, business events), the same way it would in a real incident.

Run **2–3 stories**, one at a time. Start each one from a business symptom and drill down to the line of code or the
query.

**Every switch also leaves a deployment marker.** Turning a switch on or off records a "release" on the affected app
in New Relic (Change tracking), with a deliberately upbeat note. Turning **Slow SQL** on, for example, ships
*"Optimized query: faster case-insensitive document search…"*, and turning it off ships *"Fixing query slowness…"*.
The marker appears as a vertical line on the APM charts, right where the fault starts. That lets every story end
the way real incidents often do: **"what changed?"** → the release that went out just before the spike. Open
**APM → the app → Change tracking** (or click the marker on a chart) to show the release, then the impact analysis
New Relic computes for it (errors, latency and throughput before and after).

Each switch marks the app where its code lives:
- **Portal errors:** SecureDocs-Portal.
- **Every other switch:** SecureDocs-API.

The one-off buttons don't record markers. Markers need the optional User API key app setting; see
[Recreate in your environment](../README.md#recreate-in-your-environment).

---

## Before the demo (30–45 minutes ahead)

Starting early gives New Relic enough clean data that charts, service levels and alerts all look healthy.

**1. Turn the Azure resources back on** (skip any that are already running):
```bash
az sql db update -g rg-securedocs-demo -s <sql-server-name> -n SecureDocs --service-objective S2
```
```bash
az container start -g rg-securedocs-demo -n securedocs-sql-monitor
```
Find `<sql-server-name>` with `az sql server list -g rg-securedocs-demo -o table`. The database must be on **S2**; on S0 the latency service levels break under Auto traffic.

**2. Reset the app** (Fault Control panel):
- **Reset all faults**
- **Clear production backlog (demo reset)**
- Leave **Auto traffic** on: it provides the normal baseline.

**3. Clean up New Relic:**
- **Alerts → Issues:** close any open issues from earlier runs.
- **Errors Inbox:** mark old error groups **Resolved**, so new ones stand out.
- **Synthetics:** all 4 monitors enabled and passing (run any red ones once by hand).
- **Pathpoint:** both flows green.

**4. Set the time window** to **Last 30 minutes** on dashboards, APM and service levels.

**5. Open these tabs:** the Pathpoint flows, the Business KPIs dashboard, APM (SecureDocs-API), Errors Inbox and the
Slow query investigation dashboard.

**Readiness check (5 minutes before):**
- [ ] Portal loads and sign-in works
- [ ] APM error rate about 0%, API p95 under about 2 s
- [ ] Business KPIs dashboard shows batches completing within SLA
- [ ] Azure SQL health dashboard shows CPU around 30–40%

**What you can't reset:** service level error budgets use 7- and 28-day windows, so budget burned by earlier tests
stays burned. Talk about compliance over the last 30–60 minutes instead, or use the earlier burn as "here's what
the last incident cost us."

---

## Story 1: "Clients say search is slow" (slow SQL → database root cause)

**Break it:** turn on **Slow SQL** (2500 ms). Click **Run one slow query now** for an immediate trace.

**Find it (2–3 minutes):**
1. **Service levels:** *Dashboard, search and reports under 3 s* and *Database queries under 500 ms* burn error
   budget. The **Slow database time** alert opens.
2. **APM → SecureDocs-API → Transactions:** search, dashboard and reports rise to the top. The breakdown shows the
   time is in the **database**, not the code.
3. **Slow transaction trace:** the hot span is hand-written SQL in `SqlFaultQueries.SlowDocumentSearchAsync`, with
   code-level metrics pointing to that method.
4. **Slow query investigation dashboard:**
   - **Find:** the same query appears in both the app's view and the database's own top slow queries.
   - **Why:** the execution plan shows a **table scan**. `UPPER(LTRIM(...))` and a leading-wildcard `LIKE` stop it
     from using an index (plus a `WAITFOR` delay).
   - **Who is affected:** which clients and tiers, with trace IDs.

5. **What changed:** a deployment marker sits right where the latency jumps: *"Optimized query…"*. The "optimization"
   is the regression.

**Root cause:** a non-sargable query scanning the table. **Fix:** rewrite the predicate or add a proper index.
Turning the switch off ships *"Fixing query slowness"*, and the latency drops right at that second marker.

**Say:** "The app and the database tell the same story in one place, so the DBA and the developer aren't arguing
about whose problem it is."

---

## Story 2: "Uploads are failing" (database errors → Errors Inbox, users impacted)

**Break it:** turn on **Database errors**. Click **SQL error** for an immediate one.

**Find it:**
1. The **Database errors** alert fires, the *Client API intake* synthetic fails, and the intake step in
   **Pathpoint** goes red.
2. **Errors Inbox:** a new `SqlException` group, with **users impacted** and the clients they belong to.
3. **Open the error:** the stack trace leads to a "duplicate check" in `IngestionService` that queries
   `dbo.PrintJobArchive_2019`, a legacy table that **no longer exists** ("Invalid object name").
4. **Distributed trace:** portal click → API → the failing SQL call.

5. **What changed:** the error spike starts at the release *"Improved duplicate detection: check the print-job
   archive before printing"*.

**Root cause:** code still depends on a table that was dropped. **Fix:** remove the dependency or restore the table.

**Say:** "Errors Inbox tells you not just what broke, but who it hit and where in the code."

---

## Story 3: "Everything is slow, but the database is fine" (downstream dependency → distributed tracing)

**Break it:** turn on **Slow address service** (3000 ms), or click **Run one slow trace now**.

**Find it:**
1. Intake latency rises. The **Address verification slow** and **Intake API slow** alerts fire.
2. **APM:** database time is normal, so the database is ruled out.
3. **Distributed trace:** the time is in the outbound HTTP call to the address-verification service (CASS/NCOA),
   and the waterfall makes it obvious.

**Root cause:** a slow third-party dependency. **Fix:** a timeout, a circuit breaker, or escalate to the vendor.

**Say:** "Tracing lets you rule things out in seconds. Without it, everyone starts by blaming the database."

---

## Story 4: "Are we going to miss our SLAs?" (business impact first)

This is the strongest story for business stakeholders.

**Break it:** turn on **Stall production floor**. Optionally also turn on **QA camera mismatches**.

**Find it:**
1. **Pathpoint, production flow:** *Production floor running* goes red. Then *Documents at risk* climbs, and
   **Enterprise documents past SLA** fires.
2. **Business KPIs dashboard:** queue depth and oldest item grow, showing **which clients and tiers** are at risk.
3. **With QA mismatches on:** the *Print accuracy* service level drops below 99%, *Possible PHI misprints caught*
   spikes (by camera and mismatch type), and reprints climb.

**Root cause:** the production floor isn't processing work (or the QA cameras are misreading).

**Say:** "New Relic doesn't just say 'CPU is high'. It says 'Enterprise client PCHP will miss SLA in 10 minutes.'
That's the difference between error tracking and observability."

---

## Optional story 5: performance anti-pattern (developer audiences)

**Break it:** turn on **N+1 queries**, then open a batch detail page.

**Find it:** the trace shows hundreds of identical small queries, one per document. **Fix:** load the QA results in
one query.

---

## Timing

| Signal | Appears after |
|---|---|
| Traces and errors | ~1 minute |
| Alerts | 1–3 minutes (sparse business ones up to 5) |
| Pathpoint and service levels | Right after the alerts |
| Query plans (database integration) | Up to ~2 minutes |

While you wait, talk through the architecture, or click **Send a burst of 30 requests** to speed things up.

## Resetting between stories
1. **Reset all faults**
2. **Clear production backlog (demo reset)**
3. Wait 5–10 minutes for alerts to close on their own.

Run one story at a time, so the root cause stays clear.

## Fault Control quick reference

| Toggle / button | What it does | Best shown in |
|---|---|---|
| API error rate | Ingestion, dashboard and production throw errors | Errors Inbox |
| Database errors / SQL error | Queries a table that doesn't exist | Errors Inbox, alerts |
| Portal errors / Portal exception | ~50% of portal pages crash in the web tier | Errors Inbox (portal + browser) |
| API exception | One HIPAA field validation error | Errors Inbox |
| QA camera mismatches | ~35% of printed pieces fail QA | Business KPIs, Print accuracy SLI |
| Stall production floor | The queue backs up: documents at risk, SLA misses | Pathpoint, Business KPIs |
| Slow address service | The API's downstream call stalls | Distributed tracing |
| Slow SQL | Search, dashboard and reports scan tables | Slow query dashboard, query plans |
| N+1 queries | Batch detail runs one query per document | Transaction traces |
| Clear production backlog | Quietly finishes queued work (demo reset) | — |
| Reset all faults | Turns every fault off | — |

## Be upfront with the audience
Say plainly that the faults are injected with a switch, so the failures can be reproduced on demand. Then point out
that New Relic doesn't know about the switch: it reaches each root cause from normal telemetry, exactly as it would
in a real incident.

## After the demo (save money)
```bash
az container stop -g rg-securedocs-demo -n securedocs-sql-monitor
```
```bash
az sql db update -g rg-securedocs-demo -s <sql-server-name> -n SecureDocs --service-objective S0
```
With both done the environment costs about $0.91/day instead of about $3.56/day. Turn **Auto traffic off** while
the database is on S0.
