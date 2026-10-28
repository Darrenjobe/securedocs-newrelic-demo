/**
 * SecureDocs - Portal journey                (New Relic Scripted Browser monitor, Chrome runtime)
 *
 * Business flow: a client operations user signs in to the portal, checks the dashboard,
 * submits a batch of member records, and searches for a document. This is what a client sees,
 * so it covers the page, the portal tier, the API and Azure SQL.
 *
 * Checks (each step has a time budget, with headroom for the hosted browser's overhead):
 *   1. Sign-in page loads and the client can sign in
 *   2. Dashboard shows KPIs and no error banner
 *   3. A batch can be submitted from the portal
 *   4. Document search finds documents for a common surname
 */
const assert = require('assert');
const By = $selenium.By;
const until = $selenium.until;

const PORTAL_URL = 'https://securedocs-portal-REPLACE-ME.azurewebsites.net';
const CLIENT_CODE = 'SMG';              // Summit Medical Group (Enterprise tier)
const TIMEOUT_MS = 20000;

async function step(name, budgetMs, fn) {
  const started = Date.now();
  await fn();
  const ms = Date.now() - started;
  console.log(`${name}: ${ms} ms`);
  assert.ok(ms < budgetMs, `${name} took ${ms} ms (budget ${budgetMs} ms)`);
}

// Checks for an error banner with a direct DOM query. (findElements would sit through New Relic's implicit wait,
// about 10 s, every time the banner is correctly absent.)
async function assertNoErrorBanner(where) {
  const errors = await $webDriver.executeScript(
    "return Array.from(document.querySelectorAll('.alert-danger, .error-panel')).map(e => e.innerText.trim());");
  if (errors.length > 0) assert.fail(`${where} shows an error: ${errors[0]}`);
}

// 1. Sign in
await step('Sign in', 30000, async () => {
  await $webDriver.get(`${PORTAL_URL}/Login`);
  await $webDriver.wait(until.elementLocated(By.id('SelectedClient')), TIMEOUT_MS);
  await $webDriver.findElement(By.css(`#SelectedClient option[value="${CLIENT_CODE}"]`)).click();
  const user = await $webDriver.findElement(By.id('UserName'));
  await user.clear();
  await user.sendKeys('synthetic.monitor');
  await $webDriver.findElement(By.css('form button[type="submit"]')).click();
  // Wait for either the dashboard or an error, so a failure reports the real error text straight away.
  await $webDriver.wait(until.elementLocated(By.css('.kpi .value, .alert-danger, .error-panel')), TIMEOUT_MS);
  await assertNoErrorBanner('Dashboard after sign-in');
});

// 2. Dashboard
await step('Dashboard', 15000, async () => {
  await assertNoErrorBanner('Dashboard');
  const kpis = await $webDriver.executeScript("return document.querySelectorAll('.kpi .value').length;");
  assert.ok(kpis >= 4, `Expected 4 KPI tiles, found ${kpis}`);
});

// 3. Submit a batch from the portal
await step('Submit batch', 25000, async () => {
  await $webDriver.get(`${PORTAL_URL}/Submit`);
  const count = await $webDriver.wait(until.elementLocated(By.id('Count')), TIMEOUT_MS);
  await count.clear();
  await count.sendKeys('3');
  const reference = await $webDriver.findElement(By.id('Reference'));
  await reference.sendKeys(`SYNTH-PORTAL-${Date.now()}`);
  await $webDriver.findElement(By.css('form button[type="submit"]')).click();
  await $webDriver.wait(until.elementLocated(By.css('.alert-success, .alert-danger')), TIMEOUT_MS);
  await assertNoErrorBanner('Batch submission');
  const text = await $webDriver.findElement(By.css('.alert-success')).getText();
  assert.ok(text.includes('accepted'), `Unexpected result: ${text}`);
});

// 4. Search
await step('Document search', 25000, async () => {
  await $webDriver.get(`${PORTAL_URL}/Documents?q=Garcia`);
  await $webDriver.wait(until.elementLocated(By.css('.panel h2, .alert-danger, .error-panel')), TIMEOUT_MS);
  await assertNoErrorBanner('Document search');
  const heading = await $webDriver.findElement(By.css('.panel h2')).getText();
  const found = parseInt((heading.match(/(\d+) result/) || [])[1] || '0', 10);
  assert.ok(found > 0, `Search for a common surname returned no documents: ${heading}`);
});

console.log('Portal journey completed');
