#!/usr/bin/env python3
"""
Builds two New Relic dashboards for the SecureDocs Azure SQL database:
  1. "SecureDocs - Azure SQL health"            general database health (SQL Server integration + APM)
  2. "SecureDocs - Slow query investigation"    find a slow query, see why it's slow, and who it affects
                                                 (SQL Server integration query monitoring + APM spans/transactions)

Usage:
  export NEW_RELIC_USER_KEY=NRAK-...
  python3 scripts/make_db_dashboards.py --account <ACCOUNT_ID> --validate   # run every widget query, report problems
  python3 scripts/make_db_dashboards.py --account <ACCOUNT_ID> --create      # create or update both dashboards
Without --create it only writes newrelic/dashboard-db-*.json (importable via Dashboards -> Import dashboard).
"""
import argparse
import json
import os
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GRAPHQL = "https://api.newrelic.com/graphql"

APP = "appName = 'SecureDocs-API'"
DB_SPANS = f"{APP} AND db.system = 'mssql'"
INST = "label.app = 'securedocs'"                      # SQL Server integration samples from our monitor
# The integration's own monitoring queries run in the same database; keep them out of the slow-query lists.
NOT_MONITORING = ("NOT LIKE '%sys.dm_%' AND {f} NOT LIKE '%query_store%' AND {f} NOT LIKE '%@resultTable%' "
                  "AND {f} NOT LIKE '%@QueryIdTable%' AND {f} NOT LIKE '%sys.databases%' AND {f} NOT LIKE '%sys.%'")


def app_queries(field):
    return f"{field} " + NOT_MONITORING.format(f=field)


def w(title, query, viz, col, row, width=4, height=3, extra=None):
    raw = {"facet": {"showOtherSeries": False}, "legend": {"enabled": True},
           "nrqlQueries": [{"accountIds": [ACCOUNT], "query": query}],
           "platformOptions": {"ignoreTimeRange": False}}
    if extra:
        raw.update(extra)
    return {"title": title, "layout": {"column": col, "row": row, "width": width, "height": height},
            "visualization": {"id": viz}, "rawConfiguration": raw}


def md(text, col, row, width=12, height=2):
    return {"title": "", "layout": {"column": col, "row": row, "width": width, "height": height},
            "visualization": {"id": "viz.markdown"}, "rawConfiguration": {"text": text}}


