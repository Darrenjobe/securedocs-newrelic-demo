#!/usr/bin/env bash
# Deploys the PREBUILT SecureDocs demo (no .NET SDK needed). Run from Azure Cloud Shell (Bash)
# or any machine with the Azure CLI, in a folder containing:
#   main.bicep   api.zip   portal.zip   deploy-cloudshell.sh
#
# Usage:
#   export NEW_RELIC_LICENSE_KEY=...      # New Relic INGEST - LICENSE key
#   export SQL_ADMIN_PASSWORD='...'       # 8+ chars with upper, lower, digit, symbol
#   bash deploy-cloudshell.sh
#
# By default the script FINDS a region + App Service tier your subscription can use, and a region that accepts
# new Azure SQL servers, by trying them for real (test resources are deleted straight away). To skip that, pin them:
#   LOCATION=centralus APP_SERVICE_SKU=B1 SQL_LOCATION=westus3 bash deploy-cloudshell.sh
# Other options: RG=<resource group name>
set -euo pipefail

RG="${RG:-rg-securedocs-demo}"
# Candidates tried in order (first one that works wins). Pinning LOCATION / APP_SERVICE_SKU / SQL_LOCATION tries only that.
APP_REGIONS="${LOCATION:-centralus westus3 westus2 eastus northcentralus southcentralus westus eastus2}"
APP_SKUS="${APP_SERVICE_SKU:-B1 S1 P0V3 P0V4 P1V3 F1}"
SQL_REGIONS="${SQL_LOCATION:-centralus westus3 westus2 eastus northcentralus southcentralus westus eastus2}"
: "${NEW_RELIC_LICENSE_KEY:?Set NEW_RELIC_LICENSE_KEY first}"
: "${SQL_ADMIN_PASSWORD:?Set SQL_ADMIN_PASSWORD first}"
cd "$(dirname "$0")"
for f in main.bicep api.zip portal.zip; do [ -f "$f" ] || { echo "Missing $f in $(pwd)"; exit 1; }; done

echo "Subscription: $(az account show --query '[name, id]' -o tsv | paste -sd ' ' -)"
ERR=$(mktemp)
short_reason() {
  grep -oE "Current Limit \([^)]*\): [0-9]+|not accepting creation[^\"\\]*|RequestDisallowedByPolicy|SkuNotAvailable|is not available[^\"\\]*|AuthorizationFailed|ReadOnlyDisabledSubscription|SubscriptionNotRegistered" "$ERR" | head -1 | cut -c1-100
}

# ---- Resource group (created in the first candidate region; resources can live in other regions) ----
FIRST_REGION=${APP_REGIONS%% *}
if [ "$(az group exists -n "$RG")" = "true" ]; then
  echo "==> Using existing resource group $RG ($(az group show -n "$RG" --query location -o tsv))"
else
  echo "==> Creating resource group $RG"
  az group create -n "$RG" -l "$FIRST_REGION" -o none
fi

# Make sure the resource providers are registered (new personal subscriptions often have them off).
for ns in Microsoft.Web Microsoft.Sql; do
  if [ "$(az provider show -n $ns --query registrationState -o tsv 2>/dev/null)" != "Registered" ]; then
    echo "==> Registering resource provider $ns (one-time, ~1 minute)..."
    az provider register -n $ns --wait -o none
  fi
done

# ---- Find an App Service region + tier with quota ----
LOCATION=""; SKU=""
echo "==> Finding an App Service region/tier this subscription can use..."
for r in $APP_REGIONS; do
  for s in $APP_SKUS; do
    probe="probe-plan-$RANDOM"
    if az appservice plan create -g "$RG" -n "$probe" -l "$r" --sku "$s" --is-linux -o none 2>"$ERR"; then
      az appservice plan delete -g "$RG" -n "$probe" --yes -o none 2>/dev/null || true
      LOCATION=$r; SKU=$s
      echo "    OK  $r / $s"
      break 2
    else
      echo "    no  $r / $s   $(short_reason)"
    fi
  done
done
if [ -z "$SKU" ]; then
  echo
  echo "No App Service region/tier worked. This subscription has no App Service quota."
  echo "  * Free Trial subscription? Upgrade it to Pay-As-You-Go (Portal -> Subscriptions -> Upgrade); remaining credit is kept."
  echo "  * Otherwise request quota: Portal -> Quotas -> App Service -> pick a region -> 'B1 VMs' (and 'Total Regional VMs') -> request 1-2."
  exit 1
