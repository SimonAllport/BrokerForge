# Portal connections plan 4: the admin app

Results of 5 November 2026, in the dev environment. Plan: `docs/superpowers/plans/2026-11-05-portal-connections-4-admin-app.md`. Decision: HLD D-37.

## 1. What was built

- **The connection table.** `bf_connection` in `BrokerForgeCore` holds what the administrator types: name, base URL, authentication settings, table prefix, solution, the pasted OpenAPI text and the answers as JSON (the design format's answer keys). It holds no secret.
- **Three admin Custom API actions**, served by the existing `CustomApiPlugin`, so the plug-in code is unchanged:
  - `bf_ReadDefinition` returns the review, the change list with its fingerprint, the environment's URL and key status, and the links to the host's pages.
  - `bf_Generate` starts the background job and writes its progress to the row.
  - `bf_ListConnections` lists the rows and the designs that arrived by import.

  The host answers all three before any descriptor lookup.
- **Who may set up connections.** Each API requires `prvWritebf_connection`, and the platform enforces that. The host then requires the caller, found by Entra object ID, to hold the `BrokerForge administrator` role from `BrokerForgeCore`, directly or through a team. A hand-made role with that name doesn't count, and a call without an object ID is refused.
- **The screens** are HTML web resources (`src/BrokerForge.WebResources`): the Review tab on the connection form, and the Connections page in the `BrokerForge` model-driven app. The page logic in `review-model.js` is tested with node.
- **Host pages** behind the connect app's sign-in:
  - `/admin/{env}/{instance}/secret` sets a connection's keys. Only an administrator of that environment can use it, and the value is never written back to the page or a log.
  - `/admin/{env}/{instance}/role` takes the role step as the signed-in administrator, with their own delegated Dataverse token. The connect app gained Dynamics CRM `user_impersonation`.
- **A lease outside the process.** The lease in the credential table keeps one Generate per source across replicas and the admin command. That meets the D-36 constraint.
- **The core as a managed solution.** `solution/BrokerForgeCore` holds the core as unpacked source. `tools/dev/export-core.sh` writes it and packs `out/BrokerForgeCore_managed.zip`, and `tools/dev/install-core.sh` installs that package in another environment.

## 2. Live check in dev

`WarehouseUiAPI` was onboarded from `tools/mock-api/inventory-openapi.yaml`. There was no browser in the session, so the review page's calls were made through the Web API as the az CLI user. Those are the same Custom APIs, plug-in and host.

| Step | Result |
| --- | --- |
| Create the row, `bf_ReadDefinition` | NeedsAnswers. Paging, the two filters, stable order, the price precision and the create's 422 message were asked, and there was no change list, so Generate is disabled (done criterion 3). |
| Save the answers (table renamed `ext_uiitem`, `adjustStock` unticked), read again | Ready, with a fingerprint, the role change pending, and nothing blocked or refused. |
| `bf_Generate` with the fingerprint | Succeeded as the host, design v1, with the role left to an administrator. It took six privilege grants first (section 3). |
| Key | Set with `tools/dev/set-instance-secret.sh`. The secret page needs a browser sign-in, so the page itself is tested only by contract tests. |
| Role | `tools/dev/generate-in-host.sh WarehouseUiAPI BrokerForgeWarehouseUiAPI --with-role`, run as the administrator: the same `RoleGenerator` the role page runs. The page itself is tested only by contract tests. |
| List, open, create, update, delete `ext_uiitems` | All worked. A read after the delete gives NotFound, which surfaces as `0x80040265` (HLD 15). |
| `bf_ListConnections` | `WarehouseAPI` ready (imported, no row), `WarehouseUiAPI` ready (row). |
| No secret (done criterion 7) | The test key was found in none of these: the exported `BrokerForgeWarehouseUiAPI` solution, the host's last 300 log lines, the connection row, or its two environment variables. |
| `tools/dev/acceptance.sh`, `acceptance_inventory.sh` | Both pass. |

## 3. Surprises

- **Creating a new table as the host needs more than D-36 listed.** Plan 3's live runs only updated an existing table. These privileges were found one at a time and granted with Simon's approval (`tools/dev/setup_dataverse.py`):
  - create, write and delete on CustomControlDefaultConfig;
  - read, create, write and append on system jobs (asyncoperation);
  - create, write and delete on relationships;
  - read on system charts.

  The permission check in this session refused each new grant until Simon allowed the script to run.
- **The core's host variables travelled with dev's values.** `bf_HostUrl` and `bf_HostScope` had dev's URL as their default value, and their current values were part of the solution. Every export, including the committed `solution/` source in a public repo, would have carried dev's host URL. Both now have no default, and their values are set outside the solution. `RemoveSolutionComponent` takes the component's own ID in the `solutioncomponentid` field.
- **An unpublished app can't be read back.** A new model-driven app isn't readable as a row until it's published: `return=representation` on the create gives 404. The setup finds it with `RetrieveUnpublishedMultiple`.
- **The assembly's managed identity is a dev record.** The plug-in assembly's `ManagedIdentityId` points at a dev-only record, so the export strips it, and `install-core.sh` binds the assembly to the target environment's own record.
- **Option values need the publisher's prefix.** `bf_authmode` uses the `bf` publisher's option prefix: values 100010000 to 100010004.

## 4. Not verified

- The secret page and the role page in a real browser with a real Entra sign-in. Contract tests cover them, including the admin's token being the one used for the role writes and never being logged.
- A non-administrator being refused by the live host. There is no non-admin token; the contract tests cover a System Customizer without the role.
- Installing the managed core into a second environment. No second environment exists; plan 5 runs done criterion 4.
- Done criterion 6 (a non-administrator gets Read through the generated role), as in plan 3.

## 5. Calls made while building (from the ledger)

- The design builder takes no per-environment auth overrides: in the source environment the design carries the authentication.
- Generate does not wait for keys. Spec 5 enables it when every ticked endpoint is Ready, and the key shows as "needs key" beside it.
- The secret page takes its key names from the row where the connection is edited, else from the imported design. The role page exists only where the row is.
- All three admin APIs are actions, including the read-only ones.
- Plan 1's deferred connect-page minor had already been fixed by plan 1's final review; a regression test now pins it.
- Component GUIDs (assembly, plug-in types, provider, forms, views) stay in `solution/`. They are solution component IDs, not tenant, environment, org or user IDs.

## 6. Tests

Contract 392, node 12 (`node --test 'tests/webresources/*.test.js'`), SwaggerForge 86. The full suite passes.

## 7. Final review

A fresh review of the whole branch found nothing Critical, two Important findings and seven Minor ones.

The two Important findings are fixed, each with a test that failed first:

- **I1:** a disabled Dataverse user who still held the administrator role passed the check. Only enabled users count now.
- **I2:** two connections with one name (any case) broke the list and would have generated onto one instance, its keys and its lease. Generate and the host pages now refuse with a 409 naming the clash, and the list shows both rows as "duplicate name".

The seven minors were then fixed too, at Simon's request (5 November), each with a test that fails without its fix:

- **M1:** the fingerprint `bf_ReadDefinition` returns covers the design as well as the change list. An answer changed after the review, even one that changes only a descriptor, refuses Generate with a 409 and writes nothing.
- **M2:** a failure while working out the change list (a Dataverse refusal, a generator refusal, a schema or descriptor error) is returned in `preview.error` with its cause, and the page keeps Generate disabled.
- **M3:** async calls to the admin APIs are refused.
- **M4:** three lease fixes.
  - A host with designs and no credential table refuses to start unless `Credentials:InMemory` asks for the in-memory lease.
  - The lease is taken without the caller's cancellation token, and a storage failure there is a "try again" refusal.
  - A late report from an older job no longer overwrites the row of a job started after it.
- **M5:** tests added for a null and a too-long definition, role start without antiforgery, a callback in another browser, after 10 minutes, and with a connect-page state.
- **M6:** every host page sends `Content-Security-Policy: frame-ancestors 'none'` and `X-Frame-Options: DENY`.
- **M7:** the plug-in builds with a `PathMap`. It was re-signed, re-registered and re-exported, and `export-core.sh` refuses to write source that contains a home-directory path.

Tests after the fixes: Contract 411, node 13.
