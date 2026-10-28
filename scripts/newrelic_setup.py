#!/usr/bin/env python3
"""
Creates the SecureDocs New Relic setup through NerdGraph:
  1. Synthetic monitors  (portal journey - browser, client API intake, secure file transfer, API health ping)
  2. Alert policy "SecureDocs - Business flows" with NRQL conditions (these drive Pathpoint health colours)
  3. Two Pathpoint flows: "Member data intake" and "Unique document production & proof of delivery"
It also writes the flow definitions to pathpoint/*.json (for Pathpoint -> Add a flow -> Import a flow).

Safe to re-run: monitors and conditions are updated in place; flows already created are skipped unless --recreate-flows.

Usage:
  export NEW_RELIC_USER_KEY=NRAK-...          # a USER key (not the license key)
  python3 scripts/newrelic_setup.py --account <ACCOUNT_ID> \
      --portal-url https://securedocs-portal-xxx.azurewebsites.net --api-url https://securedocs-api-xxx.azurewebsites.net
Options:
  --period EVERY_15_MINUTES   synthetic check frequency (EVERY_5_MINUTES for demo day; each check is billable)
  --location AWS_US_EAST_2    public location for the checks
  --region US|EU              New Relic data center
  --only monitors|alerts|flows   run just one part
  --export-only               no API calls: write pathpoint/*.json using dynamic entity queries (no alert signals)
  --verify                    show recent synthetic results, open incidents and flows
"""
import argparse
import json
import os
import re
import sys
import time
import urllib.request
from pathlib import Path

_HERE = Path(__file__).resolve().parent
ROOT = _HERE if (_HERE / "synthetics").is_dir() else _HERE.parent   # repo (scripts/) or bundle (azure/) layout
STATE_FILE = Path.home() / ".securedocs" / "newrelic-state.json"
ENDPOINTS = {"US": "https://api.newrelic.com/graphql", "EU": "https://api.eu.newrelic.com/graphql"}
POLICY_NAME = "SecureDocs - Business flows"
RENAMED_CONDITIONS = {"SecureDocs | QA mismatch spike (over 75 in 10 min)": "SecureDocs | QA first-pass yield below 98%"}
PERIOD_SECONDS = {"EVERY_MINUTE": 60, "EVERY_5_MINUTES": 300, "EVERY_10_MINUTES": 600, "EVERY_15_MINUTES": 900,
                  "EVERY_30_MINUTES": 1800, "EVERY_HOUR": 3600}


class E(str):
    """A GraphQL enum value (rendered without quotes)."""


def lit(v):
    if v is None:
        return "null"
    if isinstance(v, E):
        return str(v)
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, (int, float, str)):
        return json.dumps(v)
    if isinstance(v, list):
        return "[" + ", ".join(lit(x) for x in v) + "]"
    if isinstance(v, dict):
        return "{" + ", ".join(f"{k}: {lit(x)}" for k, x in v.items() if x is not None) + "}"
    raise TypeError(v)


def plain(v):
    """Enum-free copy for JSON export."""
    if isinstance(v, dict):
        return {k: plain(x) for k, x in v.items() if x is not None}
    if isinstance(v, list):
        return [plain(x) for x in v]
    return str(v) if isinstance(v, E) else v


class NerdGraph:
    def __init__(self, key, region):
        self.key, self.url = key, ENDPOINTS[region]

    def __call__(self, query):
        req = urllib.request.Request(self.url, data=json.dumps({"query": query}).encode(),
                                     headers={"Content-Type": "application/json", "API-Key": self.key})
        with urllib.request.urlopen(req, timeout=60) as r:
            body = json.loads(r.read())
        if body.get("errors"):
            raise RuntimeError("; ".join(e.get("message", str(e)) for e in body["errors"]))
        return body["data"]


# --------------------------------------------------------------------------------------------------------------------
# Definitions
# --------------------------------------------------------------------------------------------------------------------
MONITORS = [
    {"key": "portal", "name": "SecureDocs - Portal journey (browser)", "kind": "browser", "file": "portal-journey.browser.js"},
    {"key": "api", "name": "SecureDocs - Client API intake", "kind": "api", "file": "client-api-intake.api.js"},
    {"key": "ftp", "name": "SecureDocs - Secure file transfer", "kind": "api", "file": "file-transfer.api.js"},
    {"key": "ping", "name": "SecureDocs - API health", "kind": "simple"},
]

