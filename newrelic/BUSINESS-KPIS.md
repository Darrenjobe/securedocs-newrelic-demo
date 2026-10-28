# SecureDocs business KPIs in New Relic

SecureDocs sends business context alongside its APM data, so performance can be read in business terms:
*which client is affected, is an SLA at risk, how much money is stuck, could a document have been misprinted.*
All values are **PHI-free**: client codes, counts, document types, camera IDs and money **totals**, never names,
addresses, member IDs or per-person amounts.

## Import the dashboard
1. In New Relic go to **Dashboards → Import dashboard**.
2. Paste the contents of `dashboard-securedocs.json` and click **Import dashboard**.
3. Open **SecureDocs - Business KPIs**. It has 4 pages: Business overview, Intake, Production & QA, Performance × business impact.

The file is generated for a specific New Relic account. Generate it for yours first:
```bash
python3 scripts/make_dashboard.py <ACCOUNT_ID>        # writes newrelic/dashboard-securedocs.json
```
Your account ID is in the URL when you're logged in to New Relic (`.../accounts/<ID>/...`), or under your user menu → **Administration → Access management → Accounts**.

Charts fill in as data arrives. Turn on **Fault Control → Auto traffic** for a few minutes first.

## The KPIs

| Stage | KPI | Where it comes from |
|---|---|---|
| Intake | Records received per client / vs last week | `DocumentBatchIngested.accepted` |
| Intake | Rejection rate | `DocumentBatchIngested.rejected` |
| Intake | Channel mix (API / FTP / Portal) | `DocumentBatchIngested.source` |
| Intake | Undeliverable address rate | `DocumentBatchIngested.undeliverable`, `addressesChecked`; transaction attr `address.undeliverable` |
| Intake | File pickup delay | `FileTransferProcessed.queueWaitMs`; transaction attr `ftp.queueWaitMs` |
| Intake | Payment value received | `DocumentBatchIngested.amountTotal`; transaction attr `batch.amountTotal` |
| All | Client tier on every request | transaction attrs `client.code`, `client.tier` (also on errors) |
| Produce | Queue depth, oldest queued item | `ProductionBacklog.queuedDocuments`, `oldestQueuedMinutes` |
| Produce | Documents at risk / past SLA | `ProductionBacklog.atRiskCount`, `overdueCount` |
| Produce | Payment value in flight | `ProductionBacklog.valueInFlight` |
| Produce | QA first-pass yield by camera / document type | `QaInspection.passed`, `cameraId`, `documentType` (where `reprint` is false) |
| Produce | Possible PHI misprints by type | `QaInspection.mismatchType` = `name`, `postal`, `name_and_postal`; also `QaMismatchException` in Errors Inbox |
| Produce | Reprints | `QaInspection.reprint`; `BatchCompleted.reprints` |
| Deliver | Batch cycle time, SLA attainment | `BatchCompleted.cycleTimeMinutes`, `slaMinutes`, `slaMet`; breaches also raise `SlaBreachException` in Errors Inbox |
| Deliver | Print vs digital delivery | `BatchCompleted.printCount`, `digitalCount` |

**Tiers and SLAs (demo speed):** PCHP and SMG are *Enterprise* (SLA 15 min), EBA is *Standard* (SLA 30 min).
A queued document is *at risk* from 50% of its SLA. Change them with the API app settings `Sla__EnterpriseMinutes` and
`Sla__StandardMinutes`. In a real plant these would be hours or days.

**Floor capacity:** the production floor processes up to 300 documents every 10 s (about 1,800/min), comfortably above
Auto traffic's intake, so the baseline stays green. Change it with `Production__BatchSize` / `Production__IntervalSeconds`.

**A failed QA read** sends the piece back for one reprint. If the reprint also fails, the piece is pulled (`QaFailed`).

## Demo script for the business view
1. **Baseline:** Auto traffic on for a few minutes. SLA attainment is about 100%, first-pass yield about 99.5%.
2. **Stall production floor** (Fault Control → Business impact). Within ~8 minutes Enterprise documents show as
   *at risk*, then *past SLA*, and **payment value in flight** climbs on the overview page.
3. Turn the stall off. The backlog clears, the late batches complete with `slaMet = false`, SLA attainment drops,
   and `SlaBreachException` errors appear in Errors Inbox tagged with the client and tier.
   To reset quickly afterwards, use **Clear production backlog (demo reset)**. It finishes queued work quietly,
   without a burst of late-SLA events.
4. **QA camera mismatches** on. First-pass yield drops, reprints rise, and *Possible PHI misprints* breaks down
   by mismatch type and camera.
5. **Performance × business impact** page: combine with *Slow SQL* or *Slow address service* to show which clients
   and tiers feel the slowdown.

## Alert conditions worth creating (NRQL)
| Alert | NRQL | Threshold |
|---|---|---|
| Enterprise documents past SLA | `SELECT latest(overdueCount) FROM ProductionBacklog WHERE clientTier = 'Enterprise' FACET clientCode` | > 0 for 2 min |
| SLA at risk | `SELECT latest(atRiskCount) FROM ProductionBacklog FACET clientCode` | > 10 for 5 min |
| QA first-pass yield drop (possible PHI misprints) | `SELECT percentage(count(*), WHERE passed IS true) FROM QaInspection WHERE reprint IS false` | < 98 for 10 min |
| Missing client file | `SELECT sum(accepted) FROM DocumentBatchIngested FACET clientCode` | Anomaly (baseline), lower direction |
| Slow intake for Enterprise clients | `SELECT percentile(duration, 95) FROM Transaction WHERE appName = 'SecureDocs-API' AND client.tier = 'Enterprise' AND name LIKE '%Batches%'` | > 3 s for 5 min |
| Files waiting too long | `SELECT max(queueWaitMs) / 1000 FROM FileTransferProcessed` | > 60 s |

## Seeing the events locally
The New Relic agent doesn't run on macOS. To see the business events the app *would* send, start the API with
`SECUREDOCS_TELEMETRY_LOG=1`, and each event is printed to the console/log as `[telemetry] <EventType> {...}`.
