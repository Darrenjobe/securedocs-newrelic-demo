#!/bin/bash
# Double-click in Finder to stop the SecureDocs demo.
cd "$(dirname "$0")"
./stop.sh
echo
read -n 1 -s -r -p "Press any key to close this window..."
