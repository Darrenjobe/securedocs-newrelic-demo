#!/usr/bin/env python3
"""Generates the SecureDocs New Relic dashboard (importable JSON) for a given account ID.

Usage:  python3 scripts/make_dashboard.py <NEW_RELIC_ACCOUNT_ID> [output.json]
Import: New Relic -> Dashboards -> Import dashboard -> paste the file's contents.
"""
import json
import sys

API = "appName = 'SecureDocs-API'"
APPS = "appName IN ('SecureDocs-API', 'SecureDocs-Portal')"


def widget(title, query, viz, col, row, width=4, height=3):
    return {
        "title": title,
        "layout": {"column": col, "row": row, "width": width, "height": height},
        "visualization": {"id": viz},
        "rawConfiguration": {
            "facet": {"showOtherSeries": False},
            "legend": {"enabled": True},
            "nrqlQueries": [{"accountIds": [ACCOUNT], "query": query}],
            "platformOptions": {"ignoreTimeRange": False},
        },
    }


# Business KPIs exclude test traffic from New Relic synthetic monitors (source = 'Synthetic').
def markdown(text, col, row, width=12, height=2):
    return {
        "title": "",
        "layout": {"column": col, "row": row, "width": width, "height": height},
        "visualization": {"id": "viz.markdown"},
        "rawConfiguration": {"text": text},
    }


def latest_total(attr):
    # Sum of each client's most recent backlog value.
    return (f"SELECT sum(v) AS '{attr}' FROM (SELECT latest({attr}) AS v FROM ProductionBacklog "
            f"FACET clientCode LIMIT MAX) SINCE 5 minutes ago")


