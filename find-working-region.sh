#!/usr/bin/env bash
# Finds where this subscription can actually create the demo's resources, by trying for real:
#   1. a Linux App Service plan for each tier in each region (deleted again immediately)
#   2. an empty Azure SQL server in each region (deleted again immediately; servers are free without databases)
# Takes 3-10 minutes. Costs nothing meaningful (each test plan exists for a few seconds).
#
# Usage (Azure Cloud Shell or any machine with the Azure CLI, after `az login`):
#   bash find-working-region.sh
#   REGIONS="eastus centralus" SKUS="B1 P0V4" bash find-working-region.sh     # narrow it down
set -uo pipefail

REGIONS="${REGIONS:-eastus eastus2 centralus northcentralus southcentralus westus westus2 westus3}"
SKUS="${SKUS:-B1 S1 P0V3 P0V4 P1V3 F1}"
PROBE_RG="${PROBE_RG:-rg-securedocs-probe}"
PROBE_LOCATION="${PROBE_LOCATION:-eastus}"
SUFFIX=$RANDOM$RANDOM

echo "Subscription: $(az account show --query '[name, id]' -o tsv | paste -sd ' ' -)"
echo "Regions: $REGIONS"
echo "Tiers:   $SKUS"
echo
if [ "$(az group exists -n "$PROBE_RG")" != "true" ]; then
  if ! az group create -n "$PROBE_RG" -l "$PROBE_LOCATION" -o none 2>/tmp/probe_err; then
    echo "Could not create the test resource group $PROBE_RG:"; cat /tmp/probe_err
    echo "If a policy requires tags or a naming pattern, create a resource group yourself and run: PROBE_RG=<name> bash $0"
    exit 1
  fi
  CREATED_RG=1
fi

reason() { # pull the useful part out of an Azure CLI error
  grep -oE "Current Limit \([^)]*\): [0-9]+|RegionDoesNotAllowProvisioning[^\"]*|not accepting creation[^\"]*|RequestDisallowedByPolicy|disallowed by policy[^\"]*|is not available[^\"]*|SkuNotAvailable[^\"]*|NoRegisteredProviderFound[^\"]*|AuthorizationFailed" /tmp/probe_err \
    | head -1 | cut -c1-110
}

echo "== App Service plans (Linux) =="
APP_OK=()
for region in $REGIONS; do
  for sku in $SKUS; do
    name="probe-plan-$SUFFIX-$region-$sku"
    if az appservice plan create -g "$PROBE_RG" -n "$name" -l "$region" --sku "$sku" --is-linux -o none 2>/tmp/probe_err; then
      echo "  OK    $region  $sku"
      APP_OK+=("$region:$sku")
      az appservice plan delete -g "$PROBE_RG" -n "$name" --yes -o none 2>/dev/null
    else
      echo "  no    $region  $sku   $(reason)"
    fi
  done
done

echo
echo "== Azure SQL servers =="
SQL_OK=()
PW="Pr0be-$(openssl rand -hex 8 2>/dev/null || echo $SUFFIX)!"
for region in $REGIONS; do
  name="probe-sql-$SUFFIX-$region"
  if az sql server create -g "$PROBE_RG" -n "$name" -l "$region" -u probeadmin -p "$PW" -o none 2>/tmp/probe_err; then
    echo "  OK    $region"
    SQL_OK+=("$region")
    az sql server delete -g "$PROBE_RG" -n "$name" --yes -o none 2>/dev/null
  else
    r=$(reason)
    echo "  no    $region   ${r:-$(tail -c 160 /tmp/probe_err | tr '\n' ' ')}"
  fi
done

[ "${CREATED_RG:-0}" = 1 ] && az group delete -n "$PROBE_RG" --yes --no-wait -o none 2>/dev/null

echo
echo "================ Summary ================"
if [ ${#APP_OK[@]} -eq 0 ]; then
  echo "App Service: no tier/region worked. The subscription has no App Service quota (often 'Total Regional VMs' = 0)."
  echo "             Request a quota increase (Quotas -> App Service) or ask your cloud team, or use another subscription."
else
  echo "App Service works for: ${APP_OK[*]}"
fi
if [ ${#SQL_OK[@]} -eq 0 ]; then
  echo "Azure SQL:   no region worked (see reasons above; a policy may require Entra-only auth - see DEPLOY-AZURE.md G3)."
else
  echo "Azure SQL works in:    ${SQL_OK[*]}"
fi
if [ ${#APP_OK[@]} -gt 0 ] && [ ${#SQL_OK[@]} -gt 0 ]; then
  IFS=: read -r r s <<< "${APP_OK[0]}"
  echo
  echo "Suggested deploy command:"
  echo "  RG=rg-securedocs-demo2 LOCATION=$r APP_SERVICE_SKU=$s SQL_LOCATION=${SQL_OK[0]} bash deploy-cloudshell.sh"
fi