fi
if [ "$SKU" = "F1" ]; then
  echo "    NOTE: only the Free tier (F1) is available. The demo will run, but with limits: no Always On (background"
  echo "          workers pause when idle) and 60 CPU-minutes/day. Upgrade/request quota for B1 for a smoother demo."
fi

# ---- Find a region that accepts new Azure SQL servers ----
SQL_LOCATION=""
EXISTING_SQL=$(az sql server list -g "$RG" --query "[?starts_with(name,'securedocs-sql-') && state=='Ready'].{n:name,l:location}" -o tsv 2>/dev/null || true)
if [ -n "$EXISTING_SQL" ]; then
  SQL_LOCATION=$(echo "$EXISTING_SQL" | head -1 | cut -f2)
  echo "==> Reusing the existing SQL server in $SQL_LOCATION"
else
  echo "==> Finding a region that accepts new Azure SQL servers..."
  for r in $SQL_REGIONS; do
    probe="probe-sql-$RANDOM$RANDOM"
    if az sql server create -g "$RG" -n "$probe" -l "$r" -u probeadmin -p "$SQL_ADMIN_PASSWORD" -o none 2>"$ERR"; then
      az sql server delete -g "$RG" -n "$probe" --yes -o none 2>/dev/null || true
      SQL_LOCATION=$r
      echo "    OK  $r"
      break
    else
      echo "    no  $r   $(short_reason)"
    fi
  done
  if [ -z "$SQL_LOCATION" ]; then
    echo "No region accepted a new SQL server. Last error:"; tail -c 600 "$ERR"; echo
    exit 1
  fi
fi

echo
echo "==> Deploying: web apps in $LOCATION ($SKU), Azure SQL in $SQL_LOCATION, resource group $RG (3-6 minutes)..."
DEPLOYMENT="securedocs-$(date +%Y%m%d%H%M%S)"
if ! az deployment group create -g "$RG" -n "$DEPLOYMENT" -f main.bicep \
  -p sqlAdminPassword="$SQL_ADMIN_PASSWORD" newRelicLicenseKey="$NEW_RELIC_LICENSE_KEY" \
     appServiceSku="$SKU" location="$LOCATION" sqlLocation="$SQL_LOCATION" -o none; then
  echo
  echo "Infrastructure deployment failed (see the error above). Common causes:"
  echo "  * '... is not accepting creation of new ... SQL Database servers' -> run again with SQL_LOCATION=<other region>"
  echo "  * 'Current Limit (...): 0' -> run again with LOCATION/APP_SERVICE_SKU set to another combination"
  echo "  * 'RequestDisallowedByPolicy' -> a policy blocked it; see DEPLOY-AZURE.md 'Company subscription guardrails'."
  exit 1
fi

output() { az deployment group show -g "$RG" -n "$DEPLOYMENT" --query "properties.outputs.$1.value" -o tsv; }
API_APP=$(output apiAppName)
PORTAL_APP=$(output portalAppName)

echo "==> Deploying API package to $API_APP..."
az webapp deploy -g "$RG" -n "$API_APP" --src-path api.zip --type zip -o none
echo "==> Deploying portal package to $PORTAL_APP..."
az webapp deploy -g "$RG" -n "$PORTAL_APP" --src-path portal.zip --type zip -o none

API_URL=$(output apiUrl)
echo "==> Waiting for the API to create and seed the database (first start takes 1-3 minutes)..."
for i in $(seq 1 36); do
  if curl -fsS "$API_URL/health" >/dev/null 2>&1; then echo "    API is healthy."; break; fi
  sleep 10
done

echo
echo "Done."
echo "  Portal:     $(output portalUrl)"
echo "  API health: $API_URL/health"
echo "  SQL server: $(output sqlServerFqdn)  database: $(output sqlDatabaseName)"
echo "  Plan:       $SKU in $LOCATION"
echo "New Relic APM services: SecureDocs-Portal and SecureDocs-API (data appears ~2-5 min after first traffic)."
echo "Delete everything later with: az group delete -n $RG"
