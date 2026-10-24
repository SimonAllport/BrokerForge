// Quick self-check of the mock: start on a random port, exercise each route, exit non-zero on failure.
'use strict';

const assert = require('node:assert/strict');
const { server } = require('./server');

const KEY = process.env.ORDERS_API_KEY || 'dev-key';

server.listen(0, async () => {
  const base = `http://localhost:${server.address().port}`;
  const call = (path, init = {}) => fetch(base + path, { ...init, headers: { 'X-Api-Key': KEY, 'Content-Type': 'application/json', ...(init.headers || {}) } });

  try {
    assert.equal((await fetch(base + '/v2/orders')).status, 401, 'missing key is 401');

    const page1 = await (await call('/v2/orders?status=open&limit=3')).json();
    assert.equal(page1.data.length, 3);
    assert.ok(page1.data.every((o) => o.state.code === 'open'));
    assert.ok(page1.meta.nextCursor);

    const page2 = await (await call(`/v2/orders?status=open&limit=3&cursor=${page1.meta.nextCursor}`)).json();
    assert.notEqual(page2.data[0].id, page1.data[0].id);

    assert.equal((await call('/v2/orders/1')).status, 200);
    assert.equal((await call('/v2/orders/999')).status, 404);

    const created = await call('/v2/accounts/acc-001/orders', { method: 'POST', headers: { 'Idempotency-Key': 'k1' }, body: JSON.stringify({ customer: { ref: 'CUST-9' } }) });
    assert.equal(created.status, 201);
    const again = await call('/v2/accounts/acc-001/orders', { method: 'POST', headers: { 'Idempotency-Key': 'k1' }, body: JSON.stringify({ customer: { ref: 'CUST-9' } }) });
    assert.equal((await created.json()).id, (await again.json()).id, 'idempotency key replays');

    assert.equal((await call('/v2/orders/6', { method: 'PATCH', body: JSON.stringify({ customer: { ref: 'CUST-2' } }) })).status, 200);
    assert.equal((await call('/v2/orders/6/submit', { method: 'POST', body: '{}' })).status, 200);
    assert.equal((await call('/v2/orders/6/submit', { method: 'POST', body: '{}' })).status, 409);
    assert.equal((await call('/v2/orders/6', { method: 'DELETE' })).status, 204);
    assert.equal((await call('/v2/fail/500')).status, 500);

    console.log('mock-api smoke: ok');
    server.close();
  } catch (err) {
    console.error('mock-api smoke: FAILED', err);
    server.close();
    process.exit(1);
  }
});
