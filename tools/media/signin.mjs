// One sign-in per recording user, kept in a browser profile outside the repo (~/.brokerforge/media-profiles/<name>).
// Usage: node signin.mjs a   (the admin, user A)   |   node signin.mjs b   (the second user, B)
// A window opens on the dev environment; sign in there (MFA included). It closes by itself once the app has loaded,
// and refuses a profile signed in as the same person as the other one (part 2 needs two different people).
import { chromium } from 'playwright';
import { existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { org, profiles } from './env.mjs';

const name = process.argv[2];
if (!/^[ab]$/.test(name ?? '')) throw new Error('usage: node signin.mjs <a|b>');
const other = name === 'a' ? 'b' : 'a';
const dir = join(profiles, name);
const context = await chromium.launchPersistentContext(dir, { headless: false, viewport: { width: 1280, height: 800 } });
const page = context.pages()[0] ?? await context.newPage();
await page.goto(org);
console.log(`Sign in as user ${name.toUpperCase()} in the window that opened. Waiting up to 10 minutes.`);
const orgHost = new URL(org).host;
await page.waitForURL(u => u.host === orgHost && /main\.aspx|\/apps/.test(u.pathname + u.search), { timeout: 600_000 });
await page.waitForTimeout(3000);

// Who is it? The Entra object ID of the signed-in Dataverse user.
await page.goto(`${org}/api/data/v9.2/WhoAmI`);
const { UserId } = JSON.parse(await page.locator('body').innerText());
await page.goto(`${org}/api/data/v9.2/systemusers(${UserId})?$select=azureactivedirectoryobjectid`);
const oid = JSON.parse(await page.locator('body').innerText()).azureactivedirectoryobjectid;
await context.close();

const otherOid = existsSync(join(profiles, `${other}.oid`)) ? readFileSync(join(profiles, `${other}.oid`), 'utf8').trim() : undefined;
if (oid === otherOid) {
  rmSync(dir, { recursive: true, force: true });
  rmSync(join(profiles, `${name}.oid`), { force: true });
  console.error(`Profile ${name} was signed in as the same person as profile ${other}; it has been cleared. Run again and sign in as the other user.`);
  process.exit(1);
}
writeFileSync(join(profiles, `${name}.oid`), oid);
console.log(`Signed in: profile ${name} saved (user ${oid.slice(0, 8)}…).`);