def health_dashboard():
    i = f"FROM MssqlInstanceSample WHERE {INST}"
    overview = [
        md("### Azure SQL health: SecureDocs database\n"
           "Database-side metrics from the **SQL Server integration** (`nri-mssql`) and the app's view from **APM**. "
           "Azure platform metrics (DTU / CPU %) appear once the Azure Monitor integration is linked.", 1, 1),
        w("Connections", f"SELECT latest(stats.connections) AS 'Connections' {i} SINCE 10 minutes ago", "viz.billboard", 1, 3, 2),
        w("Batch requests / sec", f"SELECT average(bufferpool.batchRequestsPerSecond) AS 'Batch req/s' {i} SINCE 10 minutes ago", "viz.billboard", 3, 3, 2),
        w("Buffer cache hit %", f"SELECT latest(system.bufferPoolHitPercent) AS 'Buffer hit %' {i} SINCE 10 minutes ago", "viz.billboard", 5, 3, 2,
          extra={"thresholds": [{"alertSeverity": "WARNING", "value": 95}]}),
        w("Page life expectancy (s)", f"SELECT latest(bufferpool.pageLifeExpectancyInMilliseconds) / 1000 AS 'PLE (s)' {i} SINCE 10 minutes ago", "viz.billboard", 7, 3, 2),
        w("Blocked processes", f"SELECT latest(instance.blockedProcessesCount) AS 'Blocked' {i} SINCE 10 minutes ago", "viz.billboard", 9, 3, 2),
        w("Deadlocks / sec", f"SELECT max(stats.deadlocksPerSecond) AS 'Deadlocks/s' {i} SINCE 30 minutes ago", "viz.billboard", 11, 3, 2),
        w("Workload: batch requests and transactions / sec",
          f"SELECT average(bufferpool.batchRequestsPerSecond) AS 'Batch requests/s', average(instance.transactionsPerSecond) AS 'Transactions/s' {i} TIMESERIES AUTO",
          "viz.line", 1, 6, 6),
        w("Connections (total vs active)",
          f"SELECT average(stats.connections) AS 'Total', average(activeConnections) AS 'Active' {i} TIMESERIES AUTO", "viz.line", 7, 6, 6),
        w("Pressure: runnable, suspended and blocked",
          f"SELECT average(instance.runnableTasks) AS 'Runnable tasks (CPU queue)', average(instance.suspendedProcessesCount) AS 'Suspended (waiting)', average(instance.blockedProcessesCount) AS 'Blocked' {i} TIMESERIES AUTO",
          "viz.line", 1, 9, 6),
        w("Locks, deadlocks and errors / sec",
          f"SELECT average(stats.lockWaitsPerSecond) AS 'Lock waits/s', average(stats.deadlocksPerSecond) AS 'Deadlocks/s', average(stats.userErrorsPerSecond) AS 'User errors/s', average(stats.killConnectionErrorsPerSecond) AS 'Kill-connection errors/s' {i} TIMESERIES AUTO",
          "viz.line", 7, 9, 6),
        w("Memory: page life expectancy (s) and buffer hit %",
          f"SELECT average(bufferpool.pageLifeExpectancyInMilliseconds) / 1000 AS 'PLE (s)', average(system.bufferPoolHitPercent) AS 'Buffer hit %' {i} TIMESERIES AUTO",
          "viz.line", 1, 12, 6),
        w("Compilations / sec (plan cache churn)",
          f"SELECT average(stats.sqlCompilationsPerSecond) AS 'Compilations/s', average(stats.sqlRecompilationsPerSecond) AS 'Recompilations/s', average(instance.forcedParameterizationsPerSecond) AS 'Forced parameterizations/s' {i} TIMESERIES AUTO",
          "viz.line", 7, 12, 6),
    ]
    waits_db = [
        w("Top wait types (ms waited per second)",
          f"SELECT average(system.waitTimeInMillisecondsPerSecond) AS 'ms/s' FROM MssqlWaitSample WHERE {INST} FACET waitType SINCE 30 minutes ago LIMIT 10",
          "viz.bar", 1, 1, 6, 4),
        w("Wait time over time (top wait types)",
          f"SELECT average(system.waitTimeInMillisecondsPerSecond) FROM MssqlWaitSample WHERE {INST} FACET waitType TIMESERIES AUTO LIMIT 6",
          "viz.line", 7, 1, 6, 4),
        w("Total wait time (ms / sec)",
          f"SELECT average(system.waitTimeInMillisecondsPerSecond) AS 'Wait ms/s' {i} TIMESERIES AUTO", "viz.area", 1, 5, 4),
        w("Database I/O stall (ms per minute)",
          f"SELECT derivative(io.stallInMilliseconds, 1 minute) AS 'I/O stall ms/min' FROM MssqlDatabaseSample WHERE {INST} FACET entityName TIMESERIES AUTO",
          "viz.line", 5, 5, 4),
        w("Transaction log growth",
          f"SELECT max(log.transactionGrowth) AS 'Log growths' FROM MssqlDatabaseSample WHERE {INST} FACET entityName TIMESERIES AUTO", "viz.line", 9, 5, 4),
        w("Buffer pool used per database (MB)",
          f"SELECT latest(bufferpool.sizePerDatabaseInBytes) / 1048576 AS 'MB' FROM MssqlDatabaseSample WHERE {INST} FACET entityName TIMESERIES AUTO",
          "viz.line", 1, 8, 6),
        w("Database memory utilization %",
          f"SELECT average(memoryUtilization) AS 'Memory %' FROM MssqlDatabaseSample WHERE {INST} FACET entityName TIMESERIES AUTO", "viz.line", 7, 8, 6),
    ]
    app_view = [
        w("Share of request time spent in the database (%)",
          f"SELECT sum(databaseDuration) * 100 / sum(duration) AS 'DB time %' FROM Transaction WHERE {APP} AND transactionType = 'Web' TIMESERIES AUTO",
          "viz.line", 1, 1, 6),
        w("Database calls per request",
          f"SELECT average(databaseCallCount) AS 'DB calls / request', average(databaseDuration) * 1000 AS 'DB ms / request' FROM Transaction WHERE {APP} AND transactionType = 'Web' TIMESERIES AUTO",
          "viz.line", 7, 1, 6),
        w("Time by table and operation (APM)",
          f"SELECT sum(duration.ms) / 1000 AS 'Total s' FROM Span WHERE {DB_SPANS} FACET db.collection, db.operation SINCE 30 minutes ago LIMIT 12",
          "viz.bar", 1, 4, 6, 4),
        w("Average latency by operation (ms)",
          f"SELECT average(duration.ms) FROM Span WHERE {DB_SPANS} FACET db.collection, db.operation TIMESERIES AUTO LIMIT 6",
          "viz.line", 7, 4, 6, 4),
        w("SQL errors seen by the app",
          f"SELECT count(*) FROM TransactionError WHERE {APP} AND (error.class LIKE '%Sql%' OR error.message LIKE '%SQL%') FACET error.class, error.message SINCE 3 hours ago",
          "viz.table", 1, 8, 12),
    ]
    return {
        "name": "SecureDocs - Azure SQL health",
        "description": "General health of the SecureDocs Azure SQL database: SQL Server integration (nri-mssql) + APM.",
        "permissions": "PUBLIC_READ_WRITE",
        "pages": [
            {"name": "Health overview", "description": None, "widgets": overview},
            {"name": "Waits & database", "description": None, "widgets": waits_db},
            {"name": "App's view (APM)", "description": None, "widgets": app_view},
        ],
        "variables": [],
    }