API = "appName = 'SecureDocs-API'"
INTAKE = f"{API} AND (name LIKE '%Batches%' OR name LIKE '%Ftp%')"


def synth_fail(monitor_name):
    return f"SELECT filter(count(*), WHERE result = 'FAILED') FROM SyntheticCheck WHERE monitorName = '{monitor_name}'"


def conditions(period_s):
    """key, name, nrql, operator, critical, warning, window(s), duration(s), occurrences, fill-with-zero."""
    s = period_s
    return [
        # ---- Intake flow ----
        ("synth_portal", "Portal journey check failing", synth_fail(MONITORS[0]["name"]), "ABOVE", 0, None, s, s, "AT_LEAST_ONCE", False),
        ("synth_api", "Client API intake check failing", synth_fail(MONITORS[1]["name"]), "ABOVE", 0, None, s, s, "AT_LEAST_ONCE", False),
        ("synth_ftp", "Secure file transfer check failing", synth_fail(MONITORS[2]["name"]), "ABOVE", 0, None, s, s, "AT_LEAST_ONCE", False),
        ("synth_ping", "API health check failing", synth_fail(MONITORS[3]["name"]), "ABOVE", 0, None, s, s, "AT_LEAST_ONCE", False),
        ("intake_slow", "Intake API slow (p95 over 3 s)", f"SELECT percentile(duration, 95) FROM Transaction WHERE {INTAKE}", "ABOVE", 3, 1.5, 60, 180, "ALL", False),
        ("intake_errors", "Intake API errors (over 5%)", f"SELECT percentage(count(*), WHERE error IS true) FROM Transaction WHERE {INTAKE}", "ABOVE", 5, 1, 60, 120, "ALL", False),
        ("portal_errors", "Portal errors (over 5%)", "SELECT percentage(count(*), WHERE error IS true) FROM Transaction WHERE appName = 'SecureDocs-Portal'", "ABOVE", 5, 1, 60, 120, "ALL", False),
        ("address_slow", "Address verification slow (over 2 s)", f"SELECT average(duration) FROM Transaction WHERE {API} AND name LIKE '%Address%'", "ABOVE", 2, 1, 60, 120, "ALL", False),
        ("db_errors", "Database errors", f"SELECT count(*) FROM TransactionError WHERE {API} AND error.class LIKE '%SqlException%'", "ABOVE", 0, None, 60, 60, "AT_LEAST_ONCE", True),
        # Client-facing requests only: background production runs do bulk writes by design.
        ("db_slow", "Slow database time (p95 over 2 s)", f"SELECT percentile(databaseDuration, 95) FROM Transaction WHERE {API} AND transactionType = 'Web' AND name NOT LIKE '%Faults%'", "ABOVE", 2, 1, 60, 120, "ALL", False),
        ("files_waiting", "Files waiting too long (over 60 s)", "SELECT max(queueWaitMs) FROM FileTransferProcessed", "ABOVE", 60000, 20000, 60, 60, "AT_LEAST_ONCE", False),
        ("rejections", "Record rejection rate (over 10%)", "SELECT sum(rejected) * 100 / (sum(accepted) + sum(rejected)) FROM DocumentBatchIngested", "ABOVE", 10, 5, 300, 300, "AT_LEAST_ONCE", False),
        # ---- Production & delivery flow ----
        ("floor_stalled", "Production floor stalled", "SELECT filter(count(*), WHERE stalled IS true) FROM ProductionBacklog", "ABOVE", 0, None, 60, 60, "AT_LEAST_ONCE", True),
        ("at_risk", "Documents at risk of missing SLA", "SELECT max(atRiskCount) FROM ProductionBacklog", "ABOVE", 25, 0, 60, 60, "AT_LEAST_ONCE", True),
        ("past_sla", "Enterprise documents past SLA", "SELECT max(overdueCount) FROM ProductionBacklog WHERE clientTier = 'Enterprise'", "ABOVE", 0, None, 60, 60, "AT_LEAST_ONCE", True),
        ("composition", "Composition engine errors", f"SELECT count(*) FROM TransactionError WHERE {API} AND error.class LIKE '%CompositionEngine%'", "ABOVE", 0, None, 60, 60, "AT_LEAST_ONCE", True),
        # Count-based: normal traffic misreads ~0.5% (a handful per 10 min); the QA fault misreads ~35% (hundreds).
        ("qa_yield", "QA mismatch spike (over 75 in 10 min)", "SELECT filter(count(*), WHERE passed IS false) FROM QaInspection WHERE reprint IS false", "ABOVE", 75, 30, 600, 600, "AT_LEAST_ONCE", True),
        ("reprint_fail", "Reprints failing QA again", "SELECT filter(count(*), WHERE passed IS false) FROM QaInspection WHERE reprint IS true", "ABOVE", 2, 0, 300, 300, "AT_LEAST_ONCE", True),
        ("sla_missed", "Batches completed after SLA", "SELECT filter(count(*), WHERE slaMet IS false) FROM BatchCompleted", "ABOVE", 0, None, 300, 300, "AT_LEAST_ONCE", True),
    ]


