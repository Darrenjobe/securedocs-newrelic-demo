#!/usr/bin/env bash
# Writes importable New Relic files for YOUR account. Runs offline: no API key needed, nothing is sent.
#   newrelic/dashboard-securedocs.json       Business KPIs dashboard
#   newrelic/dashboard-db-health.json        Azure SQL health dashboard
#   newrelic/dashboard-db-slow-queries.json  Slow query investigation dashboard
#   pathpoint/*.json                         the 2 Pathpoint flows (without alert signals; add them in the UI,
#                                            or let scripts/newrelic_setup.py create the flows with signals)
# Import dashboards: New Relic -> Dashboards -> Import dashboard. Flows: Pathpoint -> Add a flow -> Import a flow.
set -euo pipefail
ACCOUNT="${1:?Usage: scripts/render_newrelic_files.sh <NEW_RELIC_ACCOUNT_ID>}"
cd "$(dirname "$0")/.."
python3 scripts/make_dashboard.py "$ACCOUNT" newrelic/dashboard-securedocs.json
python3 scripts/make_db_dashboards.py --account "$ACCOUNT"
python3 scripts/newrelic_setup.py --account "$ACCOUNT" --export-only