def pages():
    overview = [
        markdown("### SecureDocs business health\nIntake → Produce → Deliver & Prove, for the demo client portal. "
                 "Demo SLAs: **Enterprise 15 min, Standard 30 min** (at risk from 50% of SLA). "
                 "Use **Fault Control → Stall production floor** to put SLAs at risk, and **QA camera mismatches** "
                 "for possible PHI misprints.", 1, 1),
        widget("Records received (24h)", "SELECT sum(accepted) AS 'Records' FROM DocumentBatchIngested WHERE source != 'Synthetic' SINCE 1 day ago", "viz.billboard", 1, 3, 2),
        widget("SLA attainment (24h)", "SELECT percentage(count(*), WHERE slaMet IS true) AS 'SLA met' FROM BatchCompleted WHERE source != 'Synthetic' SINCE 1 day ago", "viz.billboard", 3, 3, 2),
        widget("QA first-pass yield (1h)", "SELECT percentage(count(*), WHERE passed IS true) AS 'First-pass yield' FROM QaInspection WHERE reprint IS false SINCE 1 hour ago", "viz.billboard", 5, 3, 2),
        widget("Documents at risk now", latest_total("atRiskCount"), "viz.billboard", 7, 3, 2),
        widget("Documents past SLA now", latest_total("overdueCount"), "viz.billboard", 9, 3, 2),
        widget("Payment value in flight now ($)", latest_total("valueInFlight"), "viz.billboard", 11, 3, 2),
        widget("Records received per client", "SELECT sum(accepted) FROM DocumentBatchIngested WHERE source != 'Synthetic' FACET clientCode TIMESERIES SINCE 6 hours ago", "viz.line", 1, 6, 6),
        widget("Production queue per client (documents)", "SELECT latest(queuedDocuments) FROM ProductionBacklog FACET clientCode TIMESERIES SINCE 6 hours ago", "viz.line", 7, 6, 6),
        widget("SLA attainment by client (24h)", "SELECT percentage(count(*), WHERE slaMet IS true) AS 'SLA met %' FROM BatchCompleted WHERE source != 'Synthetic' FACET clientCode SINCE 1 day ago", "viz.bar", 1, 9),
        widget("Oldest queued item (minutes) by client", "SELECT max(oldestQueuedMinutes) FROM ProductionBacklog FACET clientCode TIMESERIES SINCE 3 hours ago", "viz.line", 5, 9),
        widget("Possible PHI misprints (1h)", "SELECT count(*) AS 'Mismatches' FROM QaInspection WHERE passed IS false FACET mismatchType, cameraId SINCE 1 hour ago", "viz.table", 9, 9),
    ]
    intake = [
        widget("Records received vs last week", "SELECT sum(accepted) AS 'Records' FROM DocumentBatchIngested WHERE source != 'Synthetic' TIMESERIES SINCE 1 day ago COMPARE WITH 1 week ago", "viz.line", 1, 1, 6),
        widget("Intake channel mix", "SELECT sum(accepted) FROM DocumentBatchIngested WHERE source != 'Synthetic' FACET source SINCE 1 day ago", "viz.pie", 7, 1, 3),
        widget("Records by tier", "SELECT sum(accepted) FROM DocumentBatchIngested WHERE source != 'Synthetic' FACET clientTier SINCE 1 day ago", "viz.pie", 10, 1, 3),
        widget("Rejection rate by client (%)", "SELECT sum(rejected) / (sum(accepted) + sum(rejected)) * 100 AS 'Rejected %' FROM DocumentBatchIngested WHERE source != 'Synthetic' FACET clientCode SINCE 1 day ago", "viz.bar", 1, 4),
        widget("Undeliverable address rate by client (%)", "SELECT sum(undeliverable) / sum(addressesChecked) * 100 AS 'Undeliverable %' FROM DocumentBatchIngested WHERE source != 'Synthetic' AND addressesChecked > 0 FACET clientCode SINCE 1 day ago", "viz.bar", 5, 4),
        widget("File pickup delay (seconds)", "SELECT percentile(queueWaitMs, 95) / 1000 AS 'p95', average(queueWaitMs) / 1000 AS 'average' FROM FileTransferProcessed TIMESERIES SINCE 6 hours ago", "viz.line", 9, 4),
        widget("Intake API p95 response time by tier (s)", f"SELECT percentile(duration, 95) FROM Transaction WHERE {API} AND (name LIKE '%Batches%' OR name LIKE '%Ftp%') FACET client.tier TIMESERIES SINCE 3 hours ago", "viz.line", 1, 7, 6),
        widget("File transfers by status", "SELECT count(*) FROM FileTransferProcessed FACET status, clientCode SINCE 1 day ago", "viz.table", 7, 7, 6),
    ]
    production = [
        widget("Documents completed (print vs digital)", "SELECT sum(printCount) AS 'Print', sum(digitalCount) AS 'Email/SMS' FROM BatchCompleted WHERE source != 'Synthetic' TIMESERIES SINCE 6 hours ago", "viz.area", 1, 1, 6),
        widget("Payment value in flight by client ($)", "SELECT latest(valueInFlight) FROM ProductionBacklog FACET clientCode TIMESERIES SINCE 6 hours ago", "viz.line", 7, 1, 6),
        widget("QA first-pass yield by camera (1h)", "SELECT percentage(count(*), WHERE passed IS true) AS 'Yield %' FROM QaInspection WHERE reprint IS false FACET cameraId SINCE 1 hour ago", "viz.bar", 1, 4),
        widget("QA mismatches by type", "SELECT count(*) FROM QaInspection WHERE passed IS false FACET mismatchType TIMESERIES SINCE 3 hours ago", "viz.line", 5, 4),
        widget("Reprints", "SELECT filter(count(*), WHERE reprint IS true) AS 'Reprints', filter(count(*), WHERE reprint IS true AND passed IS false) AS 'Reprint failed again' FROM QaInspection TIMESERIES SINCE 3 hours ago", "viz.line", 9, 4),
        widget("Batch cycle time vs SLA (minutes)", "SELECT percentile(cycleTimeMinutes, 95) AS 'p95 cycle time', average(slaMinutes) AS 'SLA' FROM BatchCompleted WHERE source != 'Synthetic' FACET clientCode SINCE 1 day ago", "viz.bar", 1, 7, 6),
        widget("Documents at risk / past SLA", "SELECT sum(atRiskCount) AS 'At risk', sum(overdueCount) AS 'Past SLA' FROM ProductionBacklog TIMESERIES SINCE 3 hours ago", "viz.area", 7, 7, 6),
    ]
    impact = [
        widget("Clients affected right now (API, 30 min)", f"SELECT count(*) AS 'Requests', percentile(duration, 95) AS 'p95 (s)', percentage(count(*), WHERE error IS true) AS 'Error %' FROM Transaction WHERE {API} AND client.code IS NOT NULL FACET client.code, client.tier SINCE 30 minutes ago", "viz.table", 1, 1, 6),
        widget("Errors by type and client (1h)", "SELECT count(*) FROM TransactionError FACET error.class, client.code SINCE 1 hour ago", "viz.table", 7, 1, 6),
        widget("API p95 response time by client (s)", f"SELECT percentile(duration, 95) FROM Transaction WHERE {API} FACET client.code TIMESERIES SINCE 3 hours ago", "viz.line", 1, 4, 6),
        widget("Error rate by tier (%)", f"SELECT percentage(count(*), WHERE error IS true) FROM Transaction WHERE {APPS} FACET client.tier TIMESERIES SINCE 3 hours ago", "viz.line", 7, 4, 6),
        widget("Database time per request by client (s)", f"SELECT average(databaseDuration) FROM Transaction WHERE {API} FACET client.code TIMESERIES SINCE 3 hours ago", "viz.line", 1, 7, 6),
        widget("SLA breaches and PHI misprint errors", "SELECT count(*) FROM TransactionError WHERE error.class LIKE '%SlaBreach%' OR error.class LIKE '%QaMismatch%' FACET error.class, client.code TIMESERIES SINCE 3 hours ago", "viz.line", 7, 7, 6),
    ]
    return [
        {"name": "Business overview", "description": None, "widgets": overview},
        {"name": "Intake", "description": None, "widgets": intake},
        {"name": "Production & QA", "description": None, "widgets": production},
        {"name": "Performance × business impact", "description": None, "widgets": impact},
    ]


if __name__ == "__main__":
    if len(sys.argv) < 2 or not sys.argv[1].isdigit():
        sys.exit("Usage: python3 scripts/make_dashboard.py <NEW_RELIC_ACCOUNT_ID> [output.json]")
    ACCOUNT = int(sys.argv[1])
    import os
    default = "newrelic/dashboard-securedocs.json" if os.path.isdir("newrelic") else "dashboard-securedocs.json"
    out = sys.argv[2] if len(sys.argv) > 2 else default
    dashboard = {
        "name": "SecureDocs - Business KPIs",
        "description": "Business KPIs for the SecureDocs demo, captured as custom events and attributes alongside APM data.",
        "permissions": "PUBLIC_READ_WRITE",
        "pages": pages(),
        "variables": [],
    }
    with open(out, "w") as f:
        json.dump(dashboard, f, indent=2)
    print(f"Wrote {out} for account {ACCOUNT} ({sum(len(p['widgets']) for p in dashboard['pages'])} widgets)")
