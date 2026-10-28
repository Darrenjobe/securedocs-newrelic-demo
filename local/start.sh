#!/bin/bash
# Starts the SecureDocs demo entirely on this Mac (no Azure needed):
#   API    → http://localhost:5081   (SQLite database in ./data)
#   Portal → http://localhost:5080
# Uses the .NET runtime bundled in ./bin, so nothing needs to be installed.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
case "$(uname -m)" in
  arm64)  ARCH=mac-arm64 ;;   # Apple Silicon (M1/M2/M3/M4...)
  x86_64) ARCH=mac-x64 ;;     # Intel Macs
  *) echo "Unsupported CPU: $(uname -m)"; exit 1 ;;
esac
BIN="$HERE/bin/$ARCH"
DATA="$HERE/data"
LOGS="$HERE/logs"
mkdir -p "$DATA" "$LOGS"

if [ ! -x "$BIN/runtime/dotnet" ]; then
  echo "Prebuilt files not found in $BIN."
  echo "If you have the .NET 10 SDK installed, use ./run-from-source.sh instead."
  exit 1
fi

# Files extracted from a downloaded/emailed zip are flagged ("quarantined") by macOS; clear the flag
# so the bundled Microsoft .NET runtime is allowed to run.
xattr -dr com.apple.quarantine "$HERE" 2>/dev/null || true

port_busy() { lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }
for p in 5080 5081; do
  if port_busy "$p"; then
    echo "Port $p is already in use. Is SecureDocs already running? Run ./stop.sh first,"
    echo "or close the other program using port $p (check with: lsof -nP -iTCP:$p -sTCP:LISTEN)."
    exit 1
  fi
done

start_app() { # name dir dll port
  local name=$1 dir=$2 dll=$3 port=$4
  (
    cd "$dir"
    export ASPNETCORE_URLS="http://localhost:$port" ASPNETCORE_ENVIRONMENT=Production
    export DOTNET_ROOT="$BIN/runtime" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
    export Database__Provider=Sqlite ConnectionStrings__SecureDocs="Data Source=$DATA/securedocs.db"
    export Ftp__InboxPath="$DATA/ftp-inbox" Api__BaseUrl="http://localhost:5081" Api__PublicUrl="http://localhost:5081"
    export Portal__SelfUrl="http://localhost:5080" AddressService__BaseUrl="http://localhost:5081"
    nohup "$BIN/runtime/dotnet" "$dll" > "$LOGS/$name.log" 2>&1 &
    echo $! > "$LOGS/$name.pid"
  )
}

wait_for() { # url name seconds
  for _ in $(seq 1 "$3"); do
    curl -fsS "$1" >/dev/null 2>&1 && return 0
    sleep 1
  done
  echo "The $2 didn't start within $3 seconds. Last lines of logs/$2.log:"
  tail -20 "$LOGS/$2.log"
  "$HERE/stop.sh" >/dev/null 2>&1 || true
  exit 1
}

[ -f "$DATA/securedocs.db" ] || echo "First run: creating the demo database (about 10-30 seconds)..."
echo "Starting API on http://localhost:5081 ..."
start_app api "$BIN/api" SecureDocs.Api.dll 5081
wait_for http://localhost:5081/health api 120

echo "Starting portal on http://localhost:5080 ..."
start_app portal "$BIN/portal" SecureDocs.Web.dll 5080
wait_for http://localhost:5080/health portal 60

echo
echo "SecureDocs is running:  http://localhost:5080"
echo "  API health:  http://localhost:5081/health"
echo "  Logs:        $LOGS"
echo "Stop it with ./stop.sh (or double-click 'Stop SecureDocs.command')."
open "http://localhost:5080" 2>/dev/null || true
