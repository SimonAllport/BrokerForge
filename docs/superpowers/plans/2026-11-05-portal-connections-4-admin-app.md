# Portal connections, plan 4 of 5: the admin app

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An administrator onboards a REST source inside a BrokerForge model-driven app: fill in the connection, paste the OpenAPI text, answer the questions, Generate, set the key and apply the role, with no terminal.

**Architecture:** One Dataverse table, `bf_connection`, holds what the admin types: name, base URL, authentication settings, prefix, solution, the OpenAPI text and the answers as JSON. The host computes everything else on demand. `bf_ReadDefinition` returns the review (draft service output, change list with fingerprint, environment status, page links) as JSON. `bf_Generate` starts a background job under a lease held outside the process and writes progress to the row. `bf_ListConnections` lists this environment's connections, including designs that arrived by import, which are read-only. All three are unbound Custom APIs served by the existing `CustomApiPlugin`. The host answers them before its descriptor lookup and checks that the caller holds the BrokerForge administrator role. The screens are HTML web resources on the connection form and in the app. The secret page and the role page are host pages behind Entra sign-in. The role page gets the signed-in administrator's own Dataverse token and runs `RoleGenerator` as them (D-36). The core components are created by `setup_dataverse.py` in dev and packaged as a managed solution in `solution/` for other environments.

**Tech Stack:** .NET 8 minimal APIs, Azure.Data.Tables (existing), Dataverse Web API 9.2, plain JavaScript web resources tested with `node --test`, Python 3 dev scripts, Power Platform CLI (`pac`).

**Spec:** `docs/superpowers/specs/2026-11-03-portal-connections-design.md` (sections 5, 6 "Who may set up connections", 8, 9, 11). Earlier plans: `2026-11-03-portal-connections-1-config.md`, `2026-11-04-portal-connections-2-questions.md`, `2026-11-04-portal-connections-3-generator.md`.

## Calls this plan makes (the spec leaves them to the builder)

- **Screens are HTML web resources, not a canvas custom page.** The page is plain text in the repo, can be tested with node and uploaded by a script, and DataverseToCode can show it as a file. A canvas app is a packed `.msapp`.
- **One table.** Endpoint ticks, name edits and question answers are all answer keys already (`docs/connection-design-format.md`, "Answer keys"), so they live in one JSON column, `bf_answers`. The review is computed on every call rather than stored, so a stored review can never go stale. Generate computes it again anyway.
- **One read API.** "Save and read the definition", "Check" and "Read again" do the same thing: save the row, then call `bf_ReadDefinition`. The spec's `bf_Check` is folded into it.
- **The role step runs as the signed-in administrator on a host page**, `/admin/{environmentId}/{instance}/role`. It uses a delegated Dataverse token from the connect app with scope `{orgUrl}/user_impersonation`. The token is used for that request only and never stored or logged. The tested `RoleGenerator.ApplyAsync` runs unchanged.
- **Administrator check, in two layers.** Each admin Custom API has `ExecutePrivilegeName = prvWritebf_connection`, which the platform enforces. The host then requires the caller, found by Entra object ID, to hold the `BrokerForge administrator` role directly or through a team. Every business unit's copy of the role counts, because they all share one `parentrootroleid`, the role in `BrokerForgeCore`. A role with the same name outside that solution does not count, matching D-36's rule for `<Instance> user`.
- **Lock outside the process.** A lease row in the credential table (`Credentials:TableEndpoint`), partition `generation-lease`, row key `<env>|<instance>` in lower case. It's taken with a conditional insert, or by an ETag update once it has expired. It lasts the job limit plus 5 minutes and is released when the job ends. Without a table (tests, local runs) the lease is in memory.
- **Editing only in the source environment.** In an environment where the design arrived by import, `bf_ListConnections` shows it read-only with its status ("needs URL", "needs key", "ready"), a link to the base URL variable and the "Set key" link. There is no `bf_connection` row for it.
- **Solution and publisher.** The solution defaults to `BrokerForge<Instance>` (column `bf_solution`, editable before the first Generate). The publisher is `brokerforge<prefix>`, the convention dev already uses (`brokerforgeext`).
- **Secret names.** Static uses one instance secret, `key` (API key or basic password). OAuth with the source's own client uses `clientSecret`. The other modes use none.
- **Key strategy.** A key whose OpenAPI type is `string` with format `uuid` is `native`/`Guid`. Otherwise it's `reversible`, with `Int32`, `Int64` or `String` from the type. The review gains `TableProposal.KeyKind` for this. That's a SwaggerForge record, not the descriptor schema.

