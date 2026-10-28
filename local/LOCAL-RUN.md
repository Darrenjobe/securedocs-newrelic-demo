# Running SecureDocs on your Mac (no Azure)

This runs the whole demo on your Mac: the portal, the API, and a local SQLite database in place of Azure SQL. You don't
need Azure, .NET, Docker or anything else installed, because the files include Microsoft's .NET runtime for both Apple
Silicon and Intel Macs.

> **New Relic doesn't run in this mode.** The New Relic .NET agent doesn't support macOS. Locally you'll see every
> fault in the app itself (slow pages, error banners, QA mismatches), but nothing is sent to New Relic. For the New Relic
> demo, use the Azure setup (`../azure/DEPLOY-AZURE.md`).

## Start

**Option 1: double-click.** In Finder, double-click **`Start SecureDocs.command`**.
- The first time, macOS may say it "cannot be opened because it is from an unidentified developer". If so,
  **right-click** the file → **Open** → **Open**. You only need to do this once per file.

**Option 2: Terminal.**
```bash
cd ~/Downloads/SecureDocs-Demo/local      # wherever you unzipped it
./start.sh
```

The script starts the API (http://localhost:5081) and then the portal (**http://localhost:5080**), and opens the portal in
your browser. The first start takes 10–30 seconds while it creates the demo database (about 9,000 synthetic documents).
Later starts take a few seconds.

Closing the Terminal window doesn't stop the demo. It keeps running in the background until you stop it.

## Stop

Double-click **`Stop SecureDocs.command`**, or run `./stop.sh`.

To also **reset all demo data** (fresh database next start): `./stop.sh --reset`

## Try it

1. **Sign in:** pick any organization and click **Sign in**. The password is fake.
2. **Dashboard:** volumes, production pipeline, QA pass rate and recent batches.
3. **Submit via API:** sends a batch of synthetic records through the REST API.
4. **Secure file transfer:** **Generate & transfer** a CSV. After about 5 seconds, **Refresh** shows it *Processed* with a batch number.
5. **Production & QA:** **Run production now** to print and QA-scan queued documents.
6. **Fault Control** (bottom-right button):
   - **Slow SQL** on, then run a **Document search**. It takes about 2.5 seconds.
   - **Database errors** on, then reload the **Dashboard**. You'll see a red SQL error banner.
   - **Portal errors** on. About half of page loads crash to the error page.
   - **QA camera mismatches** on, then **Run production now**. More pieces show *Mismatch*.
   - **Send a burst of 30 requests** or **Auto traffic** simulates portal users.
   - **Reset all faults** when you're done.

## What's in this folder

```
Start SecureDocs.command   double-click to start
Stop SecureDocs.command    double-click to stop
start.sh / stop.sh         the same from Terminal (stop.sh --reset wipes demo data)
run-from-source.sh         build and run from ../source with an installed .NET 10 SDK (fallback)
bin/mac-arm64/             Apple Silicon: bundled .NET runtime + API + portal
bin/mac-x64/               Intel: bundled .NET runtime + API + portal
data/                      created on first run: SQLite database and uploaded files
logs/                      created on first run: api.log, portal.log
```

The script picks the right `bin/` folder for your Mac automatically.

## Troubleshooting

| Problem | Fix |
|---|---|
| "Port 5080 (or 5081) is already in use" | SecureDocs is probably already running: run `./stop.sh`. Otherwise another program uses that port. Find it with `lsof -nP -iTCP:5080 -sTCP:LISTEN`. |
| "dotnet" can't be opened / is damaged / was blocked | Run `xattr -dr com.apple.quarantine .` inside this folder (the script normally does this), then try again. If your company's security software still blocks it, use the fallback below. |
| Portal shows "The SecureDocs API is unreachable" | The API stopped. Check `logs/api.log`, then `./stop.sh` and `./start.sh`. |
| Want a fresh start | `./stop.sh --reset` then `./start.sh`. |
| Anything else | Look at `logs/api.log` and `logs/portal.log`. |

### Fallback: run from source with the .NET SDK
If company security blocks the bundled runtime but lets you install the official .NET SDK (often available from your
company's software portal / Self Service, or from https://dotnet.microsoft.com/download/dotnet/10.0):
```bash
./run-from-source.sh     # builds ../source and starts both apps the same way
./stop.sh                # stops them
```
