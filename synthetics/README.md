# SecureDocs synthetic monitors

Four New Relic synthetic monitors check the business journeys from outside, the way a client experiences them.
They feed the Pathpoint flows (see `../pathpoint/README.md`) and run whether or not anyone is using the portal.

| Monitor | Type | File | What it proves |
|---|---|---|---|
| SecureDocs - Portal journey (browser) | Scripted browser (Chrome) | `portal-journey.browser.js` | A client user can sign in, see the dashboard, submit a batch and search documents |
| SecureDocs - Client API intake | Scripted API (Node.js) | `client-api-intake.api.js` | A client system can submit a batch over the REST API and read it back, within time budgets |
| SecureDocs - Secure file transfer | Scripted API (Node.js) | `file-transfer.api.js` | A dropped CSV file is picked up, processed and turned into a batch within 45 s |
| SecureDocs - API health | Ping | (none) | `/health` responds and reports `Healthy` (the database is ready) |

**Test data is synthetic and tagged.** Monitor submissions carry `X-Ingestion-Source: Synthetic`, so they're recorded
with `source = 'Synthetic'` (file uploads with origin `Synthetic Upload`), and the business KPIs (dashboard and
Pathpoint) exclude them.

**Create them** with `scripts/newrelic_setup.py` (it puts your portal and API URLs into the scripts), or by hand:
New Relic → Synthetic monitoring → Create monitor → pick the type, paste the script, and change `API_URL` /
`PORTAL_URL` at the top. Runtime: Node.js (API) / Chrome (browser), using the newest runtime offered.

**Credentials:** the scripts read the demo client API key from `/api/clients`, an endpoint that only exists in this
demo. For a real system, store the key as a secure credential (`SECUREDOCS_API_KEY`) and use
`$secure.SECUREDOCS_API_KEY` instead.

**Test locally first.** `local/` contains a runner that imitates the New Relic runtime (`$http`, `$webDriver` on real
headless Chrome, `$selenium`, `$secure`, `$env`):
```bash
cd synthetics/local && npm install
npm run all            # or: node run.js ../portal-journey.browser.js --headed
```

**Verified in New Relic's runtime:** all three scripted monitors pass as on-demand runs in a New Relic account
(Node.js API runtime and Chrome browser runtime, from AWS_US_EAST_2). One lesson from that run: New Relic's browser
runtime has a ~10 s **implicit wait**, so checking that something is *absent* with `findElements` takes 10 s. The portal
script checks absence with a direct DOM query (`executeScript`) instead, and the local runner now uses the same implicit
wait so it catches this too.

**Tested locally:** all three scripts pass against the live Azure deployment with the local runner (API intake about 0.6 s,
file transfer processed in about 3.3 s, portal journey about 3.3 s). They also **fail correctly** when Fault Control injects
errors. For example, with the API error rate at 100%, the intake check fails with the `PhiValidationException` message,
and the browser journey fails within about 1 s with the dashboard's error text plus a screenshot. Do a final check in
New Relic after creating the monitors, because the local runner is close to New Relic's runtime but not identical.