def slow_query_dashboard():
    sq = f"FROM MSSQLTopSlowQueries WHERE DatabaseName != 'master' AND {app_queries('QueryText')}"
    plans = f"FROM MSSQLQueryExecutionPlans WHERE DatabaseName != 'master' AND {app_queries('SQLText')}"
    waits = f"FROM MSSQLWaitTimeAnalysis WHERE database_name != 'master' AND {app_queries('query_text')}"
    find = [
        md("### Slow query investigation: SecureDocs Azure SQL\n"
           "**1. Find** the slowest queries (database view + app view) → **2. Diagnose** them on the *Why is it slow* page "
           "(plan operators, scans, memory grants, spills, waits, blocking) → **3. Impact** on the *Who is affected* page "
           "(requests, clients, traces). Copy a **QueryID** from this page to filter the other pages.", 1, 1),
        w("Slowest queries (database view: Query Store / DMVs)",
          f"SELECT max(AvgElapsedTimeMS) AS 'Avg ms', max(MaxElapsedTimeMS) AS 'Max ms', max(AvgCPUTimeMS) AS 'Avg CPU ms', "
          f"max(AvgDiskReads) AS 'Avg disk reads', max(AvgDiskWrites) AS 'Avg disk writes', max(ExecutionCount) AS 'Executions', "
          f"latest(StatementType) AS 'Type', latest(LastExecutionTimestamp) AS 'Last run' {sq} FACET QueryID, QueryText SINCE 1 hour ago LIMIT 25",
          "viz.table", 1, 3, 12, 5),
        w("Slowest SQL statements (app view: APM, as executed by the code)",
          f"SELECT average(duration.ms) AS 'Avg ms', percentile(duration.ms, 95) AS 'p95 ms', max(duration.ms) AS 'Max ms', "
          f"count(*) AS 'Calls', sum(duration.ms) / 1000 AS 'Total s' FROM Span WHERE {DB_SPANS} FACET db.statement SINCE 1 hour ago LIMIT 25",
          "viz.table", 1, 8, 12, 5),
        w("Slow queries over time (avg ms)",
          f"SELECT max(AvgElapsedTimeMS) {sq} FACET substring(QueryText, 0, 70) TIMESERIES AUTO LIMIT 6",
          "viz.line", 1, 13, 6, 4),
        w("Where the app spends DB time (APM)",
          f"SELECT sum(duration.ms) / 1000 AS 'Total s' FROM Span WHERE {DB_SPANS} FACET db.collection, db.operation SINCE 1 hour ago LIMIT 10",
          "viz.bar", 7, 13, 6, 4),
    ]
    why = [
        md("**Reading plans:** look for **scans** with high *Estimated rows* (a missing or unusable index, e.g. non-sargable "
           "`UPPER(col) LIKE '%x%'`), **spills** (memory grant too small), **no join predicate** (accidental cross join), "
           "and more than one **plan ID** per query (plan regression). Waits show *what* the query waited on (CPU, I/O, locks).", 1, 1),
        w("Execution plan operators (per slow query)",
          f"SELECT max(TotalSubtreeCost) AS 'Plan cost', max(EstimatedOperatorCost) AS 'Operator cost', max(EstimateRows) AS 'Est. rows', "
          f"max(EstimateIO) AS 'Est. IO', max(EstimateCPU) AS 'Est. CPU', max(GrantedMemoryKb) AS 'Memory grant KB', "
          f"max(SpillOccurred) AS 'Spill', max(NoJoinPredicate) AS 'No join predicate', latest(SQLText) AS 'SQL' "
          f"{plans} FACET QueryID, NodeID, PhysicalOp, LogicalOp SINCE 1 hour ago LIMIT 50",
          "viz.table", 1, 3, 12, 5),
        w("Scans on large inputs (likely missing / unusable index)",
          f"SELECT max(EstimateRows) AS 'Est. rows', max(EstimatedOperatorCost) AS 'Operator cost', latest(SQLText) AS 'SQL' "
          f"{plans} AND PhysicalOp IN ('Clustered Index Scan', 'Table Scan', 'Index Scan') FACET QueryID, PhysicalOp SINCE 1 hour ago LIMIT 20",
          "viz.table", 1, 8, 6, 4),
        w("Plan warnings: spills and missing join predicates",
          f"SELECT count(*) AS 'Operators', max(GrantedMemoryKb) AS 'Memory grant KB', latest(SQLText) AS 'SQL' "
          f"{plans} AND (SpillOccurred = 1 OR NoJoinPredicate = 1) FACET QueryID, PhysicalOp SINCE 1 hour ago LIMIT 20",
          "viz.table", 7, 8, 6, 4),
        w("Most expensive operator types",
          f"SELECT sum(EstimatedOperatorCost) AS 'Operator cost' {plans} FACET PhysicalOp SINCE 1 hour ago LIMIT 10",
          "viz.bar", 1, 12, 4, 4),
        w("Plan stability: plans per query (more than 1 = plan change)",
          f"SELECT uniqueCount(QueryPlanID) AS 'Plans', max(AvgElapsedTimeMs) AS 'Avg ms', max(ExecutionCount) AS 'Executions', "
          f"max(TotalLogicalReads) AS 'Logical reads', max(TotalWorkerTime) AS 'Worker time' {plans} FACET QueryID SINCE 1 day ago LIMIT 20",
          "viz.table", 5, 12, 8, 4),
        w("What slow queries wait on (wait analysis)",
          f"SELECT max(total_wait_time_ms) AS 'Total wait ms', max(avg_wait_time_ms) AS 'Avg wait ms', max(wait_event_count) AS 'Wait events', "
          f"latest(query_text) AS 'SQL' {waits} FACET query_id, wait_category SINCE 1 hour ago LIMIT 25",
          "viz.table", 1, 16, 8, 4),
        w("Wait categories",
          f"SELECT sum(total_wait_time_ms) {waits} FACET wait_category SINCE 1 hour ago LIMIT 10", "viz.pie", 9, 16, 4, 4),
        w("Blocking sessions (who blocks whom)",
          "SELECT max(wait_time_in_seconds) AS 'Wait s', latest(wait_type) AS 'Wait type', latest(blocking_query_text) AS 'Blocking SQL', "
          "latest(blocked_query_text) AS 'Blocked SQL', latest(command_type) AS 'Command' FROM MSSQLBlockingSessionQueries "
          "FACET blocking_spid, blocked_spid, database_name SINCE 1 hour ago LIMIT 20",
          "viz.table", 1, 20, 12, 4),
    ]
    impact = [
        w("Requests slowed by the database (DB time over 1 s)",
          f"SELECT count(*) AS 'Requests', average(databaseDuration) AS 'Avg DB s', average(duration) AS 'Avg total s', "
          f"average(databaseCallCount) AS 'DB calls' FROM Transaction WHERE {APP} AND databaseDuration > 1 FACET name SINCE 1 hour ago LIMIT 15",
          "viz.table", 1, 1, 12, 4),
        w("Clients affected by slow database time",
          f"SELECT count(*) AS 'Slow requests', percentile(databaseDuration, 95) AS 'p95 DB s' FROM Transaction "
          f"WHERE {APP} AND databaseDuration > 1 AND client.code IS NOT NULL FACET client.code, client.tier SINCE 1 hour ago",
          "viz.table", 1, 5, 6, 4),
        w("p95 database time per request by client tier (s)",
          f"SELECT percentile(databaseDuration, 95) FROM Transaction WHERE {APP} AND transactionType = 'Web' FACET client.tier TIMESERIES AUTO",
          "viz.line", 7, 5, 6, 4),
        w("Slowest individual SQL calls, with trace IDs (open in Distributed tracing)",
          f"SELECT max(duration.ms) AS 'ms', latest(db.collection) AS 'Table', latest(db.operation) AS 'Op' FROM Span "
          f"WHERE {DB_SPANS} AND duration.ms > 1000 FACET trace.id, db.statement SINCE 1 hour ago LIMIT 20",
          "viz.table", 1, 9, 12, 5),
    ]
    return {
        "name": "SecureDocs - Slow query investigation",
        "description": "Find slow SQL, see why it's slow (plans, scans, waits, blocking) and who it affects. "
                       "SQL Server integration query monitoring + APM.",
        "permissions": "PUBLIC_READ_WRITE",
        "pages": [
            {"name": "1. Find slow queries", "description": None, "widgets": find},
            {"name": "2. Why is it slow (DBA)", "description": None, "widgets": why},
            {"name": "3. Who is affected (app)", "description": None, "widgets": impact},
        ],
        "variables": [],
    }


