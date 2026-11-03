# Portal connections, plan 1 of 5: connection designs and per-environment config

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The host can run a source from a connection design stored in a Dataverse solution part, per environment, with its secret in the host's store. No admin screens yet.

**Architecture:** A connection design is one JSON document holding everything that is the same in every environment: the pasted OpenAPI text, the admin's answers, the descriptors and table schema (as today's YAML text), the object map and the non-secret auth settings. Per-environment values (base URL, auth overrides) are Dataverse environment variables. `HostConfigStore` stops reading `IConfiguration` directly: a `ConfigInput` is built either from host settings and files (today) or from designs, and one validator turns it into the store. An `IHostConfigProvider` returns the store for a request's environment, combining the file input and that environment's designs, cached.

**Tech Stack:** .NET 8, System.Text.Json, Azure.Data.Tables + ASP.NET Core Data Protection (existing), Dataverse Web API 9.2, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-03-portal-connections-design.md`

## The five plans

| Plan | Delivers | Spec sections |
| --- | --- | --- |
| 1 (this) | Carrier verified; design format; config from designs, per environment; instance secret store and `store:` refs; host reads Dataverse as its own app user | 6 (run time, host identity), 7, 8 (store only), 11 |
| 2 | SwaggerForge raises structured questions; answers applied; a draft service that returns endpoints, statuses and questions | 5 (screen 2 content), 6 steps 1–2 |
| 3 | Generator in the host as its own identity: every column on view and form, security role, solution parts, confirmed removals and type changes with dependency check, background jobs | 6 steps 3–4 |
| 4 | Admin app: connection tables, `bf_ReadDefinition` / `bf_Check` / `bf_Generate` Custom APIs, form and review page, secret page, administrator role, core managed solution in `solution/` | 5, 6 (who may), 8, 9, 11 |
| 5 | Migration of Orders, Inventory and both Tickets sources; Bicep settings removed; HLD, guide and video redone; done criteria 1–9 run live | 10, 12 |

Each plan ends with green tests and a findings note, and is reported to Simon before the next starts.

## Global Constraints

- "Fail loudly. Never return partial, approximate or client-side-filtered results." Missing URL or secret is a typed failure naming what is missing, with the correlation ID.
- "No secrets in Dataverse rows, descriptors, logs, traces, test fixtures or exception messages."
- "Ask before changing a contract in `BrokerForge.Contracts` or the descriptor schema." This plan changes neither; a missing URL or key is `ConfigurationException` → the existing `ConfigurationError` payload.
- The repo is public: no real tenant, environment, org or user identifiers in code, tests or docs (use `contoso`, all-zero GUIDs).
- File-based config keeps working unchanged: every existing test and both acceptance scripts pass at the end of every task.
- Contract tests stay independent of Dataverse (spec 10): Dataverse is reached only through `IDataverseReader`, faked in tests.
- Writing to the dev Dataverse environment is allowed for Task 1 and Task 7 (Simon approved building against dev); nothing outside `rg-brokerforge-dev` and that environment.

## Review Focus

1. Two environments with the same instance name (WarehouseAPI in dev and test, one host): config, credentials and connect URLs must never cross. Test in Task 4 (`SameInstanceInTwoEnvironmentsStaysSeparate`) and Task 5 (`InstanceSecretsAreKeyedByEnvironment`).
2. A design and a file-configured instance with the same name in one environment: refuse loudly, never pick one. Test in Task 4 (`NameClashBetweenFilesAndDesignFails`).
3. A design that fails validation in one environment must not take down other environments or file-configured sources: that environment's request fails with `ConfigurationError` naming the design. Test in Task 6 (`InvalidDesignFailsOnlyItsEnvironment`).
4. Dataverse unreachable when the cache is cold: typed failure, not an empty config (which would read as "no such table"). When warm, the last good snapshot keeps serving until TTL plus a grace, then fails. Test in Task 6 (`DataverseDownColdFailsTyped`).
5. A secret value never appears in a design, environment variable, log line or exception text. Test in Task 5 (`SecretValueNeverInMessages`) and the log scan in Task 7.

---

### Task 1: Verify the solution part that carries a design (live spike)

**Files:**
- Create: `tools/spike/carrier.sh` (throwaway probe, kept for the record)
- Create: `docs/findings/portal-carrier.md`

**Interfaces:**
- Produces: a ruling recorded in the findings and the ledger: `Carrier = EnvironmentVariableJson | WebResource | DataSource`, its size limit, and the exact Web API reads that return it (entity set, filter, column). Task 6 implements exactly that read.

- [ ] **Step 1:** Build a test design of realistic size: Orders' descriptors and schema (`descriptors/OrdersAPI/*.yaml`, `schemas/OrdersAPI.yaml`) plus `tests/SwaggerForge.Tests/fixtures/orders-openapi.yaml`, wrapped as JSON. Record its byte size. Then pad a second copy to 4× that size, as headroom.
- [ ] **Step 2:** In a scratch solution `BrokerForgeCarrierSpike` (publisher `brokerforgeext`), try each candidate in order and stop at the first that passes all checks:
  - an environment variable definition of type JSON (`type` 100000003), with the design as `defaultvalue`;
  - a web resource (`webresourcetype` 4, XML, holding the JSON in a CDATA element; or type 3, script, if XML is refused);
  - a column on `bf_brokerdatasource`.

  Checks for each:
  - it saves at both sizes;
  - it reads back byte-identical through the Web API;
  - an exported unmanaged solution contains it as readable text (`pac solution export` then unzip: the JSON is visible, not base64);
  - importing that zip into the same environment under a new solution name, or re-importing, leaves the value unchanged.
- [ ] **Step 3:** Check that an environment variable's current value (`environmentvariablevalue`) overrides its default per environment, as the design's base URL needs. Check that a type String variable holds a 300-character URL.
- [ ] **Step 4:** Delete the scratch solution and its components. Write `docs/findings/portal-carrier.md` with the results table, the chosen carrier, its measured limit, and the exact reads. Use no real org names.
- [ ] **Step 5:** If no candidate passes, stop and report to Simon (spec 7). Do not fall back to Git.
- [ ] **Step 6: Commit** `docs: portal carrier spike findings` with both files.

### Task 2: Connection design format

**Files:**
- Create: `src/BrokerForge.Connections/BrokerForge.Connections.csproj` (net8.0, references `BrokerForge.Abstractions`; add it to `BrokerForge.slnx`)
- Create: `src/BrokerForge.Connections/ConnectionDesign.cs`, `src/BrokerForge.Connections/DesignSerializer.cs`
- Create: `tests/BrokerForge.Connections.Tests/` (xUnit, add it to the slnx), `DesignSerializerTests.cs`
- Create: `docs/connection-design-format.md` (the stable format DataverseToCode will read)

**Interfaces:**
- Produces:
  - `record ConnectionDesign(int FormatVersion, string Instance, int Version, string BrokerType, string OpenApi, IReadOnlyDictionary<string,string> Answers, DesignAuth Auth, DesignObjectMap? ObjectMap, string Schema, IReadOnlyDictionary<string,string> Descriptors)`. `FormatVersion` is always 1. `Descriptors` maps operation name to YAML text. `Schema` is the table schema YAML text.
  - `record DesignAuth(string Mode, string? StaticKind, string? HeaderName, string? QueryParam, string? Username, string? Scope, string? SsoKind, string? AsyncContext, DesignOAuth? OAuth, IReadOnlyList<string> SecretNames)`. It holds no values and no refs. `SecretNames` lists the instance secrets the design needs, for example `["apiKey"]`.
  - `record DesignOAuth(string AuthorizeUrl, string TokenUrl, IReadOnlyList<string> Scopes, string? ClientId, bool UsesConnectApp)`.
  - `record DesignObjectMap(string Table, string KeyStrategy, string KeyKind, IReadOnlyDictionary<string,string> Operations)`.
  - `record EnvironmentSettings(string? BaseUrl, IReadOnlyDictionary<string,string> AuthOverrides)`: the per-environment values.
  - `static class DesignSerializer { string Serialize(ConnectionDesign d); ConnectionDesign Parse(string json, string source); }`. Output is camelCase, indented, with keys in declaration order and dictionaries sorted ordinally, so a re-save produces no diff in DataverseToCode. Parse throws `DesignFormatException(string message)` naming `source` and the JSON path on unknown keys, a missing required key, a wrong `formatVersion`, or anything that looks like a secret value: a property named `secret`, `password`, `clientSecret`, `apiKeyValue` or `value` under `auth`.

- [ ] **Step 1: Write failing tests** in `DesignSerializerTests`:
  - `RoundTripIsByteStable`: Serialize(Parse(Serialize(d))) == Serialize(d).
  - `DictionariesAreSortedOrdinally`.
  - `UnknownKeyFails`: message contains `$.auth.colour`.
  - `FormatVersion2Fails`.
  - `AuthWithSecretValueFails`: `{"auth":{"clientSecret":"x"}}` fails, and the message does not contain `"x"`.
  - `MissingInstanceFails`.
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Connections.Tests`. Expected: build failure (types missing).
- [ ] **Step 3:** Implement with `System.Text.Json` and `JsonSerializerOptions { UnmappedMemberHandling = Disallow }`. Check required members explicitly so the message names the path.
- [ ] **Step 4:** Run the tests. Expected: all pass.
- [ ] **Step 5:** Write `docs/connection-design-format.md`: every key, its type, which are per-environment (none in the design; see `EnvironmentSettings`), the carrier from Task 1, the environment variable names `bf_<Instance>_BaseUrl` and `bf_<Instance>_Auth` (JSON of `AuthOverrides`), and "format version 1; any breaking change bumps it".
- [ ] **Step 6: Commit** `feat: connection design format`.

### Task 3: Config input separated from host settings (refactor)

**Files:**
- Create: `src/BrokerForge.Host/Config/ConfigInput.cs`, `src/BrokerForge.Host/Config/FileConfigInput.cs`
- Modify: `src/BrokerForge.Host/Config/HostConfigStore.cs` (the constructor takes `ConfigInput`; the option classes move into `ConfigInput.cs`)
- Modify: `src/BrokerForge.Host/Program.cs`, `tests/BrokerForge.Contract.Tests/*` where `new HostConfigStore(configuration)` is used

**Interfaces:**
- Produces:
  - `sealed class ConfigInput { Dictionary<string, InstanceOptions> Instances; Dictionary<string, OAuthResourceOptions> OAuthResources; Dictionary<string, ObjectMapOptions> ObjectMaps; Dictionary<(string Instance, string File), string> DescriptorTexts; Dictionary<string, string> SchemaTexts; string? ConnectBaseUrl; string? ConnectClientId; string? EnvironmentId }`. The option classes become public. `EnvironmentId` is null for file input.
  - `static ConfigInput FileConfigInput.Read(IConfiguration configuration)`: today's reading of `Instances`, `OAuthResources`, `ObjectMaps`, `Config:DescriptorsPath`, `Config:SchemasPath` and `Connect:*`. A missing or invalid path is a problem string carried in `ConfigInput.Problems` (a `List<string>`), so messages stay identical.
  - `HostConfigStore(ConfigInput input)`; plus `static HostConfigStore FromConfiguration(IConfiguration c) => new(FileConfigInput.Read(c))`.
  - Source labels in problem messages stay exactly as today (`OrdersAPI/listOrders.yaml`, `schemas/X.yaml`).

- [ ] **Step 1:** Run `dotnet test BrokerForge.slnx > $WS/before.txt` and record the pass count. This is the baseline.
- [ ] **Step 2:** Make the move. No behaviour changes; every problem message is unchanged.
- [ ] **Step 3:** Run `dotnet test BrokerForge.slnx`. Expected: the same pass count as the baseline, zero failures.
- [ ] **Step 4: Commit** `refactor: host config built from a ConfigInput`.

### Task 4: Per-environment config provider, with designs as a second input

**Files:**
- Create: `src/BrokerForge.Host/Config/IHostConfigProvider.cs`, `src/BrokerForge.Host/Config/DesignConfigInput.cs`, `src/BrokerForge.Host/Config/CompositeConfigProvider.cs`
- Modify: `src/BrokerForge.Abstractions/ServiceInstance.cs`: add `string? EnvironmentId = null` as the last parameter of `ServiceInstance`. It is not in Contracts (D-13).
- Modify: `RequestRouter`, `ExecuteEndpoint`, `ConnectEndpoints`, `CredentialStores`, `Program.cs` and the per-user auth providers' delegates, so they take the store for the request's environment.
- Modify: `src/BrokerForge.Auth/Credentials/IUserCredentialStore.cs` (`CredentialKeys.Check`)
- Test: `tests/BrokerForge.Contract.Tests/EnvironmentConfigTests.cs`

**Interfaces:**
- Consumes: `ConnectionDesign`, `EnvironmentSettings` (Task 2); `ConfigInput`, `HostConfigStore(ConfigInput)` (Task 3).
- Produces:
  - `interface IHostConfigProvider { Task<HostConfigStore> ForEnvironmentAsync(string environmentId, CancellationToken ct); HostConfigStore Files { get; } void Invalidate(string environmentId); }`.
  - `interface IDesignSource { Task<IReadOnlyList<(ConnectionDesign Design, EnvironmentSettings Settings)>> LoadAsync(string environmentId, CancellationToken ct); }`. The fake lives in tests; the Dataverse implementation comes in Task 6.
  - `static ConfigInput DesignConfigInput.Merge(ConfigInput files, string environmentId, IEnumerable<(ConnectionDesign, EnvironmentSettings)> designs)`. It adds each design as:
    - an instance, with `BaseUrl` from settings;
    - its auth, with each `SecretNames` entry `n` becoming `store:<n>` in `Secrets` and `Auth.SecretRef` set to `store:<first>` for Static;
    - an OAuth resource named `<Instance>`, when `Auth.OAuth` is set;
    - its object map;
    - its descriptors and schema texts, labelled `<Instance>/<op>.yaml (design v<Version>)` and `schemas/<Instance>.yaml (design v<Version>)`.

    A design whose instance name already exists in `files` adds the problem `Instance <X> is configured both in host settings and as a connection design in environment <env>`.
  - `CompositeConfigProvider(HostConfigStore files, IDesignSource? designs, TimeProvider time, TimeSpan ttl)`. It caches one store per environment, and builds on first use and after `ttl` or `Invalidate`. With `designs == null` it returns `Files` for every environment.
  - Missing base URL: `DesignConfigInput` sets `BaseUrl` to empty and marks the instance `Incomplete: "base URL (environment variable bf_<X>_BaseUrl)"`. It is not a startup problem; the router throws `ConfigurationException("Service instance <X> needs its base URL in this environment: set environment variable bf_<X>_BaseUrl.")` at call time. Add `IReadOnlyList<string> Incomplete` to `ServiceInstance`, defaulting to empty.
  - Credential partition: `CredentialKeys.Check` accepts `^([0-9a-f]{32}_)?[A-Za-z0-9]+$`. For a design instance, every store call passes `<envId:N>_<Instance>`; file instances pass `<Instance>` as today, so existing rows still match.
  - Connect URL: `/connect/<Instance>` for file instances, unchanged; `/connect/<environmentId>/<Instance>` for design instances. Map both routes in `ConnectEndpoints`, resolving the store through the provider.

- [ ] **Step 1: Write failing tests** in `EnvironmentConfigTests`, using an in-memory `IDesignSource` and the Inventory mock's descriptors as a design built from `descriptors/InventoryAPI` and `schemas/InventoryAPI.yaml`:
  - `DesignInstanceServesItsEnvironment`: a list call for env A returns mock rows.
  - `DesignInstanceUnknownInOtherEnvironment`: same table, env B → the error payload the host gives today for an unmapped table.
  - `SameInstanceInTwoEnvironmentsStaysSeparate`: env A and env B both have WarehouseAPI with different base URLs (two mock ports, or one mock with a path prefix); each call reaches its own URL.
  - `NameClashBetweenFilesAndDesignFails`: `ConfigurationError` names both sources.
  - `MissingBaseUrlFailsNamingTheVariable`: the message contains `bf_WarehouseAPI_BaseUrl`, and the mock receives no request.
  - `FileInstancesUnchanged`: the existing Orders end-to-end test still passes through the provider.
  - `DesignConnectUrlCarriesEnvironment`: the `ConnectionRequiredException` connect URL ends `/connect/<envA>/TicketsAPI`.
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Contract.Tests --filter EnvironmentConfigTests`. Expected: build failure, then the asserts fail.
- [ ] **Step 3:** Implement the interfaces above. `Program.cs` registers `IHostConfigProvider` and keeps `HostConfigStore` registered as `provider.Files` for anything that is still static.
- [ ] **Step 4:** Run the whole suite. Expected: everything green, including the new tests.
- [ ] **Step 5: Commit** `feat: per-environment config with connection designs`.

### Task 5: Instance secret store and `store:` references

**Files:**
- Create: `src/BrokerForge.Auth/Credentials/IInstanceSecretStore.cs`, `TableInstanceSecretStore.cs`, `InMemoryInstanceSecretStore.cs`
- Create: `src/BrokerForge.Host/Config/StoreSecretResolver.cs`
- Modify: `src/BrokerForge.Abstractions/Secrets.cs` (add the `store` scheme), `Program.cs` (`SecretResolverRouter` gets the store resolver)
- Test: `tests/BrokerForge.Auth.Tests/InstanceSecretStoreTests.cs`, `tests/BrokerForge.Contract.Tests/StoreSecretTests.cs`

**Interfaces:**
- Produces:
  - `interface IInstanceSecretStore { Task<string?> GetAsync(string environmentId, string instance, string name, CancellationToken ct); Task SetAsync(string environmentId, string instance, string name, string value, CancellationToken ct); Task<bool> ExistsAsync(string environmentId, string instance, string name, CancellationToken ct); Task DeleteAsync(string environmentId, string instance, string name, CancellationToken ct); }`.
  - The table store uses the existing table (`Credentials:TableEndpoint`). PartitionKey is `instancesecret_<envId:N>`, RowKey is `<Instance>_<name>`. The value is protected with Data Protection purpose `BrokerForge.InstanceSecret.v1`. The row holds no plain value.
  - `store:<name>` is resolved per instance and environment. `ISecretResolver.GetAsync(SecretRef, ct)` has no instance context, so the router resolves `store:` refs before the channel is built. Add `ISecretResolver` overload `GetAsync(SecretRef reference, ServiceInstance instance, CancellationToken ct)` as a default interface method delegating to the old one. `SecretResolverRouter` handles `store` using `instance.EnvironmentId` and `instance.Name`, and throws for `store` without an instance.
  - Missing value: `ConfigurationException("Service instance <X> needs its key `<name>` in this environment: an administrator sets it on the BrokerForge secret page.")`. It is raised before any source call. The message never contains a value.

- [ ] **Step 1: Write failing tests:**
  - `InstanceSecretRoundTrip` (Azurite through `npx`, as for the credential tests).
  - `InstanceSecretsAreKeyedByEnvironment`: env A's value is not visible from env B.
  - `StoredRowHasNoPlainValue`: read the raw entity and check the value string is absent.
  - `StaticDesignUsesStoredKey`: the Inventory design with `SecretNames ["apiKey"]` and `apiKeyQuery` auth reaches the mock with the stored key.
  - `MissingStoredKeyFailsBeforeSourceCall`: `ConfigurationError` naming `apiKey`, and the mock sees nothing.
  - `SecretValueNeverInMessages`: across every failure path in this task, the payload and the captured logs do not contain the test value.
- [ ] **Step 2:** Run them. Expected: they fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the whole suite. Expected: green.
- [ ] **Step 5: Commit** `feat: instance secrets in the host store`.

### Task 6: Dataverse design source, read as the host's own identity

**Files:**
- Create: `src/BrokerForge.Host/Config/DataverseDesignSource.cs`, `src/BrokerForge.Host/Config/IDataverseReader.cs`, `src/BrokerForge.Host/Config/DataverseReader.cs`
- Modify: `src/BrokerForge.Host/Ingress/TrustOptions.cs` (`AllowedCaller.OrgUrl`, optional; it is required for any environment when `Config:Designs:Enabled` is true), `Program.cs`
- Test: `tests/BrokerForge.Contract.Tests/DataverseDesignSourceTests.cs`

**Interfaces:**
- Consumes: the carrier ruling and reads from Task 1; `IDesignSource` and `CompositeConfigProvider` (Task 4).
- Produces:
  - `interface IDataverseReader { Task<JsonDocument> GetAsync(string orgUrl, string relativeUrl, CancellationToken ct); }`. The real one gets a token for `<orgUrl>/.default` from the host's `TokenCredential` and uses a named `HttpClient` `dataverse` with a 30-second timeout and `RemoveAllLoggers`.
  - `DataverseDesignSource(IOptions<TrustOptions>, IDataverseReader)`. It finds the org URL for the environment, reads every carrier item whose name starts with the BrokerForge design prefix fixed in Task 1, and parses each with `DesignSerializer`. It reads the environment variables `bf_<Instance>_BaseUrl` and `bf_<Instance>_Auth` (the current value, else the default) into `EnvironmentSettings`.
  - Settings: `Config:Designs:Enabled` (bool, default false) and `Config:Designs:CacheSeconds` (default 300, at most 300; above that is a startup problem).
  - Failure rules, matching Review Focus 3 and 4:
    - a design that fails to parse or validate makes only that environment's store fail. Every request for that environment gets `ConfigurationException` naming the design and the problem; other environments and the file instances keep working;
    - cold cache with Dataverse unreachable or 401/403 gives `ConfigurationException("Could not read connection designs for environment <id> from Dataverse: <status or reason>.")`;
    - warm cache past its TTL with Dataverse unreachable serves the last good store for up to one more TTL, logs a warning each time, and then fails as cold.

- [ ] **Step 1: Write failing tests** with a fake `IDataverseReader`:
  - `ReadsDesignAndEnvironmentVariables`: the current value beats the default.
  - `InvalidDesignFailsOnlyItsEnvironment`.
  - `DataverseDownColdFailsTyped`.
  - `DataverseDownWarmServesThenFails`: uses a fake `TimeProvider`.
  - `CacheSecondsAbove300Rejected`.
  - `MissingOrgUrlRejectedWhenEnabled`.
- [ ] **Step 2:** Run them. Expected: they fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the whole suite. Expected: green.
- [ ] **Step 5: Commit** `feat: read connection designs from Dataverse`.

### Task 7: Live in dev, with Inventory as a design

**Files:**
- Modify: `tools/dev/setup_dataverse.py` (create the host's application user from the host managed identity's client ID, with a `BrokerForge host` security role granting Read on `environmentvariabledefinition`, `environmentvariablevalue` and the carrier's table), `infra/containerApps.bicep` (`Trust__AllowedCallers__0__OrgUrl`, `Config__Designs__Enabled=true`; keep every existing setting)
- Create: `tools/dev/publish_design.py` + `publish-design.sh <Instance> <Solution>`. It builds the design from `descriptors/<Instance>`, `schemas/<Instance>.yaml` and the instance's current host settings, and writes the carrier and the two environment variables into the given solution. This is a stopgap that plan 3's generator replaces.
- Create: `tools/dev/set-instance-secret.sh <Instance> <name>`. It reads the value from Key Vault and writes it to the host's table store through a one-off host admin command (`dotnet run --project src/BrokerForge.Host -- set-instance-secret ...` with the dev table endpoint). It never prints the value. Plan 4's page replaces it.
- Create: `docs/findings/portal-1.md`

- [ ] **Step 1:** Create a copy source rather than moving Inventory, so Inventory stays file-based and its acceptance keeps passing:
  - draft `WarehouseAPI` (table `ext_stockitem`) with `bf draft` from `tools/mock-api/inventory-openapi.yaml` into a scratch folder, as `tools/media/capture.mjs` does;
  - apply the same review edits as capture.mjs, then run `bf validate`;
  - generate its table from that folder with `bf generate`;
  - publish the design from the same folder. Its auth settings are Inventory's (`apiKeyQuery`, `api_key`), with `SecretNames ["apiKey"]`.
- [ ] **Step 2:** Deploy (`tools/dev/deploy.sh`). Set the key with `set-instance-secret.sh WarehouseAPI apiKey`.
- [ ] **Step 3:** Verify through `dv`:
  - listing `ext_stockitems` returns the mock rows;
  - before setting the key, the same read fails with the `needs its key` message;
  - after removing `bf_WarehouseAPI_BaseUrl`'s current value and invalidating (wait out the TTL), the read fails naming the variable.

  Then restore everything.
- [ ] **Step 4:** Run `tools/dev/acceptance.sh` and `tools/dev/acceptance_inventory.sh`. Expected: both pass unchanged.
- [ ] **Step 5:** Log scan (phase 3 findings 5 method): no line contains the test key, `eyJ` or `Bearer `.
- [ ] **Step 6:** Write `docs/findings/portal-1.md` with the results, timings (cold design load, warm call) and anything that differed from this plan.
- [ ] **Step 7: Commit** `feat(dev): WarehouseAPI served from a connection design; findings`.
