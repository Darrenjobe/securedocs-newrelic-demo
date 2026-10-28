#!/usr/bin/env python3
"""
Creates (or updates) the SecureDocs service levels (SLIs/SLOs) in New Relic: the commitments a healthcare document
processor would report to clients (28-day windows) or track internally (7-day windows).

Usage:
  export NEW_RELIC_USER_KEY=NRAK-...
  python3 scripts/newrelic_service_levels.py --account <ACCOUNT_ID> --check    # current compliance per SLI, no changes
  python3 scripts/newrelic_service_levels.py --account <ACCOUNT_ID> --create   # create or update all SLIs
"""
import argparse
import json
import os
import sys
import urllib.request

GRAPHQL = "https://api.newrelic.com/graphql"

API = "appName = 'SecureDocs-API'"
PORTAL = "appName = 'SecureDocs-Portal'"
INTAKE = f"{API} AND (name LIKE '%/Batches/Submit/%' OR name LIKE '%/Ftp/Upload/%')"
# Lookups (single batch, lists) vs aggregates (dashboard KPIs, free-text search, volume report) get separate SLOs.
LOOKUP_APIS = (f"{API} AND (name LIKE '%/Clients/GetBatches/%' OR name LIKE '%/Batches/Get/%' OR "
               f"name LIKE '%/Clients/GetQa/%' OR name LIKE '%/Clients/GetTransfers/%' OR name LIKE '%/Clients/GetClients/%')")
REPORT_APIS = (f"{API} AND (name LIKE '%/Clients/GetDashboard/%' OR name LIKE '%/Clients/Search/%' OR "
               f"name LIKE '%/Clients/VolumeReport/%')")
PAGES = f"{PORTAL} AND name LIKE 'WebTransaction/Razor/Pages/%'"