def kpi(account, name, frm, agg, attribute=None, where=None, alias=None, category=None, description=None,
        since="TWENTY_FOUR_HOURS", compare="SEVEN_DAYS", threshold=None):
    select = {"aggregationType": E(agg), "attribute": attribute, "alias": alias or name, "threshold": threshold}
    tw = {"relativeRange": {"since": E(since), "compareAgainst": E(compare) if compare else None}}
    return {"name": name, "category": category, "description": description, "accountId": account,
            "query": {"from": frm, "where": where, "select": select, "timeWindow": tw}}


REAL = "source != 'Synthetic'"   # business KPIs exclude synthetic-monitor test traffic


def flows(account, cond, mon, export_only=False):
    """cond/mon: key -> {guid, name}. In export-only mode alert signals are skipped and monitors use entity queries."""

    def alert(*keys):
        if export_only:
            return []
        return [{"guid": cond[k]["guid"], "name": cond[k]["name"], "type": E("ALERT")} for k in keys if k in cond]

    def monitor(key):
        # Monitor entities are attached for drill-down; health comes from the alert conditions.
        if export_only or key not in mon:
            return []
        return [{"guid": mon[key]["guid"], "name": mon[key]["name"], "type": E("ENTITY"), "isExcluded": True}]

    def apm(name):
        return {"query": f"accountId={account} AND domain='APM' AND type='APPLICATION' AND name='{name}'", "isExcluded": True}

    def step(name, signals=(), search=None, link=None):
        s = {"name": name, "signals": list(signals), "config": {"healthRollup": E("WORST_STATUS_WINS")}}
        if search:
            s["entitySearchQuery"] = search
        if export_only and not signals and search is None:
            s["entitySearchQuery"] = apm("SecureDocs-API")
        if link:
            s["link"] = link
        return s

    def stage(name, levels, related, kpis=()):
        return {"name": name, "healthRollup": E("AUTOMATIC_ROLL_UP"), "related": related,
                "stageKpis": list(kpis), "levels": [{"steps": lv} for lv in levels]}

    first, mid, last = {"source": False, "target": True}, {"source": True, "target": True}, {"source": True, "target": False}

    intake = {
        "name": "SecureDocs - Member data intake",
        "category": "Data intake",
        "description": ("A health-plan client gets its member file into the plant: connect, send data by API or secure "
                        "file transfer, pass HIPAA validation and address verification, and land in the production "
                        "queue in time for the mail date."),
        "refreshInterval": E("ONE_MINUTE"),
        "healthRollup": E("AUTOMATIC_ROLL_UP"),
        "kpis": [
            kpi(account, "Records received", "DocumentBatchIngested", "SUM", "accepted", REAL, category="Volume"),
            kpi(account, "Records rejected", "DocumentBatchIngested", "SUM", "rejected", REAL, category="Quality"),
            kpi(account, "Files processed", "FileTransferProcessed", "COUNT", where="status = 'Processed' AND origin != 'Synthetic Upload'", category="Volume"),
            kpi(account, "Avg file pickup delay (ms)", "FileTransferProcessed", "AVERAGE", "queueWaitMs", category="Timeliness"),
            kpi(account, "Undeliverable addresses", "DocumentBatchIngested", "SUM", "undeliverable", REAL, category="Quality"),
            kpi(account, "Payment value received ($)", "DocumentBatchIngested", "SUM", "amountTotal", REAL, category="Financial"),
        ],
        "stages": [
            stage("Client connects", [[
                step("Client portal sign-in", alert("synth_portal", "portal_errors") + monitor("portal"), apm("SecureDocs-Portal")),
                step("Client API available", alert("synth_ping") + monitor("ping"), apm("SecureDocs-API")),
            ]], first),
            stage("Client sends member data", [[
                step("Submit batch via API", alert("synth_api", "intake_slow", "intake_errors") + monitor("api"), apm("SecureDocs-API")),
                step("Secure file transfer (FTP / SFTP drop)", alert("synth_ftp", "files_waiting") + monitor("ftp")),
            ]], mid, [kpi(account, "Batches received", "DocumentBatchIngested", "COUNT", where=REAL, since="SIXTY_MINUTES", compare="TWENTY_FOUR_HOURS")]),
            stage("Validate & verify", [
                [step("HIPAA field validation", alert("rejections"))],
                [step("Address verification (CASS / NCOA)", alert("address_slow"))],
            ], mid, [kpi(account, "Records rejected (1h)", "DocumentBatchIngested", "SUM", "rejected", REAL, since="SIXTY_MINUTES", compare="TWENTY_FOUR_HOURS")]),
            stage("Stored & queued for production", [[
                step("Azure SQL - SecureDocs database", alert("db_errors", "db_slow")),
                step("Production queue accepting work", alert("floor_stalled")),
            ]], last, [kpi(account, "Records queued for production", "DocumentBatchIngested", "SUM", "accepted", REAL, since="SIXTY_MINUTES", compare="TWENTY_FOUR_HOURS")]),
        ],
    }

    production = {
        "name": "SecureDocs - Unique document production & proof of delivery",
        "category": "Production & delivery",
        "description": ("Every document is unique and may contain PHI: compose and print each piece, have QA cameras "
                        "verify the name and address on every piece (reprint on mismatch), then deliver and prove "
                        "completion within the client's SLA."),
        "refreshInterval": E("ONE_MINUTE"),
        "healthRollup": E("AUTOMATIC_ROLL_UP"),
        "kpis": [
            kpi(account, "Documents completed", "BatchCompleted", "SUM", "documents", REAL, category="Volume"),
            kpi(account, "Batches that missed SLA", "BatchCompleted", "COUNT", where=f"slaMet IS false AND {REAL}", category="SLA"),
            kpi(account, "p95 batch cycle time (min)", "BatchCompleted", "PERCENTILE", "cycleTimeMinutes", REAL, threshold=95, category="SLA"),
            kpi(account, "Possible PHI misprints caught", "QaInspection", "COUNT", where="passed IS false", category="Compliance"),
            kpi(account, "Reprints", "QaInspection", "COUNT", where="reprint IS true", category="Cost"),
            kpi(account, "Payment value delivered ($)", "BatchCompleted", "SUM", "amountTotal", REAL, category="Financial"),
        ],
        "stages": [
            stage("Production queue", [[
                step("Production floor running", alert("floor_stalled")),
                step("Work within SLA", alert("at_risk", "past_sla")),
            ]], first, [kpi(account, "Max documents at risk (30 min)", "ProductionBacklog", "MAX", "atRiskCount", since="THIRTY_MINUTES", compare=None)]),
            stage("Compose & print unique pieces", [[
                step("Composition engine", alert("composition"), apm("SecureDocs-API")),
                step("Production database (Azure SQL)", alert("db_errors", "db_slow")),
            ]], mid),
            stage("QA camera verification", [
                [step("Camera read: name & address match", alert("qa_yield"))],
                [step("Reprint & re-verify", alert("reprint_fail"))],
            ], mid, [kpi(account, "Misprints caught (1h)", "QaInspection", "COUNT", where="passed IS false", since="SIXTY_MINUTES", compare="TWENTY_FOUR_HOURS")]),
            stage("Deliver & prove", [[
                step("Batches completed within SLA", alert("sla_missed")),
                step("Client sees status & proof (portal / API)", alert("synth_api", "portal_errors") + monitor("api"), apm("SecureDocs-Portal")),
            ]], last, [kpi(account, "Print vs digital: digital deliveries", "BatchCompleted", "SUM", "digitalCount", REAL, since="TWENTY_FOUR_HOURS", compare="SEVEN_DAYS")]),
        ],
    }
    return [intake, production]


