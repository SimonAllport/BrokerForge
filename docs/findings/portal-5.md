# Portal connections plan 5: today's sources on connections

Results of 6 November 2026 against the dev environment and a second environment, **BrokerForge test** (a Developer environment created for this). Plan: `docs/superpowers/plans/2026-11-06-portal-connections-5-migration.md`. Decision: HLD D-38.

## Done criteria (spec 12)

| # | Result |
| --- | --- |
| 1 | Inventory set up through the screens: New connection, questions, answers, Generate, the administrator's step, the key, then list, open, New and Save, Delete. The full run, with the form, was recorded in the second environment (video part 1). In dev everything worked except the record form (section 4). |
| 2 | Pass: the undeclared filter and the unmapped 500 (`acceptance.sh`), the undeclared filter (`acceptance_inventory.sh`) |
| 3 | Pass: questions shown, Generate disabled until answered |
| 4 | Pass: imported into BrokerForge test; needs URL, then needs key, both typed and before any request; then everything works (section 6) |
| 5 | Pass: removal and type change listed, a hand-made view blocks and is named, then applied as the host and restored (section 4) |
| 6 | Pass: a second user reads through the generated role alone; admin messages refuse them (section 5) |
| 7 | Pass: 0 matches (section 7) |
| 8 | Pass: Orders, Inventory and both Tickets sources run from designs; no source settings or descriptor files in Bicep or the image |
| 9 | HLD D-05, D-10, D-16, D-30, sections 6, 14 and 15 and D-38 updated; the guide and the video are redone for the screens |

## 1. What changed

- **SwaggerForge asks four more questions.** Where a header's value comes from (`op.<op>.header.<name>`), where a body property with no column comes from (`op.<op>.body.<path>`), whether to retry (`op.<op>.retry`; a create only with an `Idempotency-Key` header answered `uuid`, as the validator requires), and the table's external name (`table.externalName`), so an existing table keeps the name it was made with. `docs/connection-design-format.md` lists them.
- **The definitions reproduce today's descriptors.** The Orders definition now sits beside the mock (`tools/mock-api/orders-openapi.yaml`), and with its answers it drafts all five committed descriptors and the schema. There are two differences: `createOrder`'s account is a fixed value rather than instance config, and its `409: Conflict` isn't drafted, because the definition documents no 409 and the mock never sends one. Both Tickets sources reproduce from `tickets-openapi.yaml`, TicketsEntraAPI apart from its operation names.
- **An Impersonate connection uses the connect app.** Plan 4's `DesignBuilder` built no `auth.oauth` for Impersonate, so an Impersonate connection made on screen would have been refused by the host. Found while writing the Tickets tests.
- **The host runs with designs only.** An unset `Config:DescriptorsPath` means no file descriptors, not a startup error. The image carries no `descriptors/` or `schemas/`, and Bicep has no `Instances`, `ObjectMaps`, `OAuthResources` or descriptor path settings. It now allows TicketsEntraAPI's scope (`DesignScopes`) and the Tickets mock's private name, and takes an optional second environment.
- **The connect page no longer blames the key.** It used to say "its stored key `clientSecret` is not set" for any configuration problem. Live, the problem was the egress rule refusing the Tickets mock's token URL, while the key was set. The page now says there is a configuration problem and gives a reference; the host's log line has the same reference and the real message, which can name hosts but never a value.
- **Dev tools.** `tools/dev/migrate-sources.sh` writes the connection rows of Orders and both Tickets sources and generates them through `bf_ReadDefinition` and `bf_Generate`. `remove_old_inventory.py` removes the old Inventory from dev. `setup-plugin-app.sh` makes an environment's own plug-in app, and `install-core.sh` takes it.

## 2. Moving Orders and Tickets

| Step | Result |
| --- | --- |
| Deploy the SwaggerForge changes with the files still in | `acceptance.sh` 14 passed, `acceptance_inventory.sh` 17 passed |
| `migrate-sources.sh` (review only) | All three Ready. No table changes; Generate would add the form and view, the design and its variables; the role is left to an administrator |
| `migrate-sources.sh --generate` | Three jobs Succeeded, design v1, role pending. The files still served: `acceptance.sh` passed |
| Roles, `generate-in-host.sh --with-role` | 2 writes each |
| Deploy without files or settings, set the two keys | `bf_ListConnections`: OrdersAPI, TicketsAPI, TicketsEntraAPI, WarehouseAPI and WarehouseUiAPI ready |
| `acceptance.sh` from the design | All 14 assertions passed, including the undeclared filter and the unmapped 500 |
| Tickets before connecting | Both tables give `ConnectionRequiredException` with `/connect/<environment>/<instance>`: credentials saved under the bare name are not used |

## 3. What driving the screens found

Plan 4's pages had never been used in a real browser. Recording Inventory through them found these, each fixed with a test that failed first:

