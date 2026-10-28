#!/usr/bin/env bash
# Builds the handoff bundle dist/SecureDocs-Demo.zip:
#   START-HERE.md
#   local/   run everything on a Mac (bundled .NET runtime for Apple Silicon + Intel, SQLite)
#   azure/   prebuilt Linux packages with the New Relic agent + Bicep + deploy guide
#   source/  full source
# Needs: .NET 10 SDK (dotnet on PATH, or DOTNET=/path/to/dotnet), curl, zip, rsync. Run on macOS or Linux.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
DOTNET="${DOTNET:-dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
CACHE="$ROOT/.package-cache"
WORK="$CACHE/work"
OUT="$WORK/SecureDocs-Demo"
rm -rf "$WORK" && mkdir -p "$OUT/local/bin" "$OUT/azure" "$OUT/source" dist

publish() { # project rid outdir [extra args]
  "$DOTNET" publish "src/$1" -c Release -r "$2" --self-contained false -o "$3" "${@:4}" -v quiet -nologo
}

echo "==> Azure packages (linux-x64, with New Relic agent)"
publish SecureDocs.Api linux-x64 "$WORK/linux/api"
publish SecureDocs.Web linux-x64 "$WORK/linux/portal"
(cd "$WORK/linux/api" && zip -qr "$OUT/azure/api.zip" .)
(cd "$WORK/linux/portal" && zip -qr "$OUT/azure/portal.zip" .)
cp DEPLOY-AZURE.md infra/main.bicep deploy-cloudshell.sh find-working-region.sh "$OUT/azure/"
cp newrelic/dashboard-securedocs.json newrelic/BUSINESS-KPIS.md scripts/make_dashboard.py "$OUT/azure/"
mkdir -p "$OUT/azure/synthetics" "$OUT/azure/pathpoint"
cp synthetics/*.js synthetics/README.md "$OUT/azure/synthetics/"
mkdir -p "$OUT/azure/synthetics/local" && cp synthetics/local/run.js synthetics/local/package.json synthetics/local/README.md synthetics/local/.gitignore "$OUT/azure/synthetics/local/"
cp pathpoint/*.json pathpoint/README.md "$OUT/azure/pathpoint/"
cp scripts/newrelic_setup.py "$OUT/azure/"
mkdir -p "$OUT/azure/mssql" && cp newrelic/mssql/* "$OUT/azure/mssql/" && cp scripts/deploy_sql_monitor.sh scripts/azure_newrelic_link.py scripts/make_db_dashboards.py scripts/newrelic_service_levels.py newrelic/dashboard-db-*.json newrelic/SERVICE-LEVELS.md newrelic/CUSTOM-ATTRIBUTES-DEMO.md "$OUT/azure/"

echo "==> Local Mac builds + bundled .NET runtime"
[ -f "$CACHE/dotnet-install.sh" ] || curl -sSL https://dot.net/v1/dotnet-install.sh -o "$CACHE/dotnet-install.sh"
for pair in "osx-arm64:mac-arm64:arm64" "osx-x64:mac-x64:x64"; do
  IFS=: read -r rid folder arch <<< "$pair"
  dest="$OUT/local/bin/$folder"
  if [ ! -x "$CACHE/runtime-$arch/dotnet" ]; then
    bash "$CACHE/dotnet-install.sh" --channel 10.0 --runtime aspnetcore --architecture "$arch" \
      --install-dir "$CACHE/runtime-$arch" >/dev/null
  fi
  mkdir -p "$dest"
  cp -R "$CACHE/runtime-$arch" "$dest/runtime"
  publish SecureDocs.Api "$rid" "$dest/api" -p:UseAppHost=false
  publish SecureDocs.Web "$rid" "$dest/portal" -p:UseAppHost=false
  # The New Relic agent doesn't run on macOS; debug symbols aren't needed.
  rm -rf "$dest"/api/newrelic "$dest"/portal/newrelic "$dest"/api/*.pdb "$dest"/portal/*.pdb
done
cp local/*.sh local/*.command local/LOCAL-RUN.md "$OUT/local/"
chmod +x "$OUT"/local/*.sh "$OUT"/local/*.command "$OUT"/azure/*.sh

echo "==> Source"
rsync -a --exclude bin --exclude obj --exclude App_Data --exclude .deploy --exclude dist --exclude .package-cache \
  --exclude 'local/bin' --exclude 'local/data' --exclude 'local/logs' --exclude 'local/.build' --exclude '*.db*' \
  --exclude .git --exclude .DS_Store --exclude node_modules --exclude 'failure-*.png' ./ "$OUT/source/"
cp packaging/START-HERE.md "$OUT/"
mkdir -p "$OUT/demo" && cp newrelic/DEMO-RUNBOOK.md newrelic/INSTRUMENTATION-GUIDE.md "$OUT/demo/"

echo "==> Zipping"
rm -f dist/SecureDocs-Demo.zip
(cd "$WORK" && zip -qry "$ROOT/dist/SecureDocs-Demo.zip" SecureDocs-Demo)
ls -lh dist/SecureDocs-Demo.zip
