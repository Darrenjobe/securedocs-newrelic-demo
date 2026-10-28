#!/usr/bin/env bash
# Deploys the SecureDocs demo to Azure: infrastructure (Bicep) + both apps (zip deploy).
#
# Prereqs: Azure CLI (logged in with `az login`), .NET 10 SDK, zip.
# Usage:
#   export NEW_RELIC_LICENSE_KEY=...        # New Relic ingest license key
#   export SQL_ADMIN_PASSWORD='...'         # 8+ chars, upper/lower/digit/symbol
#   ./deploy.sh        # optional: RG=... LOCATION=... SQL_LOCATION=... APP_SERVICE_SKU=... ./deploy.sh
set -euo pipefail

RG="${RG:-rg-securedocs-demo}"
LOCATION="${LOCATION:-eastus2}"
SQL_LOCATION="${SQL_LOCATION:-$LOCATION}"   # region for Azure SQL (if LOCATION does not accept new SQL servers)
SKU="${APP_SERVICE_SKU:-B1}"      # e.g. B1, S1, P0v3, P0v4, F1. Use a tier you have quota for
: "${NEW_RELIC_LICENSE_KEY:?Set NEW_RELIC_LICENSE_KEY}"
: "${SQL_ADMIN_PASSWORD:?Set SQL_ADMIN_PASSWORD}"

cd "$(dirname "$0")"
OUT=.deploy
rm -rf "$OUT" && mkdir -p "$OUT"

echo "==> Web apps: $LOCATION ($SKU)   SQL: $SQL_LOCATION   Resource group: $RG"
if [ "$(az group exists -n "$RG")" = "true" ]; then
  echo "    Using existing resource group $RG ($(az group show -n "$RG" --query location -o tsv))"
else
  az group create -n "$RG" -l "$LOCATION" -o none
fi

echo "==> Deploying infrastructure (App Service plan, 2 web apps, Azure SQL)..."
DEPLOYMENT=securedocs-$(date +%Y%m%d%H%M%S)
if ! az deployment group create -g "$RG" -n "$DEPLOYMENT" -f infra/main.bicep \
  -p sqlAdminPassword="$SQL_ADMIN_PASSWORD" newRelicLicenseKey="$NEW_RELIC_LICENSE_KEY" appServiceSku="$SKU" location="$LOCATION" sqlLocation="$SQL_LOCATION" -o none; then
  echo
  echo "Infrastructure deployment failed. Common causes:"
  echo "  * 'Operation cannot be completed without additional quota ... (B1 VMs): 0'"
  echo "      -> no App Service quota for $SKU in $LOCATION. Set APP_SERVICE_SKU / LOCATION to a combination"
  echo "         you have quota for (run: bash find-working-region.sh), or request a quota increase."
  echo "  * 'RegionDoesNotAllowProvisioning ... not accepting creation of new ... SQL Database servers'"
  echo "      -> set SQL_LOCATION to another region (e.g. centralus, westus3) and run again."
  echo "  * 'RequestDisallowedByPolicy' -> a company policy blocked it; see DEPLOY-AZURE.md 'Company subscription guardrails'."
  exit 1
fi

output() { az deployment group show -g "$RG" -n "$DEPLOYMENT" --query "properties.outputs.$1.value" -o tsv; }
API_APP=$(output apiAppName)
PORTAL_APP=$(output portalAppName)

echo "==> Building..."
dotnet publish src/SecureDocs.Api -c Release -o "$OUT/api"
dotnet publish src/SecureDocs.Web -c Release -o "$OUT/portal"
(cd "$OUT/api" && zip -qr ../api.zip .)
(cd "$OUT/portal" && zip -qr ../portal.zip .)

echo "==> Deploying API to $API_APP (first start creates + seeds the database, ~1-2 min)..."
az webapp deploy -g "$RG" -n "$API_APP" --src-path "$OUT/api.zip" --type zip -o none
echo "==> Deploying portal to $PORTAL_APP..."
az webapp deploy -g "$RG" -n "$PORTAL_APP" --src-path "$OUT/portal.zip" --type zip -o none

echo
echo "Done."
echo "  Portal: $(output portalUrl)"
echo "  API:    $(output apiUrl)/health"
echo "  SQL:    $(output sqlServerFqdn) / $(output sqlDatabaseName)"
echo "In New Relic look for APM services 'SecureDocs-Portal' and 'SecureDocs-API' (data appears a few minutes after first traffic)."
