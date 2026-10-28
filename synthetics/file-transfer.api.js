/**
 * SecureDocs - Secure file transfer          (New Relic Scripted API monitor, Node.js runtime)
 *
 * Business flow: a client drops a member data file (CSV) for processing, the way most healthcare clients
 * send their monthly or annual mailing files. The file must be received, picked up by the ingestion worker,
 * parsed and turned into a production batch.
 *
 * Checks:
 *   1. The file is accepted                     POST /api/ftp/upload            -> 202
 *   2. It is picked up and processed in time    GET  /api/clients/{code}/ftp-transfers  (polls until "Processed")
 *   3. It produced a batch with every record    (recordCount, batchId)
 *
 * NOTE: in production, store the client API key as a secure credential and use $secure.SECUREDOCS_API_KEY.
 */
const assert = require('assert');

const API_URL = 'https://securedocs-api-REPLACE-ME.azurewebsites.net';
const CLIENT_CODE = 'EBA';              // Evergreen Benefits Administrators (Standard tier)
const PROCESSING_BUDGET_MS = 45000;     // file must be processed within this time
const POLL_EVERY_MS = 3000;

function call(method, options) {
  return new Promise((resolve, reject) => {
    $http[method](options, (err, response, body) => {
      if (err) return reject(err);
      try {
        resolve({ status: response.statusCode, body: typeof body === 'string' && body ? JSON.parse(body) : body });
      } catch (e) {
        reject(new Error(`${options.url} returned non-JSON (${response.statusCode}): ${String(body).slice(0, 200)}`));
      }
    });
  });
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Client credentials (demo shortcut; use $secure in production)
const clients = await call('get', { url: `${API_URL}/api/clients` });
const client = clients.body.find((c) => c.code === CLIENT_CODE);
assert.ok(client, `Client ${CLIENT_CODE} not found`);

// 1. Upload a small synthetic CSV as multipart/form-data
const rows = 5;
const csv = ['DocumentType,Channel,RecipientName,AddressLine1,City,State,PostalCode,MemberId,Email,Phone,Amount']
  .concat(Array.from({ length: rows }, (_, i) =>
    `EOB,Print,Synthetic Monitor ${i + 1},200 Test St,Denver,CO,80202,MSYN${String(i).padStart(5, '0')},,,${(i + 1) * 10}`))
  .join('\r\n') + '\r\n';
const fileName = `SYNTH_${CLIENT_CODE}_${Date.now()}.csv`;
const boundary = `----securedocs${Date.now()}`;
const multipart =
  `--${boundary}\r\nContent-Disposition: form-data; name="clientCode"\r\n\r\n${CLIENT_CODE}\r\n` +
  `--${boundary}\r\nContent-Disposition: form-data; name="file"; filename="${fileName}"\r\nContent-Type: text/csv\r\n\r\n${csv}` +
  `--${boundary}--\r\n`;

const uploadStart = Date.now();
const upload = await call('post', {
  url: `${API_URL}/api/ftp/upload`,
  headers: {
    'X-Api-Key': client.apiKey,
    'X-Ingestion-Source': 'Synthetic',
    'Content-Type': `multipart/form-data; boundary=${boundary}`,
  },
  body: multipart,
});
assert.strictEqual(upload.status, 202, `Upload returned ${upload.status}: ${JSON.stringify(upload.body)}`);
const transferId = upload.body.id;
console.log(`Uploaded ${fileName} as transfer ${transferId}`);

// 2. Wait for the ingestion worker to pick it up and process it
let transfer;
while (Date.now() - uploadStart < PROCESSING_BUDGET_MS) {
  await sleep(POLL_EVERY_MS);
  const list = await call('get', { url: `${API_URL}/api/clients/${CLIENT_CODE}/ftp-transfers` });
  transfer = list.body.find((t) => t.id === transferId);
  console.log(`Transfer ${transferId}: ${transfer ? transfer.status : 'not listed yet'}`);
  if (transfer && (transfer.status === 'Processed' || transfer.status === 'Failed')) break;
}
const elapsed = Date.now() - uploadStart;
assert.ok(transfer, `Transfer ${transferId} never appeared`);
assert.notStrictEqual(transfer.status, 'Failed', `Processing failed: ${transfer.error}`);
assert.strictEqual(transfer.status, 'Processed', `Not processed after ${elapsed} ms (status ${transfer.status})`);

// 3. Every record made it into a batch
assert.strictEqual(transfer.recordCount, rows, `Expected ${rows} records, got ${transfer.recordCount}`);
assert.ok(transfer.batchId, 'No batch was created');
console.log(`File processed into batch ${transfer.batchId} in ${elapsed} ms`);
