# SecureDocs Pathpoint flows

Two Pathpoint flows model the business journeys that matter most to a healthcare document plant like
SecureDocs: **getting a client's member data in**, and **turning it into unique, correct documents delivered
on time with proof**. Each flow maps those journeys to the telemetry behind them. Business people see stages
in plain language; engineers can click through to the trace, query or monitor behind each step.

| | Flow 1: Member data intake | Flow 2: Unique document production & proof of delivery |
|---|---|---|
| Business question | *"Did our client's file get in, clean and on time?"* | *"Is every piece correct and will we hit the client's SLA?"* |
| Covers | Intake → Validate | Produce → Deliver & Prove |
| Who cares | Client success / account management, intake ops | Plant operations, compliance, account management |
| Failure costs | Missed mail dates, regulatory deadlines (e.g. annual member notices) | HIPAA exposure (misprints), SLA penalties, client churn |

How health works: each step's colour comes from **NRQL alert conditions** in the policy
*SecureDocs - Business flows*. Green means no open incident, yellow a warning, red critical. The APM services and synthetic
monitors are attached to steps too (excluded from the colour) so you can drill straight into traces and check results.

---

## Flow 1: Member data intake

*A health-plan client gets its member file into the plant: connect, send data by API or secure file transfer, pass
HIPAA validation and address verification, and land in the production queue in time for the mail date.*

**Flow KPIs** (last 24 h vs the same time 7 days earlier; synthetic test traffic excluded)
| KPI | Why it matters |
|---|---|
| Records received | Is client volume normal? A drop can mean a missing file before a mail date |
| Records rejected | Data quality problems on the client's side or in our rules |
| Files processed | Secure-file-transfer throughput |
| Avg file pickup delay (ms) | How long client files wait before we touch them |
| Undeliverable addresses | Stale mailing lists mean returned mail and wasted postage |
| Payment value received ($) | Dollars entering the plant (checks, statements) |

**Stages, steps and touchpoints**
| Stage | Step | Touchpoints (signals) | Turns red when… |
|---|---|---|---|
| 1. Client connects | Client portal sign-in | Synthetic *Portal journey (browser)*, APM *SecureDocs-Portal* | The scripted sign-in → dashboard → submit → search fails; portal error rate > 5% |
| | Client API available | Synthetic *API health* ping, APM *SecureDocs-API* | `/health` fails or isn't "Healthy" |
| 2. Client sends member data | Submit batch via API | Synthetic *Client API intake*, APM *SecureDocs-API* | Scripted submit fails; intake p95 > 3 s; intake errors > 5% |
| | Secure file transfer (FTP / SFTP drop) | Synthetic *Secure file transfer* | Test file not processed within 45 s; any file waits > 60 s |
| 3. Validate & verify | Level 1: HIPAA field validation | Business event `DocumentBatchIngested` | Rejection rate > 10% |
| | Level 2: Address verification (CASS / NCOA) | Downstream address service (APM span) | Address checks average > 2 s |
| 4. Stored & queued for production | Azure SQL - SecureDocs database | APM database telemetry | SQL errors; p95 DB time > 2 s |
| | Production queue accepting work | `ProductionBacklog` events | Production floor stalled |

Stage KPIs: *Batches received (1h)*, *Records rejected (1h)*, *Records queued for production (1h)*.

---

## Flow 2: Unique document production & proof of delivery

*Every document is unique and may contain PHI: compose and print each piece, have QA cameras verify the name and
address on every piece (reprint on mismatch), then deliver and prove completion within the client's SLA.*

**Flow KPIs** (last 24 h vs the same time 7 days earlier)
| KPI | Why it matters |
|---|---|
| Documents completed | Plant throughput |
| Batches that missed SLA | Contract penalties and unhappy clients |
| p95 batch cycle time (min) | How long clients wait end to end |
| Possible PHI misprints caught | Each one is a HIPAA risk that QA stopped |
| Reprints | Rework cost from QA failures |
| Payment value delivered ($) | Money delivered to members on time |

