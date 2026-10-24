// Orders mock API for BrokerForge contract tests (CLAUDE.md, tools/mock-api).
// Zero dependencies. Integer keys, so the reversible key strategy is exercised.
// Auth: X-Api-Key must equal ORDERS_API_KEY (default "dev-key"); a dummy value, never a real secret.

'use strict';

const http = require('node:http');
const { randomUUID } = require('node:crypto');

const PORT = Number(process.env.PORT || 4010);
const API_KEY = process.env.ORDERS_API_KEY || 'dev-key';

function seed() {
  const orders = new Map();
  const statuses = ['open', 'shipped', 'cancelled'];
  for (let i = 1; i <= 25; i++) {
    orders.set(i, {
      id: i,
      reference: `ORD-${String(i).padStart(4, '0')}`,
      state: { code: statuses[i % 3] },
      customer: { ref: `CUST-${(i % 5) + 1}` },
      placedAt: new Date(Date.UTC(2026, 0, i, 9, 0, 0)).toISOString(),
      lines: [{ sku: `SKU-${i}`, qty: (i % 4) + 1 }],
    });
  }
  return orders;
}

const state = { orders: seed(), nextId: 26, idempotency: new Map() };

function send(res, status, body, headers = {}) {
  const payload = body === undefined ? '' : JSON.stringify(body);
  res.writeHead(status, { 'Content-Type': 'application/json', ...headers });
  res.end(payload);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    req.on('data', (chunk) => { data += chunk; });
    req.on('end', () => {
      if (!data) return resolve(undefined);
      try { resolve(JSON.parse(data)); } catch { reject(new Error('invalid json')); }
    });
    req.on('error', reject);
  });
}

function listOrders(url, res) {
  let rows = [...state.orders.values()];

  const status = url.searchParams.get('status');
  if (status) {
    const wanted = new Set(status.split(','));
    rows = rows.filter((o) => wanted.has(o.state.code));
  }

  const customer = url.searchParams.get('customer');
  if (customer) rows = rows.filter((o) => o.customer.ref === customer);

  const sort = url.searchParams.get('sort');
  if (sort) {
    const desc = sort.startsWith('-');
    const field = desc ? sort.slice(1) : sort;
    if (!['reference', 'placedAt'].includes(field)) {
      return send(res, 400, { error: { detail: `cannot sort by ${field}` } });
    }
    rows.sort((a, b) => (a[field] < b[field] ? -1 : a[field] > b[field] ? 1 : 0) * (desc ? -1 : 1));
  } else {
    rows.sort((a, b) => a.id - b.id);
  }

  const limit = Math.min(Number(url.searchParams.get('limit') || 10), 100);
  const offset = url.searchParams.get('cursor') ? Number(Buffer.from(url.searchParams.get('cursor'), 'base64url').toString()) : 0;
  const page = rows.slice(offset, offset + limit);
  const next = offset + limit < rows.length ? Buffer.from(String(offset + limit)).toString('base64url') : null;

  return send(res, 200, { data: page, meta: { nextCursor: next, total: rows.length } });
}

async function route(req, res) {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  const parts = url.pathname.split('/').filter(Boolean);

  if (url.pathname === '/health') return send(res, 200, { status: 'ok' });

  if (req.headers['x-api-key'] !== API_KEY) {
    return send(res, 401, { error: { detail: 'missing or wrong X-Api-Key' } });
  }

  // Test hooks for error-handling contract tests (HLD 11).
  if (url.pathname === '/v2/fail/500') return send(res, 500, { error: { detail: 'deliberate failure' } });
  if (url.pathname === '/v2/fail/malformed') return send(res, 200, { unexpected: true });
  if (url.pathname === '/v2/slow') {
    const ms = Number(url.searchParams.get('ms') || 5000);
    return setTimeout(() => send(res, 200, { data: [], meta: { nextCursor: null } }), ms);
  }
  if (url.pathname === '/__reset' && req.method === 'POST') {
    state.orders = seed();
    state.nextId = 26;
    state.idempotency.clear();
    return send(res, 204);
  }

  if (parts[0] !== 'v2') return send(res, 404, { error: { detail: 'not found' } });

  // GET /v2/orders
  if (req.method === 'GET' && parts.length === 2 && parts[1] === 'orders') return listOrders(url, res);

  // POST /v2/accounts/:accountId/orders
  if (req.method === 'POST' && parts.length === 4 && parts[1] === 'accounts' && parts[3] === 'orders') {
    const key = req.headers['idempotency-key'];
    if (key && state.idempotency.has(key)) return send(res, 201, state.orders.get(state.idempotency.get(key)));

    let body;
    try { body = await readBody(req); } catch { return send(res, 422, { error: { detail: 'body is not JSON' } }); }
    if (!body || !body.customer || !body.customer.ref) return send(res, 422, { error: { detail: 'customer.ref is required' } });

    const id = state.nextId++;
    const order = {
      id,
      reference: `ORD-${String(id).padStart(4, '0')}`,
      state: { code: 'open' },
      customer: { ref: body.customer.ref },
      placedAt: new Date().toISOString(),
      lines: Array.isArray(body.lines) ? body.lines : [],
      placedBy: body.placedBy || null,
      account: parts[2],
    };
    state.orders.set(id, order);
    if (key) state.idempotency.set(key, id);
    return send(res, 201, order, { Location: `/v2/orders/${id}` });
  }

  // /v2/orders/:id and /v2/orders/:id/submit
  if (parts[1] === 'orders' && parts.length >= 3) {
    const id = Number(parts[2]);
    const order = state.orders.get(id);
    if (!order) return send(res, 404, { error: { detail: `order ${parts[2]} not found` } });

    if (parts.length === 3 && req.method === 'GET') return send(res, 200, order);

    if (parts.length === 3 && req.method === 'PATCH') {
      let body;
      try { body = await readBody(req); } catch { return send(res, 422, { error: { detail: 'body is not JSON' } }); }
      if (order.state.code === 'cancelled') return send(res, 409, { error: { detail: 'cancelled orders cannot change' } });
      if (body && body.customer && body.customer.ref) order.customer.ref = body.customer.ref;
      if (body && Array.isArray(body.lines)) order.lines = body.lines;
      return send(res, 200, order);
    }

    if (parts.length === 3 && req.method === 'DELETE') {
      state.orders.delete(id);
      return send(res, 204);
    }

    if (parts.length === 4 && parts[3] === 'submit' && req.method === 'POST') {
      if (order.state.code !== 'open') return send(res, 409, { error: { detail: `order is ${order.state.code}` } });
      order.state.code = 'submitted';
      order.submittedAt = new Date().toISOString();
      return send(res, 200, order);
    }
  }

  return send(res, 404, { error: { detail: 'not found' } });
}

const server = http.createServer((req, res) => {
  const correlation = req.headers['x-correlation'] || randomUUID();
  res.setHeader('X-Correlation', correlation);
  route(req, res).catch((err) => send(res, 500, { error: { detail: err.message } }));
});

if (require.main === module) {
  server.listen(PORT, () => console.log(`Orders mock API on http://localhost:${PORT}`));
}

module.exports = { server };
