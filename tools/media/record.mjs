// Records the how-to video (out/brokerforge-how-to.mp4, with captions drawn in, an .srt beside it and chapter times) or,
// with --shots, takes the guide's screenshots into docs/guide/images/.
// Usage: node record.mjs [--shots] [part1|part2]   (both parts when none is named)
// Needs: the signed-in profile (node signin.mjs a), the az
// CLI signed in (the Tickets mock passwords are read from the dev Key Vault and typed, never printed), and ffmpeg.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { chromium } from 'playwright';
import { envId, here, hostUrl, org, out, placeholders, profiles, sanitise } from './env.mjs';
import { initScript, size, stage } from './stage.mjs';

const shots = process.argv.includes('--shots');
const parts = process.argv.slice(2).filter(a => /^part[12]$/.test(a));
const run = parts.length ? parts : ['part1', 'part2'];
const orgHost = new URL(org).host;
const raw = join(out, 'raw');
mkdirSync(raw, { recursive: true });

function vaultSecret(name) {
  const vault = /VAULT=["']?([^"'\n]+)/.exec(readFileSync(join(homedir(), '.brokerforge', 'dev-ids.env'), 'utf8'))[1];
  return execFileSync('az', ['keyvault', 'secret', 'show', '--vault-name', vault, '--name', name, '--query', 'value', '-o', 'tsv'], { encoding: 'utf8' }).trim();
}

/** A browser for one user (profile a or b): recording when making the video. Returns the page, its stage and a stop(). */
async function open(user, segment, prefix) {
  const context = await chromium.launchPersistentContext(join(profiles, user), {
    headless: true, viewport: size, deviceScaleFactor: shots ? 2 : 1,
    ...(shots ? {} : { recordVideo: { dir: join(raw, segment), size } }),
  });
  await context.addInitScript(initScript({ pairs: placeholders, letter: user.toUpperCase(), orgHost }));
  const page = context.pages()[0] ?? await context.newPage();
  const started = Date.now();
  const s = stage(page, { mode: shots ? 'shots' : 'video', prefix });
  const cues = [];
  // Every caption is also a subtitle cue, timed from the start of this recording.
  const say = s.say;
  s.say = async (text, ms) => { const from = Date.now() - started; await say(text, ms); cues.push({ from, to: Date.now() - started, text }); };
  const title = s.title;
  s.title = async (text, ms) => { const from = Date.now() - started; await title(text, ms); cues.push({ from, to: Date.now() - started, text }); };
  const chapters = [];
  s.chapter = name => chapters.push({ title: name, at: (Date.now() - started) / 1000 });
  // Waiting worth nobody's time (a new table settling) is marked and cut when the part is assembled.
  const skips = [];
  s.skipFrom = () => skips.push({ from: (Date.now() - started) / 1000, to: null });
  s.skipTo = () => { skips[skips.length - 1].to = (Date.now() - started) / 1000; };
  async function stop() {
    const video = page.video();
    await context.close();
    return shots ? undefined : { file: await video.path(), cues, chapters, skips: skips.filter(k => k.to != null) };
  }
  return { page, s, stop, context };
}

/** Waits through Microsoft's sign-in (silent with a signed-in profile; picks the account if asked) back to `host`. */
async function throughSignIn(page, host) {
  const until = Date.now() + 90_000;
  while (new URL(page.url()).host !== host) {
    if (Date.now() > until) throw new Error(`Sign-in did not return to ${host}; run node signin.mjs again for this profile.`);
    // The connect app's delegated Dataverse permission (plan 4, approved by Simon) is consented once, for this user only:
    // "Consent on behalf of your organization" is left unticked.
    if (await page.getByText('Permissions requested').isVisible().catch(() => false)) {
      const org = page.getByRole('checkbox');
      if (await org.isChecked().catch(() => false)) await org.uncheck();
      await page.getByRole('button', { name: 'Accept' }).click();
      await page.waitForTimeout(2000);
      continue;
    }
    for (const sel of ['#tilesHolder [role=listitem]', 'div[data-test-id] >> nth=0', '#idSIButton9']) {
      const el = page.locator(sel).first();
      if (await el.isVisible().catch(() => false)) { await el.click().catch(() => undefined); break; }
    }
    await page.waitForTimeout(1000);
  }
  await page.waitForLoadState('domcontentloaded');
}

const list = etn => `${org}/main.aspx?pagetype=entitylist&etn=${etn}`;
const connectUrl = instance => `${hostUrl}/connect/${envId}/${instance}`;

/**
 * Connect page: makes sure the user has (or has not) a connection to `instance`, off camera. `mockUser` is the Tickets
 * mock's account (alice, bob) for OAuth; for Impersonate the Entra sign-in is the profile's own.
 */
async function setConnection(user, mockUser, wanted, instance = 'TicketsAPI') {
  const { page, stop } = await open(user, `setup-${user}`, 'setup');
  await page.goto(connectUrl(instance));
  await throughSignIn(page, new URL(hostUrl).host);
  const status = await page.locator('p').first().innerText();
  const connected = /Status: connected/i.test(status);
  if (wanted && !connected) {
    await page.getByRole('button', { name: /^Connect/ }).click();
    if (mockUser) {
      await page.locator('select[name=user]').selectOption(mockUser);
      await page.locator('input[name=password]').fill(vaultSecret(`tickets-${mockUser}-password`));
      await page.getByRole('button', { name: 'Sign in' }).click();
    } else await throughSignIn(page, new URL(hostUrl).host);
    await page.getByText(/Status: connected/).waitFor({ timeout: 60_000 }).catch(async e => {
      await page.screenshot({ path: join(out, 'connect-failed.png') });
      console.log(`connect failed at ${new URL(page.url()).pathname}: ${sanitise((await page.locator('body').innerText()).slice(0, 600))}`);
      throw e;
    });
  }
  if (!wanted && (connected || /expired|could not be read/.test(status))) await page.getByRole('button', { name: 'Disconnect' }).click();
  await page.waitForTimeout(1500);
  console.log(`setup: ${user} ${(await page.locator('p').first().innerText()).replace(/\s+/g, ' ')}`);
  await stop();
}

/** Opens a table's list and waits for its rows (or its error banner). */
async function openList(page, etn, expect, absent) {
  await page.goto(list(etn));
  await page.getByText(expect, { exact: true }).first().waitFor({ timeout: 90_000 });
  await page.waitForTimeout(1500);
  // A per-user list showing someone else's rows means the wrong person is signed in: stop rather than record it.
  if (absent && await page.getByText(absent, { exact: true }).count()) throw new Error(`${etn} shows ${absent}: is this profile signed in as the right user?`);
}

// ---------------------------------------------------------------- Part 1: onboard a REST API (admin)

/** Picks an option of a Dataverse choice column, whichever control the form draws for it. */
async function choose(page, s, label, option) {
  const select = page.locator(`select[aria-label="${label}"]`);
  if (await select.count()) { await s.tap(select); await select.selectOption({ label: option }); return; }
  await s.tap(page.getByRole('combobox', { name: label }));
  await s.tap(page.getByRole('option', { name: option, exact: true }));
}

async function part1() {
  const { page, s, stop, context } = await open('a', 'part1', 'p1');
  const field = label => page.locator(`input[aria-label="${label}"]`);
  const review = page.frameLocator('iframe[title="Review"], iframe[id^="WebResource_review"]').first();
  const answers = JSON.parse(readFileSync(join(here, '..', '..', 'tests', 'SwaggerForge.Tests', 'fixtures', 'inventory-answers.json'), 'utf8'));
  const definition = readFileSync(join(here, '..', 'mock-api', 'inventory-openapi.yaml'), 'utf8');
  const connections = `${org}/main.aspx?pagetype=webresource&webresourceName=bf_/brokerforge/connections.html`;
  try {
    return await part1Steps();
  } catch (e) {
    await page.screenshot({ path: join(out, 'part1-failed.png') }).catch(() => undefined);
    const said = await review.locator('#message').innerText().catch(() => '(no review page)');
    console.log(`part1 failed at ${new URL(page.url()).search.slice(0, 60)}; review page says: ${sanitise(said)}`);
    throw e;
  }

  async function part1Steps() {

  // Off camera: a fresh start, so a second take doesn't make a second InventoryAPI row (the table, if made, stays).
  execFileSync('bash', ['-c', `source "${join(here, '..', 'spike', 'dv.sh')}"; for id in $(dv GET "bf_connections?\\$select=bf_connectionid&\\$filter=bf_name%20eq%20'InventoryAPI'" | head -1 | /usr/bin/python3 -c 'import json,sys; print(" ".join(r["bf_connectionid"] for r in json.load(sys.stdin)["value"]))'); do dv DELETE "bf_connections($id)" >/dev/null; done`]);
  // Off camera (everything before the first chapter is cut): wake the host and load Dataverse and the connections page.
  await page.goto(connections);
  await page.frameLocator('iframe').first().getByText('Connections').first().waitFor({ timeout: 120_000 }).catch(() => undefined);
  await page.waitForTimeout(3000);
  s.chapter('Introduction');
  await s.title('BrokerForge · Part 1: add a REST connection');
  await s.say('BrokerForge shows an external API in Dataverse as a virtual table. Adding one is done here, in the BrokerForge app: no code, no files, no redeploy.', 6400);
  await s.say('We will connect an inventory API from its OpenAPI (Swagger) definition. The definition is the only thing anyone writes.', 5600);
  await s.shot('01-connections');

  s.chapter('New connection');
  await page.goto(`${org}/main.aspx?pagetype=entityrecord&etn=bf_connection`);
  await field('Name').waitFor({ timeout: 90_000 });
  await page.waitForTimeout(1500);
  await s.say('A connection needs a name, the base URL of the API and how BrokerForge signs in to it.');
  await s.type(field('Name'), 'InventoryAPI');
  const baseUrl = field('Base URL');
  // A typed value can't be masked like page text, so the field is covered with the placeholder address while it shows.
  // The cover sits inside the field's own box, so it scrolls with it.
  await baseUrl.evaluate(input => {
    const host = input.parentElement;
    if (getComputedStyle(host).position === 'static') host.style.position = 'relative';
    const c = document.createElement('div');
    c.id = 'bf-cover-baseurl';
    c.textContent = 'https://inventory.contoso.example';
    Object.assign(c.style, { position: 'absolute', left: input.offsetLeft + 'px', top: input.offsetTop + 'px', width: input.offsetWidth + 'px',
      height: input.offsetHeight + 'px', background: getComputedStyle(input).backgroundColor || '#f5f5f5', display: 'flex', alignItems: 'center',
      padding: getComputedStyle(input).padding, boxSizing: 'border-box', font: getComputedStyle(input).font, color: '#242424', zIndex: 10, pointerEvents: 'none' });
    host.appendChild(c);
  });
  await s.type(baseUrl, execFileSync('bash', ['-c', `source ~/.brokerforge/dev-ids.env; az containerapp show -g "$RG" -n bf-dev-orders-mock --query properties.configuration.ingress.fqdn -o tsv`], { encoding: 'utf8' }).trim().replace(/^/, 'https://'));
  await choose(page, s, 'Authentication', 'Static');
  await s.type(field('Static kind'), 'apiKeyQuery');
  await s.type(field('Query parameter'), 'api_key');
  await s.type(field('Table prefix'), 'ext');
  await s.say('This API takes a key in its query string. The key itself is never typed here: it is set on the host\'s own page later.', 5600);
  const swagger = page.locator('textarea[aria-label="Swagger / OpenAPI"]');
  await s.tap(swagger);
  await swagger.fill(definition);
  await s.say('Paste the OpenAPI definition, YAML or JSON, into the Swagger box.');
  await s.shot('02-new-connection');
  await s.tap(page.getByRole('menuitem', { name: /^Save \(/ }));
  await page.waitForURL(/[?&]id=/, { timeout: 60_000 });
  await page.waitForTimeout(2500);
  await page.evaluate(() => document.getElementById('bf-cover-baseurl')?.remove());

  s.chapter('Review and answer');
  await s.tap(page.getByRole('tab', { name: 'Review and generate' }));
  await s.tap(review.locator('#read'));
  await review.locator('#endpoints').getByText(/questions|Ready/).first().waitFor({ timeout: 120_000 });
  await page.waitForTimeout(2000);
  await s.say('BrokerForge lists every endpoint it found and what each becomes: list, open, new, save, delete, or an action.', 5600);
  await s.say('Where the definition leaves a gap it asks, rather than guess. How are results paged? Which filters does the API really support?', 6000);
  await s.say('Generate stays disabled until every ticked endpoint is ready.', 3600);
  await s.shot('03-questions');
  for (const [key, value] of Object.entries(answers)) {
    if (key.startsWith('column.') && key.endsWith('.displayName')) {
      const path = key.slice('column.'.length, -'.displayName'.length);
      const cell = review.locator('#table tr', { has: page.locator(`td:text-is("${path}")`) }).locator('input');
      await cell.fill(value);
      await cell.dispatchEvent('change');
      continue;
    }
    const q = review.locator(`[data-key="${key}"]`);
    if (!(await q.count())) continue;
    const select = q.locator('select');
    if (await select.count()) { await s.tap(select); await select.selectOption(value); }
    else { await q.locator('input').fill(value); await q.locator('input').dispatchEvent('change'); }
  }
  await s.say('Each answer is saved with the connection, and anything left unticked fails loudly if someone uses it.', 5200);
  await s.tap(review.locator('#saveAnswers'));
  await review.getByText('Ready to generate.').waitFor({ timeout: 120_000 });
  await page.waitForTimeout(1500);
  await s.say('Every endpoint is ready. The preview lists exactly what Generate will create.');
  await s.shot('04-ready');

  s.chapter('Generate');
  await s.tap(review.locator('#generate'));
  await s.say('Generate creates the virtual table, every column, a view and a form that show them, the actions, and a security role, all in a solution for this source.', 6800);
  await review.getByText(/^Generated|^Generate failed/).first().waitFor({ timeout: 900_000 });
  if (await review.getByText(/^Generate failed/).count()) throw new Error('Generate failed: ' + await review.getByText(/^Generate failed/).first().innerText());
  await page.waitForTimeout(1500);
  await s.shot('05-generated');
  const roleLink = review.getByRole('link', { name: 'Finish as administrator' });
  if (await roleLink.count()) {
    await s.say('The role\'s privileges and the action can only be set by an administrator, so that step is a click, done as you.', 5200);
    const [rolePage] = await Promise.all([context.waitForEvent('page'), s.tap(roleLink)]);
    await throughSignIn(rolePage, new URL(hostUrl).host);
    const apply = rolePage.getByRole('button', { name: 'Do it as me' });
    if (await apply.count()) {
      await apply.click();
      await throughSignIn(rolePage, new URL(hostUrl).host);
    }
    await rolePage.getByText(/Done as you|up to date/).first().waitFor({ timeout: 300_000 });
    await rolePage.waitForTimeout(1500);
    await rolePage.close();
  }

  s.chapter('Set the key');
  await s.tap(page.getByRole('tab', { name: 'Review and generate' }));
  await s.tap(review.locator('#read'));
  // An earlier take may have stored the key already: then the page offers Replace key, which shows the same page.
  const setKey = review.getByRole('link', { name: /^(Set|Replace) key$/ });
  await setKey.waitFor({ timeout: 120_000 });
  await s.say('The connection shows "needs key". Set key opens the host\'s page, where the key is stored encrypted. It never enters Dataverse.', 6000);
  const [keyPage] = await Promise.all([context.waitForEvent('page'), s.tap(setKey)]);
  await throughSignIn(keyPage, new URL(hostUrl).host);
  const keyStage = stage(keyPage, { mode: shots ? 'shots' : 'video', prefix: 'p1' });
  await keyStage.type(keyPage.locator('input[name=value]').first(), vaultSecret('inventory-api-key'), { secret: true });
  await keyStage.tap(keyPage.getByRole('button', { name: /^(Set|Replace) key$/ }));
  await keyPage.getByRole('button', { name: 'Replace key' }).waitFor({ timeout: 60_000 });
  await keyPage.waitForTimeout(1500);
  await keyPage.close();
  await page.goto(connections);
  await page.frameLocator('iframe').first().getByText('InventoryAPI').first().waitFor({ timeout: 60_000 });
  await page.waitForTimeout(2000);
  await s.say('Back on the connections page, InventoryAPI is ready.');
  await s.shot('06-ready-list');

  // A table made minutes ago can show an error at first. Wait for it in a second tab, which isn't recorded, so the
  // video only opens the list and the form once they work.
  s.skipFrom();
  const warm = await context.newPage();
  for (let attempt = 1; ; attempt++) {
    try {
      await openList(warm, 'ext_item', 'Item 003');
      await warm.goto(`${org}/main.aspx?pagetype=entityrecord&etn=ext_item&id=00000301-0000-0000-0000-000000000000`);
      await warm.locator('input[aria-label="On hand"]').waitFor({ timeout: 45_000 });
      break;
    } catch (e) { if (attempt === 8) throw e; await warm.waitForTimeout(30_000); }
  }
  await warm.close();
  s.skipTo();

  s.chapter('Use the table');
  await openList(page, 'ext_item', 'Item 003');
  await s.say('The table lists rows straight from the API, with every column on the view. Nothing is copied into Dataverse.', 5600);
  await s.shot('07-list');

  s.chapter('Use it on a form');
  await s.tap(page.getByText('Item 003', { exact: true }).first());
  // A form made minutes ago can answer 502 at first: close the error and load it again, up to four times.
  for (let attempt = 1; ; attempt++) {
    try { await field('On hand').waitFor({ timeout: 45_000 }); break; }
    catch (e) {
      if (attempt === 4) throw e;
      await page.getByRole('button', { name: 'OK' }).click().catch(() => undefined);
      await page.waitForTimeout(20_000);
      await page.goto(`${org}/main.aspx?pagetype=entityrecord&etn=ext_item&id=00000301-0000-0000-0000-000000000000`);
    }
  }
  await page.waitForTimeout(2500);
  await s.say('Opening a record reads it from the API. The form shows every column.', 4600);
  await s.shot('08-form');
  await s.tap(field('On hand'));
  await field('On hand').fill('25');
  await page.keyboard.press('Tab');
  await s.tap(page.getByRole('menuitem', { name: /^Save \(/ }));
  await page.getByText('- Saved').first().waitFor({ timeout: 30_000 }).catch(() => undefined);
  await page.waitForTimeout(1500);
  await s.say('Save sends the change to the API.');
  await s.tap(page.getByRole('menuitem', { name: /^New/ }));
  await field('SKU').waitFor({ timeout: 60_000 });
  await page.waitForTimeout(1500);
  await s.type(field('Name'), 'Demo item');
  await s.type(field('SKU'), 'SKU-DEMO');
  await s.type(field('On hand'), '3');
  await s.type(field('Price'), '9.99');
  await s.tap(page.getByRole('menuitem', { name: /^Save \(/ }));
  await page.waitForURL(/[?&]id=/, { timeout: 60_000 });
  await page.waitForTimeout(2500);
  await s.say('New and Save create a record in the API, and the API\'s new ID becomes the record\'s key.', 5200);
  await s.shot('09-form-new');
  await s.tap(page.getByRole('menuitem', { name: /^Delete/ }));
  await s.tap(page.getByRole('dialog').getByRole('button', { name: 'Delete' }));
  await page.waitForTimeout(3000);
  await s.say('Delete removes it from the API.');
  // Off camera: Item 003 back as it was.
  execFileSync('bash', ['-c', `source "${join(here, '..', 'spike', 'dv.sh')}"; dv PATCH 'ext_items(00000301-0000-0000-0000-000000000000)' '{"ext_onhand":21}' >/dev/null`]);

  s.chapter('Other environments');
  await s.say('The connection travels to test and production in its solution. There, set the base URL variable if it differs, and the key on the host\'s page.', 6400);
  await s.say('Until then its tables fail with a named error, "needs URL" or "needs key". They never call the dev API.', 5200);
  await s.title('Next: part 2, connect your account', 2800);
  return stop();
  }
}

// ---------------------------------------------------------------- Part 2: connect your account (end user)

async function part2() {
  // One signed-in Dataverse user throughout (a second person's sign-in can't be automated): the rows follow the
  // account connected at the source. Off camera first: not connected, and the host and mock awake.
  await setConnection('a', 'alice', false);
  const { page, s, stop } = await open('a', 'part2', 'p2');
  await page.goto(list('ext_ticket'));
  await page.locator('text=ConnectionRequiredException').first().waitFor({ timeout: 90_000 });
  await page.waitForTimeout(1500);
  s.chapter('A per-user table');
  await s.title('BrokerForge · Part 2: connect your account');
  await s.say('Tickets is a per-user table: each person sees the tickets their own account can see in the source.', 4800);
  await s.say('Until you connect your account, the list shows this error instead of rows. It never falls back to anyone else\'s access.', 5800);
  await s.say('The message gives the address of the connect page.');
  await s.shot('01-error');

  /** On the connect page: Connect (or Connect again), sign in at the source as `user`, back to "connected". */
  async function connectAs(user, first) {
    await s.tap(page.getByRole('button', { name: /^Connect/ }));
    await page.locator('select[name=user]').waitFor();
    if (first) await s.say('Connect sends you to the source to sign in. This demo source has its own sign-in page.');
    await s.tap(page.locator('select[name=user]'));
    await page.locator('select[name=user]').selectOption(user);
    await s.type(page.locator('input[name=password]'), vaultSecret(`tickets-${user}-password`), { secret: true });
    if (first) await s.shot('03-source-sign-in');
    await s.tap(page.getByRole('button', { name: 'Sign in' }));
    await page.getByText(/Status: connected/).waitFor({ timeout: 60_000 });
    await page.waitForTimeout(800);
  }

  s.chapter('Connect');
  await s.hush();
  await page.goto(connectUrl('TicketsAPI'));
  await throughSignIn(page, new URL(hostUrl).host);
  await page.waitForTimeout(800);
  await s.say('The connect page signs you in with your work account, and shows the connection\'s status for this source.', 5000);
  await s.shot('02-connect-page');
  await connectAs('alice', true);
  await s.say('Connected. BrokerForge stores the grant encrypted, under your account only, and refreshes it when it expires.', 5200);
  await s.shot('04-connected');

  s.chapter('Your rows');
  await s.hush();
  await openList(page, 'ext_ticket', 'Ticket 1', 'Ticket 8');
  await s.say('Back in Dataverse, the list shows the tickets your source account can see: Ticket 1 to Ticket 7.');
  await s.shot('05-rows');
  await s.tap(page.getByText('Ticket 1', { exact: true }).first());
  await page.locator('input[aria-label="Title"]').waitFor({ timeout: 90_000 });
  await page.waitForTimeout(2000);
  const ticketForm = page.url();
  await s.say('Forms work the same way: opening a ticket reads it from the source with your connection.');
  await s.shot('06-ticket-form');

  s.chapter('A different account, different rows');
  await s.hush();
  await page.goto(connectUrl('TicketsAPI'));
  await page.getByText(/Status: connected/).waitFor({ timeout: 60_000 });
  await s.say('The rows follow the account that is connected. Connect again, this time as a different source account…');
  await connectAs('bob', false);
  await s.hush();
  await openList(page, 'ext_ticket', 'Ticket 8', 'Ticket 1');
  await s.say('…and the same table shows only that account\'s tickets: Ticket 8 to Ticket 12. Each person sees their own.', 5400);
  await s.shot('07-rows-other');

  s.chapter('Disconnect');
  await s.hush();
  await page.goto(connectUrl('TicketsAPI'));
  await page.getByText(/Status: connected/).waitFor({ timeout: 60_000 });
  await s.say('To remove your connection, open the connect page again and choose Disconnect.');
  await s.tap(page.getByRole('button', { name: 'Disconnect' }));
  await page.waitForTimeout(1200);
  await s.say('The stored grant is deleted.');
  await s.shot('08-disconnected');
  await s.hush();
  await page.goto(list('ext_ticket'));
  await page.locator('text=ConnectionRequiredException').first().waitFor({ timeout: 90_000 });
  await page.waitForTimeout(1000);
  await s.say('The table asks you to connect again.');
  await s.hush();
  await page.goto(ticketForm);
  await page.locator('text=ConnectionRequiredException').first().waitFor({ timeout: 90_000 });
  await page.waitForTimeout(1500);
  await s.say('So does a form: the same message, with the connect page address. Connect any time to get your rows back.', 5400);
  await s.shot('09-form-error');
  await s.title('That\'s BrokerForge', 2600);
  const segment = await stop();
  // Off camera: leave the admin connected as before.
  await setConnection('a', 'alice', true);
  return [segment];
}

// ---------------------------------------------------------------- Video assembly

const srtTime = ms => { const h = Math.floor(ms / 3.6e6), m = Math.floor(ms / 6e4) % 60, s = Math.floor(ms / 1000) % 60; return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')},${String(Math.floor(ms % 1000)).padStart(3, '0')}`; };
const duration = file => Number(execFileSync('ffprobe', ['-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', file], { encoding: 'utf8' }));

async function assemble(segments, name) {
  const list = join(raw, `${name}.txt`);
  const files = [];
  const cues = [], chapters = [];
  let offset = 0;
  for (const [i, seg] of segments.entries()) {
    // Starts at the first chapter: the page loading before it is not worth watching.
    const from = Math.max(0, (seg.chapters[0]?.at ?? 0) - 0.3);
    // The kept stretches: from the first chapter to the end, less the marked waits.
    const skips = (seg.skips ?? []).filter(k => k.to > from).sort((a, b) => a.from - b.from);
    const keep = [];
    let at = from;
    for (const k of skips) { if (k.from > at) keep.push([at, k.from]); at = Math.max(at, k.to); }
    keep.push([at, null]);
    // A time in the recording, as a time in the kept video (null when it falls in a cut).
    const shift = t => {
      let out = 0;
      for (const [a, b] of keep) {
        if (t < a) return null;
        if (b == null || t <= b) return out + (t - a);
        out += b - a;
      }
      return null;
    };
    for (const [j, [a, b]] of keep.entries()) {
      const mp4 = join(raw, `${name}-${i}-${j}.mp4`);
      execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-ss', String(a), ...(b == null ? [] : ['-to', String(b)]), '-i', seg.file,
        '-c:v', 'libx264', '-preset', 'slow', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', '25', mp4]);
      files.push(mp4);
    }
    for (const c of seg.cues) {
      const f = shift(c.from / 1000), t = shift(c.to / 1000);
      if (f != null && t != null) cues.push({ ...c, from: (f + offset) * 1000, to: (t + offset) * 1000 });
    }
    for (const c of seg.chapters) { const t = shift(c.at); if (t != null) chapters.push({ title: c.title, at: Math.round((t + offset) * 10) / 10 }); }
    offset += keep.reduce((sum, [a, b]) => sum + ((b ?? (a + duration(files[files.length - 1]))) - a), 0);
  }
  writeFileSync(list, files.map(f => `file '${f}'`).join('\n'));
  const mp4 = join(out, `${name}.mp4`);
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', list, '-c', 'copy', '-movflags', '+faststart', mp4]);
  writeFileSync(join(out, `${name}.srt`), cues.filter(c => c.to > 0).map((c, i) => `${i + 1}\n${srtTime(Math.max(0, c.from))} --> ${srtTime(c.to)}\n${c.text}\n`).join('\n'));
  writeFileSync(join(out, `${name}.json`), JSON.stringify(chapters, null, 2));
  console.log(`${mp4} (${Math.round(offset)} s)`, chapters);
}

// node record.mjs --join: the full video from parts recorded on their own (part 1 in one environment, part 2 in another).
if (process.argv.includes('--join')) {
  const parts = ['part1', 'part2'].map(p => join(out, `brokerforge-how-to-${p}`));
  const list = join(raw, 'join.txt');
  writeFileSync(list, parts.map(p => `file '${p}.mp4'`).join('\n'));
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', list, '-c', 'copy', '-movflags', '+faststart', join(out, 'brokerforge-how-to.mp4')]);
  const offset = duration(`${parts[0]}.mp4`);
  const toMs = t => { const [h, m, rest] = t.split(':'); const [sec, ms] = rest.split(','); return ((+h * 60 + +m) * 60 + +sec) * 1000 + +ms; };
  const cues = parts.flatMap((p, i) => readFileSync(`${p}.srt`, 'utf8').trim().split(/\n\n+/).map(block => {
    const [, time, ...text] = block.split('\n');
    const [from, to] = time.split(' --> ').map(toMs);
    return { from: from + i * offset * 1000, to: to + i * offset * 1000, text: text.join('\n') };
  }));
  writeFileSync(join(out, 'brokerforge-how-to.srt'), cues.map((c, i) => `${i + 1}\n${srtTime(c.from)} --> ${srtTime(c.to)}\n${c.text}\n`).join('\n'));
  const chapters = parts.flatMap((p, i) => JSON.parse(readFileSync(`${p}.json`, 'utf8')).map(c => ({ title: c.title, at: Math.round((c.at + i * offset) * 10) / 10 })));
  writeFileSync(join(out, 'brokerforge-how-to.json'), JSON.stringify(chapters, null, 2));
  console.log(`joined (${Math.round(duration(join(out, 'brokerforge-how-to.mp4')))} s)`, chapters);
  process.exit(0);
}

// node record.mjs --connect <instance> <alice|bob|entra|none>: set the profile's connection off camera, record nothing.
const connectArg = process.argv.indexOf('--connect');
if (connectArg > 0) {
  const [instance, who] = process.argv.slice(connectArg + 1);
  await setConnection('a', who === 'entra' || who === 'none' ? null : who, who !== 'none', instance);
  process.exit(0);
}

const all = [];
for (const part of run) {
  const result = part === 'part1' ? [await part1()] : await part2();
  if (!shots) { await assemble(result, `brokerforge-how-to-${part}`); all.push(...result); }
}
if (!shots && run.length === 2) await assemble(all, 'brokerforge-how-to');
if (!existsSync(out)) mkdirSync(out);
