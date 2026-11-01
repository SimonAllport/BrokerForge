'use strict';
const { test, before, after, beforeEach } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const path = require('node:path');

const KEY = 'bf-test-dummy-key';
const INV_KEY = 'bf-test-dummy-inventory-key';
let child;
let base;

function start(env) {
  return new Promise((resolve, reject) => {
    const proc = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
      env: { ...process.env, PORT: '0', MOCK_SLOW_MS: '300', ...env },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let out = '';
    proc.stdout.on('data', (d) => {
      out += d;
      const m = /listening (\d+)/.exec(out);
      if (m) resolve({ proc, port: Number(m[1]) });
    });
    proc.on('exit', (code) => reject(Object.assign(new Error(`exited ${code}`), { code })));
  });
}

async function call(method, p, { body, headers = {}, key = KEY } = {}) {
  const h = { ...headers };
  if (key) h['x-api-key'] = key;
  if (body !== undefined) h['content-type'] = 'application/json';
  const res = await fetch(base + p, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual' });
  const text = await res.text();
  let json;
  try { json = text ? JSON.parse(text) : undefined; } catch { json = undefined; }
  return { status: res.status, headers: res.headers, json, text };
}

before(async () => {
  const s = await start({ MOCK_API_KEY: KEY, MOCK_INVENTORY_KEY: INV_KEY });
  child = s.proc;
  base = `http://127.0.0.1:${s.port}`;
});
after(() => child.kill());
beforeEach(async () => { assert.equal((await call('POST', '/__reset')).status, 204); });

test('exits when MOCK_API_KEY is missing', async () => {
  await assert.rejects(start({ MOCK_API_KEY: '' }), (e) => e.code !== 0);
});

test('rejects missing api key', async () => {
  assert.equal((await call('GET', '/v2/orders', { key: null })).status, 401);
  assert.equal((await call('GET', '/v2/orders', { key: 'wrong' })).status, 401);
});

test('cursor pages cover all 120 orders exactly once', async () => {
  const seen = new Set();
  let cursor = null;
  let calls = 0;
  do {
    const r = await call('GET', `/v2/orders?limit=50${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`);
    assert.equal(r.status, 200);
    for (const o of r.json.data) { assert.ok(!seen.has(o.id), `duplicate ${o.id}`); seen.add(o.id); }
    cursor = r.json.meta.nextCursor;
    calls++;
  } while (cursor);
  assert.equal(seen.size, 120);
  assert.equal(calls, 3);
});

test('limit over 100 is rejected', async () => {
  assert.equal((await call('GET', '/v2/orders?limit=101')).status, 400);
});

test('status csv filters', async () => {
  const r = await call('GET', '/v2/orders?status=open,shipped&limit=100');
  assert.ok(r.json.data.every((o) => ['open', 'shipped'].includes(o.state.code)));
  const all = await call('GET', '/v2/orders-all?status=open');
  assert.equal(all.json.data.length, 40);
  const cust = await call('GET', '/v2/orders-all?customer=CUST-01');
  assert.equal(cust.json.data.length, 12);
});

test('sort descending', async () => {
  const r = await call('GET', '/v2/orders?sort=-reference&limit=3');
  assert.deepEqual(r.json.data.map((o) => o.reference), ['REF-0120', 'REF-0119', 'REF-0118']);
  assert.equal((await call('GET', '/v2/orders?sort=bogus')).status, 400);
});

test('offset, page number and link variants page through all orders', async () => {
  const off = await call('GET', '/v2/orders-offset?offset=100&limit=50');
  assert.equal(off.json.items.length, 20);
  assert.equal(off.json.total, 120);
  const paged = await call('GET', '/v2/orders-paged?page=3&size=50');
  assert.equal(paged.json.results.length, 20);
  let url = '/v2/orders-link?limit=50';
  let n = 0;
  while (url) {
    const r = await call('GET', url);
    assert.ok(Array.isArray(r.json));
    n += r.json.length;
    const link = r.headers.get('link');
    url = link ? new URL(/<([^>]+)>;\s*rel="next"/.exec(link)[1]).pathname + new URL(/<([^>]+)>/.exec(link)[1]).search : null;
  }
  assert.equal(n, 120);
});

test('retrieve, update and delete', async () => {
  const got = await call('GET', '/v2/orders/ord_0001');
  assert.equal(got.json.reference, 'REF-0001');
  assert.equal((await call('GET', '/v2/orders/ord_9999')).json.error.detail.includes('ord_9999'), true);
  assert.equal((await call('PATCH', '/v2/orders/ord_0001', { body: { customer: { ref: 'CUST-99' } } })).status, 204);
  const after = await call('GET', '/v2/orders/ord_0001');
  assert.equal(after.json.customer.ref, 'CUST-99');
  assert.equal(after.json.reference, 'REF-0001');
  assert.equal((await call('DELETE', '/v2/orders/ord_0001')).status, 204);
  assert.equal((await call('GET', '/v2/orders/ord_0001')).status, 404);
  assert.equal((await call('DELETE', '/v2/orders/ord_0001')).status, 404);
});

test('idempotency key replays', async () => {
  const body = { customer: { ref: 'CUST-02' }, lines: [{ sku: 'A', qty: 1 }] };
  const a = await call('POST', '/v2/accounts/acc1/orders', { body, headers: { 'idempotency-key': 'k1' } });
  const b = await call('POST', '/v2/accounts/acc1/orders', { body, headers: { 'idempotency-key': 'k1' } });
  assert.equal(a.status, 201);
  assert.equal(a.json.id, b.json.id);
  const c = await call('POST', '/v2/accounts/acc1/orders', { body, headers: { 'idempotency-key': 'k2' } });
  assert.notEqual(c.json.id, a.json.id);
  assert.equal((await call('GET', `/v2/orders/${a.json.id}`)).json.customer.ref, 'CUST-02');
  const bad = await call('POST', '/v2/accounts/acc1/orders', { body: { lines: [] } });
  assert.equal(bad.status, 422);
  assert.match(bad.json.error.detail, /customer\.ref/);
});

test('create keeps a numeric totals.gross and defaults it to 0', async () => {
  const a = await call('POST', '/v2/accounts/acc1/orders', { body: { customer: { ref: 'CUST-04' }, totals: { gross: 12.5 } } });
  assert.equal((await call('GET', `/v2/orders/${a.json.id}`)).json.totals.gross, 12.5);
  const b = await call('POST', '/v2/accounts/acc1/orders', { body: { customer: { ref: 'CUST-04' }, totals: { gross: null } } });
  assert.equal((await call('GET', `/v2/orders/${b.json.id}`)).json.totals.gross, 0);
});

test('create takes state.code and defaults it to open', async () => {
  const a = await call('POST', '/v2/accounts/acc1/orders', { body: { customer: { ref: 'CUST-04' }, state: { code: 'shipped' } } });
  assert.equal((await call('GET', `/v2/orders/${a.json.id}`)).json.state.code, 'shipped');
  const b = await call('POST', '/v2/accounts/acc1/orders', { body: { customer: { ref: 'CUST-04' } } });
  assert.equal((await call('GET', `/v2/orders/${b.json.id}`)).json.state.code, 'open');
  const c = await call('POST', '/v2/accounts/acc1/orders', { body: { customer: { ref: 'CUST-04' }, state: { code: 'lost' } } });
  assert.equal(c.status, 422);
});

test('location create', async () => {
  const r = await call('POST', '/v2/accounts/acc1/orders-location', { body: { customer: { ref: 'CUST-03' } } });
  assert.equal(r.status, 201);
  assert.match(r.headers.get('location'), /^\/v2\/orders\/ord_\d{4}$/);
  assert.equal(r.text, '');
});

test('fault values', async () => {
  assert.equal((await call('GET', '/v2/orders?customer=__fail500')).status, 500);
  assert.equal((await call('GET', '/v2/orders?customer=__teapot')).status, 418);
  assert.equal((await call('GET', '/v2/orders?customer=__fail503once')).status, 503);
  assert.equal((await call('GET', '/v2/orders?customer=__fail503once')).status, 200);
  const t0 = Date.now();
  assert.equal((await call('GET', '/v2/orders?customer=__slow')).status, 200);
  assert.ok(Date.now() - t0 >= 280);
  assert.deepEqual((await call('GET', '/v2/broken')).json, { notData: [] });
  assert.equal((await call('GET', '/v2/soft-error')).json.ok, false);
});

test('echo returns the request as received without the api key', async () => {
  const r = await call('POST', '/v2/echo/a%2Fb?x=1&y=two', { body: { hello: 'world' }, headers: { 'x-custom': 'v' } });
  assert.equal(r.json.method, 'POST');
  assert.equal(r.json.path, '/v2/echo/a%2Fb');
  assert.deepEqual(r.json.query, { x: '1', y: 'two' });
  assert.equal(r.json.rawQuery, 'x=1&y=two');
  assert.equal(r.json.headers['x-custom'], 'v');
  assert.equal(r.json.headers['x-api-key'], undefined);
  assert.equal(r.json.contentType, 'application/json');
  assert.equal(r.json.body, '{"hello":"world"}');
  assert.equal(r.json.id, 'a/b');
});

test('last echo is kept for contract tests and echo carries an empty rows list', async () => {
  assert.equal((await call('GET', '/__echo/last')).status, 404);
  const r = await call('PUT', '/v2/echo/x?q=1', { body: { a: 1 } });
  assert.deepEqual(r.json.rows, []);
  const last = await call('GET', '/__echo/last');
  assert.equal(last.json.method, 'PUT');
  assert.equal(last.json.rawQuery, 'q=1');
  await call('POST', '/__reset');
  assert.equal((await call('GET', '/__echo/last')).status, 404);
});

test('reset restores seed', async () => {
  await call('DELETE', '/v2/orders/ord_0002');
  await call('POST', '/v2/accounts/a/orders', { body: { customer: { ref: 'X' } } });
  await call('POST', '/__reset');
  assert.equal((await call('GET', '/v2/orders/ord_0002')).status, 200);
  assert.equal((await call('GET', '/v2/orders-all')).json.data.length, 120);
});

// ---- Inventory API (/inv/v1, api_key query parameter) ----

const inv = (method, p, body) => call(method, `/inv/v1${p}${p.includes('?') ? '&' : '?'}api_key=${INV_KEY}`, { body, key: null });

test('inventory requires api_key query', async () => {
  assert.equal((await call('GET', '/inv/v1/items', { key: null })).status, 401);
  assert.equal((await call('GET', '/inv/v1/items?api_key=wrong', { key: null })).status, 401);
  // The Orders header key is not an inventory credential.
  assert.equal((await call('GET', '/inv/v1/items')).status, 401);
  assert.equal((await inv('GET', '/items')).status, 200);
});

test('inventory offset paging with total covers all items once', async () => {
  const seen = new Set();
  let offset = 0;
  let total;
  for (;;) {
    const r = await inv('GET', `/items?limit=20&offset=${offset}`);
    assert.equal(r.status, 200);
    total = r.json.total;
    for (const item of r.json.items) { assert.ok(!seen.has(item.id)); seen.add(item.id); }
    offset += 20;
    if (offset >= total) break;
  }
  assert.equal(total, 45);
  assert.equal(seen.size, 45);
  assert.equal((await inv('GET', '/items?limit=51')).status, 400);
  const discontinued = await inv('GET', '/items?state=discontinued&limit=50');
  assert.equal(discontinued.json.total, 9);
  assert.ok(discontinued.json.items.every((x) => x.state === 'discontinued'));
});

test('inventory retrieve, create, update, delete', async () => {
  const one = await inv('GET', '/items/7');
  assert.deepEqual(Object.keys(one.json).sort(), ['id', 'name', 'onHand', 'price', 'sku', 'state', 'updatedAt']);
  assert.equal(one.json.id, 7);
  assert.equal((await inv('GET', '/items/999')).status, 404);

  const created = await inv('POST', '/items', { name: 'Widget', sku: 'W-1', onHand: 3, price: 2.5, state: 'active' });
  assert.equal(created.status, 201);
  assert.equal(created.json.id, 46);
  assert.equal((await inv('POST', '/items', { sku: 'no name' })).status, 422);
  assert.equal((await inv('POST', '/items', { name: 'x', state: 'lost' })).status, 422);

  assert.equal((await inv('PATCH', '/items/46', { price: 3.75, state: 'discontinued' })).status, 204);
  const after = await inv('GET', '/items/46');
  assert.equal(after.json.price, 3.75);
  assert.equal(after.json.state, 'discontinued');
  assert.equal(after.json.name, 'Widget');

  assert.equal((await inv('DELETE', '/items/46')).status, 204);
  assert.equal((await inv('GET', '/items/46')).status, 404);
});

test('inventory adjust returns onHand and adjustedAt', async () => {
  const before = (await inv('GET', '/items/3')).json.onHand;
  const r = await inv('POST', '/items/3/adjust', { delta: 5, reason: 'count' });
  assert.equal(r.status, 200);
  assert.equal(r.json.onHand, before + 5);
  assert.match(r.json.adjustedAt, /^\d{4}-\d\d-\d\dT/);
  assert.equal((await inv('POST', '/items/3/adjust', { delta: -1000 })).status, 422);
  assert.equal((await inv('POST', '/items/3/adjust', { reason: 'no delta' })).status, 422);
  assert.equal((await inv('POST', '/items/999/adjust', { delta: 1 })).status, 404);
});

test('reset restores the inventory seed', async () => {
  await inv('DELETE', '/items/1');
  assert.equal((await call('POST', '/__reset')).status, 204);
  assert.equal((await inv('GET', '/items/1')).status, 200);
});