- **Review page.** Its buttons were hidden on a new connection; the connection ID went to `Xrm.WebApi` as a bare string, which arrives as `undefined` (it must be `{ guid }`); answers were written to a form field that isn't on the form, so they were never saved; and after Generate started, the page could stop following it before the job's first report.
- **Generate as the host can't create a Custom API.** Dataverse needs `prvCreateSdkMessageProcessingStep`, and leaves it out of the host user's effective privileges (93 of the role's 94) even when its role holds it at organisation depth. Simon chose an administrator step: Generate as the host leaves creating, recreating and removing actions pending with the role, and the host's administrator page runs Generate as the signed-in administrator, with the fingerprint of the list it showed. `prvCreateSdkMessage` was granted first, by the same route.
- **The generated view sorted on the primary name.** Inventory's list declares no sort, so the app's list failed with `UnsupportedQueryException`, which the app shows as "You do not have permission". Generated views no longer sort, and a generated view that does is rewritten. Tickets had the same problem.
- **The data source record wasn't in the source's solution,** so importing it elsewhere failed with a missing dependency (component type 183). Generate now adds it.
- **The connect app's Dataverse consent** had never been given. The recording accepts it for the signed-in user only.

## 4. Inventory through the screens (done criteria 1 to 3, 5)

| Step | Result |
| --- | --- |
| Old Inventory removed (`remove_old_inventory.py`) | `ext_items` 404; the mock keeps its 45 items |
| New connection, Save, Save and read | Questions shown (paging, two filters, stable order, price precision, 422 messages); Generate disabled (criterion 3) |
| Answers, Save answers and check | Ready; the preview lists the table, its columns and the action |
| Generate as the host | Succeeded; the action and the role left for an administrator |
| Finish as administrator (Entra sign-in for Dataverse, consent for this user) | Action `ext_AdjustStock` and role `InventoryAPI user` created as the administrator |
| Set key on the host's page | Connection ready |
| The table in the app | Lists the items. **The record form fails in dev** (see below) |
| `acceptance_inventory.sh` | All 17 assertions passed, including the undeclared filter (criterion 2) |
| Read again: `sku` removed and `price` an integer, with a hand-made view using SKU | Changes listed: remove SKU, change Price from decimal 2 to int; SKU blocked, naming the view; Generate refused, nothing written |
| The same with the view deleted | First run: Generate as the host failed at `delete column ext_price`, needing `prvReadAttributeMap`, which Simon then approved. After the grant: Succeeded, 12 writes; SKU gone, Price a whole number, the default view without SKU (criterion 5) |
| Original definition restored | Listed add SKU and change Price back; Succeeded, 12 writes; columns as before; `acceptance_inventory.sh` passes again |

**Platform constraint, dev only.** `ext_item` was deleted and remade about ten times today. Since then Dataverse answers 502 to `GetClientMetadata` for its record form, while the identical form of a new table (`ext_invitem`) opens, and the same solution's `ext_item` form opens in the second environment. Removing the action and the role made no difference. Not caused by anything BrokerForge writes; the video's form scenes wait on it.

## 5. A non-administrator (done criterion 6)

The second test user (signed in by Simon in media profile `b`) has Basic User and the hand-made Tickets role from phase 3, nothing for Inventory. Calls from that user's own browser session (`PROFILE=b node tools/media/check-reader.mjs`):

| Roles | List `ext_items` | `bf_ListConnections` |
| --- | --- | --- |
| Basic User, BrokerForge Tickets user | 403, missing `prvReadext_item` | 403, missing `prvWritebf_connection` |
| plus the generated `InventoryAPI user`, nothing else | 200, 3 rows | 403, missing `prvWritebf_connection` |

So the generated role alone gives Read, and the admin messages refuse a non-administrator (plan 4 left that unverified live). A user created for this, `bf-reader`, was stopped by the tenant's MFA registration and deleted.

## 6. Second environment (done criterion 4)

**BrokerForge test**, a Developer environment created through the Power Platform admin API (a Sandbox needs paid capacity), with its own plug-in app (`setup-plugin-app.sh`), the managed core (`install-core.sh`) and the host's `Trust:AllowedCallers:1`.

| Step | Result |
| --- | --- |
| Import `BrokerForgeInventoryAPI` (managed) | Succeeded once the data source record was in the solution |
| `bf_ListConnections` | InventoryAPI: needs URL |
| Read `ext_items` | `ConfigurationError`: needs its base URL, naming `bf_InventoryAPI_BaseUrl`. The router refuses before any adapter runs, so no call reaches the source |
| Set `bf_InventoryAPI_BaseUrl` there | needs key; a read gives `ConfigurationError` naming the key, again before any request |
| Set the key for that environment | ready; list, create, open, update and delete all work |
| The `ext_item` form there | Opens with all six fields |

## 7. No secret anywhere (done criterion 7)

The values of `orders-api-key`, `inventory-api-key` and `tickets-client-secret` were compared, printing counts only, against the managed and unmanaged exports of BrokerForgeOrders, BrokerForgeTickets, BrokerForgeTicketsEntra, BrokerForgeInventoryAPI and BrokerForgeCore, every `bf_connection` row, every environment variable definition and value, and the host's log stream (the current revision's 96 lines). 0 matches. Older logs in Application Insights were not searched, because that would mean sending the key in a query.

## 8. Final review

A fresh review of the branch found no Critical problems and five Important ones. Four are fixed, each with a test that failed first:

- the administrator's page could apply changes it never showed: it now refuses while Generate itself has changes left, and the administrator's run refuses anything but actions and the role;
- the confirmed list was taken when the button was pressed: the page now carries the fingerprint it showed;
- the administrator's run took no lease: it now takes Generate's;
- the row went on saying a step was left: the administrator's run now reports on the row.

The fifth, a publish that fails at 429 and is never retried, is recorded in HLD 15 for a live check first. The host role's two SDK privileges, no longer needed, were taken off. Deferred minors: the review page can poll forever if the row never shows the job it started; no test for the callback's confirmation refusal; the connect page shows configuration messages, which name hosts, to end users; retry is accepted on a POST action without an idempotency check.
