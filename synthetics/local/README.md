# Run the monitor scripts locally

Imitates the New Relic synthetics runtime (`$http`, `$webDriver`, `$selenium`, `$secure`, `$env`) so you can test
script changes before uploading them. It's close to New Relic's runtime but not identical, so always do a final check
in New Relic (monitor editor → **Validate**, or a test run).

```bash
cd synthetics/local
npm install                                   # once
node run.js ../client-api-intake.api.js
node run.js ../file-transfer.api.js
node run.js ../portal-journey.browser.js      # add --headed to watch Chrome
```
Needs Node.js 18+ and Google Chrome. Selenium downloads the matching ChromeDriver itself.
Secure credentials: `export SECURE_SECUREDOCS_API_KEY=...` makes `$secure.SECUREDOCS_API_KEY` available.
A failed browser run saves a screenshot next to `run.js`.