# --------------------------------------------------------------------------------------------------------------------
# Actions
# --------------------------------------------------------------------------------------------------------------------
def load_state():
    try:
        return json.loads(STATE_FILE.read_text())
    except Exception:
        return {}


def save_state(state):
    STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
    STATE_FILE.write_text(json.dumps(state, indent=2))
    os.chmod(STATE_FILE, 0o600)


def monitor_script(m, portal_url, api_url):
    text = (ROOT / "synthetics" / m["file"]).read_text()
    text = re.sub(r"const API_URL = '[^']*';", f"const API_URL = '{api_url}';", text)
    return re.sub(r"const PORTAL_URL = '[^']*';", f"const PORTAL_URL = '{portal_url}';", text)


def find_entity(ng, query):
    data = ng(f'{{ actor {{ entitySearch(query: {lit(query)}) {{ results {{ entities {{ guid name }} }} }} }} }}')
    ents = data["actor"]["entitySearch"]["results"]["entities"]
    return ents[0] if ents else None


def setup_monitors(ng, a, args):
    out = {}
    for m in MONITORS:
        base = {"name": m["name"], "period": E(args.period), "status": E("ENABLED"),
                "locations": {"public": [args.location]},
                "tags": [{"key": "app", "values": ["SecureDocs"]}, {"key": "flow", "values": ["SecureDocs"]}]}
        if m["kind"] == "simple":
            base.update({"uri": f"{args.api_url}/health",
                         "advancedOptions": {"responseValidationText": "Healthy", "useTlsValidation": True}})
            create, update = "syntheticsCreateSimpleMonitor", "syntheticsUpdateSimpleMonitor"
        else:
            base["script"] = monitor_script(m, args.portal_url, args.api_url)
            if m["kind"] == "browser":
                base["runtime"] = {"runtimeType": "CHROME_BROWSER", "runtimeTypeVersion": args.browser_runtime, "scriptLanguage": "JAVASCRIPT"}
                base["advancedOptions"] = {"enableScreenshotOnFailureAndScript": True}
                create, update = "syntheticsCreateScriptBrowserMonitor", "syntheticsUpdateScriptBrowserMonitor"
            else:
                base["runtime"] = {"runtimeType": "NODE_API", "runtimeTypeVersion": args.api_runtime, "scriptLanguage": "JAVASCRIPT"}
                create, update = "syntheticsCreateScriptApiMonitor", "syntheticsUpdateScriptApiMonitor"

        existing = find_entity(ng, f"accountId = {a} AND domain = 'SYNTH' AND type = 'MONITOR' AND name = '{m['name']}'")
        if existing:
            res = ng(f"mutation {{ {update}(guid: {lit(existing['guid'])}, monitor: {lit(base)}) {{ errors {{ description type }} }} }}")[update]
            guid, verb = existing["guid"], "updated"
        else:
            res = ng(f"mutation {{ {create}(accountId: {a}, monitor: {lit(base)}) {{ errors {{ description type }} monitor {{ guid }} }} }}")[create]
            guid, verb = (res.get("monitor") or {}).get("guid"), "created"
        if res.get("errors"):
            raise RuntimeError(f"{m['name']}: {res['errors']}")
        out[m["key"]] = {"guid": guid, "name": m["name"]}
        print(f"  monitor {verb}: {m['name']}")
    return out


