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

## 3. Generation as the host's own identity

On 4 November Simon approved giving the `BrokerForge host` role what generation needs, and `tools/dev/setup_dataverse.py --host-user` now grants it. The list began as create, read, write and delete on the components Generate writes. The platform named three missing privileges, one run at a time: `prvReadRelationship`, `prvReadEntityDataSource` (the data source table is a virtual-table data source, so Create, Write and Delete were added too) and `prvReadUser` (needed for role changes). Custom API request parameters and response properties have no privileges of their own; the Custom API's cover them.

Run inside the container with `tools/dev/generate-in-host.sh` against `WarehouseAPI`:

| Step | Result |
| --- | --- |
| `--preview` | No table changes; the role found in `BrokerForgeWarehouse`; only the design publish listed |
| Run | Succeeded, 0 writes, design v1 (the published design was unchanged) |
| Delete taken off `WarehouseAPI user` by hand, run again | The preview lists the role change. The run failed at step `role` with 0 writes: HTTP 403, 0x80048d3c, "Add/Update/Removal of privilege(s) on a Role requires calling user to have the privilege(s) being modified", naming `prvDeleteext_stockitem` |
| Restored as the az CLI user | The role holds Delete at organisation depth again |

**Honest constraint (spike section 5 answered).** A caller can only add a privilege to a role if it holds that privilege itself. Each generated table brings its own privileges (`prv<Verb><table>`), and the host's role doesn't hold them, so the host can't set up `<Instance> user` for a new table or change it for an existing one. Its own role can't take them on either, for the same reason.

**Decision (Simon, 4 November):** an administrator does the role step. Generate as the host leaves the role out, and reports the change it would have made as pending. The admin command prints `needs an administrator: …` and exits 3. `--with-role` applies the role too, for an administrator running the same command as themselves. In dev that means `tools/dev/generate-in-host.sh <Instance> <Solution> --with-role`, which runs it locally as the az CLI user with the container's Trust settings. The `generate` command now starts before the host loads its serving stores, so that local run needs nothing else. In plan 4, the Generate page will apply the role as the signed-in administrator.

| Step (after the decision, image 121cdd3) | Result |
| --- | --- |
| Delete taken off `WarehouseAPI user` again, run in the host | Succeeded, 0 writes, design v1; `needs an administrator: set role WarehouseAPI user to Read, Create, Write, Delete on ext_stockitem`; exit 3 |
| `--with-role` locally as the az CLI user | Succeeded, 1 write (the privilege); the role holds Delete at organisation depth |
| Run in the host again | Succeeded, 0 writes, nothing pending; exit 0 |
| `tools/dev/acceptance.sh`, `tools/dev/acceptance_inventory.sh` | All assertions passed |

Also found: `az containerapp exec` is rate-limited. After about eight calls in a few minutes it returns 429 with `retry-after: 600`.

**Not verified:** done criterion 6 (a non-administrator gets Read through the generated role). The role exists in dev with the right privileges, but no non-administrator token was used.

## 4. Final review

A fresh review of the branch found three Important problems, each now fixed with a test that failed first:

- **Rename only:** a display-name change left the form's old label in place, and later runs never noticed. The form is now compared cell by cell, with labels and controls.
- **Late checks:** some checks ran after writes, and their message said "made no changes":
  - the design's instance name and auth overrides;
  - a privilege that doesn't exist;
  - the platform dropping the provider.

  The first two are now checked in the preview. Anything refused after a write is a failure naming the step and the writes made.
- **Descriptors not checked:** the host path didn't check the design's descriptors against its schema, so it could publish a design the host then refuses to load. It now checks them the way the CLI does.

A fourth problem is recorded as a constraint in D-36 instead: the one-job-at-a-time guard holds only within one host process. Plan 4 needs a lock outside the process before `bf_Generate` starts jobs.

## 5. Deferred minors, fixed

- Any failure after the checks (a network error, an HttpClient timeout, a token failure) names the step and the writes made, as Dataverse errors already did. The caller's own cancellation passes through.
- Dependents the platform deletes with the component (dependency type 1) no longer block. Built-in component types are named, and types from 10000 take their names from `solutioncomponentdefinitions`.
- The role is the one of that name in the source's solution. A hand-made role of the name is refused before any write.
- A dry run plans removals and recreates without `--confirm`, and prints the fingerprint a real run will need.
- The busy guard treats instance names case-insensitively, and a job's final state is stored before its key is freed.
- `GenerationJob.NeedsConfirmation` replaces matching failure text for the admin command's exit 4.

## 6. Tests

- Generator: 72 tests.
- Contract: 326 tests, 21 of them new for jobs and the admin command.
- The full solution passes.
