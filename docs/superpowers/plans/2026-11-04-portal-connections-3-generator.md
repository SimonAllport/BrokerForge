# Portal connections, plan 3 of 5: the generator in the host

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The host generates a source's Dataverse parts from a connection design as its own application user, in the background: the table and columns, every column on the default view and main form, the `<Instance> user` role, the Custom APIs and the solution parts. A changed design shows a confirmed list of changes, including removals and type changes, checked against Dataverse's dependencies.

**Architecture:** `BrokerForge.Generator` stays a library. `GenerationPlan.Diff` compares the schema with what exists and returns a `ChangeList`: adds, in-place updates, removals and recreates, each one line of text, with a fingerprint of the whole list. `Generator.PreviewAsync` returns that list plus any change blocked by a dependency outside BrokerForge's generated parts. `Generator.RunAsync` recomputes the preview and applies it only when its fingerprint equals the confirmed one and nothing is blocked. The design itself (web resource) and its two environment variables are written by a new `DesignPublisher`, which replaces `tools/dev/publish_design.py`. In the host, `GenerationJobs` runs one job per environment and instance in the background with a `DataverseClient` whose token comes from the host's `TokenCredential`. It reports through `IGenerationProgress` and invalidates the environment's config cache when a job succeeds. Plan 4's `bf_Generate` Custom API starts these jobs and writes progress to the connection row. In this plan the callers are a host admin command and `bf generate`.

**Tech Stack:** .NET 8, System.Text.Json, Dataverse Web API 9.2 (metadata, `systemforms`, `savedqueries`, `roles`, `RetrieveDependenciesForDelete`), xUnit with the existing `FakeDataverse`.

**Spec:** `docs/superpowers/specs/2026-11-03-portal-connections-design.md` (section 6 steps 3–4, section 7, section 6 "Host identity").

## Global Constraints

- "Fail loudly. Never return partial, approximate or client-side-filtered results." A refused or blocked run writes nothing and names every reason.
- "No secrets in Dataverse rows, descriptors, logs, traces, test fixtures or exception messages." The design carries `SecretNames` only. The `Auth` environment variable carries non-secret settings only.
- "Ask before changing a contract in `BrokerForge.Contracts` or the descriptor schema." This plan changes neither.
- "BrokerForge never removes someone else's component." Only the table's default main form and default public view count as generated parts. Every other dependent blocks the change.
- "Generate then applies exactly that list." The list is identified by its fingerprint, and a different list at apply time refuses the run.
- "Generate runs only in the environment the design belongs to." Other environments get changes by importing the solution as an upgrade.
- Generation runs in the background; a plug-in call has 2 minutes. A job has its own time limit of 15 minutes and fails typed when it runs out.
- The repo is public: no real tenant, environment, org or user identifiers (use `contoso`, all-zero GUIDs).
- Writing to the dev Dataverse environment is allowed (as in plans 1 and 2). Nothing outside `rg-brokerforge-dev` and that environment.
- Every existing test and both acceptance scripts pass at the end of every task.

## Review Focus

1. **The environment changed between preview and Generate** (someone added a column by hand, or another run finished first): the fingerprint differs, so nothing is applied and the refusal shows the new list. Test in Task 3 `Run_FingerprintStale_RefusedNothingWritten`.
2. **A removal blocked by a hand-made view while other changes are fine:** nothing in the list is applied, not even the adds, and the block names the view. Test in Task 3 `Run_BlockedRemoval_NoWritesAtAll`.
3. **A Dataverse failure halfway through a run** (HTTP 500 on the fourth write): the job fails with a message naming the failed call and the writes already made, and a second run with a fresh preview converges without duplicates. Test in Task 3 `Run_FailsHalfway_RerunConverges` and Task 5 `Job_GeneratorThrows_FailedWithMessage`.
4. **Two Generate requests for one instance at once,** or for the same instance name in two environments: the second in one environment is refused as busy; the two environments run independently. Test in Task 5 `Start_SameInstanceTwice_SecondBusy` and `Start_SameNameTwoEnvironments_BothRun`.
5. **A secret reaching a solution part:** the published web resource and both environment variables contain no secret value, even when the design's `SecretNames` match keys that are set in the store. Test in Task 4 `Publish_NoSecretValueInAnyWrite`.

