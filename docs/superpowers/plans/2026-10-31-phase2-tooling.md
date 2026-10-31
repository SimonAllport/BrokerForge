# Phase 2 Tooling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Onboard a second REST source (an Inventory mock API) from its OpenAPI document to working Dataverse virtual table and Custom API with configuration and descriptors only, no new BrokerForge code.

**Architecture:** A table schema file sits beside each source's descriptors and makes validator rule 7 and the generator possible. SwaggerForge turns OpenAPI 3 into draft schema and descriptors and reports what it could not determine instead of guessing. One CLI (`bf`) validates descriptors in CI and drives the generator, which creates tables, columns, data source records and Custom APIs through the Dataverse Web API. Custom APIs run end to end: descriptor `customapi:` binds, a second plug-in type, and a host endpoint path.

**Tech Stack:** .NET 8 (tools, host, libraries), net462 / C# 7.3 (plug-in), netstandard2.0 (Contracts), YamlDotNet, Microsoft.OpenApi.Readers 1.6.22, Azure.Identity, Node (mock), GitHub Actions.

**Spec:** `docs/hld.md` (5.3, 6, 7.1, 14, D-10, D-11), `docs/descriptor-spec.md` (2, 3, 6, 12, 13 Custom API), `CLAUDE.md` phase 2 criteria.

## Global Constraints