# entity: which New Relic entity the SLI belongs to (resolved by name/type)
#   key, entity, name, description, from, valid-where, good-where | None, bad-where | None, target %, window days
SLIS = [
    # ---- Client portal (what clients see) ----
    ("portal_availability", ("APM", "SecureDocs-Portal"), "Client portal availability",
     "Share of portal page requests served without a server error. Reported to clients monthly.",
     "Transaction", PAGES, None, "error IS true", 99.9, 28),
    ("portal_latency", ("APM", "SecureDocs-Portal"), "Client portal pages under 2 s",
     "Share of portal page requests answered by the server in under 2 seconds.",
     "Transaction", PAGES, "duration < 2", None, 95.0, 28),
    ("portal_lcp", ("BROWSER", "SecureDocs-Portal"), "Portal page load (LCP) under 2.5 s",
     "Real-user page loads whose Largest Contentful Paint is under 2.5 s (Core Web Vitals 'good').",
     "PageViewTiming", f"{PORTAL} AND timingName = 'largestContentfulPaint'", "largestContentfulPaint < 2.5", None, 90.0, 7),
    # ---- Client API / data intake ----
    ("intake_availability", ("APM", "SecureDocs-API"), "Client data intake availability (API + file transfer)",
     "Share of batch submissions and file uploads accepted without a server error. Reported to clients monthly.",
     "Transaction", INTAKE, None, "error IS true", 99.9, 28),
    ("intake_latency", ("APM", "SecureDocs-API"), "Client data intake under 3 s",
     "Share of batch submissions and file uploads acknowledged in under 3 seconds.",
     "Transaction", INTAKE, "duration < 3", None, 99.0, 28),
    ("status_lookup_latency", ("APM", "SecureDocs-API"), "Batch status lookups under 500 ms",
     "Share of batch status, QA result and file-transfer lookups (portal and client systems) answered in under 500 ms.",
     "Transaction", LOOKUP_APIS, "duration < 0.5", None, 99.0, 7),
    ("report_latency", ("APM", "SecureDocs-API"), "Dashboard, search and reports under 3 s",
     "Share of dashboard KPI, document search and volume report requests answered in under 3 seconds.",
     "Transaction", REPORT_APIS, "duration < 3", None, 95.0, 7),
    # ---- Database ----
    ("db_query_latency", ("INFRA", "ms-instance:securedocs-sql"), "Database queries under 500 ms",
     "Share of SQL calls made by the application that complete in under 500 ms (measured by APM).",
     "Span", f"{API} AND db.system = 'mssql'", "duration.ms < 500", None, 99.0, 7),
    ("db_error_free", ("INFRA", "ms-instance:securedocs-sql"), "Database-backed requests without SQL errors",
     "Share of application requests that use the database and complete without a SQL error.",
     "Transaction", f"{API} AND databaseCallCount > 0", None, "errorType LIKE '%SqlException%'", 99.9, 28),
    # ---- Synthetic checks (outside-in) ----
    ("synth_portal", ("SYNTH", "SecureDocs - Portal journey (browser)"), "Portal journey check success",
     "Scripted sign-in → dashboard → submit → search journey succeeds.",
     "SyntheticCheck", "monitorName = 'SecureDocs - Portal journey (browser)'", None, "result = 'FAILED'", 99.5, 28),
    ("synth_api", ("SYNTH", "SecureDocs - Client API intake"), "Client API intake check success",
     "Scripted client API submission and read-back succeeds within its time budgets.",
     "SyntheticCheck", "monitorName = 'SecureDocs - Client API intake'", None, "result = 'FAILED'", 99.9, 28),
    ("synth_ftp", ("SYNTH", "SecureDocs - Secure file transfer"), "Secure file transfer check success",
     "Test file is received, picked up and turned into a batch within 45 s.",
     "SyntheticCheck", "monitorName = 'SecureDocs - Secure file transfer'", None, "result = 'FAILED'", 99.5, 28),
    ("synth_ping", ("SYNTH", "SecureDocs - API health"), "API uptime",
     "API health endpoint responds and reports Healthy.",
     "SyntheticCheck", "monitorName = 'SecureDocs - API health'", None, "result = 'FAILED'", 99.9, 28),
    # ---- Business KPIs (contractual commitments) ----
    ("kpi_batch_sla", ("APM", "SecureDocs-API"), "Batches completed within contractual SLA",
     "Share of client batches fully produced and delivered within the client's SLA (Enterprise / Standard). "
     "The headline number in a monthly client service review.",
     "BatchCompleted", "source != 'Synthetic'", "slaMet IS true", None, 99.0, 28),
    ("kpi_file_pickup", ("APM", "SecureDocs-API"), "Client files processed within 60 s of arrival",
     "Share of client file transfers picked up and processed successfully within 60 s of upload.",
     "FileTransferProcessed", "origin != 'Synthetic Upload'", "status = 'Processed' AND queueWaitMs < 60000", None, 99.0, 28),
    ("kpi_qa_yield", ("APM", "SecureDocs-API"), "Print accuracy: QA first-pass yield",
     "Share of printed pieces whose name and address pass the QA camera check first time (no reprint needed). "
     "A quality commitment for PHI-bearing documents.",
     "QaInspection", "reprint IS false", "passed IS true", None, 99.0, 28),
]


# Service levels renamed since an earlier version: the old one is updated in place (deleting needs extra permissions).
RENAMED = {"Dashboard, search and reports under 3 s": "Status and reporting API under 1 s"}


def gql(key, query, variables=None):
    req = urllib.request.Request(GRAPHQL, data=json.dumps({"query": query, "variables": variables or {}}).encode(),
                                 headers={"Content-Type": "application/json", "API-Key": key})
    body = json.loads(urllib.request.urlopen(req, timeout=90).read())
    if body.get("errors"):
        raise RuntimeError("; ".join(e.get("message", str(e)) for e in body["errors"]))
    return body["data"]