---

### Task 1: Live spike: forms, views, dependencies, removals and roles on a virtual table

**Files:**
- Create: `tools/spike/generator.sh` (throwaway probe, kept for the record)
- Create: `docs/findings/portal-generator-spike.md`

**Interfaces:**
- Produces: the exact Web API calls Tasks 2–4 implement, recorded in the findings and the ledger:
  - how to find the table's default main form (`systemforms`, `type` 2, by `objecttypecode`) and default public view (`savedqueries`, `querytype` 0, `isdefault` true);
  - the `formxml`, `fetchxml` and `layoutxml` shapes that show N columns, and the publish call that makes them visible;
  - the `RetrieveDependenciesForDelete` call for an attribute and for a Custom API, and the fields that name each dependent (component type, object ID and how to get its name);
  - DELETE of an attribute and of a Custom API on a virtual table;
  - role create, `AddPrivilegesRole` at depth `Global`, `prvRead<SchemaName>` naming for a virtual table, and whether the role and web resource join the solution through the `MSCRM.SolutionUniqueName` header or need `AddSolutionComponent`;
  - the privileges a non-administrator host role needs for all of the above.

- [ ] **Step 1:** In a scratch solution `BrokerForgeGeneratorSpike`, run `bf generate` for a scratch instance from the Inventory fixtures (copy into a temp folder; instance `SpikeAPI`, table `ext_spikeitem`). Read the platform-created forms and views and record their names, types and XML.
- [ ] **Step 2:** PATCH the default main form and default public view to show every column, publish, and read them back. Record the minimal XML that works.
- [ ] **Step 3:** Create a hand-made public view that uses one column. Call `RetrieveDependenciesForDelete` for that column and for one the generated parts use. Record the response rows. DELETE the second column and confirm the platform removes it. Then try to DELETE the first and record the error.
- [ ] **Step 4:** Create role `SpikeAPI user` with `prvReadext_spikeitem` at `Global`, in the solution. Record whether the header puts it in the solution. Record the privilege rule for granting: can a role holder grant a privilege it doesn't hold? Look this up in Microsoft Learn. If it can be checked live without a second identity, check it.
- [ ] **Step 5:** DELETE a Custom API created for the spike and confirm its parameters go with it.
- [ ] **Step 6:** Delete the scratch solution's components and the solution. Write the findings with no real org names. If dependency lookup or removals aren't possible, stop and report to Simon (spec principle 6).
- [ ] **Step 7: Commit** `docs: generator spike findings` with both files.

### Task 2: The change list

**Files:**
- Modify: `src/BrokerForge.Generator/GenerationPlan.cs`
- Test: `tests/BrokerForge.Generator.Tests/ChangeListTests.cs`

