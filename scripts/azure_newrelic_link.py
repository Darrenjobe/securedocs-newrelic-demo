#!/usr/bin/env python3
"""
Links an Azure subscription to New Relic and turns on the Azure Monitor integration for the SecureDocs resources
(App Service sites + plan, Azure SQL server + database), limited to one resource group.

Prereqs:
  1. A read-only service principal (Reader on the resource group), saved as JSON (az ad sp create-for-rbac output):
       az ad sp create-for-rbac --name newrelic-securedocs-reader --role Reader \
         --scopes /subscriptions/<SUB_ID>/resourceGroups/rg-securedocs-demo > ~/.securedocs/azure-newrelic-sp.json
  2. export NEW_RELIC_USER_KEY=NRAK-...

Usage:
  python3 scripts/azure_newrelic_link.py --account <ACCOUNT_ID> --subscription <SUB_ID> [--resource-group rg-securedocs-demo]
  python3 scripts/azure_newrelic_link.py --account <ACCOUNT_ID> --status      # show linked accounts + whether metrics arrive
"""
import argparse
import json
import os
import sys
import urllib.request
from pathlib import Path

ENDPOINTS = {"US": "https://api.newrelic.com/graphql", "EU": "https://api.eu.newrelic.com/graphql"}
LINK_NAME = "SecureDocs Azure"
RESOURCE_TYPES = [
    "microsoft.web/sites",                  # portal + API web apps
    "microsoft.web/serverfarms",            # App Service plan (CPU / memory)
    "microsoft.sql/servers/databases",      # Azure SQL database (DTU, CPU, deadlocks, connections)
    "microsoft.sql/servers",
]


def ng(key, region, query):
    req = urllib.request.Request(ENDPOINTS[region], data=json.dumps({"query": query}).encode(),
                                 headers={"Content-Type": "application/json", "API-Key": key})
    body = json.loads(urllib.request.urlopen(req, timeout=60).read())
    if body.get("errors"):
        raise RuntimeError("; ".join(e.get("message", str(e)) for e in body["errors"]))
    return body["data"]


def linked_azure(key, region, account):
    accts = ng(key, region, f"{{ actor {{ account(id: {account}) {{ cloud {{ linkedAccounts {{ id name disabled "
                            f"provider {{ slug }} }} }} }} }} }}")["actor"]["account"]["cloud"]["linkedAccounts"]
    return [a for a in accts if a["provider"]["slug"] == "azure"]


def status(key, region, account):
    for a in linked_azure(key, region, account):
        print(f"linked: {a['name']} (id {a['id']}, disabled={a['disabled']})")
    q = ("SELECT count(*), uniqueCount(entity.name) FROM Metric WHERE metricName LIKE 'azure.%' "
         "FACET azure.resourceType SINCE 30 minutes ago")
    rows = ng(key, region, f'{{ actor {{ account(id: {account}) {{ nrql(query: {json.dumps(q)}) {{ results }} }} }} }}')
    print("Azure metrics (last 30 min):")
    for r in rows["actor"]["account"]["nrql"]["results"]:
        print("  ", r)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--account", type=int, required=True)
    p.add_argument("--subscription")
    p.add_argument("--resource-group", default="rg-securedocs-demo")
    p.add_argument("--sp-file", default=str(Path.home() / ".securedocs" / "azure-newrelic-sp.json"))
    p.add_argument("--metrics-interval", type=int, default=300, help="seconds between metric polls (default 300)")
    p.add_argument("--region", default="US", choices=list(ENDPOINTS))
    p.add_argument("--status", action="store_true")
    args = p.parse_args()

    key = os.environ.get("NEW_RELIC_USER_KEY") or sys.exit("Set NEW_RELIC_USER_KEY")
    if args.status:
        status(key, args.region, args.account)
        return
    if not args.subscription:
        sys.exit("--subscription is required")
    sp = json.loads(Path(args.sp_file).read_text())

    existing = [a for a in linked_azure(key, args.region, args.account) if a["name"] == LINK_NAME]
    if existing:
        linked_id = existing[0]["id"]
        print(f"Azure account already linked (id {linked_id})")
    else:
        link = {"applicationId": sp["appId"], "clientSecret": sp["password"], "name": LINK_NAME,
                "subscriptionId": args.subscription, "tenantId": sp["tenant"]}
        fields = ", ".join(f"{k}: {json.dumps(v)}" for k, v in link.items())
        r = ng(key, args.region, f"mutation {{ cloudLinkAccount(accountId: {args.account}, accounts: {{azure: [{{{fields}}}]}}) "
                                 f"{{ linkedAccounts {{ id name }} errors {{ message type }} }} }}")["cloudLinkAccount"]
        if r.get("errors"):
            sys.exit(f"Link failed: {r['errors']}")
        linked_id = r["linkedAccounts"][0]["id"]
        print(f"Azure account linked: {LINK_NAME} (id {linked_id})")

    monitor = (f"{{linkedAccountId: {linked_id}, enabled: true, metricsPollingInterval: {args.metrics_interval}, "
               f"resourceGroups: {json.dumps([args.resource_group])}, resourceTypes: {json.dumps(RESOURCE_TYPES)}}}")
    r = ng(key, args.region, f"mutation {{ cloudConfigureIntegration(accountId: {args.account}, integrations: "
                             f"{{azure: {{azureMonitor: [{monitor}]}}}}) {{ integrations {{ id name }} errors {{ message type }} }} }}")
    res = r["cloudConfigureIntegration"]
    if res.get("errors"):
        sys.exit(f"Integration failed: {res['errors']}")
    print(f"Azure Monitor integration enabled for {args.resource_group}: {', '.join(RESOURCE_TYPES)}")
    print("Metrics usually appear within 5-15 minutes. Check with --status.")


if __name__ == "__main__":
    main()