## Global Constraints

- "Fail loudly. Never return partial, approximate or client-side-filtered results." A missing URL or key is a typed failure naming what is missing, with the correlation ID.
- "No secrets in Dataverse rows, descriptors, logs, traces, test fixtures or exception messages." The secret page never echoes or logs the value, and the role page never logs or stores the admin's token.
- "Thin plug-in": "The plug-in gains two Custom API routes and nothing else." Here that means only registrations of the existing `CustomApiPlugin`. No plug-in code changes.
- "Ask before changing a contract in `BrokerForge.Contracts` or the descriptor schema." This plan changes neither. Refusals use existing types: `CallerRejectedException` (403 Forbidden) for a non-administrator, and `MappedSourceException(Validation)` or `(Conflict)` for "not ready" and "busy".
- The repo is public: no real tenant, environment, org or user identifiers in code, tests or docs (use `contoso` and all-zero GUIDs).
- File-based config keeps working unchanged: every existing test and both acceptance scripts pass at the end of every task.
- Writing to the dev Dataverse environment and `rg-brokerforge-dev` is allowed (Simon). Use `/usr/bin/python3`. In zsh never name a variable `path`.
- The host role gains only what this plan needs: `prvReadbf_connection`, `prvWritebf_connection` and `prvReadTeam`.

## Review Focus

1. A System Customizer, who holds `prvWritebf_connection` but not the BrokerForge administrator role, calls `bf_Generate`. The host must refuse with 403 and start nothing. Test in Task 2 (`Admin_CustomizerWithoutRole_Refused`) and Task 4 (`Generate_NotAdmin_NoJob`).
2. Two hosts start Generate for one source at once (two replicas, or a replica and the admin command): the second is refused as busy. A lease left by a stopped replica is taken once it expires. Test in Task 3 (`Lease_SecondHolderRefused`, `Lease_ExpiredTakenOver`).
3. The admin changes an answer between the review and Generate: the change list differs, the job stops before writing and needs confirming, and the row says so. Test in Task 4 (`Generate_AnswersChangedSinceReview_NeedsConfirmationNothingWritten`).
4. A caller with no Entra object ID (an async step, or a call made on behalf of another user) is refused, never treated as the host. Test in Task 2 (`Admin_NoObjectId_Refused`).
5. Pasted text the draft service rejects (empty, HTML, too long), or a row with no OpenAPI text: `bf_ReadDefinition` returns problems, never a 500. Test in Task 2 (`Read_EmptyDefinition_ProblemNotError`).

---

### Task 1: The design builder

**Files:**
- Modify: `tools/SwaggerForge/Review.cs`, `tools/SwaggerForge/Drafter.cs` (`TableProposal.KeyKind`)
- Create: `src/BrokerForge.Host/Admin/ConnectionRow.cs`, `src/BrokerForge.Host/Admin/DesignBuilder.cs`
- Test: `tests/SwaggerForge.Tests/ReviewTests.cs` (or the existing review test file), `tests/BrokerForge.Contract.Tests/Admin/DesignBuilderTests.cs`

