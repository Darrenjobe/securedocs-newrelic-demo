# SecureDocs demo: start here

SecureDocs is a demo client portal for a healthcare document printing and delivery business. Clients send member data
(through the API, file transfer or the portal), the data is validated and printed, emailed or texted, and QA cameras
check every printed piece. A **Fault Control** panel injects errors, slow traces and slow SQL queries for **New Relic APM**
demos. All data is synthetic.

There are **two separate ways** to run it:

| | **1. Local (your Mac)** | **2. Azure** |
|---|---|---|
| Folder | `local/` | `azure/` |
| Guide | `local/LOCAL-RUN.md` | `azure/DEPLOY-AZURE.md` |
| Needs | A Mac (Apple Silicon or Intel). Nothing to install. | An Azure subscription and a New Relic license key |
| Database | SQLite file on your Mac | Azure SQL |
| New Relic | No (the agent doesn't run on macOS) | Yes: APM, distributed tracing, databases, Errors Inbox |
| Time | 1 minute | 10–30 minutes |
| Use it for | Trying the app and Fault Control, rehearsing the demo | The real New Relic demo |

**Quick start (local):** open `local/` and double-click **Start SecureDocs.command**. The first time, right-click → **Open**.
Your browser opens http://localhost:5080.

The two options are independent: the local copy never connects to Azure, and the Azure copy doesn't need the local one.

**Running the demo:** `demo/DEMO-RUNBOOK.md` covers the pre-demo reset, four break-and-troubleshoot stories,
timings and the post-demo cost savings. `demo/INSTRUMENTATION-GUIDE.md` is a customer-facing overview of every
instrumentation step, so a customer can bring their own app to the same level.

`source/` has the full source code (C# / .NET 10), needed only if you want to change or rebuild the app.