- Phase 2 is done when: SwaggerForge emits descriptors that pass the validator, plus a table schema; the validator runs in CI over `descriptors/`; the generator creates tables, columns and Custom APIs through the Web API; a second mock API is onboarded with no new code.
- Fail loudly: no partial, approximate or guessed output. SwaggerForge omits what it cannot determine and says why; the generator never alters or drops an existing column.
- No secrets in Dataverse rows, descriptors, schema files, logs, test fixtures or exception messages.
- Thin plug-in: no descriptor or source logic in either plug-in type. net462, C# 7.3, Contracts compiled in (D-08).
- Ask before changing `BrokerForge.Contracts` or the descriptor schema; update the docs in the same change (Task 0 asks once for all of this plan's changes).
- Prefixes: `bf_` for framework components, the client's prefix (`ext_` in examples) for generated ones.
- Every request and log line carries `correlationId`.
- Dev scripts run with `/usr/bin/python3`; signing uses `cert.pem` and `cert.key` (CLAUDE.md).

## Review Focus

1. **OpenAPI the drafter cannot read with confidence** (`oneOf`/`allOf`, `$ref` cycles, no response schema, no `operationId`): SwaggerForge reports the operation as skipped with the reason and still writes the rest. Test in Task 5: `Draft_UnsupportedShapes_SkippedWithReasons`.
2. **The generator re-run against a table whose column changed type in the schema**: refused with the column, the old and the new type; nothing is altered. Test in Task 6: `Generate_ColumnTypeChanged_RefusesWithoutWriting`.
3. **A Custom API call missing a required input or carrying the wrong type**: Validation error naming the parameter before any source call. Test in Task 4: `CustomApi_MissingRequiredInput_400Validation`.
4. **Two service instances declaring the same `customapi:` unique name**: host refuses to start, naming both. Test in Task 4: `Config_DuplicateCustomApiName_StartupFails`.
5. **A declared Custom API output missing from the source response**: `MappingException` naming the output and path; an output marked `optional: true` is null. Test in Task 3: `Outputs_MissingPath_ThrowsMappingNamingOutput`.

---

### Task 0: Decisions to confirm before Task 1

Simon confirms or changes each; record the answers in `docs/hld.md` section 16 as D-16 onwards and in `docs/descriptor-spec.md` before Task 1 starts.

- **P1 Scope split.** This plan covers the CLAUDE.md phase 2 criteria. Moving service instances, object maps and descriptors into the section 6 Dataverse tables (the rest of D-10) becomes a follow-on plan, "phase 2b", because the done criterion does not need it and it adds the host reading Dataverse with its own identity. Until then a new source is host settings plus descriptor files, redeployed without code. SwaggerForge runs offline against an OpenAPI file; `IBroker.DiscoverAsync` (FR-02 discovery through the host) stays unimplemented until 2b, and its message is changed to say so.
- **P2 Table schema file.** New file per source, `schemas/<ServiceInstance>.yaml`, documented as descriptor spec section 14:
  ```yaml
  table: ext_item                 # logical name; prefix from the file
  displayName: Item
  displayCollectionName: Items
  externalName: item
  primaryName: ext_name           # string column used as the primary name
  columns:
    ext_name:   { type: string, maxLength: 100, displayName: Name }
    ext_onhand: { type: int, displayName: On hand }
    ext_price:  { type: decimal, precision: 2, displayName: Price }
    ext_state:  { type: optionset, options: { active: 100000000, discontinued: 100000001 }, displayName: State }
  customApis:
    ext_AdjustStock:
      displayName: Adjust stock
      inputs:  { ItemId: { type: int, required: true }, Delta: { type: int, required: true }, Reason: { type: string } }
      outputs: { OnHand: int, AdjustedAt: datetime }
  ```
  Column types: `string`, `memo`, `int`, `decimal`, `bool`, `datetime`, `date`, `optionset`. No `money` (D-15) and no `guid` columns besides the key. Custom API parameter types: `string`, `int`, `decimal`, `bool`, `datetime`, `guid`.
- **P3 Contracts change.** `CanonicalRequest.Inputs` (`List<Cell>`, name in `Col`) and `CanonicalResponse.Outputs` (`List<Cell>`), HLD 7.1 updated. `Message` carries the Custom API unique name; `ServiceInstance` and `Table` are null for unbound Custom APIs and the host resolves the instance from the descriptor's `binds`.
- **P4 Unbound Custom APIs only.** Entity-bound Custom APIs and the `id` binding inside them wait for a later phase; the parser rejects `id` in a `customapi:` descriptor with a message saying so.
- **P5 Custom API plug-in configuration.** A Custom API has no data source record, so `CustomApiPlugin` reads the host URL and scope from Dataverse environment variables `bf_HostUrl` and `bf_HostScope` (HLD 14 already names environment variables for this). Setup creates them in `BrokerForgeCore`.
- **P6 Descriptor additions.** `customapi:<UniqueName>` binds, `param` bindings and `response.outputs` (spec 2, 3, 13) are enabled. Also: map `type: json` (serialises a JSON subtree to a string, which closes the `ext_lines_json` gap) and a `col` modifier `optionset: <map>` that reverse-maps a Dataverse option value to the source value on writes (closes the phase 1 option set refusal). `steps`, `hook`, `jsonata` and `cache` stay rejected, with messages naming the later phase.
- **P7 Tools layout.** `src/BrokerForge.Schema` (schema model and parser), `src/BrokerForge.Generator` (library), `tools/SwaggerForge` (library), `tools/BrokerForge.Cli` (`bf validate`, `bf draft`, `bf generate`). All .NET 8.
- **P8 Second source.** "Inventory API" in the existing Node mock under `/inv/v1`: API key in query parameter `api_key` (Static `apiKeyQuery`), integer keys (reversible Int32), offset paging with a `total`, and a stock adjustment action that becomes the Custom API. Its OpenAPI 3.0 document lives at `tools/mock-api/inventory-openapi.yaml`.
- **P9 What counts as "no new code".** The onboarding commit may add files under `schemas/`, `descriptors/`, host settings in `infra/`, the Key Vault secret in `tools/dev/deploy.sh`'s secret list, and acceptance assertions. It may not touch `src/`. Task 9 checks this with `git diff --stat`.

- [ ] **Step 1:** Get Simon's answers. Write D-16 to D-24 (one per point) into HLD 16, spec sections 2, 3, 6 and new 14, HLD 7.1. Commit: `docs: phase 2 decisions`.

---

### Task 1: Table schema, validator rule 7, `bf validate`

**Files:** Create `src/BrokerForge.Schema/` (`TableSchema.cs`, `SchemaParser.cs`, `SchemaException.cs`), `tools/BrokerForge.Cli/` (`Program.cs`, `ValidateCommand.cs`), `schemas/OrdersAPI.yaml`, `tests/BrokerForge.Schema.Tests/`. Modify `src/BrokerForge.Descriptors/Validation/DescriptorValidator.cs`, `BrokerForge.slnx`.

**Interfaces:**
- Produces: `record SourceSchema(string Table, string DisplayName, string DisplayCollectionName, string ExternalName, string PrimaryName, IReadOnlyDictionary<string, ColumnSchema> Columns, IReadOnlyDictionary<string, CustomApiSchema> CustomApis)`; `record ColumnSchema(string Type, string DisplayName, int? MaxLength, int? Precision, IReadOnlyDictionary<string,int>? Options)`; `record CustomApiSchema(string DisplayName, IReadOnlyDictionary<string, ParamSchema> Inputs, IReadOnlyDictionary<string, string> Outputs)`; `record ParamSchema(string Type, bool Required)`; `SchemaParser.Parse(string yaml, string source) -> SourceSchema` (strict like `DescriptorParser`: unknown keys throw `SchemaException` naming the path).
- Produces: `DescriptorValidator.Validate(OperationDescriptor d, ReservedNames reserved, SourceSchema? schema = null)`. With a schema: rule 7, every map/`col`/`filter`/`sort` column exists in the schema, and option set maps cover exactly the schema's options.
- Produces: `bf validate <descriptorsRoot> --schemas <schemasRoot>`: exit 0 when clean; exit 1 printing `<file>: rule <n> <yamlPath>: <message>` per error.

- [ ] **Step 1:** Tests `SchemaParser_ParsesOrdersSchema`, `SchemaParser_UnknownKey_Throws`, `SchemaParser_MoneyType_RefusedNamingD15`, `Rule7_UnmappedColumn_Rejected` (listOrders without `ext_total` against a schema that has it), `Validate_ColumnNotInSchema_Rejected`, `Validate_OptionSetMissingSchemaOption_Rejected`, `Cli_Validate_RepoDescriptors_ExitsZero`, `Cli_Validate_BrokenDescriptor_ExitsOneWithFileAndRule`.
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Schema.Tests tests/BrokerForge.Descriptors.Tests`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement. `schemas/OrdersAPI.yaml` describes `ext_order` as setup creates it today (`ext_name`, `ext_status`, `ext_customerref`, `ext_placed`, `ext_lines_json` memo, `ext_total` decimal).
- [ ] **Step 4:** Run the same tests and `dotnet run --project tools/BrokerForge.Cli -- validate descriptors --schemas schemas`. Expected: PASS, exit 0.
- [ ] **Step 5:** Commit `feat: table schema files, validator rule 7 and bf validate`.

### Task 2: CI

**Files:** Create `.github/workflows/ci.yml`.

- [ ] **Step 1:** Workflow on push and pull request: `actions/setup-dotnet` with 8.0.x and 10.0.x (the `.slnx` needs the 10 SDK), `actions/setup-node` 20, `dotnet build BrokerForge.slnx`, `dotnet test BrokerForge.slnx`, `npm ci && npm test` in `tools/mock-api`, `dotnet run --project tools/BrokerForge.Cli -- validate descriptors --schemas schemas`. No secrets: contract tests use the dummy key they set themselves.
- [ ] **Step 2:** Verify locally with `act` if installed; otherwise run each command in order and record that the workflow itself is unverified until the first push.
- [ ] **Step 3:** Commit `ci: build, test and validate descriptors`.

### Task 3: Descriptor features for Custom APIs, JSON columns and option set writes

**Files:** Modify `src/BrokerForge.Descriptors/` (parser, model, validator, renderer, `ResponseMapper`), `descriptors/OrdersAPI/*.yaml`, spec sections per P6. Tests in `tests/BrokerForge.Descriptors.Tests/`.

**Interfaces:**
- Produces: `OperationDescriptor.CustomApi` (unique name or null); `ParamBinding(string Name)`; `ResponseSpec.Outputs : IReadOnlyDictionary<string, MapEntry>`; `BindingContext.Inputs : IReadOnlyDictionary<string, Cell>?`; `ResponseMapper.MapOutputs(OperationDescriptor d, BrokerResult r, string correlationId) -> List<Cell>`; `ColBinding.OptionSet` (map name or null).

- [ ] **Step 1:** Tests: `Parse_CustomApiDescriptor_SpecExample` (the spec 13 `submitOrder` YAML), `Parse_IdInCustomApi_RejectedUntilBoundApis`, `Validate_ParamOutsideCustomApi_Rule2`, `Render_ParamBindings_PathHeaderBody`, `Outputs_MapsTypedCells`, `Outputs_MissingPath_ThrowsMappingNamingOutput`, `Outputs_OptionalMissing_Null`, `Map_JsonType_SerialisesSubtree` (`$.lines` → `[{"sku":"A","qty":2}]` string), `Render_ColOptionSet_ReverseMapsToSourceValue` (100000001 → `shipped`), `Render_ColOptionSet_UnknownValue_ThrowsValidation`, `Render_OptionSetCellWithoutMap_StillRefused`.
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Descriptors.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement. Remove the matching phase 2 rejections. Orders: `ext_lines_json` mapped with `type: json` on reads (drop it from `unmapped`); `ext_status` written through `{ col: ext_status, optionset: orderStatus }` in create and update.
- [ ] **Step 4:** Run the Descriptors and Contract test projects. Expected: PASS, including the existing Orders end-to-end tests.
- [ ] **Step 5:** Commit `feat(descriptors): Custom API binds, params and outputs; json map type; option set writes`.

### Task 4: Contracts and host for Custom APIs

**Files:** Modify `src/BrokerForge.Contracts/Canonical/*.cs` (P3), `src/BrokerForge.Host/Config/HostConfigStore.cs`, `Routing/RequestRouter.cs`, `Endpoints/ExecuteEndpoint.cs`. Tests in `tests/BrokerForge.Contract.Tests/` with a `descriptors/host/EchoAPI` Custom API descriptor and `tests/BrokerForge.Contracts.Tests/WireParityTests.cs`.

**Interfaces:**
- Consumes: Task 3 `MapOutputs`, `BindingContext.Inputs`.
- Produces: `HostConfigStore.CustomApi(string uniqueName) -> (ServiceInstance, OperationDescriptor)?`; responses with `outputs`.

- [ ] **Step 1:** Tests: `Wire_InputsAndOutputs_DcjsStjParity`, `CustomApi_EchoesInputsToSource_ReturnsOutputs`, `CustomApi_MissingRequiredInput_400Validation`, `CustomApi_WrongInputType_400Validation`, `CustomApi_Unknown_400Unsupported`, `CustomApi_ServiceInstanceMismatch_400`, `Config_DuplicateCustomApiName_StartupFails`, `CustomApi_SourceError_MappedThroughErrors`.
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Contract.Tests tests/BrokerForge.Contracts.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement. Required inputs and types come from the descriptor's `param` bindings plus the schema's `customApis` block, which the host loads from `Config:SchemasPath/<instance>.yaml` (Dockerfile copies `schemas/`).
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit `feat: Custom API execution in the canonical contract and host`.

### Task 5: SwaggerForge

**Files:** Create `tools/SwaggerForge/` (`Drafter.cs`, `DraftResult.cs`, `Heuristics.cs`), `tests/SwaggerForge.Tests/` with fixtures `orders-openapi.yaml` (hand-written for the Orders mock) and `unsupported.yaml`. Modify `tools/BrokerForge.Cli` (`bf draft`).

**Interfaces:**
- Consumes: `SchemaParser`, `DescriptorParser`, `DescriptorValidator` (Task 1).
- Produces: `Drafter.Draft(OpenApiDocument doc, DraftOptions o) -> DraftResult`; `record DraftOptions(string ServiceInstance, string Prefix, string Table)`; `record DraftResult(string SchemaYaml, IReadOnlyDictionary<string,string> Descriptors, IReadOnlyList<SkippedOperation> Skipped)`; `bf draft <openapi> --instance <name> --prefix <p> --table <logical> --out <dir>` writes `schemas/<instance>.yaml`, `descriptors/<instance>/*.yaml` and `<out>/<instance>-draft-report.md`, and refuses to overwrite existing files.

Mapping rules (anything not matched is skipped with the reason, never guessed):
- `GET /things` returning an array (or an object with one array property) → `RetrieveMultiple`. Paging only when the parameters identify it: `limit`+`cursor` with a next-cursor response field → cursor; `limit`+`offset` (+ `total`) → offset; `page`+`pageSize` → pageNumber; otherwise skipped "paging not identifiable". Query parameters with an `enum` or a simple scalar type become `eq` filters on the mapped column; `sort` parameters are not guessed.
- `GET /things/{id}` → `Retrieve`; `POST /things` → `Create` (key from response `id`, or `Location` with a pattern); `PATCH`/`PUT /things/{id}` → `Update`; `DELETE /things/{id}` → `Delete`.
- Any other `POST` with an `operationId` → `customapi:<prefix>_<PascalCase operationId>`, inputs from path, query and flat JSON body properties, outputs from flat response properties.
- Columns from the item schema's flat scalar properties; enums become option sets numbered from 100000000 in document order; nested objects flattened one level (`state.code` → `ext_statecode`); arrays become `memo` with `type: json`.
- Every emitted descriptor must pass `DescriptorValidator` with the emitted schema; a failure is a SwaggerForge bug and fails the run.

- [ ] **Step 1:** Tests: `Draft_OrdersOpenApi_EmitsFiveCrudDescriptorsThatValidate`, `Draft_OrdersOpenApi_SchemaMatchesOrdersColumns`, `Draft_CursorPagingIdentified`, `Draft_PagingUnidentifiable_ListSkippedWithReason`, `Draft_UnsupportedShapes_SkippedWithReasons` (oneOf, cycle, no operationId, no response schema), `Draft_ActionBecomesCustomApi`, `Cli_Draft_RefusesToOverwrite`.
- [ ] **Step 2:** Run `dotnet test tests/SwaggerForge.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement with `Microsoft.OpenApi.Readers` 1.6.22; YAML emitted with YamlDotNet in the field order of the spec examples.
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat: SwaggerForge drafts schema and descriptors from OpenAPI`.

### Task 6: Generator

**Files:** Create `src/BrokerForge.Generator/` (`DataverseClient.cs`, `TableGenerator.cs`, `CustomApiGenerator.cs`, `GenerationPlan.cs`), `tests/BrokerForge.Generator.Tests/` (fake `HttpMessageHandler` with recorded Web API responses). Modify `tools/BrokerForge.Cli` (`bf generate`), `tools/dev/setup_dataverse.py` (core only: provider, `bf_brokerdatasource`, both plug-in types, environment variables; `ext_order` moves to the generator), `tools/dev/setup-dataverse.sh`.

**Interfaces:**
- Consumes: `SourceSchema`, descriptors.
- Produces: `bf generate --org <url> --schemas schemas --descriptors descriptors --instance <name> --solution <unique> --publisher <unique> --host-url <url> --scope <scope> [--dry-run]`. Uses `AzureCliCredential`, then `DefaultAzureCredential`. `--dry-run` prints the planned Web API calls without writing.

Behaviour:
- Ensures the source solution and publisher, the `bf_brokerdatasource` record for the instance (host URL, scope, instance name; no secrets), the virtual table bound to the BrokerForge provider and that record, each column with its `ExternalName` equal to its logical name, the option sets, and each Custom API with its request parameters and response properties bound to `BrokerForge.Plugin.CustomApiPlugin`.
- Re-runs are no-ops when nothing changed. A changed column type, a removed column or a changed parameter is refused with a list of differences; nothing is altered or deleted.
- Reads back `TableType` and `DataProviderId` after creating a table and fails if the platform dropped them (HLD 15).

- [ ] **Step 1:** Tests: `Generate_NewTable_PostsEntityThenColumns`, `Generate_Rerun_NoWrites`, `Generate_ColumnTypeChanged_RefusesWithoutWriting`, `Generate_ColumnRemoved_Refuses`, `Generate_CustomApi_CreatesApiParamsAndProperties`, `Generate_PlatformDroppedProvider_Fails`, `Generate_DryRun_NoWrites`, `Generate_NoSecretInAnyPayload`.
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Generator.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement against the Web API with `HttpClient`.
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat: generator creates tables, columns and Custom APIs through the Web API`.

### Task 7: Custom API plug-in

**Files:** Create `src/BrokerForge.Plugin/CustomApiPlugin.cs`, `src/BrokerForge.Plugin/Translation/ParameterTranslator.cs`. Modify `tests/BrokerForge.Plugin.Tests/` (linked sources, `CustomApiPluginTests.cs`). Plug-in version stays `0.1.x` so the assembly updates in place.

**Interfaces:**
- Consumes: Task 4 contract (`Inputs`, `Outputs`).
- Produces: `ParameterTranslator.ToInputs(ParameterCollection inputs, string correlationId) -> List<Cell>`; `ParameterTranslator.SetOutputs(ParameterCollection outputs, List<Cell> cells, string correlationId)`.

- [ ] **Step 1:** Tests: `Execute_CustomApi_SendsInputsAndSetsTypedOutputs`, `Execute_CustomApi_NoHostUrlVariable_BrokerUnavailableNamingIt`, `Execute_CustomApi_HostError_PassesThrough`, `ToInputs_UnsupportedType_UnsupportedQuery`, `SetOutputs_UnknownOutput_Mapping`.
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Plugin.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement. Environment variable values read once per execution through `IOrganizationService` (current value, else default value).
- [ ] **Step 4:** Run the tests and build the net462 plug-in. Expected: PASS, build succeeds.
- [ ] **Step 5:** Commit `feat(plugin): Custom API handler`.

### Task 8: Orders through the new tooling, live

**Files:** Modify `tools/dev/acceptance.py` (option set write, `ext_lines_json` read back), `docs/findings/phase2.md` (new).

- [ ] **Step 1:** `tools/dev/deploy.sh`, `tools/dev/setup-dataverse.sh` (core), then `bf generate` for OrdersAPI against the existing `ext_order`. Expected: no-op apart from anything the schema adds; if the generator wants to change an existing column, stop and record why.
- [ ] **Step 2:** `tools/dev/acceptance.sh`. Expected: all phase 1 assertions plus the two new ones pass.
- [ ] **Step 3:** Commit `test: Orders through generator and new descriptor features, live`.

### Task 9: Inventory mock, then onboarding with no new code

**Files (mock, before the onboarding commit):** Modify `tools/mock-api/server.js`, `data.js`, `server.test.js`; create `tools/mock-api/inventory-openapi.yaml` (P8).

**Files (onboarding commit):** `schemas/InventoryAPI.yaml`, `descriptors/InventoryAPI/*.yaml`, host settings in `infra/containerApps.bicep`, the `inventory-api-key` secret in `tools/dev/deploy.sh`, `tools/dev/acceptance_inventory.py` and `.sh`.

- [ ] **Step 1:** Mock tests: `inventory requires api_key query`, `inventory offset paging with total covers all items once`, `inventory retrieve, create, update, delete`, `inventory adjust returns onHand and adjustedAt`. Run `npm test`; expected FAIL; implement; expected PASS. Commit `test(mock): Inventory API`.
- [ ] **Step 2:** Record `BASE=$(git rev-parse HEAD)`. Run `bf draft tools/mock-api/inventory-openapi.yaml --instance InventoryAPI --prefix ext --table ext_item --out out/`. Review the report; edit the YAML only (display names, filters the source supports). Run `bf validate`.
- [ ] **Step 3:** Add the instance and object map to the host settings, deploy, `bf generate --instance InventoryAPI`.
- [ ] **Step 4:** `tools/dev/acceptance_inventory.sh`: list with an offset page boundary, retrieve, create, update, delete, the `ext_AdjustStock` action through the Web API, an undeclared filter refused, a missing required input refused. Expected: all pass.
- [ ] **Step 5:** `git diff --stat $BASE..HEAD -- src/` prints nothing. Commit `feat: onboard InventoryAPI with configuration only`.
- [ ] **Step 6:** Write `docs/findings/phase2.md` (results, what SwaggerForge skipped and why, platform differences, time taken against NFR-06), update CLAUDE.md current state and commands. Commit `docs: phase 2 findings`.
