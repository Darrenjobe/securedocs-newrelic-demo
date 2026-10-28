#!/usr/bin/env bash
# Runs the New Relic infrastructure agent + SQL Server integration (nri-mssql) in an Azure Container Instance
# so it can monitor the SecureDocs Azure SQL database (Azure SQL Database has no host to install an agent on).
#
# Prereqs: az login; NEW_RELIC_LICENSE_KEY and SQL_MONITOR_PASSWORD exported (or in ~/.securedocs/deploy.env);
#          the read-only 'newrelic' login on the database (see newrelic/mssql/README.md).
# Usage:   bash scripts/deploy_sql_monitor.sh            # create or update
#          az container stop|start -g rg-securedocs-demo -n securedocs-sql-monitor    # pause between demos
set -euo pipefail
RG="${RG:-rg-securedocs-demo}"
NAME="${NAME:-securedocs-sql-monitor}"
IMAGE="${IMAGE:-newrelic/infrastructure-bundle:3.5.4}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if [ -f ~/.securedocs/deploy.env ]; then set -a; . ~/.securedocs/deploy.env; set +a; fi
: "${NEW_RELIC_LICENSE_KEY:?}" "${SQL_MONITOR_PASSWORD:?}"

SQL_SERVER=$(az sql server list -g "$RG" --query "[?starts_with(name,'securedocs-sql-')].fullyQualifiedDomainName | [0]" -o tsv)
LOCATION=$(az sql server list -g "$RG" --query "[?starts_with(name,'securedocs-sql-')].location | [0]" -o tsv)
CONFIG=$(sed -e "s|__SQL_SERVER__|$SQL_SERVER|" -e "s|__SQL_MONITOR_PASSWORD__|$SQL_MONITOR_PASSWORD|" \
  "$ROOT/newrelic/mssql/mssql-config.yml.template")

if [ "$(az provider show -n Microsoft.ContainerInstance --query registrationState -o tsv)" != "Registered" ]; then
  echo "==> Registering Microsoft.ContainerInstance (one-time)"; az provider register -n Microsoft.ContainerInstance --wait
fi

if az container show -g "$RG" -n "$NAME" -o none 2>/dev/null; then
  echo "==> Replacing existing $NAME"; az container delete -g "$RG" -n "$NAME" --yes -o none
fi
echo "==> Creating $NAME in $LOCATION (monitoring $SQL_SERVER)"
az container create -g "$RG" -n "$NAME" -l "$LOCATION" --image "$IMAGE" --os-type Linux \
  --cpu 0.5 --memory 1 --restart-policy Always \
  --environment-variables NRIA_DISPLAY_NAME="$NAME" NRIA_IS_FORWARD_ONLY=true NRIA_LOG_LEVEL="${LOG_LEVEL:-info}" \
  --secure-environment-variables NRIA_LICENSE_KEY="$NEW_RELIC_LICENSE_KEY" \
  --secrets "mssql-config.yml=$CONFIG" --secrets-mount-path /etc/newrelic-infra/integrations.d \
  -o none
echo "==> Running. Logs: az container logs -g $RG -n $NAME"
