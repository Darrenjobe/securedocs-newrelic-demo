#!/bin/bash
# Double-click in Finder to start the SecureDocs demo.
cd "$(dirname "$0")"
./start.sh
echo
read -n 1 -s -r -p "Press any key to close this window (SecureDocs keeps running)..."
