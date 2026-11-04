// Runs the real commands the admin scenes show and keeps what they printed (out/capture.json), so the video and the guide
// show real output: bf draft on the Inventory OpenAPI as a new "WarehouseAPI" source, bf validate failing then passing,
// bf generate --dry-run against the dev environment (it only reads), and an undeclared filter through the Web API.
// Needs the az CLI signed in to the dev tenant. Real hosts and org names are replaced (env.mjs).
import { execFileSync } from 'node:child_process';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { org, out, repo, sanitise } from './env.mjs';

const work = mkdtempSync(join(tmpdir(), 'bf-media-'));
const cli = join(repo, 'tools', 'BrokerForge.Cli');
execFileSync('dotnet', ['build', cli, '-v', 'q', '-nologo'], { stdio: 'ignore' });

/** Runs a command and returns what it printed (stdout and stderr) and its exit code; a failure is an expected result here. */
function run(file, args, cwd = work) {
  try {
    return { text: execFileSync(file, args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }), code: 0 };
  } catch (e) {
    return { text: `${e.stdout ?? ''}${e.stderr ?? ''}`, code: e.status };
  }
}
const bf = (...args) => run('dotnet', ['run', '--no-build', '--project', cli, '--', ...args]);
const read = rel => readFileSync(join(work, rel), 'utf8');
const edit = (rel, from, to) => writeFileSync(join(work, rel), read(rel).replace(from, to));

mkdirSync(join(work, 'openapi'));
cpSync(join(repo, 'tools/mock-api/inventory-openapi.yaml'), join(work, 'openapi/WarehouseAPI.yaml'));
const c = {};
c.draftCommand = 'bf draft openapi/WarehouseAPI.yaml --instance WarehouseAPI --prefix ext --table ext_stockitem --out drafts';
c.draft = bf('draft', 'openapi/WarehouseAPI.yaml', '--instance', 'WarehouseAPI', '--prefix', 'ext', '--table', 'ext_stockitem', '--out', 'drafts');
c.report = read('drafts/WarehouseAPI-draft-report.md');
c.listItemsDrafted = read('descriptors/WarehouseAPI/listItems.yaml');
c.schemaDrafted = read('schemas/WarehouseAPI.yaml');

// The review: the notes the draft left, and the names a person would want to read.
edit('descriptors/WarehouseAPI/listItems.yaml', 'sortStable: false', 'sortStable: true');
edit('schemas/WarehouseAPI.yaml', 'displayName: Stockitem\n', 'displayName: Stock item\n');
edit('schemas/WarehouseAPI.yaml', 'displayCollectionName: Stockitems', 'displayCollectionName: Stock items');
edit('schemas/WarehouseAPI.yaml', 'displayName: Sku', 'displayName: SKU');
c.listItems = read('descriptors/WarehouseAPI/listItems.yaml');
c.schema = read('schemas/WarehouseAPI.yaml');

c.validateCommand = 'bf validate descriptors --schemas schemas';
edit('descriptors/WarehouseAPI/listItems.yaml', 'filter: ext_name', 'filter: ext_title');
c.validateFail = bf('validate', 'descriptors', '--schemas', 'schemas');
edit('descriptors/WarehouseAPI/listItems.yaml', 'filter: ext_title', 'filter: ext_name');
c.validateOk = bf('validate', 'descriptors', '--schemas', 'schemas');

// The host settings for the new instance: Inventory's, renamed, as they sit in infra/containerApps.bicep.
const bicep = readFileSync(join(repo, 'infra/containerApps.bicep'), 'utf8').split('\n');
c.instanceSettings = bicep.filter(l => /\$\{(inventory|itemMap)\}/.test(l)).map(l => l.trim()
  .replace('${inventory}', '${warehouse}').replace('${itemMap}', '${stockMap}').replace("'InventoryAPI'", "'WarehouseAPI'")
  .replace('inventory-api-key', 'warehouse-api-key')).join('\n');
c.perUserSettings = bicep.filter(l => /\$\{tickets\}/.test(l)).map(l => l.trim()).join('\n');

const dev = `source "$HOME/.brokerforge/spike-ids.env"; source "$HOME/.brokerforge/dev-ids.env"`;
const generate = (instance, solution) => run('bash', ['-c', `${dev}; dotnet run --no-build --project "${cli}" -- generate --org "${org}" --schemas schemas --descriptors descriptors --instance ${instance} --solution ${solution} --publisher brokerforgeext --host-url "$DEV_HOST_URL" --scope "api://$HOST_APP/.default" --dry-run`]);
c.generateCommand = 'tools/dev/generate.sh WarehouseAPI BrokerForgeWarehouse --dry-run';
c.generateDry = generate('WarehouseAPI', 'BrokerForgeWarehouse');
c.generateInventoryCommand = 'tools/dev/generate.sh InventoryAPI BrokerForgeInventory --dry-run';
c.generateInventory = run('bash', [join(repo, 'tools/dev/generate.sh'), 'InventoryAPI', 'BrokerForgeInventory', '--dry-run'], repo);
// Only the summary line: the rest is the build.
c.generateInventory.text = c.generateInventory.text.split('\n').filter(l => /InventoryAPI:|would /.test(l)).join('\n');

c.undeclaredCommand = "dv GET 'ext_items?$filter=ext_onhand gt 5'";
const undeclared = run('bash', ['-c', `source "${join(repo, 'tools/spike/dv.sh')}"; dv GET 'ext_items?$filter=ext_onhand%20gt%205'`]);
const body = JSON.parse(undeclared.text.slice(0, undeclared.text.lastIndexOf('\nHTTP')));
c.undeclared = { text: `HTTP ${undeclared.text.trim().split('HTTP ').at(-1)}\n${body.error.message.replace(/ \(correlationId/, '\n(correlationId')}`, code: 0 };

rmSync(work, { recursive: true, force: true });
mkdirSync(out, { recursive: true });
const text = sanitise(JSON.stringify(c, null, 2));
writeFileSync(join(out, 'capture.json'), text);
for (const k of ['draft', 'validateFail', 'validateOk', 'generateDry', 'generateInventory', 'undeclared']) console.log(`--- ${k} (exit ${c[k].code})\n${sanitise(c[k].text)}`);
