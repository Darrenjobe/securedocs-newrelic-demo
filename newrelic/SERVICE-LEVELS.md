# SecureDocs service levels

16 service levels (SLIs with SLOs) a healthcare document processor would track. The **28-day** ones are the kind you
report to clients in a monthly service review; the **7-day** ones are internal engineering targets. Find them in
New Relic under **Service levels**, or on each entity's *Service levels* tab.

| Entity | Service level | Target | Why it matters |
|---|---|---|---|
| Portal (APM) | Client portal availability | 99.9% / 28d | Clients can sign in, check status and submit work |
| Portal (APM) | Client portal pages under 2 s | 95% / 28d | Portal feels responsive to client operations staff |
| Portal (Browser) | Portal page load (LCP) under 2.5 s | 90% / 7d | Real-user experience (Core Web Vitals "good") |
| API | Client data intake availability (API + file transfer) | 99.9% / 28d | Clients can always hand over their member data |
| API | Client data intake under 3 s | 99% / 28d | Client systems and scripts don't time out |
| API | Batch status lookups under 500 ms | 99% / 7d | Client systems polling batch status stay fast |
| API | Dashboard, search and reports under 3 s | 95% / 7d | Aggregates and free-text search get a realistic, looser target |
| Azure SQL | Database queries under 500 ms | 99% / 7d | The database isn't the bottleneck (measured from the app) |
| Azure SQL | Database-backed requests without SQL errors | 99.9% / 28d | Data reads and writes succeed |
| Synthetic | Portal journey check success | 99.5% / 28d | End-to-end client journey works from outside |
| Synthetic | Client API intake check success | 99.9% / 28d | Client API path works from outside |
| Synthetic | Secure file transfer check success | 99.5% / 28d | File drop → processed batch works |
| Synthetic | API uptime | 99.9% / 28d | Classic uptime number |
| API (business) | **Batches completed within contractual SLA** | 99% / 28d | The headline client commitment: on-time production |
| API (business) | **Client files processed within 60 s of arrival** | 99% / 28d | Files don't sit waiting before mail dates |
| API (business) | **Print accuracy: QA first-pass yield** | 99% / 28d | Quality of PHI-bearing documents (misprints caught by QA) |

Synthetic test traffic is excluded from the business service levels (`source != 'Synthetic'`).

## Demo
- **Stall production floor** → *Batches completed within contractual SLA* burns error budget once late batches complete.
- **QA camera mismatches** → *Print accuracy* drops below 99% within minutes (normal misread rate is about 0.5%).
- **Slow SQL** → *Database queries under 500 ms* burns budget (and at higher delays *Dashboard, search and reports under 3 s*), and the slow
  query dashboard shows the cause.
- **Database errors** / **API error rate** → availability service levels burn budget, and the synthetic checks fail.
- An undersized database (S0) visibly breaks the latency service levels. Upgrading to S2 restores them.

## Re-create or adjust
```bash
python3 scripts/newrelic_service_levels.py --account <ID> --check    # current compliance (last hour), no changes
python3 scripts/newrelic_service_levels.py --account <ID> --create   # create or update all 16
```
Targets and definitions are in the `SLIS` list at the top of the script.

## Results after upgrading Azure SQL from S0 to S2
| | S0 (10 DTU) | S2 (50 DTU) |
|---|---|---|
| Database CPU | 90–100% | about 31–37% |
| API p95 response time | 9.25 s | 1.84 s |
| SQL call average / p95 | 436 ms / 2,240 ms | 65 ms / 228 ms |
| Requests served (same Auto traffic) | 1x | about 2.6x |

On S0 the latency service levels broke (portal pages under 2 s: 72%; SQL under 500 ms: 87%). On S2 they meet target.
The remaining SQL-latency miss in our run came from the *Slow SQL* fault, which was left on.