def setup_alerts(ng, a, args):
    pol = ng(f'{{ actor {{ account(id: {a}) {{ alerts {{ policiesSearch(searchCriteria: {{name: {lit(POLICY_NAME)}}}) '
             f'{{ policies {{ id name }} }} }} }} }} }}')["actor"]["account"]["alerts"]["policiesSearch"]["policies"]
    pol = [p for p in pol if p["name"] == POLICY_NAME]
    if pol:
        policy_id = pol[0]["id"]
    else:
        policy_id = ng(f'mutation {{ alertsPolicyCreate(accountId: {a}, policy: {{name: {lit(POLICY_NAME)}, '
                       f'incidentPreference: PER_CONDITION}}) {{ id }} }}')["alertsPolicyCreate"]["id"]
        print(f"  policy created: {POLICY_NAME}")
    existing = ng(f'{{ actor {{ account(id: {a}) {{ alerts {{ nrqlConditionsSearch(searchCriteria: {{policyId: {lit(policy_id)}}}) '
                  f'{{ nrqlConditions {{ id name entityGuid }} }} }} }} }} }}')["actor"]["account"]["alerts"]["nrqlConditionsSearch"]["nrqlConditions"]
    by_name = {c["name"]: c for c in existing}

    out = {}
    for key, name, nrql, op, crit, warn, window, duration, occ, fill in conditions(PERIOD_SECONDS[args.period]):
        full = f"SecureDocs | {name}"
        terms = [{"priority": E("CRITICAL"), "operator": E(op), "threshold": crit, "thresholdDuration": duration,
                  "thresholdOccurrences": E(occ)}]
        if warn is not None:
            terms.append({"priority": E("WARNING"), "operator": E(op), "threshold": warn, "thresholdDuration": duration,
                          "thresholdOccurrences": E(occ)})
        signal = {"aggregationWindow": window, "aggregationMethod": E("EVENT_TIMER"), "aggregationTimer": 60,
                  "fillOption": E("STATIC" if fill else "NONE"), "fillValue": 0 if fill else None}
        # Sparse signals (counts of rare events) close once the events stop instead of waiting for the 24 h limit.
        expiration = ({"expirationDuration": 600, "closeViolationsOnExpiration": True, "openViolationOnExpiration": False}
                      if fill else None)
        cond = {"name": full, "enabled": True, "nrql": {"query": nrql}, "signal": signal, "terms": terms,
                "expiration": expiration,
                "violationTimeLimitSeconds": 86400,
                "description": "SecureDocs business-flow health signal (used by Pathpoint)."}
        if full not in by_name and RENAMED_CONDITIONS.get(full) in by_name:
            by_name[full] = by_name[RENAMED_CONDITIONS[full]]
        if full in by_name:
            r = ng(f"mutation {{ alertsNrqlConditionStaticUpdate(accountId: {a}, id: {lit(by_name[full]['id'])}, "
                   f"condition: {lit(cond)}) {{ id entityGuid }} }}")["alertsNrqlConditionStaticUpdate"]
            verb = "updated"
        else:
            r = ng(f"mutation {{ alertsNrqlConditionStaticCreate(accountId: {a}, policyId: {lit(policy_id)}, "
                   f"condition: {lit(cond)}) {{ id entityGuid }} }}")["alertsNrqlConditionStaticCreate"]
            verb = "created"
        out[key] = {"guid": r["entityGuid"], "name": full, "id": r["id"]}
        print(f"  condition {verb}: {full}")
    return out, policy_id


