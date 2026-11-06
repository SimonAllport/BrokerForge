// Done criterion 6 (plan 5): a non-administrator reads a generated table through its generated role only.
// Signs in as the test reader (TEST_READER_UPN in ~/.brokerforge/dev-ids.env; its password is read from the dev Key Vault,
// secret test-reader-password, and typed, never printed) in profile `reader`, then, from that browser session, calls the
// Dataverse Web API: a list of the table, and an admin Custom API, which must be refused.
// Usage: node check-reader.mjs <table entity set> <connection name>   e.g. node check-reader.mjs ext_items InventoryAPI
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { chromium } from 'playwright';
import { here, org, profiles, sanitise } from './env.mjs';

const [entitySet = 'ext_items', connection = 'InventoryAPI'] = process.argv.slice(2);
const ids = Object.fromEntries(readFileSync(join(homedir(), '.brokerforge', 'dev-ids.env'), 'utf8').split('\n')
  .map(l => /^([A-Z_]+)=["']?([^"'\n]*)/.exec(l)).filter(Boolean).map(m => [m[1], m[2]]));
const password = () => execFileSync('az', ['keyvault', 'secret', 'show', '--vault-name', ids.VAULT, '--name', 'test-reader-password', '--query', 'value', '-o', 'tsv'], { encoding: 'utf8' }).trim();

const context = await chromium.launchPersistentContext(join(profiles, 'reader'), { headless: true });
const page = context.pages()[0] ?? await context.newPage();
await page.goto(org);
const orgHost = new URL(org).host;
const until = Date.now() + 180_000;
while (new URL(page.url()).host !== orgHost) {
  if (Date.now() > until) {
    await page.screenshot({ path: join(here, 'out', 'reader-signin.png') });
    throw new Error(`sign-in did not reach Dataverse; the page says: ${(await page.locator('body').innerText()).replace(/\s+/g, ' ').slice(0, 300)}`);
  }
  const email = page.locator('input[type=email]');
  const pwd = page.locator('input[type=password]');
  if (await email.isVisible().catch(() => false)) { await email.fill(ids.TEST_READER_UPN); await page.locator('#idSIButton9').click(); }
  else if (await pwd.isVisible().catch(() => false)) { await pwd.fill(password()); await page.locator('#idSIButton9').click(); }
  else if (await page.getByText(ids.TEST_READER_UPN).first().isVisible().catch(() => false)) await page.getByText(ids.TEST_READER_UPN).first().click();
  else for (const name of [/Skip for now/i, /^No$/, /^Yes$/, /Ask later/i])
    if (await page.getByRole('button', { name }).or(page.getByRole('link', { name })).first().isVisible().catch(() => false)) {
      await page.getByRole('button', { name }).or(page.getByRole('link', { name })).first().click();
      break;
    }
  await page.waitForTimeout(2000);
}
await page.waitForTimeout(5000);

const result = await page.evaluate(async ([entitySet, connection]) => {
  const api = '/api/data/v9.2/';
  const headers = { Accept: 'application/json', 'OData-Version': '4.0', 'Content-Type': 'application/json' };
  const who = await (await fetch(api + 'WhoAmI', { headers })).json();
  const list = await fetch(api + entitySet + '?$top=3', { headers });
  const listBody = await list.json();
  const rows = await fetch(api + `bf_connections?$select=bf_connectionid&$filter=bf_name eq '${connection}'`, { headers });
  const read = await fetch(api + 'bf_ListConnections', { method: 'POST', headers, body: '{}' });
  const readBody = await read.json();
  return {
    user: who.UserId ? 'signed in' : JSON.stringify(who).slice(0, 200),
    list: list.status + ' ' + (listBody.value ? `${listBody.value.length} rows` : (listBody.error?.message ?? '').slice(0, 300)),
    connectionRows: rows.status,
    adminApi: read.status + ' ' + (readBody.error?.message ?? 'answered').slice(0, 300),
  };
}, [entitySet, connection]);
for (const [k, v] of Object.entries(result)) console.log(`${k}: ${sanitise(String(v)).replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/g, '<guid>')}`);
await context.close();