def gql(key, query, variables=None):
    req = urllib.request.Request(GRAPHQL, data=json.dumps({"query": query, "variables": variables or {}}).encode(),
                                 headers={"Content-Type": "application/json", "API-Key": key})
    body = json.loads(urllib.request.urlopen(req, timeout=90).read())
    if body.get("errors"):
        raise RuntimeError("; ".join(e.get("message", str(e)) for e in body["errors"]))
    return body["data"]


def validate(key, dashboards):
    bad = 0
    for d in dashboards:
        print(f"== {d['name']}")
        for p in d["pages"]:
            for wd in p["widgets"]:
                q = (wd["rawConfiguration"].get("nrqlQueries") or [{}])[0].get("query")
                if not q:
                    continue
                if "SINCE" not in q:
                    q += " SINCE 1 hour ago"
                try:
                    res = gql(key, "query($a: Int!, $q: Nrql!) { actor { account(id: $a) { nrql(query: $q) { results } } } }",
                              {"a": ACCOUNT, "q": q})["actor"]["account"]["nrql"]["results"]
                    vals = [v for r in res for k, v in r.items()
                            if k not in ("beginTimeSeconds", "endTimeSeconds", "facet") and v not in (None, 0, [], {})]
                    status = "ok" if vals else "NO DATA"
                except RuntimeError as e:
                    status, bad = f"ERROR: {e}"[:160], bad + 1
                print(f"  [{status:7}] {p['name']} / {wd['title']}")
    return bad


