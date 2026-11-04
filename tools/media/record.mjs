// Records the how-to video (out/brokerforge-how-to.mp4, with captions drawn in, an .srt beside it and chapter times) or,
// with --shots, takes the guide's screenshots into docs/guide/images/.
// Usage: node record.mjs [--shots] [part1|part2]   (both parts when none is named)
// Needs: node capture.mjs first (the admin scenes' real output), the two signed-in profiles (node signin.mjs a|b), the az
// CLI signed in (the Tickets mock passwords are read from the dev Key Vault and typed, never printed), and ffmpeg.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { chromium } from 'playwright';
import { here, hostUrl, org, out, placeholders, profiles } from './env.mjs';
import { initScript, size, stage } from './stage.mjs';

const shots = process.argv.includes('--shots');
const parts = process.argv.slice(2).filter(a => /^part[12]$/.test(a));
const run = parts.length ? parts : ['part1', 'part2'];
const capture = JSON.parse(readFileSync(join(out, 'capture.json'), 'utf8'));
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
  async function stop() {
    const video = page.video();
    await context.close();
    return shots ? undefined : { file: await video.path(), cues, chapters };
  }
  return { page, s, stop, context };
}

/** Waits through Microsoft's sign-in (silent with a signed-in profile; picks the account if asked) back to `host`. */
async function throughSignIn(page, host) {
  const until = Date.now() + 90_000;
  while (new URL(page.url()).host !== host) {
    if (Date.now() > until) throw new Error(`Sign-in did not return to ${host}; run node signin.mjs again for this profile.`);
    for (const sel of ['#tilesHolder [role=listitem]', 'div[data-test-id] >> nth=0', '#idSIButton9']) {
      const el = page.locator(sel).first();
      if (await el.isVisible().catch(() => false)) { await el.click().catch(() => undefined); break; }
    }
    await page.waitForTimeout(1000);
  }
  await page.waitForLoadState('domcontentloaded');
}

const list = etn => `${org}/main.aspx?pagetype=entitylist&etn=${etn}`;
const connectUrl = instance => `${hostUrl}/connect/${instance}`;

