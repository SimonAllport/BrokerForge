# Portal connections plan 5: migrate today's sources Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Orders, Inventory, TicketsAPI and TicketsEntraAPI run from connection designs in Dataverse, their host settings and baked-in files are gone, and every done criterion in spec 12 has been run live.

**Architecture:** Two code changes, then live work. SwaggerForge learns three more questions (where a header's value comes from, where a body property with no column comes from, and whether to retry), so the Orders definition reproduces today's descriptors. The host learns to start with no descriptor files at all. Then the dev environment is cut over: designs are generated while the files still serve (the host refuses a design whose name the files use, so nothing changes yet), and one deploy removes the files and settings so the designs take over. Inventory is deleted from dev and onboarded again through the screens, which is also part 1 of the new video. A second environment gets the core and the Inventory solution.

**Tech Stack:** .NET 8 host, SwaggerForge (`tools/SwaggerForge`), xUnit, Python dev scripts (`/usr/bin/python3`), Bicep, Playwright and ffmpeg (`tools/media`).

**Spec:** `docs/superpowers/specs/2026-11-03-portal-connections-design.md` (sections 10, 11 and 12 above all). Earlier results: `docs/findings/portal-1.md` to `portal-4.md`; what the Orders definition still lacks is in `portal-2.md` section 2.

## Global Constraints

- No change to `BrokerForge.Contracts` or `docs/descriptor-spec.md`. New answer keys go in `docs/connection-design-format.md` ("Answer keys"); the design format stays at version 1.
- No secret in any Dataverse row, environment variable, solution, log, trace, fixture or exception message. Keys move from Key Vault to the instance secret store only through `tools/dev/set-instance-secret.sh`, which never prints them.
- The repo is public. No real tenant, environment, org or user IDs in committed files (use contoso and all-zero GUIDs). The second environment's IDs live in `~/.brokerforge/dev-ids.env`.
- Fail loudly: SwaggerForge never fills a gap with a guess. Each new gap is a question, or the endpoint stays out.
- "Matches" means the same behaviour: `tools/dev/acceptance.sh` and `tools/dev/acceptance_inventory.sh` pass, and so do the contract tests. Descriptor YAML may differ in names that change no behaviour (option set map names, operation names).
- The contract tests keep loading `descriptors/` and `schemas/` from files. Those directories stay in the repo; only the image stops carrying them.
- `bf validate` stays in CI. `bf draft` and `bf generate` stay as developer tools but leave the guide.
- Environment trust (`Trust:AllowedCallers`) stays in Bicep (spec 11).
- Use `/usr/bin/python3` for dev scripts. In zsh, never name a variable `path`. `az containerapp exec` allows about 8 calls, then 10 minutes' wait.
- Widening the host identity's privileges needs Simon's approval.

## Review Focus

1. **A Tickets user's stored token from before the cutover.** It is stored under the bare instance name, and a design instance looks under `<env>_<instance>`. The user must get `ConnectionRequiredException` with the connect URL, never a 500 and never the service account. Test in Task 2.
2. **The host with no descriptor or schema path and no designs for an environment.** It must start, and a call for an unknown table must fail with the typed unknown or `ConfigurationException`, not a null reference. Test in Task 3.
3. **An optional header answered `none`, and a required header.** `none` leaves the header out; a required header must not offer `none`. Test in Task 1.
4. **Retry on a non-idempotent create.** `op.<op>.retry: yes` on a POST with no header answered `uuid` must be refused as an invalid answer naming the key, because the engine needs an Idempotency-Key binding. Test in Task 1.
5. **A design imported into an environment with no base URL value.** Calls must throw the typed "needs URL" exception naming `bf_<Instance>_BaseUrl` and never reach a source. There is a unit test from plan 1; Task 8 checks it live.

## Calls made in this plan (for Simon to check)

- Orders' headers, `placedBy` and `retry` become questions rather than being dropped (spec 6.1 allows extending SwaggerForge).
- Orders and both Tickets sources are migrated by a script that calls the same Custom APIs the screen buttons call (`bf_ReadDefinition`, `bf_Generate`). Inventory goes through the real screens, because done criterion 1 needs that.
- Dev's existing Inventory (solution `BrokerForgeInventory`, table `ext_item`, Custom API `ext_AdjustStock`) is deleted so that criterion 1 starts from "no source exists". Virtual tables hold no data; the mock keeps the items.
- Tickets users connect again once after the cutover, because design credentials sit in an environment partition (portal-1). Old rows are left in place and ignored.
- The second environment is one Simon names or creates. Its IDs go in `~/.brokerforge/dev-ids.env` as `SECOND_ENVID` and `SECOND_ORGURL`, and `deploy.sh` adds `Trust:AllowedCallers:1` when they are set.

## Stops that need Simon

- Task 7: a sign-in as a second, non-administrator user (done criterion 6).
- Task 8: a second environment to install into (done criterion 4).
- Tasks 6 and 9: `node signin.mjs a` again if the media profile's sign-in has expired (MFA).

---

### Task 1: The Orders definition beside the mock, with header, body and retry questions

**Files:**
- Move: `tests/SwaggerForge.Tests/fixtures/orders-openapi.yaml` → `tools/mock-api/orders-openapi.yaml`
- Modify: `tools/mock-api/orders-openapi.yaml` (add the `X-Correlation` and `Idempotency-Key` header parameters to `createOrder`, `X-Correlation` to the other four, `placedBy` as a create-body property that the order item schema lacks, and a 422 response with an `error.detail` body on `updateOrder`)
- Modify: `tools/SwaggerForge/Drafter.cs`, `tools/SwaggerForge/Review.cs` (questions), `tests/SwaggerForge.Tests/fixtures/orders-answers.json`
- Modify: `tests/SwaggerForge.Tests/ReproduceTests.cs`, every test that reads the old fixture path (grep `orders-openapi`), `tests/BrokerForge.Contract.Tests/Admin/DesignBuilderTests.cs` if it reads it
- Test: `tests/SwaggerForge.Tests/HeaderBodyRetryTests.cs`
- Docs: `docs/connection-design-format.md` (answer keys)

**Interfaces:**
- Produces three answer keys:

| Key | Values | Asked when |
| --- | --- | --- |
| `op.<op>.header.<name>` | `correlationId`, `uuid`, `userObjectId`, `value` (then `op.<op>.value.<name>` is asked), `none` (only when the header isn't required) | a header parameter that isn't the authentication's header or in `DraftOptions.ReservedNames`; required headers must be answered, optional ones default to not sent |
| `op.<op>.body.<path>` | `userObjectId`, `correlationId`, `none` (only when the property isn't required) | a create or update body property that matches no column of the item |
| `op.<op>.retry` | `no`, `yes` | optional on every operation, absent means `no`; on a POST, `yes` is valid only when one of its headers is answered `uuid` |

- Emits: header answers as `headers: { <name>: { context: … } | { computed: uuid } | { static: … } }`; body answers as `{ context: … }` in `body.template`; `retry: yes` as `retry: { idempotent: true, attempts: 3, on: [429, 503, network] }`.
- A header the descriptor spec reserves (rule 4): required makes the endpoint Unsupported, naming the header; optional is not asked and not sent.

- [ ] **Step 1: Write the failing tests** in `HeaderBodyRetryTests.cs`, each on a small inline definition, in the style of `KeyKindTests`:
  - `Review_RequiredHeader_AskedWithoutNone`: the question's choices are `correlationId, uuid, userObjectId, value`.
  - `Review_OptionalHeaderUnanswered_NotSent`: Ready, and the descriptor has no `headers`.
  - `Review_OptionalHeaderNone_NotSent` and `Review_HeaderValue_AsksForTheValueAndSendsItStatic`.
  - `Review_AuthHeader_NotAsked`: an `X-Api-Key` header parameter that the security scheme names is not asked.
  - `Review_ReservedRequiredHeader_Unsupported`: a required `Authorization` header parameter, with the reason naming it.
  - `Review_BodyPropertyWithNoColumn_AskedAndBound`: `userObjectId` gives `placedBy: { context: userObjectId }`.
  - `Review_RetryYesOnPostWithoutUuidHeader_InvalidAnswerNamingKey`, `Review_RetryYesOnPostWithUuidHeader_EmitsRetry` and `Review_RetryAbsent_NoRetry`.
- [ ] **Step 2: Run them.** `dotnet test tests/SwaggerForge.Tests --filter "FullyQualifiedName~HeaderBodyRetryTests"`. Expected: FAIL, because no question is asked.
- [ ] **Step 3: Implement the questions** in the drafter and review, beside the existing `op.<op>.value.<param>` and `op.<op>.error.<status>` questions. Reuse their wording style: plain words and fixed choices.
- [ ] **Step 4: Move the Orders definition and change the reproduction test.** Edit the definition as listed under Files, and add the answers to `orders-answers.json` (`header.X-Correlation: correlationId` on all five, `header.Idempotency-Key: uuid` and `retry: yes` on create, `body.placedBy: userObjectId` on create, and update's 422 message `$.error.detail`). Rename `Reproduce_Orders_ListAndGetExactly_RestApartFromWhatTheDefinitionOmits` to `Reproduce_Orders_AllFiveMatchTheCommittedDescriptors`. It should assert that the five descriptor trees equal `descriptors/OrdersAPI/*.yaml`, with option set map names compared by position (as today) and nothing else excused. Check whether today's Orders `retry` is on update and delete too, and answer to match. Inventory's reproduction must stay exact without new answers.
- [ ] **Step 5: Run.** `dotnet test tests/SwaggerForge.Tests` and `dotnet test tests/BrokerForge.Contract.Tests --filter "FullyQualifiedName~DesignBuilder"`. Expected: PASS, with Inventory still exact.
- [ ] **Step 6: Docs and commit.** Add the three keys to `docs/connection-design-format.md`. Then `git commit -m "feat(swaggerforge): header, body and retry questions; Orders definition beside the mock reproduces all five descriptors"`.

### Task 2: Tickets answers, and the credential partition after cutover

**Files:**
- Create: `tests/SwaggerForge.Tests/fixtures/tickets-answers.json`, `tests/SwaggerForge.Tests/fixtures/tickets-entra-answers.json`
- Modify: `tests/SwaggerForge.Tests/ReproduceTests.cs`, `tests/BrokerForge.Contract.Tests/Admin/DesignBuilderTests.cs`
- Test: a credential-partition test beside the existing `CredentialPartition` tests in `tests/BrokerForge.Auth.Tests` (grep `CredentialPartition`)

**Interfaces:**
- Consumes: `DraftService.Review`, `DesignBuilder.Build(row, review)`, and `ConnectionRow` (as built by `DesignBuilderTests.Inventory`).
- Produces: the two answer files Task 4's migration script sends. `tickets-entra-answers.json` sets `table.name` to `ext_entraticket` and the display names to match `schemas/TicketsEntraAPI.yaml`. Its operation names stay `listTickets`, `getTicket` and `createTicket`, which is a name difference only.

- [ ] **Step 1: Write the failing tests:**
  - `Reproduce_Tickets_MatchesCommittedDescriptors` and `Reproduce_TicketsEntra_MatchesCommittedDescriptorsApartFromOperationNames`, both from `tools/mock-api/tickets-openapi.yaml`.
  - `Build_TicketsOAuthOwnClient_LikeTheBicepSettings`: Mode OAuth, scopes `[tickets.read]`, client ID `bf-tickets`, `SecretNames == ["clientSecret"]`, KeyStrategy reversible, KeyKind String.
  - `Build_TicketsEntraImpersonate_ConnectApp`: Mode Impersonate, `UsesConnectApp`, scopes `[api://00000000-0000-0000-0000-000000000000/user_impersonation, offline_access]`, no secret names.
  - `Partition_DesignInstance_DoesNotMatchBareNameRow` (Review Focus 1): a credential stored under `TicketsAPI` isn't found for a design instance `TicketsAPI` with an environment ID, and the provider throws `ConnectionRequiredException` with a connect URL. Use the existing provider test setup in `tests/BrokerForge.Auth.Tests`.
- [ ] **Step 2: Run.** Expected: the reproductions FAIL until the fixtures exist. The partition test may pass straight away, since plan 1 built the partition. If it does, keep it as a pinning test and say so in the ledger.
- [ ] **Step 3: Write the two answer fixtures** until the reproductions pass. If a difference can't be closed with an answer, stop and record it in the ledger. Don't excuse it in the test.
- [ ] **Step 4: Run** `dotnet test BrokerForge.slnx > <workspace>/t2.log; tail -5 <workspace>/t2.log`. Expected: all pass.
- [ ] **Step 5: Commit** `test: Tickets sources reproduce from their definition; old credential rows ask for a new connection`.

### Task 3: A host with no descriptor files, and Bicep without source settings

**Files:**
- Modify: `src/BrokerForge.Host/Config/FileConfigInput.cs` (only if the test fails), `src/BrokerForge.Host/Dockerfile` (drop `COPY descriptors/` and `COPY schemas/`, and fix the comment)
- Modify: `infra/containerApps.bicep`: remove every `Instances__*`, `ObjectMaps__*` and `OAuthResources__*` setting, the `orders`, `map`, `inventory`, `itemMap`, `tickets`, `ticketMap`, `entraTickets` and `entraTicketMap` variables, and `Config__DescriptorsPath` and `Config__SchemasPath`. Keep `Connect__*`, the mock's settings and `Config__Designs__*`. Add `Trust__AllowedCallers__1__*` from new optional parameters `secondEnvironmentId` and `secondOrgUrl`, only when they are set.
- Modify: `infra/main.bicep`, `tools/dev/deploy.sh` (pass `SECOND_ENVID` and `SECOND_ORGURL` from `~/.brokerforge/dev-ids.env` when present)
- Test: `tests/BrokerForge.Contract.Tests/Config/NoFileConfigTests.cs`

**Interfaces:**
- Produces: a host image that serves only designs. **Don't deploy it before Task 5 step 3.**

- [ ] **Step 1: Write the tests** (Review Focus 2):
  - `FromConfiguration_NoPaths_EmptyStoreNoProblems`.
  - `Host_NoFiles_StartsAndServesADesign`: `HostFactory` with `Config:DescriptorsPath` and `Config:SchemasPath` unset, and one design through the existing design fake (see the plan 1 tests, grep `DesignConfigInput`). A list call succeeds.
  - `Host_NoFiles_UnknownTable_TypedError`: an unknown table gives the existing typed unknown-table error, not a 500.
- [ ] **Step 2: Run** `dotnet test tests/BrokerForge.Contract.Tests --filter "FullyQualifiedName~NoFileConfig"`. Expected: either FAIL, which you then fix in `FileConfigInput`/`HostConfigStore`, or PASS. If they pass, ledger that they're pinning tests.
- [ ] **Step 3: Edit the Dockerfile and Bicep.** Run `az bicep build --file infra/main.bicep --stdout > /dev/null`. Expected: exit 0. Then `grep -c 'Instances__\|ObjectMaps__\|OAuthResources__\|DescriptorsPath' infra/*.bicep`. Expected: 0 on every file.
- [ ] **Step 4: Full suite.** `dotnet test BrokerForge.slnx`, and the CI validate command from CLAUDE.md. Expected: all pass.
- [ ] **Step 5: Commit** `feat(host): run with designs only; remove source settings and baked-in descriptors from the image and Bicep`.

### Task 4: Migration script

**Files:**
- Create: `tools/dev/migrate_sources.py`, `tools/dev/migrate-sources.sh`
- Reuse: `tools/dev/acceptance.py` (`call`) and the `dv` helpers. `core_admin.py` shows how rows and Custom APIs are written.

**Interfaces:**
- Consumes: the answer fixtures from Tasks 1 and 2; `bf_ReadDefinition` and `bf_Generate` (inputs `ConnectionId`, plus `Fingerprint` for Generate); the review JSON (`status`, `preview.fingerprint`, `preview.changes`, `preview.pending`, `preview.error`) from `src/BrokerForge.Host/Admin/AdminApi.cs`.
- Produces: `migrate-sources.sh [--generate] <Instance>...`, where the instance is one of `OrdersAPI`, `TicketsAPI` or `TicketsEntraAPI`. For each one it:
  - creates or updates the `bf_connection` row (name, base URL, mode and its non-secret settings, prefix `ext`, the solution that exists today: `BrokerForgeOrders`, or the Tickets one found in dev, the definition text and the answers);
  - calls `bf_ReadDefinition` and prints the status, the change list and what's pending;
  - with `--generate`, calls `bf_Generate` with the printed fingerprint and polls the row until the job ends.

  The base URLs and the Tickets OAuth URLs come from the deploy outputs (as `deploy.sh` reads them), and the Entra app ID from `~/.brokerforge/dev-ids.env`. The row values are the same as those the Bicep settings had.

- [ ] **Step 1: Write the script.** It has no unit tests; dev scripts are verified live. It refuses to send `--generate` when the change list has anything except additions or parts. It prints the list and exits 4, as `bf generate` does.
- [ ] **Step 2: Dry check.** `tools/dev/migrate-sources.sh OrdersAPI` (no `--generate`). Expected: Ready. The change list is empty or adds only (parts: design publish, variables, role pending). Read the full output for removals. Any removal or recreate is a ledger ruling before going on.
- [ ] **Step 3: Commit** `feat(dev): migrate-sources creates the connection rows for today's sources and generates them`.

### Task 5: Live cutover in dev (Orders and both Tickets sources)

- [ ] **Step 1: Deploy the Task 1–2 host with the files still in.** Check out the commit before Task 3 into a temporary worktree and run `tools/dev/deploy.sh` from it, so that the new SwaggerForge serves while the settings remain. Expected: `acceptance.sh` and `acceptance_inventory.sh` pass.
- [ ] **Step 2: Generate the three designs.** `tools/dev/migrate-sources.sh --generate OrdersAPI TicketsAPI TicketsEntraAPI`. Expected: each job Succeeded with the role pending. `bf_ListConnections` shows each as "needs key" (Orders, TicketsAPI) or ready (TicketsEntraAPI). The host log names each design as left out for clashing with the files, and the acceptance scripts still pass, because the files serve. Apply the roles as administrator: `tools/dev/generate-in-host.sh <Instance> <Solution> --with-role` for each. Mind the exec limit.
- [ ] **Step 3: Cut over.** Deploy the branch head (Task 3 included). Then set the keys: `tools/dev/set-instance-secret.sh OrdersAPI key orders-api-key` and `tools/dev/set-instance-secret.sh TicketsAPI clientSecret tickets-client-secret`. Expected: the host log shows the designs loaded with no failures. `bf_ListConnections` shows OrdersAPI, TicketsAPI and TicketsEntraAPI ready, Inventory missing, and WarehouseAPI and WarehouseUiAPI unchanged.
- [ ] **Step 4: Orders acceptance.** `tools/dev/acceptance.sh`. Expected: every assertion passes, including the undeclared filter and the unmapped 500 (done criterion 2, Orders). Also check one create in the mock's log for `Idempotency-Key` and `placedBy`.
- [ ] **Step 5: Tickets.** List `ext_tickets` and `ext_entratickets` as the az user. Expected: `ConnectionRequiredException` with a connect URL under `/connect/TicketsAPI` (Review Focus 1, live). Connect as the mock's `alice`, then as `bob`, with `tools/media` (the part 2 flow, off camera), and list each time. Expected: different rows for alice and bob. TicketsEntraAPI returns the signed-in Entra user's rows.
- [ ] **Step 6: Record** the results in `<workspace>/live.md` for Task 9's findings. Commit any script fixes.

### Task 6: Inventory through the screens (done criteria 1, 2, 3, 5), recorded as video part 1

**Files:**
- Modify: `tools/media/record.mjs`. Part 1 becomes the screens:
  - open the BrokerForge app, New connection;
  - Name `InventoryAPI`, base URL, Static, header `X-Api-Key`, prefix `ext`;
  - paste `tools/mock-api/inventory-openapi.yaml`;
  - Save and read the definition, and show the questions with Generate disabled;
  - answer them as `inventory-answers.json` says, and show Ready;
  - Generate, then Done; Set key on the host page;
  - open the table: list, open, New and Save, edit, Delete.
- Modify: `tools/media/README.md`. Remove `capture.mjs` and `console.html` if nothing else uses them.
- Create: `tools/dev/remove_old_inventory.py` (deletes `ext_AdjustStock`, the `ext_item` table and the `BrokerForgeInventory` solution in dev, and refuses to run against any org but dev's)

- [ ] **Step 1: Remove the old Inventory in dev.** Run `remove_old_inventory.py`. Expected: `ext_items` returns 404 from the Web API, and the mock still has its 45 items.
- [ ] **Step 2: Record part 1.** Run `node record.mjs part1` (sign in again first if asked). The key typed on the host page is read from Key Vault `inventory-api-key` by the script, never printed. Expected:
  - the video shows the questions and a disabled Generate before the answers (criterion 3);
  - after Generate and the key, `bf_ListConnections` shows InventoryAPI ready;
  - the table works on screen (criterion 1);
  - no terminal was used except to run the mock.
- [ ] **Step 3: Acceptance.** `tools/dev/acceptance_inventory.sh`. Expected: everything passes, including the undeclared filter (criterion 2, Inventory).
- [ ] **Step 4: Read again through the screens (criterion 5).** Do this in the browser, with a Playwright script that isn't recorded:
  - paste a copy of the definition without `sku` and with `price` as an integer;
  - make a hand-made view on `ext_item` that uses `ext_sku`;
  - Save and read: the review lists remove SKU (blocked, naming the view) and recreate Price, and Generate is disabled;
  - delete the view, Save and read, Generate;
  - expect SKU gone, Price an integer, and the form and view updated;
  - paste the original definition back, Generate, and run `acceptance_inventory.sh` again: it passes.
- [ ] **Step 5: Commit** the media changes and the removal script with `feat(media): part 1 shows onboarding through the screens`. Add the results to `<workspace>/live.md`.

### Task 7: A non-administrator (done criterion 6) — needs Simon

- [ ] **Step 1: Ask Simon** to assign `InventoryAPI user` to the second user (`TICKETS_BOB_OID` in `~/.brokerforge/dev-ids.env`) and sign in once as that user in a private window, or with `node signin.mjs b` if he prefers it scripted. That user should open the BrokerForge app's Inventory table, then the connections page.
- [ ] **Step 2: Expected:** the list shows the items, which is Read through the generated role, with no hand-made role. The connections page shows an error naming the missing privilege, and a `bf_ReadDefinition` attempt is refused, which is the live non-admin refusal plan 4 left unverified. Record what Simon reports, word for word, in `<workspace>/live.md`.

### Task 8: A second environment (done criterion 4) — needs Simon

- [ ] **Step 1: Ask Simon** for the environment: an existing spare one, or his go-ahead to create one with `pac admin create` (a sandbox with a Dataverse database). Put `SECOND_ENVID` and `SECOND_ORGURL` in `~/.brokerforge/dev-ids.env`. Run `tools/dev/deploy.sh`, which adds `Trust:AllowedCallers:1`. Then run `tools/dev/install-core.sh <org url> <env id>`. Expected: `bf_ListConnections` there returns an empty list.
- [ ] **Step 2: Move Inventory.** Export the Inventory solution from dev as managed, using `export_core.py`'s Web API export with the solution name, and import it into the second environment. Expected: the import succeeds, and `bf_ListConnections` there shows InventoryAPI as "needs URL", because the variable values stay outside the solution.
- [ ] **Step 3: Before the URL and key.** Read `ext_items` there. Expected: a typed "needs URL" error naming `bf_InventoryAPI_BaseUrl`. The mock's log shows no request. Set the URL variable, read again, and expect the typed "needs key" error with no request in the mock's log.
- [ ] **Step 4: Set the key on the host page** for the second environment (Simon in the browser, or `set-instance-secret.sh` with the second environment's ID). List, open, create and delete. Expected: all work. Record it in `<workspace>/live.md`.

### Task 9: Secrets check, docs, guide, video part 2, findings

**Files:**
- Modify:
  - `docs/hld.md`: D-05, D-10, D-16 and D-30 marked replaced by D-34 to D-37 with one line each on what replaces them; section 6 rewritten for `bf_connection` and designs, retiring the `bf_serviceinstance`, `bf_operation`, `bf_objectmap` and `bf_oauthresource` rows; the section 14 row for service instances and descriptors rewritten as solutions; a new decision D-38 for the three new questions and the cutover.
  - `CLAUDE.md`: current state, commands (`migrate-sources.sh`, `remove_old_inventory.py`), and the layout notes on `descriptors/` and `schemas/` as contract-test fixtures.
  - `docs/guide/how-to-guide.md` and its images: part 1 is the screens; part 2 connects to TicketsAPI as today; the `bf` steps are removed.
- Create: `docs/findings/portal-5.md`
- Rebuild: `docs/guide/BrokerForge How-to Guide.docx` (`node build-guide.mjs`); the video (`node record.mjs part2`, then the full join)

- [ ] **Step 1: Secrets search (criterion 7).** Search for the values of `orders-api-key`, `inventory-api-key` and `tickets-client-secret`, compared inside a script that prints only the count. Search:
  - the exported Orders, Inventory and Tickets solutions;
  - every `bf_connection` row;
  - the environment variable values in both environments;
  - the host's last 500 log lines.

  Expected: 0 matches everywhere.
- [ ] **Step 2: Write the docs** with the human-writing skill.
- [ ] **Step 3: Rebuild the guide and the video.** Check frames at the chapter times, and check that no real host, org or account name shows.
- [ ] **Step 4: Findings.** Write `docs/findings/portal-5.md`: a table of done criteria 1 to 9, each with its evidence from `<workspace>/live.md` or marked **Not verified** with the reason; what changed; the rulings.
- [ ] **Step 5: Full suite and commit.** Run `dotnet test BrokerForge.slnx`, `node --test 'tests/webresources/*.test.js'` and `cd tools/mock-api && npm test`. Expected: all pass. Commit `docs: portal-5 findings; HLD D-05, D-10, D-16, D-30, sections 6 and 14; guide and video for the screens`.
