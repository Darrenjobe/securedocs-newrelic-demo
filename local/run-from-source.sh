#!/bin/bash
# Alternative to start.sh: builds and runs from source with an installed .NET 10 SDK
# (use this if your Mac's security settings block the bundled runtime, or after changing code).
#   Install the SDK: https://dotnet.microsoft.com/download/dotnet/10.0  (or: brew install --cask dotnet-sdk)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$HERE/../source/src"; [ -d "$SRC" ] || SRC="$HERE/../src"
command -v dotnet >/dev/null || { echo "The .NET SDK isn't installed. See https://dotnet.microsoft.com/download/dotnet/10.0"; exit 1; }
DATA="$HERE/data"; LOGS="$HERE/logs"; OUT="$HERE/.build"
mkdir -p "$DATA" "$LOGS"
for p in 5080 5081; do
  lsof -nP -iTCP:$p -sTCP:LISTEN >/dev/null 2>&1 && { echo "Port $p is in use. Run ./stop.sh first."; exit 1; }
done
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
echo "Building (first build downloads NuGet packages, ~1-2 minutes)..."
dotnet publish "$SRC/SecureDocs.Api" -c Release -o "$OUT/api" -p:UseAppHost=false >/dev/null
dotnet publish "$SRC/SecureDocs.Web" -c Release -o "$OUT/portal" -p:UseAppHost=false >/dev/null

run() { # name dir dll port
  ( cd "$2"
    export ASPNETCORE_URLS="http://localhost:$4" ASPNETCORE_ENVIRONMENT=Production
    export Database__Provider=Sqlite ConnectionStrings__SecureDocs="Data Source=$DATA/securedocs.db"
    export Ftp__InboxPath="$DATA/ftp-inbox" Api__BaseUrl="http://localhost:5081" Api__PublicUrl="http://localhost:5081"
    export Portal__SelfUrl="http://localhost:5080" AddressService__BaseUrl="http://localhost:5081"
    nohup dotnet "$3" > "$LOGS/$1.log" 2>&1 &
    echo $! > "$LOGS/$1.pid" )
}
wait_for() { for _ in $(seq 1 "$3"); do curl -fsS "$1" >/dev/null 2>&1 && return 0; sleep 1; done
  echo "The $2 didn't start. Last lines of logs/$2.log:"; tail -20 "$LOGS/$2.log"; exit 1; }

run api "$OUT/api" SecureDocs.Api.dll 5081;          wait_for http://localhost:5081/health api 120
run portal "$OUT/portal" SecureDocs.Web.dll 5080;    wait_for http://localhost:5080/health portal 60
echo "SecureDocs is running: http://localhost:5080   (stop with ./stop.sh)"
open "http://localhost:5080" 2>/dev/null || true