**Interfaces:**
- Produces:
  - `TableProposal(..., string? KeyKind)`: `"Guid"`, `"Int32"`, `"Int64"` or `"String"`, or null while the key is unknown.
  - `sealed record ConnectionRow(Guid Id, string Name, string? BaseUrl, string AuthMode, string? StaticKind, string? HeaderName, string? QueryParam, string? Username, string? Scope, string? SsoKind, string? AuthorizeUrl, string? TokenUrl, string? OAuthScopes, string? ClientId, bool UsesConnectApp, string Prefix, string? Solution, string? OpenApi, string? AnswersJson)`, with `static ConnectionRow FromJson(JsonObject row)`, which reads the Web API columns below.
  - `static class DesignBuilder`:
    - `IReadOnlyDictionary<string,string> Answers(ConnectionRow row)` throws `MappedSourceException(Validation)` naming `bf_answers` when the JSON isn't an object of strings.
    - `DraftReview Review(ConnectionRow row)` calls `DraftService.Review(row.OpenApi, new DraftOptions(row.Name, row.Prefix, ReservedNames), Answers(row))`. ReservedNames are the static header or query names.
    - `ConnectionDesign Build(ConnectionRow row, DraftReview ready)` throws `MappedSourceException(Validation)` unless `ready.Ready`.
    - `string SolutionOf(ConnectionRow row)` returns `row.Solution ?? $"BrokerForge{row.Name}"`.
    - `string PublisherOf(ConnectionRow row)` returns `$"brokerforge{row.Prefix}"`.
    - `string? AuthJson(ConnectionRow row)` returns this environment's auth overrides, or null.
- Columns of `bf_connection` (Task 6 creates them): `bf_name`, `bf_baseurl`, `bf_authmode` (choice: ServiceAccount 1, Static 2, OAuth 3, Impersonate 4, Sso 5), `bf_statickind`, `bf_headername`, `bf_queryparam`, `bf_username`, `bf_scope`, `bf_ssokind`, `bf_authorizeurl`, `bf_tokenurl`, `bf_oauthscopes` (space-separated), `bf_clientid`, `bf_usesconnectapp`, `bf_prefix`, `bf_solution`, `bf_openapi` (memo, 1,048,576), `bf_answers` (memo), and the generation columns from Task 4.

- [ ] **Step 1: Write the failing tests.**
  - `Review_UuidKey_KeyKindGuid`, `Review_IntegerKey_KeyKindInt32`, `Review_Int64Key_KeyKindInt64`, `Review_StringKey_KeyKindString`: small inline definitions.
  - `Build_Inventory_MatchesCommittedDescriptors`: a row with `tools/mock-api/inventory-openapi.yaml` and the answers in `tests/SwaggerForge.Tests/fixtures/inventory-answers.json` builds a design with:
    - `Schema` equal to `schemas/InventoryAPI.yaml`;
    - `Descriptors` equal to `descriptors/InventoryAPI/*.yaml`;
    - `ObjectMap` = `ext_item`, `reversible`, `Int32`, and operations `RetrieveMultiple`→`listItems`, `Retrieve`→`getItem`, `Create`→`createItem`, `Update`→`updateItem`, `Delete`→`deleteItem`;
    - `Auth.SecretNames == ["key"]` for a Static row;
    - `BrokerType == "rest"`, `FormatVersion == 1`, and `Answers` equal to the row's.
  - `Build_NotReady_RefusedNamingOpenQuestions`.
  - `Answers_NotAnObjectOfStrings_RefusedNamingColumn`.
  - `Build_OAuthOwnClient_SecretNamesClientSecret`.
  - `Build_ConnectApp_NoSecretNames`.
  - `Build_DesignSerializesAndParses`: round trip through `DesignSerializer`.
- [ ] **Step 2: Run them and watch them fail.** Run `dotnet test tests/SwaggerForge.Tests` and `dotnet test tests/BrokerForge.Contract.Tests --filter "FullyQualifiedName~DesignBuilder"`. Expected: build errors on `KeyKind` and `DesignBuilder`.
- [ ] **Step 3: Implement.**
  - The object map's operations come from the ready descriptors' `binds`, for table messages only.
  - The key kind comes from `ready.Table.KeyKind`: `Guid` maps to strategy `native`, anything else to `reversible`.
  - Auth fields that are empty strings count as absent.
- [ ] **Step 4: Run the tests.** Expected: PASS. Then run the full `dotnet test BrokerForge.slnx`: green.
- [ ] **Step 5: Commit.** `feat(admin): design builder from a connection row; review reports the key kind`

### Task 2: Admin check and `bf_ReadDefinition`