**Interfaces:**
- Consumes: `SourceSchema` (`src/BrokerForge.Schema`), `ExistingTable`, `ExistingColumn`, `ExistingCustomApi` (today's).
- Produces:
  - `enum ChangeKind { AddTable, AddColumn, UpdateColumn, RemoveColumn, RecreateColumn, AddApi, UpdateApi, RemoveApi, RecreateApi }`
  - `sealed record Change(ChangeKind Kind, string Target, string Text)`. `Target` is `table.column` or the API's unique name. `Text` is plain words, for example `add column Colour (ext_colour, string 50)`, `remove column SKU (ext_sku)` or `change Price (ext_price) from int to decimal 2`.
  - `sealed record ChangeList(IReadOnlyList<Change> Changes, IReadOnlyList<string> Refusals, string Fingerprint)`. Changes are ordered by kind, then target, ordinal. `Fingerprint` is the first 16 hex characters of the SHA-256 of the change texts joined by `\n`; it is `"none"` when there are no changes.
  - `static ChangeList GenerationPlan.Diff(SourceSchema schema, ExistingTable? table, IReadOnlyDictionary<string, ExistingCustomApi?> apis, string providerId, string? dataSourceId, string? customApiPluginTypeId, string instance)`. It replaces `Compare`. Today's table-level differences (wrong table type, provider, data source, ExternalName) stay `Refusals`. A change of primary name column or key is also a refusal.

Rules, which are the plan's decisions:
- A display name change is `UpdateColumn` / `UpdateApi`, applied in place.
- Any other column change (type, max length, precision, options, ExternalName) is `RecreateColumn`. Virtual tables hold no data, so a recreate loses nothing.
- A column in Dataverse but not in the schema is `RemoveColumn`. The primary name column is never removed; if the schema no longer has it, that's a refusal.
- A Custom API whose plug-in type, binding, kind, inputs or outputs differ is `RecreateApi`. One in Dataverse with the schema's prefix and the instance's plug-in type, but not in the schema, is `RemoveApi`. To find those, `ReadApisAsync` lists `customapis` whose uniquename starts with the prefix and whose plug-in type is BrokerForge's.

- [ ] **Step 1: Write the failing tests:**
  - `Diff_NothingExists_AddTableAndColumnsAndApis`;
  - `Diff_Same_NoChangesFingerprintNone`;
  - `Diff_PropertyGone_RemoveColumn`;
  - `Diff_TypeChanged_RecreateWithBothTypesInText`;
  - `Diff_DisplayNameOnly_UpdateInPlace`;
  - `Diff_PrimaryNameGone_Refusal`;
  - `Diff_ApiInputChanged_RecreateApi`;
  - `Diff_ApiGone_RemoveApi`;
  - `Diff_SameInputTwice_SameFingerprint` (determinism);
  - `Diff_DifferentList_DifferentFingerprint`;
  - the existing `GeneratorTests` refusal cases still refusing.
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Generator.Tests --filter ChangeListTests`. Expected: FAIL to compile (no `Diff`).
- [ ] **Step 3:** Implement `Diff` and the records. Leave `Generator.RunAsync` working by having it treat any change other than an add as a refusal, which is today's behaviour, until Task 3.
- [ ] **Step 4:** Run `dotnet test tests/BrokerForge.Generator.Tests`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(generator): change list with fingerprint`.

### Task 3: Preview, confirm and apply: view, form, role, removals and recreates

**Files:**
- Modify: `src/BrokerForge.Generator/Generator.cs`, `TableGenerator.cs`, `CustomApiGenerator.cs`
- Create: `src/BrokerForge.Generator/FormsAndViews.cs`, `src/BrokerForge.Generator/RoleGenerator.cs`, `src/BrokerForge.Generator/Dependencies.cs`
- Modify: `tests/BrokerForge.Generator.Tests/FakeDataverse.cs` (forms, views, roles, privileges, dependencies, DELETE, failure injection)
- Test: `tests/BrokerForge.Generator.Tests/GeneratorTests.cs`, `tests/BrokerForge.Generator.Tests/ChangeApplyTests.cs`

**Interfaces:**
- Consumes: Task 1's calls; Task 2's `ChangeList`.
- Produces:
  - `GenerateOptions` gains `IReadOnlySet<string> Messages` (the table messages the descriptors bind: `RetrieveMultiple`, `Retrieve`, `Create`, `Update`, `Delete`) and `string? ConfirmedFingerprint`. `bf generate` fills `Messages` from `descriptors/<Instance>/*.yaml`; the host fills it from the design's descriptors.
  - `sealed record Blocked(Change Change, string Dependent)`, where `Dependent` names the component, for example `view "Open orders" (savedquery 0000…)`.
  - `sealed record GenerationPreview(ChangeList Changes, IReadOnlyList<Blocked> Blocked, IReadOnlyList<string> Parts)`. `Parts` lists the view, form and role updates the run would also make.
  - `static Task<GenerationPreview> Generator.PreviewAsync(DataverseClient client, SourceSchema schema, GenerateOptions o, CancellationToken ct)`. Reads only.
  - `RunAsync` computes the preview and then refuses with `GenerationRefusedException`, writing nothing, when:
    - there are refusals;
    - something is blocked;
    - the list has a change other than an add and `ConfirmedFingerprint` is null;
    - `ConfirmedFingerprint` doesn't equal the preview's.

    A list with only adds runs without a fingerprint, so `bf generate` on a new source works as today.
  - The apply order is:
    1. table;
    2. columns added, then columns recreated;
    3. columns taken off the generated form and view;
    4. columns removed or recreated (delete, then add);
    5. display-name updates;
    6. the form and view rewritten with every column (primary name first, then schema order);
    7. publish;
    8. APIs removed, recreated, added and updated;
    9. role `<Instance> user` (org-level privileges `prvRead` when `RetrieveMultiple` or `Retrieve` is bound; `prvCreate`, `prvWrite` and `prvDelete` when bound), made exactly equal to that set on re-runs.

  - `GenerationRefusedException` messages say which step failed. A `DataverseException` during apply is rethrown wrapped as `GenerationFailedException(string step, IReadOnlyList<string> writesDone, Exception inner)`.

- [ ] **Step 1: Write the failing tests:**
  - `Run_NewSource_FormAndViewShowEveryColumn`, which checks the PATCHed formxml and layoutxml list every schema column;
  - `Run_NewSource_RoleWithPrivilegesFromMessages`: Read+Create+Write+Delete for Orders' messages, Read only for a list-only schema;
  - `Run_RemovalWithoutFingerprint_Refused`;
  - `Run_FingerprintStale_RefusedNothingWritten`;
  - `Run_BlockedRemoval_NoWritesAtAll`, where the fake reports a dependent savedquery that isn't the default view;
  - `Run_RemoveColumn_TakenOffFormAndViewThenDeleted`, which checks the write order;
  - `Run_TypeChange_DeleteThenAddSameName`;
  - `Run_ApiInputChange_DeleteThenCreate`;
  - `Run_FailsHalfway_RerunConverges`: the fake fails the fourth write once, `GenerationFailedException` names the step and the writes made, and a fresh preview plus a run finish with no duplicate POST;
  - `Run_Twice_SecondRunNoWrites` (idempotent re-run, including role and parts);
  - `Preview_WritesNothing`.
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Generator.Tests`. Expected: the new tests FAIL.
- [ ] **Step 3:** Implement. `Dependencies.ForDeleteAsync(client, componentType, objectId)` returns the dependents, excluding the default main form and default public view IDs. `FormsAndViews` builds the XML from Task 1's findings.
- [ ] **Step 4:** Run `dotnet test tests/BrokerForge.Generator.Tests`. Expected: PASS.
- [ ] **Step 5:** Update `tools/BrokerForge.Cli` `bf generate`:
  - it prints the preview (changes, blocked, fingerprint);
  - it takes `--confirm <fingerprint>`;
  - exit codes: 0 applied or nothing to do, 1 refused or blocked, 4 confirmation needed.

  Update `tools/dev/generate.sh` and the `CLAUDE.md` command line. Run the CLI tests. Expected: PASS.
- [ ] **Step 6: Commit** `feat(generator): preview, confirmed apply, view, form and role`.

### Task 4: Solution parts: the design and its environment variables

**Files:**
- Create: `src/BrokerForge.Generator/DesignPublisher.cs` (the Generator project references `BrokerForge.Connections`)
- Test: `tests/BrokerForge.Generator.Tests/DesignPublisherTests.cs`
- Delete: `tools/dev/publish_design.py`, `tools/dev/publish-design.sh` (replaced; the `generate` command in Task 5 does their job)

**Interfaces:**
- Consumes: `ConnectionDesign`, `DesignSerializer`, `DesignCarrier` (`src/BrokerForge.Connections`); `DataverseClient`.
- Produces:
  - `static Task<int> DesignPublisher.PublishAsync(DataverseClient client, ConnectionDesign design, string prefix, string solution, string? baseUrl, string? authJson, CancellationToken ct)`. It returns the version written. It writes:
    - the web resource `<prefix>_/brokerforge/<Instance>/design.xml` (type 4), with the version one more than the published one, or 1 when there is none; when the content apart from the version is unchanged, it keeps the version and writes nothing;
    - `PublishXml` for it;
    - `bf_<Instance>_BaseUrl` (String) and `bf_<Instance>_Auth` (JSON), in the solution with the given value as the definition's default value. These are the defaults that travel, per spec 7, so this replaces plan 1's "current value without the solution header".
  - A design whose `Instance` doesn't match a name the format allows is a `DesignFormatException` before any write.
- Ruling to record: spec 7 says dev's value is the default and each environment sets its own. Plan 1 kept dev's URL out of the solution because it pointed at dev's internal mock. Now the default is the admin's base URL and travels; test and prod override it with a current value. `DataverseDesignSource` already prefers the current value.

- [ ] **Step 1: Write the failing tests:**
  - `Publish_New_Version1WebResourceAndBothVariables`;
  - `Publish_Changed_VersionIncremented`;
  - `Publish_Unchanged_NoWrites`;
  - `Publish_NoSecretValueInAnyWrite`, where a design with `SecretNames: [apiKey]` and a sentinel value `s3cr3t-sentinel` never appears in any request body;
  - `Publish_ContentRoundTripsThroughDesignSource`, where the written content parses with `DesignCarrier.Unwrap` and `DesignSerializer.Parse` into an equal design.
- [ ] **Step 2:** Run them. Expected: FAIL to compile.
- [ ] **Step 3:** Implement. Wire it into `Generator.RunAsync` as the step after the role, when `GenerateOptions.Design` (a new `DesignPublish?` holding design, base URL and auth JSON) is set.
- [ ] **Step 4:** Run `dotnet test tests/BrokerForge.Generator.Tests`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(generator): publish the design and its environment variables`.

### Task 5: Background generation jobs in the host

**Files:**
- Create: `src/BrokerForge.Host/Generation/GenerationJobs.cs`, `GenerationRequest.cs`, `IGenerationProgress.cs`, `HostDataverseClientFactory.cs`
- Create: `src/BrokerForge.Host/Admin/GenerateCommand.cs` (`generate` admin command, next to `SetInstanceSecretCommand`)
- Modify: `src/BrokerForge.Host/Program.cs`, `src/BrokerForge.Host/BrokerForge.Host.csproj` (reference `BrokerForge.Generator`)
- Test: `tests/BrokerForge.Contract.Tests/GenerationJobTests.cs`

**Interfaces:**
- Consumes:
  - `Generator.PreviewAsync` and `RunAsync`, with `GenerateOptions` as Task 3 extends it;
  - `DesignPublisher` through `GenerateOptions.Design`;
  - `IHostConfigProvider.Invalidate(environmentId)`;
  - `TrustOptions` (`AllowedCaller.OrgUrl`, the environment's URL).
- Produces:
  - `sealed record GenerationRequest(string EnvironmentId, ConnectionDesign Design, string Solution, string Publisher, string? BaseUrl, string? AuthJson, string? ConfirmedFingerprint, string CorrelationId)`.
  - `enum JobState { Queued, Running, Succeeded, Failed }`.
  - `sealed record GenerationJob(Guid Id, string EnvironmentId, string Instance, JobState State, IReadOnlyList<string> Writes, int? DesignVersion, string? Failure, DateTimeOffset Started, DateTimeOffset? Finished)`.
  - `interface IGenerationProgress { Task ReportAsync(GenerationJob job, CancellationToken ct); }`. The default implementation logs `Generation {Instance} in {EnvironmentId}: {State}` with the correlation ID and never the design text. Plan 4 adds the connection-row writer.
  - `sealed class GenerationJobs`:
    - `GenerationJob Start(GenerationRequest r)` returns at once. It throws `GenerationBusyException` when a job for the same environment and instance is Queued or Running, and `ConfigurationException` when the environment has no `OrgUrl`.
    - `GenerationJob? Get(Guid id)`.
    - `Task<GenerationPreview> PreviewAsync(GenerationRequest r, CancellationToken ct)`.
    - Jobs run on the thread pool under a 15-minute `CancellationTokenSource`. A timeout fails the job with `Generation timed out after 15 minutes; writes made: …`.
    - Success calls `Invalidate(environmentId)`. Finished jobs are kept for 24 hours in memory; a host restart forgets them, and plan 4's row keeps the last result.
  - `HostDataverseClientFactory.Create(string orgUrl)` returns a `DataverseClient` on the named `dataverse` HttpClient (base address `<org>/api/data/v9.2/`), with a token for `<org>/.default` from the host's `TokenCredential`.
  - Admin command `generate <environmentId> <Instance> --solution <S> --publisher <P> (--design-file <path> | --from-environment) [--base-url <u>] [--auth-file <path>] [--preview] [--confirm <fp>]`:
    - it uses the host's credential (managed identity in the container; `AzureCliCredential` when `--credential cli` is given, for local runs);
    - it runs one job synchronously and prints the preview or the job result;
    - it prints no design text.

    `--from-environment` regenerates from the design already published there.
- [ ] **Step 1: Write the failing tests** (with a fake `HttpMessageHandler` Dataverse, reusing the generator test fake by linking `FakeDataverse.cs`):
  - `Start_ReturnsAtOnceThenSucceeds`;
  - `Start_SameInstanceTwice_SecondBusy`;
  - `Start_SameNameTwoEnvironments_BothRun`;
  - `Job_GeneratorThrows_FailedWithMessage`;
  - `Job_Timeout_FailedTyped` (time limit injectable for the test);
  - `Job_Succeeded_InvalidatesThatEnvironmentOnly`;
  - `Progress_NeverContainsDesignText`;
  - `Start_UnknownEnvironment_ConfigurationException`.
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Contract.Tests --filter GenerationJobTests`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement and register (`GenerationJobs` singleton, default `IGenerationProgress`, factory). Add the `generate` command to `Program.cs` the way `set-instance-secret` is dispatched.
- [ ] **Step 4:** Run `dotnet test BrokerForge.slnx`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(host): background generation jobs as the host identity`.

### Task 6: Host role, live run in dev, findings and decision

**Files:**
- Modify: `tools/dev/setup_dataverse.py` (the `BrokerForge host` role gains the privileges Task 1 listed)
- Create: `tools/dev/generate-in-host.sh <Instance> <Solution> [args]` (runs the admin command in the container, like `set-instance-secret.sh`)
- Create: `docs/findings/portal-3.md`
- Modify: `docs/hld.md` (decision D-36: change list, fingerprint confirmation, generated parts, role, defaults travel, background jobs)

- [ ] **Step 1:** Run `setup_dataverse.py --host-user` against dev and record the role's privileges.
- [ ] **Step 2:** Deploy the host (`tools/dev/deploy.sh`). Run `generate-in-host.sh WarehouseAPI BrokerForgeWarehouse --from-environment --preview`, then with `--confirm`. Expected:
  - the default view and form show every column;
  - role `WarehouseAPI user` exists with Read, Create, Write and Delete on `ext_stockitem`;
  - design version 2, because the defaults now travel;
  - the host logs `design v2` on the next call.

  If granting privileges as the host role is refused, record the platform message and stop and report to Simon. Do not give the host System Administrator without his say.
- [ ] **Step 3:** Locally, with `--credential cli`, change the WarehouseAPI design: remove `sku` and change `price` from decimal to int. Preview should list both changes. Add a hand-made view that uses `ext_sku`. Preview should name it as blocked, and Generate should refuse with nothing applied. Remove the view, confirm, and check that the column is gone, `ext_price` is int, and the form and view are updated. Then restore the design and generate again.
- [ ] **Step 4:** Assign `WarehouseAPI user` to a non-administrator test user (from `TICKETS_BOB_OID` in `~/.brokerforge/dev-ids.env`) and list `ext_stockitems` as that user, if a token for them can be had without a password. Otherwise record it as **Not verified**: plan 5 runs done criterion 6.
- [ ] **Step 5:** Run both acceptance scripts. Expected: all assertions pass.
- [ ] **Step 6:** Write `portal-3.md`: what ran, timings, the role privileges, anything not verified. Write D-36. Search the solution export and host logs for the WarehouseAPI key (phase 3 findings 5 method) and record the result.
- [ ] **Step 7: Commit** `docs: portal-3 findings, D-36`.