def entity_guid(key, account, domain, name):
    op = "LIKE" if name.endswith("securedocs-sql") else "="
    value = f"{name}%" if op == "LIKE" else name
    q = f"accountId = {account} AND domain = '{domain}' AND name {op} '{value}'"
    ents = gql(key, "query($q: String!) { actor { entitySearch(query: $q) { results { entities { guid name type } } } } }",
               {"q": q})["actor"]["entitySearch"]["results"]["entities"]
    ents = [e for e in ents if e["type"] in ("APPLICATION", "MONITOR", "MSSQLINSTANCE")]
    if not ents:
        raise RuntimeError(f"entity not found: {domain} {name}")
    return ents[0]["guid"]


def compliance(key, account, frm, valid, good, bad, since="1 hour ago"):
    if good:
        sel = f"filter(count(*), WHERE {good}) * 100 / count(*)"
    else:
        sel = f"(count(*) - filter(count(*), WHERE {bad})) * 100 / count(*)"
    q = f"SELECT {sel} AS pct, count(*) AS n FROM {frm} WHERE {valid} SINCE {since}"
    r = gql(key, "query($a: Int!, $q: Nrql!) { actor { account(id: $a) { nrql(query: $q) { results } } } }",
            {"a": account, "q": q})["actor"]["account"]["nrql"]["results"][0]
    return r.get("pct"), r.get("n")


def indicator(account, s):
    key, _, name, desc, frm, valid, good, bad, target, days = s
    events = {"accountId": account, "validEvents": {"from": frm, "where": valid}}
    if good:
        events["goodEvents"] = {"from": frm, "where": f"({valid}) AND ({good})"}
    else:
        events["badEvents"] = {"from": frm, "where": f"({valid}) AND ({bad})"}
    return {"name": name, "description": desc, "events": events,
            "objectives": [{"target": target, "name": f"{target}% over {days} days",
                            "timeWindow": {"rolling": {"count": days, "unit": "DAY"}}}]}


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--account", type=int, required=True)
    p.add_argument("--check", action="store_true")
    p.add_argument("--create", action="store_true")
    p.add_argument("--since", default="1 hour ago", help="window for --check, e.g. '30 minutes ago'")
    a = p.parse_args()
    key = os.environ.get("NEW_RELIC_USER_KEY") or sys.exit("Set NEW_RELIC_USER_KEY")

    for s in SLIS:
        skey, (domain, ename), name, _, frm, valid, good, bad, target, days = s
        pct, n = compliance(key, a.account, frm, valid, good, bad, a.since)
        pct_s = f"{pct:6.2f}%" if isinstance(pct, (int, float)) else "   n/a "
        flag = "" if not isinstance(pct, (int, float)) else ("  ✓" if pct >= target else "  ✗ below target")
        print(f"{pct_s} (n={int(n or 0):>6}, target {target}%/{days}d)  {name}{flag}")
        if not a.create:
            continue
        guid = entity_guid(key, a.account, domain, ename)
        existing = gql(key, "query($g: EntityGuid!) { actor { entity(guid: $g) { serviceLevel { indicators { id name } } } } }",
                       {"g": guid})["actor"]["entity"]["serviceLevel"]["indicators"] or []
        match = [i for i in existing if i["name"] == name] or [i for i in existing if i["name"] == RENAMED.get(name)]
        ind = indicator(a.account, s)
        if match:
            upd = {k: v for k, v in ind.items()}
            upd["events"] = {k: v for k, v in ind["events"].items() if k != "accountId"}
            gql(key, "mutation($id: ID!, $i: ServiceLevelIndicatorUpdateInput!) { serviceLevelUpdate(id: $id, indicator: $i) { id } }",
                {"id": match[0]["id"], "i": upd})
            print("          updated")
        else:
            gql(key, "mutation($g: EntityGuid!, $i: ServiceLevelIndicatorCreateInput!) { serviceLevelCreate(entityGuid: $g, indicator: $i) { id } }",
                {"g": guid, "i": ind})
            print("          created")


if __name__ == "__main__":
    main()