**Files:**
- Create: `src/BrokerForge.Host/Admin/AdminCheck.cs`, `src/BrokerForge.Host/Admin/AdminApi.cs`, `src/BrokerForge.Host/Admin/ConnectionRows.cs`
- Modify: `src/BrokerForge.Host/Endpoints/ExecuteEndpoint.cs` (dispatch before the descriptor lookup), `src/BrokerForge.Host/Program.cs` (registrations)
- Test: `tests/BrokerForge.Contract.Tests/Admin/AdminApiTests.cs`, using the Generator tests' `FakeDataverse` pattern (copy or share it through a test utility; don't add test code to production)

**Interfaces:**
- Consumes: `DesignBuilder` and `ConnectionRow` (Task 1); `GenerationJobs.PreviewAsync` (plan 3); `IHostConfigProvider` and `IInstanceSecretStore` (plan 1); `IGenerationClientFactory`.
- Produces:
  - `AdminCheck.RequireAsync(DataverseClient hostClient, string? userObjectId, string correlationId, CancellationToken ct)`. It throws `CallerRejectedException` with "Only a BrokerForge administrator can set up connections." It finds the user by `azureactivedirectoryobjectid`, then their roles and their teams' roles, and accepts a role whose `parentrootroleid` is the `BrokerForge administrator` role that is a component (type 20) of solution `BrokerForgeCore`.
  - `ConnectionRows.ReadAsync(DataverseClient, Guid id, ct) → ConnectionRow` (404 `MappedSourceException(NotFound)` when it's missing) and `ConnectionRows.WriteGenerationAsync(...)` (Task 4).
  - `AdminApi.Messages = { "bf_ReadDefinition", "bf_Generate", "bf_ListConnections" }` and `AdminApi.HandleAsync(CanonicalRequest, ct) → CanonicalResponse` with `Outputs`. Input `ConnectionId` is a Guid. `bf_ReadDefinition` returns output `Review`, a string holding JSON: `{ "review": <DraftReview>, "preview": { "fingerprint", "changes": [text], "blocked": [text], "parts": [text], "pending": [text] } | null, "environment": { "baseUrlSet": bool, "secrets": [{ "name", "set" }] }, "secretUrl", "roleUrl", "readOnly": false }`. The preview is present only when the review is ready.
  - `secretUrl` is `{Connect:PublicBaseUrl}/admin/{env}/{instance}/secret`, and `roleUrl` is the same with `/role`. `Connect:PublicBaseUrl` is the setting the connect URL already uses.
- [ ] **Step 1: Write the failing tests.**
  - `Admin_NoObjectId_Refused` (403).
  - `Admin_CustomizerWithoutRole_Refused`.
  - `Admin_RoleFromAnotherSolution_Refused`.
  - `Admin_RoleThroughTeam_Allowed`.
  - `Admin_ChildBusinessUnitCopy_Allowed`.
  - `Read_InventoryReady_ReviewAndPreviewWithFingerprint`.
  - `Read_OpenQuestions_NoPreviewStatusNeedsAnswers`.
  - `Read_EmptyDefinition_ProblemNotError`.
  - `Read_MissingRow_NotFound`.
  - `Read_EnvironmentStatus_NamesMissingKey`: a Static row with no `key` in the secret store gives `secrets: [{name:"key", set:false}]`.
  - `Read_NeverEchoesSecret`: the review JSON for a row whose environment has a key does not contain the key.
  - `Execute_AdminMessage_NotRoutedToDescriptors`: a descriptor-bound Custom API still works next to it.
- [ ] **Step 2: Run them and watch them fail.** Expected: build errors on `AdminApi`.
- [ ] **Step 3: Implement.**
  - The host reads rows and roles as its own application user through `IGenerationClientFactory`, for the environment's `OrgUrl`.
  - `ExecuteEndpoint` calls `AdminApi` when `AdminApi.Messages.Contains(request.Message)`, after `guard.Check` and before the config lookup.
  - Errors go through `ErrorTranslator` as today.
- [ ] **Step 4: Run the tests and the full suite.** Expected: green.
- [ ] **Step 5: Commit.** `feat(admin): bf_ReadDefinition and the administrator check`

### Task 3: Generation lease outside the process

**Files:**
- Create: `src/BrokerForge.Host/Generation/GenerationLease.cs`
- Modify: `src/BrokerForge.Host/Generation/GenerationJobs.cs` (`Start` → `StartAsync`; take the lease before queueing and release it after the final report), `src/BrokerForge.Host/Admin/GenerateCommand.cs`, `src/BrokerForge.Host/Program.cs`, `docs/hld.md` (D-36 constraint now met)
- Test: `tests/BrokerForge.Contract.Tests/Generation/GenerationLeaseTests.cs` (Azurite, as the credential store tests do), plus the existing `GenerationJobTests` updated for `StartAsync`

**Interfaces:**
- Produces:
  - `interface IGenerationLease { Task<string?> TryAcquireAsync(string env, string instance, TimeSpan duration, CancellationToken ct); Task ReleaseAsync(string env, string instance, string token, CancellationToken ct); }`. Acquire returns a holder token, or null when another holder's lease hasn't expired.
  - Implementations `TableGenerationLease(TableClient, TimeProvider)` and `InMemoryGenerationLease(TimeProvider)`, and `GenerationLeases.Create(IConfiguration, TokenCredential, TimeProvider)`, which picks the table when `Credentials:TableEndpoint` is set.
  - `GenerationJobs.StartAsync(GenerationRequest r, CancellationToken ct) → Task<GenerationJob>`. It throws `GenerationBusyException` naming the other job, if it's in this process, or "another host".
- [ ] **Step 1: Write the failing tests.**
  - `Lease_SecondHolderRefused`.
  - `Lease_ExpiredTakenOver`, using a fake `TimeProvider`.
  - `Lease_ReleaseByOtherTokenIgnored`.
  - `Lease_KeyIgnoresCase`.
  - The same four for the in-memory lease (as a theory).
  - `Jobs_TwoHostsShareLease_SecondBusy`: two `GenerationJobs` instances with one lease.
  - `Jobs_LeaseReleasedAfterFailure`.
- [ ] **Step 2: Run them and watch them fail.**
- [ ] **Step 3: Implement.**
  - The table lease uses `AddEntityAsync` and, on 409, reads the row. It replaces the row with the read ETag only when the row has expired, and a 412 counts as busy.
  - The lease duration is the job limit plus 5 minutes.
  - Rewrite D-36's in-process constraint as met, and name the lease.
- [ ] **Step 4: Run the tests and the full suite.** Expected: green.
- [ ] **Step 5: Commit.** `feat(generation): lease outside the process before a job starts`

### Task 4: `bf_Generate` and progress on the row

**Files:**
- Create: `src/BrokerForge.Host/Generation/ConnectionRowProgress.cs`
- Modify:
  - `src/BrokerForge.Host/Generation/GenerationRequest.cs`: `Guid? ConnectionId = null` on the request and the job;
  - `src/BrokerForge.Host/Admin/AdminApi.cs`;
  - `src/BrokerForge.Host/Admin/ConnectionRows.cs`;
  - `src/BrokerForge.Host/Program.cs`: progress is the logger plus the row writer.
- Test: `tests/BrokerForge.Contract.Tests/Admin/GenerateApiTests.cs`

**Interfaces:**
- Consumes: `DesignBuilder.Build`, `SolutionOf`, `PublisherOf` and `AuthJson` (Task 1); `AdminCheck` (Task 2); `GenerationJobs.StartAsync` (Task 3).
- Produces:
  - `bf_Generate` takes inputs `ConnectionId` (Guid) and `Fingerprint` (string, optional). Its outputs are `JobId` (string) and `State` (string).
  - The row columns, written as the host: `bf_generationstate` (text: Queued, Running, Succeeded, Failed), `bf_generationjobid`, `bf_generationstarted`, `bf_generationfinished` and `bf_generationresult` (memo, JSON `{ "writes": [], "failure": null, "pending": [], "designVersion": 3, "needsConfirmation": false }`).
  - The job runs with `ApplyRole = false`, `BaseUrl = row.BaseUrl` and `AuthJson = DesignBuilder.AuthJson(row)`.
- [ ] **Step 1: Write the failing tests.**
  - `Generate_NotAdmin_NoJob`.
  - `Generate_NotReady_RefusedNamingOpenQuestions`.
  - `Generate_Ready_JobRunsRowShowsRunningThenSucceededWithPendingRole`.
  - `Generate_AnswersChangedSinceReview_NeedsConfirmationNothingWritten`.
  - `Generate_Busy_Conflict`.
  - `Generate_ProgressWriteFails_JobResultUnchanged`.
  - `Generate_RowResultHasNoDesignText`: no OpenAPI text and no secret in `bf_generationresult`.
- [ ] **Step 2: Run them and watch them fail.**
- [ ] **Step 3: Implement.**
  - The handler reads the row, reviews it, builds the design and calls `StartAsync`.
  - `ConnectionRowProgress` writes only jobs that carry a `ConnectionId`.
- [ ] **Step 4: Run the tests and the full suite.** Expected: green.
- [ ] **Step 5: Commit.** `feat(admin): bf_Generate starts a job and reports on the connection row`

### Task 5: Host admin pages, `bf_ListConnections` and the connect-page minor

**Files:**
- Create: `src/BrokerForge.Host/Admin/AdminEndpoints.cs`, `src/BrokerForge.Host/Admin/ConnectionList.cs`
- Modify:
  - `src/BrokerForge.Host/Connect/ConnectEndpoints.cs`: `Find` turns a `ConfigurationException` or `DesignSourceUnavailableException` from `ForEnvironmentAsync` into a message page (plan 1 deferred minor);
  - `src/BrokerForge.Host/Admin/AdminApi.cs`;
  - `src/BrokerForge.Host/Program.cs`;
  - `tools/dev/setup-connect-app.sh`: delegated Dynamics CRM `user_impersonation` on the connect app, with admin consent.
- Test: `tests/BrokerForge.Contract.Tests/Admin/AdminPagesTests.cs`, `tests/BrokerForge.Contract.Tests/Connect/ConnectPageTests.cs` (one added test)

**Interfaces:**
- Consumes: `AdminCheck` (Task 2), with a variant that takes the signed-in `oid`; `IInstanceSecretStore`; `GenerationJobs.ReadPublishedAsync`; `RoleGenerator.ApplyAsync(client, table, instance, messages, solution, ct)`; `TokenEndpointClient.RedeemCodeAsync` with an `OAuthResource` on the tenant's Entra endpoints, `UsesConnectApp = true` and scopes `[{orgUrl}/user_impersonation]`; `ConnectState` (reused with `Instance` = `role:<instance>`).
- Produces:
  - `GET` and `POST` `/admin/{environmentId:guid}/{instance}/secret`, behind the Connect policy and antiforgery:
    - the form shows each of the design's `SecretNames` as "set" or "needs key", with a password field;
    - POST stores the value with `IInstanceSecretStore.SetAsync`;
    - a value is 1–4096 characters on one line;
    - an environment not in `Trust:AllowedCallers`, an instance with no design, or a name the design doesn't list gives 404;
    - a non-administrator gets 403.
  - `GET /admin/{environmentId:guid}/{instance}/role` shows the pending role change, read from the published design as the host.
  - `POST .../role/start` redirects to Entra authorise with the delegated scope and PKCE.
  - `GET /admin/callback` redeems the code, builds a `DataverseClient` whose token is the admin's, runs `RoleGenerator.ApplyAsync`, and shows "role `<Instance> user` set". Nothing about the token is logged or stored.
  - `bf_ListConnections` has no inputs and returns output `Connections`, JSON `[{ "name", "rowId": guid|null, "readOnly": bool, "status": "ready"|"needs URL"|"needs key"|"not generated", "designVersion", "baseUrlVariable", "secretUrl" }]`. It covers this environment's rows and published designs, and a design with no row is `readOnly`.
- [ ] **Step 1: Write the failing tests.**
  - `Secret_Admin_StoresAndShowsSet`.
  - `Secret_NotAdmin_Forbidden`.
  - `Secret_UnknownEnvironment_NotFound`.
  - `Secret_NameNotInDesign_NotFound`.
  - `Secret_ValueNeverLogged`, using a log capture and searching it for the value.
  - `Secret_MissingAntiforgery_Refused`.
  - `Role_Callback_AppliesWithAdminToken`: the fake Dataverse records the bearer token used for the role writes, and it is the redeemed one, not the host's.
  - `Role_CallbackOtherUser_Refused`.
  - `Role_TokenNeverLogged`.
  - `List_ImportedDesignReadOnlyNeedsKey`.
  - `List_RowWithoutDesign_NotGenerated`.
  - `Connect_DesignsFailToLoad_MessagePage` (the minor).
- [ ] **Step 2: Run them and watch them fail.**
- [ ] **Step 3: Implement.**
  - Pages use `ConnectPages` (HTML-encoded, no values written back).
  - The role callback checks the admin again with the host's client before using the token.
- [ ] **Step 4: Run the tests and the full suite.** Expected: green.
- [ ] **Step 5: Commit.** `feat(admin): secret and role pages, bf_ListConnections; fix: connect page explains a design load failure`

### Task 6: The core components in Dataverse

**Files:**
- Create in `src/BrokerForge.WebResources/`:
  - `connection.js`: the form OnLoad, which hands `formContext` and `Xrm` to the review page through `getContentWindow()`;
  - `review.html` and `review.js`: screen 2 and screen 3 on the form's Review tab;
  - `review-model.js`: pure functions, with no DOM or Xrm;
  - `connections.html` and `connections.js`: the app's home page, from `bf_ListConnections`.
- Create: `tests/webresources/review-model.test.js` (`node --test`)
- Modify: `tools/dev/setup_dataverse.py`, `.github/workflows/ci.yml` (run `node --test tests/webresources`)

**Interfaces:**
- Consumes: the API shapes from Tasks 2, 4 and 5.
- Produces, in solution `BrokerForgeCore` (dev, unmanaged):
  - table `bf_connection` with the Task 1 columns and Task 4 generation columns (the generation columns are read-only on the form);
  - its main form (General tab: screen 1 fields; Review tab: `review.html`) and its default view;
  - web resources `bf_/brokerforge/*.{html,js}`;
  - Custom APIs `bf_ReadDefinition`, `bf_Generate` and `bf_ListConnections`. They are unbound functions or actions as listed, run by the `BrokerForge.Plugin.CustomApiPlugin` plug-in type, with `ExecutePrivilegeName = prvWritebf_connection`, and use the Task 2, 4 and 5 parameter names and types (Guid is type 12, String is type 10);
  - role `BrokerForge administrator` with organisation Read, Create, Write and Delete on `bf_connection`;
  - model-driven app `BrokerForge` with a site map holding Connections (`connections.html`) and the `bf_connection` table;
  - `HOST_PRIVILEGES` plus `prvReadbf_connection`, `prvWritebf_connection` and `prvReadTeam`.
- The pure functions in `review-model.js`:
  - `answersFrom(review, edits)` returns the answers object to save;
  - `canGenerate(read)` is true only when the review is ready, the preview has nothing blocked, and the base URL and every secret are set;
  - `statusLine(row)` gives the generation state, with "stopped without a report; Generate again" for a Running row whose started time is more than 20 minutes ago;
  - `doneLinks(orgUrl, table)` gives the links for screen 3.
- [ ] **Step 1: Write the failing node tests.**
  - `canGenerate false while a question is open`.
  - `canGenerate false when blocked`.
  - `canGenerate false when the key is missing`.
  - `answersFrom keeps untouched answers and writes op.<op>.use=no for an unticked endpoint`.
  - `statusLine flags a stale Running job`.
  - `doneLinks encodes the table name`.
- [ ] **Step 2: Run `node --test tests/webresources` and watch them fail.**
- [ ] **Step 3: Implement the model, the pages and the setup steps.**
  - Every setup step looks its component up first, as the existing ones do.
  - The page shows Generate enabled only when `canGenerate` is true, sends the review's fingerprint with Generate, and polls the row every 5 seconds while the job is Queued or Running.
  - When a succeeded job has a pending role, the page shows "Apply the role" linking to `roleUrl`. Next to each secret it shows "Set key" linking to `secretUrl`.
- [ ] **Step 4: Run the node tests and run `tools/dev/setup-dataverse.sh` against dev.** Expected: the tests pass. The script completes, and a second run changes nothing.
- [ ] **Step 5: Commit.** `feat(core): connection table, admin Custom APIs, review page, administrator role, BrokerForge app`

### Task 7: The core as a managed solution

**Files:**
- Create: `solution/BrokerForgeCore/` (`pac solution unpack` output, committed), `tools/dev/export-core.sh`, `tools/dev/install-core.sh <orgUrl>`
- Modify: `CLAUDE.md` (commands; `solution/` now exists), `docs/hld.md` section 16 (D-37)

**Interfaces:**
- `export-core.sh` exports `BrokerForgeCore` from dev as unmanaged, unpacks it into `solution/BrokerForgeCore`, and packs `out/BrokerForgeCore_managed.zip` from that source.
- `install-core.sh <orgUrl>` imports the managed zip, then runs the per-environment steps that can't travel: the plug-in's managed identity record, `bf_HostUrl` and `bf_HostScope` values, and the host application user with its role. The host's `Trust:AllowedCallers` entry for that environment stays a host deploy step (spec 11).
- [ ] **Step 1: Install pac if it's missing** (`dotnet tool install --global Microsoft.PowerApps.CLI.Tool`), then run `export-core.sh`. Expected: `solution/BrokerForgeCore/` has `Other/Solution.xml`, the `bf_connection` entity folder, the web resources and the Custom APIs as readable files.
- [ ] **Step 2: Check the export.** Search `solution/` for the dev environment ID, org URL, host URL and any GUID from `~/.brokerforge/*.env`. Expected: no match, or each match is moved into an `install-core.sh` step. The repo is public.
- [ ] **Step 3: Install into a second environment if one exists.** Run `install-core.sh` against it, then call `bf_ListConnections` there. Expected: an empty list. If there is no second environment, record **Not verified** for plan 5 (done criterion 4).
- [ ] **Step 4: Commit.** `feat(solution): BrokerForgeCore as unpacked source and a managed package; install script`

### Task 8: Live check in dev and the findings

**Files:**
- Create: `docs/findings/portal-4.md`
- Modify: `docs/hld.md` (D-37: the calls listed at the top of this plan; section 6, who may set up connections), `CLAUDE.md` (handover state, commands)

- [ ] **Step 1: Deploy.** Run `tools/dev/deploy.sh`, then `setup-dataverse.sh` and `setup-connect-app.sh` (for the new delegated permission). Assign `BrokerForge administrator` to Simon's dev user (the az CLI user).
- [ ] **Step 2: Onboard through the screens.** In the BrokerForge app:
  1. Create connection `WarehouseUiAPI`, with the internal Inventory mock URL, Static `apiKey` header mode, prefix `ext`, and the Inventory OpenAPI text.
  2. Save and read: questions show and Generate is disabled (done criterion 3).
  3. Answer them; set the key on the secret page; Generate.
  4. The row shows Succeeded with the role pending; apply the role on the role page.
  5. List, open, create, update and delete items in the new table.

  Drive the steps through the Web API as the az CLI user where a browser isn't available, and say so in the findings. Expected: every step works, and nothing but the mock setup ran from a terminal.
- [ ] **Step 3: Check that refusals are refused.** As a user without the administrator role, call `bf_ReadDefinition` (if a non-admin token can be had; otherwise **Not verified**). Then search the solution export and the host logs for the test key. Expected: 403, and no match.
- [ ] **Step 4: Run the existing acceptance.** Run `tools/dev/acceptance.sh` and `tools/dev/acceptance_inventory.sh`. Expected: both pass.
- [ ] **Step 5: Write `portal-4.md`.** It records what was verified, what wasn't, surprises, and the calls made. Then run `dotnet test BrokerForge.slnx` and `node --test tests/webresources`: green.
- [ ] **Step 6: Commit.** `docs: portal-4 findings, D-37, handover`