def write_flow_files(flow_defs):
    d = ROOT / "pathpoint"
    d.mkdir(exist_ok=True)
    for f in flow_defs:
        slug = re.sub(r"[^a-z0-9]+", "-", f["name"].lower()).strip("-").replace("securedocs-", "")
        (d / f"{slug}.json").write_text(json.dumps(plain(f), indent=2))
        print(f"  wrote pathpoint/{slug}.json")


def setup_flows(ng, a, flow_defs, state, recreate):
    created = state.setdefault("flows", {})
    for f in flow_defs:
        if f["name"] in created and not recreate:
            print(f"  flow exists (skipped, use --recreate-flows): {f['name']}")
            continue
        if f["name"] in created:
            try:
                ng(f"mutation {{ pathPointDelete(guid: {lit(created[f['name']])}) {{ guid }} }}")
                print(f"  flow deleted (recreating): {f['name']}")
            except RuntimeError as e:
                print(f"  could not delete old flow ({e}); creating a new one")
        r = ng(f"mutation {{ pathPointCreate(scope: {{id: {a}, type: ACCOUNT}}, pathpoint: {lit(f)}) "
               f"{{ guid name version message excludedKpis }} }}")["pathPointCreate"]
        created[f["name"]] = r["guid"]
        extra = f" (note: {r['message']})" if r.get("message") else ""
        if r.get("excludedKpis"):
            extra += f" excluded KPIs: {r['excludedKpis']}"
        print(f"  flow created: {f['name']}{extra}")


