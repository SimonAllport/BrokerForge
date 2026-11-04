# Portal connections plan 3: the generator in the host

Results of 4 November 2026 against the dev environment. Plan: `docs/superpowers/plans/2026-11-04-portal-connections-3-generator.md`. Decision: HLD D-36. Spike: `docs/findings/portal-generator-spike.md`.

## 1. What changed

- **Change list.** `GenerationPlan.Diff` compares the schema with Dataverse and lists each change in plain words, with a fingerprint. Adds apply as before. A rename, removal or recreate applies only with the fingerprint of the list the admin saw. `bf generate` prints the list and exits 4 until it is run with `--confirm <fingerprint>`.
- **Dependency check.** Before deleting a column or Custom API, the generator lists what uses it. The table's main form and default view are its own and are updated first. Anything else blocks the whole run and is named.
- **Form, view and role.** Every run makes the main form and default view show every column, and keeps role `<Instance> user` at the privileges the descriptors bind.
- **Design and variables.** `DesignPublisher` writes the design web resource (the version goes up only when the design changed) and the two variable definitions into the solution. This environment's values are written outside it.
- **Host jobs.** `GenerationJobs` runs Generate in the background as the host's application user, one job per environment and instance, with a 15-minute limit. The `generate` admin command runs one job, with `--design-file` or `--from-environment`. Plan 4's `bf_Generate` will start the same jobs.

## 2. Live run in dev (as the az CLI user)

`bf generate` against the `WarehouseAPI` copy (table `ext_stockitem`, solution `BrokerForgeWarehouse`), with its schema and descriptors taken from the published design:

| Step | Result |
| --- | --- |
| Dry run | No table changes; parts: form and view, role `WarehouseAPI user` with Read, Create, Write and Delete; 4 writes planned |
| Run | 5 writes in 26 s: form, view, publish, role, privileges |
| Run again | "nothing to change" |
| Design changed: SKU removed, Price decimal 2 to int, On hand renamed In stock; a hand-made view uses `ext_sku` | List of 3 changes (fingerprint shown). Run refused: `remove column SKU (ext_sku) is blocked: view "SKU check (hand-made)" (…) uses it`. Nothing written. |
| Hand-made view deleted, run without `--confirm` | Exit 4, the same list and fingerprint |
| Run with `--confirm` | 11 writes in 48 s: SKU and Price taken off the form and view, published, both deleted, Price added as int, On hand relabelled in place, publish, form and view rewritten |
| Read back | Columns Name, In stock, Price (Integer), State, Updated at; the view shows exactly those; run again: nothing to change |
| Restored to the published design | 11 writes, then nothing to change. `GET ext_stockitems` through the plug-in returns the items with SKU and price again |
| `tools/dev/acceptance.sh`, `tools/dev/acceptance_inventory.sh` | All assertions passed |

One live fix came out of this: `systemform` has no `createdon`, so the main form is now picked by its platform name, Information.

## 3. Not done: generation as the host's own identity

Granting the `BrokerForge host` role what generation needs is waiting on Simon. That role belongs to the host's managed identity. The privileges are create, write and delete on tables, columns, Custom APIs and roles, plus forms, views, solutions, web resources and variable definitions. Together that is close to a System Customizer.

The permission check in this session stopped the setup script from granting them, and the decision is a security one. So these were not run:

- task 6 steps 1 and 2: the role update, the deploy, and `generate-in-host.sh` in the container;
- the check of whether a role holder can grant table privileges it doesn't hold (spike section 5).

`tools/dev/setup_dataverse.py` is unchanged.

**Not verified:** done criterion 6 (a non-administrator gets Read through the generated role). The role exists in dev with the right privileges, but no non-administrator token was used.

## 4. Tests

- Generator: 58 tests.
- Contract: 320 tests, 15 of them new for jobs and the admin command.
- The full solution passes.
