/**
 * SecureDocs - Client API intake            (New Relic Scripted API monitor, Node.js runtime)
 *
 * Business flow: a health-plan client's system submits a batch of member records over the REST API,
 * and then checks its status. This is the machine-to-machine path a client's IT team integrates with.
 *
 * Checks:
 *   1. API is up and the database is ready            GET  /health
 *   2. Client credentials work                        GET  /api/clients   (demo only, see note below)
 *   3. A batch is accepted, validated, address-checked POST /api/batches   -> 201, all records accepted
 *   4. The batch can be read back with its documents  GET  /api/batches/{id}
 *   5. Each step and the whole journey stay within time budgets
 *
 * Test data is synthetic, and the batch is tagged X-Ingestion-Source: Synthetic, so business KPIs can exclude it.
 * NOTE: in production, store the client API key as a secure credential and use $secure.SECUREDOCS_API_KEY
 *       instead of reading it from /api/clients (an endpoint that only exists in this demo).
 */
const assert = require('assert');

const API_URL = 'https://securedocs-api-REPLACE-ME.azurewebsites.net';
const CLIENT_CODE = 'PCHP';           // Pacific Coast Health Plan (Enterprise tier)
const STEP_BUDGET_MS = 8000;          // max time for any single call
const JOURNEY_BUDGET_MS = 20000;      // max time for the whole check

function call(method, options) {
  const started = Date.now();
  return new Promise((resolve, reject) => {
    $http[method](options, (err, response, body) => {
      if (err) return reject(err);
      const ms = Date.now() - started;
      console.log(`${method.toUpperCase()} ${options.url} -> ${response.statusCode} in ${ms} ms`);
      if (ms >= STEP_BUDGET_MS) return reject(new Error(`${options.url} took ${ms} ms (budget ${STEP_BUDGET_MS} ms)`));
      try {
        resolve({ status: response.statusCode, body: typeof body === 'string' && body ? JSON.parse(body) : body });
      } catch (e) {
        reject(new Error(`${options.url} returned non-JSON (${response.statusCode}): ${String(body).slice(0, 200)}`));
      }
    });
  });
}

const journeyStart = Date.now();

// 1. Health
const health = await call('get', { url: `${API_URL}/health` });
assert.strictEqual(health.status, 200, `API health returned ${health.status}: ${JSON.stringify(health.body)}`);
assert.strictEqual(health.body.status, 'Healthy', `API is ${health.body.status}`);

// 2. Client credentials (demo shortcut; use $secure in production)
const clients = await call('get', { url: `${API_URL}/api/clients` });
assert.strictEqual(clients.status, 200);
const client = clients.body.find((c) => c.code === CLIENT_CODE);
assert.ok(client, `Client ${CLIENT_CODE} not found`);

// 3. Submit a small synthetic batch
const reference = `SYNTH-API-${Date.now()}`;
const documents = [
  { documentType: 'EOB', channel: 'Print', recipientName: 'Synthetic Monitor', addressLine1: '100 Test Ave',
    city: 'Oakland', state: 'CA', postalCode: '94612', memberId: 'M00000001', amount: 12.34 },
  { documentType: 'Check', channel: 'Print', recipientName: 'Synthetic Monitor', addressLine1: '100 Test Ave',
    city: 'Oakland', state: 'CA', postalCode: '94612', memberId: 'M00000002', amount: 56.78 },
  { documentType: 'MedicalStatement', channel: 'Email', recipientName: 'Synthetic Monitor',
    memberId: 'M00000003', email: 'synthetic.monitor@example.com', amount: 9.99 },
];
const submit = await call('post', {
  url: `${API_URL}/api/batches`,
  headers: { 'X-Api-Key': client.apiKey, 'X-Ingestion-Source': 'Synthetic', 'Content-Type': 'application/json' },
  body: JSON.stringify({ clientCode: CLIENT_CODE, reference, documents }),
});
assert.strictEqual(submit.status, 201, `Batch submit returned ${submit.status}: ${JSON.stringify(submit.body)}`);
assert.strictEqual(submit.body.accepted, documents.length, `Only ${submit.body.accepted}/${documents.length} records accepted`);
assert.strictEqual(submit.body.rejected, 0, `Records rejected: ${JSON.stringify(submit.body.warnings)}`);
console.log(`Batch ${submit.body.batchId} (${reference}) accepted`);

// 4. Read the batch back
const batch = await call('get', { url: `${API_URL}/api/batches/${submit.body.batchId}` });
assert.strictEqual(batch.status, 200);
assert.strictEqual(batch.body.documents.length, documents.length, 'Batch is missing documents');

// 5. Whole-journey budget
const total = Date.now() - journeyStart;
console.log(`Client API intake journey completed in ${total} ms`);
assert.ok(total < JOURNEY_BUDGET_MS, `Journey took ${total} ms (budget ${JOURNEY_BUDGET_MS} ms)`);