/** Connect page: makes sure the user has (or has not) a TicketsAPI connection, off camera. */
async function setConnection(user, mockUser, wanted) {
  const { page, stop } = await open(user, `setup-${user}`, 'setup');
  await page.goto(connectUrl('TicketsAPI'));
  await throughSignIn(page, new URL(hostUrl).host);
  const status = await page.locator('p').first().innerText();
  const connected = /Status: connected/i.test(status);
  if (wanted && !connected) {
    await page.getByRole('button', { name: /^Connect/ }).click();
    await page.locator('select[name=user]').selectOption(mockUser);
    await page.locator('input[name=password]').fill(vaultSecret(`tickets-${mockUser}-password`));
    await page.getByRole('button', { name: 'Sign in' }).click();
    await page.getByText(/Status: connected/).waitFor({ timeout: 60_000 });
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

async function part1() {
  const { page, s, stop } = await open('a', 'part1', 'p1');
  const ui = (fn, ...args) => page.evaluate(([fn, args]) => window.ui[fn](...args), [fn, args]);
  const consolePage = async () => { await page.goto(pathToFileURL(join(here, 'console.html')).href); await page.waitForFunction(() => window.ui); };
  const t = x => x.text.replace(/\n$/, '');

  // Off camera (everything before the first chapter is cut): wake the host and load Dataverse and the item form once,
  // so the scenes below don't show a minute of blank page.
  await openList(page, 'ext_item', 'Item 003');
  await page.getByText('Item 003', { exact: true }).first().click();
  await page.locator('input[aria-label="On hand"]').waitFor({ timeout: 120_000 });
  await consolePage();
  s.chapter('Introduction');
  await s.title('BrokerForge · Part 1: onboard a REST API');
  await s.say('BrokerForge shows an external API in Dataverse as a virtual table. A new REST source needs configuration only: no code.', 5200);
  await s.say('We will onboard an inventory API as a new source called WarehouseAPI, starting from its OpenAPI file.');

  s.chapter('Draft descriptors from OpenAPI');
  await s.say('bf draft reads the OpenAPI file and writes one descriptor per operation, a table schema, and a report.');
  await ui('run', capture.draftCommand, t(capture.draft));
  await s.say('Six descriptors drafted, nothing skipped, and four notes to review.');
  await s.shot('01-draft');

  await ui('open', 'drafts/WarehouseAPI-draft-report.md', capture.report, { kind: 'md' });
  await page.evaluate(() => window.ui.highlight(window.ui.lineOf('## To review') + 2, window.ui.lineOf('sortStable')));
  await s.say('The report lists what a person should check. Here: the decimal precision, the primary name, and whether the source sorts rows in a stable order.', 6000);
  await s.shot('02-report');

  s.chapter('Review the descriptor');
  await ui('open', 'descriptors/WarehouseAPI/listItems.yaml', capture.listItemsDrafted);
  await page.evaluate(() => window.ui.highlight(window.ui.lineOf('query:'), window.ui.lineOf('ops: [eq]') + 3));
  await s.say('The list descriptor declares the only filters Dataverse may send: state and name, each with the eq operator.', 5000);
  await page.evaluate(() => window.ui.highlight(window.ui.lineOf('paging:'), window.ui.lineOf('total:')));
  await s.say('Paging is offset-based, up to 50 rows a page.');
  await page.evaluate(() => window.ui.highlight(window.ui.lineOf('response:'), window.ui.lineOf('type: datetime')));
  await s.say('The response map turns each JSON field into a column, with its type.');
  await page.evaluate(() => window.ui.scroll(window.ui.lineOf('sortStable') - 17));
  const sortLine = await page.evaluate(() => window.ui.lineOf('sortStable'));
  await s.say('The source returns items in id order, so the order is stable: set sortStable to true.');
  await ui('edit', sortLine, 'false', 'true');
  await page.waitForTimeout(800);
  await s.shot('03-descriptor');

  await ui('open', 'schemas/WarehouseAPI.yaml', capture.schemaDrafted);
  await s.say('The table schema becomes the Dataverse table and its columns. Tidy the display names people will see.');
  await ui('edit', 2, 'Stockitem', 'Stock item');
  await ui('edit', 3, 'Stockitems', 'Stock items');
  const sku = await page.evaluate(() => window.ui.lineOf('displayName: Sku'));
  await ui('edit', sku, 'Sku', 'SKU');
  await page.waitForTimeout(800);
  await s.shot('04-schema');

  s.chapter('Validate');
  await ui('open', 'descriptors/WarehouseAPI/listItems.yaml', capture.listItems.replace('filter: ext_name', 'filter: ext_title'));
  await page.evaluate(() => window.ui.highlight(window.ui.lineOf('ext_title')));
  await s.say('bf validate checks every descriptor against the specification and the table schema. Say we mistype a column name…');
  await ui('run', capture.validateCommand, t(capture.validateFail));
  await s.say('…validation fails, naming the file, the rule and the column. CI runs the same check on every push.', 4600);
  await s.shot('05-validate-fail');
  const bad = await page.evaluate(() => window.ui.lineOf('ext_title'));
  await ui('edit', bad, 'ext_title', 'ext_name');
  await ui('run', capture.validateCommand, t(capture.validateOk));
  await s.say('Fixed: all six descriptors are valid.');
  await s.shot('06-validate-ok');

  s.chapter('Configure the service instance');
  await ui('open', 'infra/containerApps.bicep (host settings)', capture.instanceSettings);
  await page.evaluate(() => window.ui.highlight(window.ui.lineOf('Auth__Mode'), window.ui.lineOf('Auth__SecretRef')));
  await s.say('The host needs the service instance: its base URL and how to authenticate. The API key is a Key Vault reference, never the key itself.', 6000);
  await page.evaluate(() => window.ui.highlight(window.ui.lineOf('stockMap}ServiceInstance'), window.ui.lineOf('Operations__Delete')));
  await s.say('The object map ties the table to the instance, picks the key strategy, and names the operation for each Dataverse message.', 5600);
  await s.say('Add the settings, put the API key in Key Vault, then deploy the host with tools/dev/deploy.sh.');
  await s.say('To use the same API on another server, test or production, change only BaseUrl and the Key Vault reference for that environment. Descriptors and tables stay the same.', 6600);
  await s.say('The OpenAPI file itself is only read once, by bf draft. At run time the host uses the descriptors and these settings.', 5600);
  await s.shot('07-instance');

  s.chapter('Generate the Dataverse components');
  await s.say('bf generate creates the solution, the data source record, the virtual table, its columns and any Custom APIs. Try it with --dry-run first.', 6000);
  await ui('run', capture.generateCommand, t(capture.generateDry));
  await s.say('Nine writes planned. Run it again without --dry-run to make them.');
  await s.shot('08-generate');
  await ui('run', capture.generateInventoryCommand, t(capture.generateInventory));
  await s.say('This dev environment already has the same API onboarded as InventoryAPI. Re-running generate finds nothing to change.', 5200);

  s.chapter('Use the table');
  await s.hush();
  await openList(page, 'ext_item', 'Item 003');
  await s.say('In Dataverse the table lists rows straight from the API. A generated view shows only the name column, so add the others.', 5600);
  await s.shot('09-list');
  await s.tap(page.getByRole('button', { name: 'Edit columns' }));
  await s.tap(page.getByRole('button', { name: /Add columns/ }));
  await page.waitForTimeout(1200);
  for (const col of ['SKU', 'On hand', 'Price', 'State']) await s.tap(page.getByRole('dialog').last().getByText(col, { exact: true }));
  await s.shot('10-add-columns');
  await s.tap(page.getByRole('button', { name: 'Close' }).last());
  await s.tap(page.getByRole('button', { name: 'Apply' }));
  await page.getByRole('columnheader', { name: /On hand/ }).first().waitFor({ timeout: 30_000 });
  await page.waitForTimeout(2500);
  await s.say('Every value comes from the inventory API at the moment the list is read. Nothing is copied into Dataverse.', 5000);
  await s.shot('11-columns');

  s.chapter('Use it on a form');
  await s.tap(page.getByText('Item 003', { exact: true }).first());
  const field = label => page.locator(`input[aria-label="${label}"]`);
  await field('On hand').waitFor({ timeout: 90_000 });
  await page.waitForTimeout(2500);
  await s.say('Opening a record reads it from the API (the getItem descriptor). The record ID is the source\'s own key, encoded.', 5600);
  await s.shot('12-form');
  await s.tap(field('On hand'));
  await field('On hand').fill('25');
  await page.keyboard.press('Tab');
  await s.tap(page.getByRole('menuitem', { name: /^Save \(/ }));
  await page.getByText('- Saved').first().waitFor({ timeout: 30_000 }).catch(() => undefined);
  await page.waitForTimeout(1500);
  await s.say('Save sends only the changed column to the API (updateItem). Nothing is stored in Dataverse.');
  await s.shot('13-form-saved');
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
  await s.say('A new record is created in the API (createItem), and the source\'s new ID becomes the record\'s key.', 5200);
  await s.shot('14-form-new');
  await s.tap(page.getByRole('menuitem', { name: /^Delete/ }));
  await s.tap(page.getByRole('dialog').getByRole('button', { name: 'Delete' }));
  await page.waitForTimeout(3000);
  await s.say('Delete removes it from the API (deleteItem).');
  // Off camera: Item 003 back as it was.
  execFileSync('bash', ['-c', `source "${join(here, '..', 'spike', 'dv.sh')}"; dv PATCH 'ext_items(00000301-0000-0000-0000-000000000000)' '{"ext_onhand":21}' >/dev/null`]);

  s.chapter('Fail loudly');
  await consolePage();
  await ui('open', 'descriptors/InventoryAPI/listItems.yaml', readFileSync(join(here, '..', '..', 'descriptors/InventoryAPI/listItems.yaml'), 'utf8'));
  await page.evaluate(() => window.ui.highlight(window.ui.lineOf('query:'), window.ui.lineOf('paging:') - 1));
  await s.say('Filtering only works on the columns the descriptor declares. Anything else fails with a named error, never with partial results.', 6000);
  await ui('run', capture.undeclaredCommand, t(capture.undeclared));
  await s.say('Here: On hand has no declared filter, so the query is refused, with a correlation ID to find it in the host logs.', 5600);
  await s.shot('15-undeclared');

  s.chapter('Per-user sources');
  await ui('open', 'infra/containerApps.bicep (per-user instance)', capture.perUserSettings);
  await page.evaluate(() => window.ui.highlight(window.ui.lineOf('Auth__Mode'), window.ui.lineOf('Auth__OAuthResource')));
  await s.say('A per-user source is configured the same way, with an OAuth, Impersonate or SSO mode. Each user then connects their own account: part 2.', 6200);
  await s.say('Users also need a security role with Read (and Create, to add rows) on the table. bf generate does not create one yet.', 5600);
  await s.shot('16-peruser');
  await s.title('Next: part 2, connect your account', 2800);
  return stop();
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
    const mp4 = join(raw, `${name}-${i}.mp4`);
    execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-ss', String(from), '-i', seg.file, '-c:v', 'libx264', '-preset', 'slow', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', '25', mp4]);
    files.push(mp4);
    for (const c of seg.cues) cues.push({ ...c, from: c.from + (offset - from) * 1000, to: c.to + (offset - from) * 1000 });
    for (const c of seg.chapters) chapters.push({ title: c.title, at: Math.round((c.at - from + offset) * 10) / 10 });
    offset += duration(mp4);
  }
  writeFileSync(list, files.map(f => `file '${f}'`).join('\n'));
  const mp4 = join(out, `${name}.mp4`);
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', list, '-c', 'copy', '-movflags', '+faststart', mp4]);
  writeFileSync(join(out, `${name}.srt`), cues.filter(c => c.to > 0).map((c, i) => `${i + 1}\n${srtTime(Math.max(0, c.from))} --> ${srtTime(c.to)}\n${c.text}\n`).join('\n'));
  writeFileSync(join(out, `${name}.json`), JSON.stringify(chapters, null, 2));
  console.log(`${mp4} (${Math.round(offset)} s)`, chapters);
}

const all = [];
for (const part of run) {
  const result = part === 'part1' ? [await part1()] : await part2();
  if (!shots) { await assemble(result, `brokerforge-how-to-${part}`); all.push(...result); }
}
if (!shots && run.length === 2) await assemble(all, 'brokerforge-how-to');
if (!existsSync(out)) mkdirSync(out);
