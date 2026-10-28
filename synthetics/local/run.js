#!/usr/bin/env node
/**
 * Runs a New Relic synthetic monitor script locally, imitating the New Relic runtime:
 *   $http       request-style HTTP client (callback: err, response, body)  - scripted API & browser monitors
 *   $webDriver  Selenium WebDriver on Chrome (headless unless --headed)   - scripted browser monitors
 *   $selenium   the selenium-webdriver module (By, until, Key, ...)
 *   $secure     secure credentials, read from environment variables SECURE_<NAME>
 *   $env        { LOCATION, MONITOR_ID, JOB_ID, ... } stubs
 * Like New Relic, the WebDriver has a 10 s implicit wait.
 * This is close to, but not identical to, New Relic's runtime. Always run a final check in New Relic.
 *
 * Usage:  node run.js ../client-api-intake.api.js
 *         node run.js ../portal-journey.browser.js [--headed]
 */
const fs = require('fs');
const path = require('path');

const file = process.argv[2];
if (!file) { console.error('Usage: node run.js <script.js> [--headed]'); process.exit(2); }
const headed = process.argv.includes('--headed');
const isBrowser = /browser/i.test(path.basename(file));
const source = fs.readFileSync(file, 'utf8');

const $http = require('request');
const $secure = new Proxy({}, {
  get: (_, name) => {
    const v = process.env[`SECURE_${String(name)}`];
    if (v === undefined) throw new Error(`Secure credential ${String(name)} not set (export SECURE_${String(name)}=...)`);
    return v;
  },
});
const $env = { LOCATION: 'LOCAL', MONITOR_ID: 'local', JOB_ID: `local-${Date.now()}`, USER_DEFINED_VARIABLES: {} };

(async () => {
  let $selenium, $webDriver;
  if (isBrowser) {
    $selenium = require('selenium-webdriver');
    const chrome = require('selenium-webdriver/chrome');
    const options = new chrome.Options().addArguments('--window-size=1366,1000');
    if (!headed) options.addArguments('--headless=new');
    $webDriver = await new $selenium.Builder().forBrowser('chrome').setChromeOptions(options).build();
    // New Relic's browser runtime uses an implicit wait; mirror it so absence checks behave the same.
    await $webDriver.manage().setTimeouts({ implicit: 10000 });
  }
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  const run = new AsyncFunction('$http', '$webDriver', '$selenium', '$secure', '$env', 'require', 'console', source);
  const started = Date.now();
  let failed = null;
  try {
    await run($http, $webDriver, $selenium, $secure, $env, require, console);
  } catch (e) {
    failed = e;
  }
  if (failed && $webDriver) {
    try {
      const png = await $webDriver.takeScreenshot();
      const shot = path.join(__dirname, `failure-${Date.now()}.png`);
      fs.writeFileSync(shot, png, 'base64');
      console.log(`Screenshot: ${shot}`);
    } catch { /* ignore */ }
  }
  if ($webDriver) await $webDriver.quit();
  const ms = Date.now() - started;
  if (failed) {
    console.error(`\nRESULT: FAILED after ${ms} ms\n${failed.stack || failed}`);
    process.exit(1);
  }
  console.log(`\nRESULT: SUCCESS in ${ms} ms`);
})().catch((e) => { console.error(e); process.exit(1); });

// Errors thrown inside callbacks (as in New Relic) fail the check.
process.on('uncaughtException', (e) => { console.error(`\nRESULT: FAILED (uncaught)\n${e.stack || e}`); process.exit(1); });