def verify(ng, a):
    def nrql(q):
        return ng(f'{{ actor {{ account(id: {a}) {{ nrql(query: {lit(q)}) {{ results }} }} }} }}')["actor"]["account"]["nrql"]["results"]
    print("Synthetic checks (last 60 min):")
    for r in nrql("SELECT count(*), filter(count(*), WHERE result = 'FAILED') AS failed, latest(result), "
                  "latest(error) FROM SyntheticCheck WHERE monitorName LIKE 'SecureDocs%' FACET monitorName SINCE 60 minutes ago"):
        print("  ", json.dumps(r))
    print("Open alert incidents:")
    for r in nrql("SELECT latest(event) FROM NrAiIncident WHERE policyName = 'SecureDocs - Business flows' "
                  "FACET conditionName SINCE 3 hours ago LIMIT 50"):
        print("  ", json.dumps(r))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--account", type=int, required=True)
    p.add_argument("--portal-url", help="portal base URL, e.g. https://securedocs-portal-xxxx.azurewebsites.net")
    p.add_argument("--api-url", help="API base URL, e.g. https://securedocs-api-xxxx.azurewebsites.net")
    p.add_argument("--period", default="EVERY_15_MINUTES", choices=list(PERIOD_SECONDS))
    p.add_argument("--location", default="AWS_US_EAST_2")
    p.add_argument("--region", default="US", choices=list(ENDPOINTS))
    p.add_argument("--api-runtime", default="16.10")
    p.add_argument("--browser-runtime", default="100")
    p.add_argument("--only", choices=["monitors", "alerts", "flows"])
    p.add_argument("--recreate-flows", action="store_true")
    p.add_argument("--export-only", action="store_true")
    p.add_argument("--verify", action="store_true")
    args = p.parse_args()
    args.portal_url, args.api_url = (args.portal_url or "").rstrip("/"), (args.api_url or "").rstrip("/")
    a = args.account

    if args.export_only:
        write_flow_files(flows(a, {}, {}, export_only=True))
        print("Import them in Pathpoint (Add a flow -> Import a flow). Health signals: add the alert conditions in the UI.")
        return

    if not args.verify and args.only in (None, "monitors") and not (args.portal_url and args.api_url):
        sys.exit("Pass --portal-url and --api-url (the URLs deploy.sh printed).")
    key = os.environ.get("NEW_RELIC_USER_KEY")
    if not key:
        sys.exit("Set NEW_RELIC_USER_KEY (a New Relic USER key, starts with NRAK-).")
    ng = NerdGraph(key, args.region)
    if args.verify:
        verify(ng, a)
        return

    state = load_state()
    state.setdefault("account", a)
    if args.only in (None, "monitors"):
        print("Synthetic monitors:")
        checks = len(MONITORS) * (2592000 // PERIOD_SECONDS[args.period])
        print(f"  ({args.period} from 1 location = about {checks:,} checks/month)")
        state["monitors"] = setup_monitors(ng, a, args)
        save_state(state)
    if args.only in (None, "alerts"):
        print("Alert conditions:")
        state["conditions"], state["policyId"] = setup_alerts(ng, a, args)
        save_state(state)
    if args.only in (None, "flows"):
        missing = [k for k in ("conditions", "monitors") if not state.get(k)]
        if missing:
            sys.exit(f"Run monitors and alerts first (missing: {missing}).")
        print("Pathpoint flows:")
        defs = flows(a, state["conditions"], state["monitors"])
        write_flow_files(defs)
        setup_flows(ng, a, defs, state, args.recreate_flows)
        save_state(state)
    print("Done.")


if __name__ == "__main__":
    main()