**Stages, steps and touchpoints**
| Stage | Step | Touchpoints (signals) | Turns yellow / red when… |
|---|---|---|---|
| 1. Production queue | Production floor running | `ProductionBacklog` | Floor stalled |
| | Work within SLA | `ProductionBacklog` (per client & tier) | Yellow: any document at risk (≥ 50% of SLA used). Red: > 25 at risk, or any Enterprise document past SLA |
| 2. Compose & print unique pieces | Composition engine | APM *SecureDocs-API* errors | `CompositionEngineException` |
| | Production database (Azure SQL) | APM database telemetry | SQL errors; slow DB time |
| 3. QA camera verification | Level 1: Camera read, name & address match | `QaInspection` events (8 cameras) | More than 30 (yellow) / 75 (red) mismatches in 10 min; normal traffic has a handful |
| | Level 2: Reprint & re-verify | `QaInspection` (reprint = true) | More than 2 reprints fail again in 5 min |
| 4. Deliver & prove | Batches completed within SLA | `BatchCompleted` | Any batch finishes after its SLA |
| | Client sees status & proof (portal / API) | Synthetic *Client API intake*, APM *SecureDocs-Portal* | Status check fails; portal errors > 5% |

Stage KPIs: *Max documents at risk (30 min)*, *Misprints caught (1h)*, *Digital deliveries*.

---

## Demo script
| Fault Control switch | What turns colour in Pathpoint | Story |
|---|---|---|
| **Stall production floor** | Flow 2 *Production queue*: yellow at about 8 min, then red (Enterprise past SLA); Flow 1 *Stored & queued* red | "The plant stopped. Before any client calls, you can see which SLAs are at risk and how many dollars are stuck." |
| Stall off | Flow 2 *Deliver & prove* red (*Batches completed after SLA*), then recovers | "The backlog clears, but you know exactly which batches missed SLA and for which clients." |
| **QA camera mismatches** | Flow 2 *QA camera verification* red; *Possible PHI misprints* KPI climbs | "Possible PHI misprints are caught and reprinted, and you see it as a business risk, not just a log line." |
| **Slow address service** (5 s) | Flow 1 *Validate & verify* red; intake synthetic slows | "A third-party dependency slows intake. Pathpoint shows the business step, tracing shows the cause." |
| **Database errors** | Both flows: Azure SQL steps red | "One shared dependency, two business flows affected." |
| **API error rate 30%** | Flow 1 *Client sends member data* red; synthetic fails | "Clients can't submit. You know before they do." |

**Resetting between demos:** after a stall, use **Fault Control → Clear production backlog (demo reset)**. It finishes
all queued work without generating late-SLA events, so both flows return to green within a few minutes.

Incidents open within about 1–5 minutes of turning a fault on (depending on the condition's window), and close a few
minutes after you reset.

## Setting it up
**Automated (recommended).** Needs a New Relic **User** API key (`NRAK-…`):
```bash
export NEW_RELIC_USER_KEY=NRAK-...
python3 scripts/newrelic_setup.py --account <ACCOUNT_ID> --portal-url <portal URL> --api-url <api URL>
```
This creates or updates the 4 synthetic monitors, the alert policy with 19 conditions, and both flows. It also writes
the flow JSON files here (`pathpoint/*.json`) with your account's signal IDs. `--verify` shows recent check results and open incidents.

**Manual.** In New Relic open **Pathpoint → + Add a flow → Import a flow**, paste a file from this folder, then choose
**Everything**. The flow files are generated for your account and aren't in the repo: run
`scripts/render_newrelic_files.sh <ACCOUNT_ID>` (offline, no key needed). It writes the stages, steps, KPIs and APM
links but no alert signals; add those per step with **+ Add signals → Alerts**, or use the setup script above.

**What the setup creates:** flows *SecureDocs - Member data intake* and *SecureDocs - Unique
document production & proof of delivery*, policy *SecureDocs - Business flows* (19 conditions), and 4 monitors named
*SecureDocs - …*. Open them from **All Capabilities → Pathpoint**.

**Synthetic monitors (manual).** Paste the scripts from `../synthetics/` into new monitors: *Scripted browser* for
`portal-journey.browser.js`, *Scripted API* for the two `.api.js` files, plus a *Ping* monitor on `<api URL>/health`
that checks for the text `Healthy`. Change `API_URL` / `PORTAL_URL` at the top of each script.

**Cost.** Synthetic checks are billable beyond your plan's included amount. At `EVERY_15_MINUTES` from 1 location,
4 monitors run about 11,500 checks/month. Use `--period EVERY_HOUR` between demos and `EVERY_5_MINUTES` on demo day.
