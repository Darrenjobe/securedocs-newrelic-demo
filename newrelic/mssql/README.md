# Azure SQL monitoring with New Relic (supported options)

Database 360 doesn't officially support Azure SQL Database yet (SQL Server support covers self-hosted and AWS RDS).
These are the supported ways to monitor the SecureDocs Azure SQL database:

| Option | What you get | Status |
|---|---|---|
| **APM (.NET agent)** | Every SQL statement from the app's side: time, calls, slow-query traces, query plans, DB time per transaction, linked to traces | On |
| **SQL Server integration (`nri-mssql`)** | The database's own view: connections, batch requests, deadlocks, buffer pool, waits, per-database I/O and size, **top slow queries, wait analysis, execution plans, blocking sessions** | On (this folder) |
| **Azure Monitor integration** | Azure platform metrics: DTU / CPU %, storage, connection failures, App Service CPU / memory | Ready, needs the *cloud integrations* permission in New Relic (`scripts/azure_newrelic_link.py`) |

## How the SQL Server integration runs
Azure SQL Database has no host to install an agent on, so the New Relic **infrastructure agent + `nri-mssql`**
(image `newrelic/infrastructure-bundle`) runs in a small **Azure Container Instance** (`securedocs-sql-monitor`,
0.5 vCPU / 1 GB, about $20/month) in the same region. It connects over TLS with certificate verification, using a
**read-only** login.

1. Create the monitoring login: `create-monitoring-login.sql` (run as the SQL admin; master and SecureDocs).
   The login can read monitoring views (performance counters, query stats, waits) and cannot change data.
2. Put `SQL_MONITOR_PASSWORD` and `NEW_RELIC_LICENSE_KEY` in `~/.securedocs/deploy.env`.
3. `bash scripts/deploy_sql_monitor.sh` renders `mssql-config.yml.template` into an encrypted secret volume and
   (re)creates the container. It runs in *forward-only* mode, so it reports only the database, not a fake host.

Pause between demos:
```bash
az container stop  -g rg-securedocs-demo -n securedocs-sql-monitor
az container start -g rg-securedocs-demo -n securedocs-sql-monitor
```

## Where to look in New Relic
- **Infrastructure → Third-party services → Microsoft SQL Server** (or entity search for the server name): instance
  and database dashboards, plus the **Query performance monitoring** tabs (slow queries, wait analysis, plans, blocking).
- NRQL event types: `MssqlInstanceSample`, `MssqlDatabaseSample`, `MssqlWaitSample`, `MSSQLTopSlowQueries`,
  `MSSQLWaitTimeAnalysis`, `MSSQLQueryExecutionPlans`, `MSSQLBlockingSessionQueries`.

## Why Query details was empty (and the fix)
Query details shows a query's **execution plan**, and plans weren't being collected:
1. **One connection can't serve both jobs on Azure SQL.** Metrics open their own per-database connections (the
   integration adds `database=` itself), but query monitoring uses the main connection, which defaults to `master`.
   With `database=SecureDocs` on everything, the per-database connections failed ("key provided more than once").
   **Fix:** two integration instances in `mssql-config.yml.template`: one for metrics (default connection), one for
   query monitoring only (`INVENTORY: true` skips metrics) connected to `SecureDocs`.
2. **The plan query timed out** after 30 s (`context deadline exceeded`): shredding plan XML is expensive on a busy S0
   database. **Fix:** `TIMEOUT: 120` for the query-monitoring instance.
3. **No query was slow enough.** Plans are only collected for queries averaging more than
   `QUERY_MONITORING_RESPONSE_TIME_THRESHOLD` that ran in the last 2 × `QUERY_MONITORING_FETCH_INTERVAL` (here 2
   minutes). On a freshly deployed database the Slow SQL fault's table scans take only ~80–110 ms. Its `WAITFOR` delay
   runs as a separate statement and isn't counted against the query, so a 500 ms threshold caught nothing.
   **Fix:** threshold 50 ms. Normal app queries (~20 ms) still get no plans; the fault queries do.
4. These errors are invisible at the default log level. Run `LOG_LEVEL=debug bash scripts/deploy_sql_monitor.sh`
   to see the integration's own messages.

## Dashboards
`scripts/make_db_dashboards.py --account <ID> --validate --create` builds (and checks every query of) two dashboards:
- **SecureDocs - Azure SQL health:** connections, throughput, buffer/PLE, blocking, deadlocks, locks, compilations,
  top waits, I/O stall, log growth, plus the app's view (DB time share, calls per request, latency by table and operation).
- **SecureDocs - Slow query investigation:** *Find* (slowest queries from both the database and the app),
  *Why* (plan operators, scans, spills, plan changes, per-query waits, blocking), *Who is affected* (slow requests,
  clients and tiers, trace IDs of the slowest SQL calls).

## What we verified
- Instance, database and wait metrics, top slow queries (with full SQL text) and wait-time analysis arrive from Azure SQL.
- Connect to the **app database** (`EXTRA_CONNECTION_URL_ARGS: database=SecureDocs`). Connected to `master`, query
  monitoring only sees master's own activity, because Azure SQL Database isolates each database.
- Execution-plan and blocking-session events only appear when there are qualifying queries or blocking at the time.
- The S0 database (10 DTU) ran at about 90% CPU under Auto traffic before the integration was added. That's a real
  capacity finding, and the integration itself adds only a little load.

## Notes
- `CERTIFICATE_LOCATION` must point to a **.pem** CA bundle (`/etc/ssl/cert.pem` in the Alpine-based image). The
  driver rejects `.crt` files, and the integration refuses SSL without either a certificate or `TRUST_SERVER_CERTIFICATE`.
- Volume/disk metrics don't exist on Azure SQL Database (managed storage), so `ENABLE_DISK_METRICS_IN_BYTES` is off.