def upsert(key, d):
    found = gql(key, "query($q: String!) { actor { entitySearch(query: $q) { results { entities { guid name } } } } }",
                {"q": f"accountId = {ACCOUNT} AND type = 'DASHBOARD' AND name = '{d['name']}'"})
    ents = [e for e in found["actor"]["entitySearch"]["results"]["entities"] if e["name"] == d["name"]]
    if ents:
        r = gql(key, "mutation($g: EntityGuid!, $d: DashboardInput!) { dashboardUpdate(guid: $g, dashboard: $d) { errors { description } entityResult { guid } } }",
                {"g": ents[0]["guid"], "d": d})["dashboardUpdate"]
        verb = "updated"
    else:
        r = gql(key, "mutation($a: Int!, $d: DashboardInput!) { dashboardCreate(accountId: $a, dashboard: $d) { errors { description } entityResult { guid } } }",
                {"a": ACCOUNT, "d": d})["dashboardCreate"]
        verb = "created"
    if r.get("errors"):
        raise RuntimeError(r["errors"])
    guid = r["entityResult"]["guid"]
    print(f"{verb}: {d['name']}  https://one.newrelic.com/redirect/entity/{guid}")


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--account", type=int, required=True)
    p.add_argument("--validate", action="store_true")
    p.add_argument("--create", action="store_true")
    a = p.parse_args()
    ACCOUNT = a.account
    dashes = [health_dashboard(), slow_query_dashboard()]
    for d, name in zip(dashes, ["dashboard-db-health.json", "dashboard-db-slow-queries.json"]):
        (ROOT / "newrelic" / name).write_text(json.dumps(d, indent=2))
        print(f"wrote newrelic/{name} ({sum(len(pg['widgets']) for pg in d['pages'])} widgets)")
    if a.validate or a.create:
        key = os.environ.get("NEW_RELIC_USER_KEY") or sys.exit("Set NEW_RELIC_USER_KEY")
        if a.validate and validate(key, dashes):
            sys.exit("Fix the errors above before creating.")
        if a.create:
            for d in dashes:
                upsert(key, d)
