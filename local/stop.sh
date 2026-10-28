#!/bin/bash
# Stops the local SecureDocs demo.  ./stop.sh --reset  also deletes the demo database and uploaded files.
HERE="$(cd "$(dirname "$0")" && pwd)"
LOGS="$HERE/logs"
stopped=0
for name in portal api; do
  pidfile="$LOGS/$name.pid"
  if [ -f "$pidfile" ]; then
    pid=$(cat "$pidfile")
    if kill "$pid" 2>/dev/null; then
      for _ in $(seq 1 10); do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
      kill -9 "$pid" 2>/dev/null || true
      echo "Stopped $name (pid $pid)"; stopped=1
    fi
    rm -f "$pidfile"
  fi
done
# Fallback: anything still serving the demo ports that is a SecureDocs process.
for port in 5080 5081; do
  for pid in $(lsof -tnP -iTCP:$port -sTCP:LISTEN 2>/dev/null); do
    if ps -p "$pid" -o command= | grep -q "SecureDocs"; then kill "$pid" 2>/dev/null && echo "Stopped SecureDocs on port $port (pid $pid)" && stopped=1; fi
  done
done
[ $stopped = 1 ] || echo "SecureDocs was not running."
if [ "${1:-}" = "--reset" ]; then
  rm -rf "$HERE/data"
  echo "Demo data deleted. A fresh database is created on the next start."
fi
