# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## BrokerForge handover

Read this first, then `docs/hld.md` and `docs/descriptor-spec.md`.

**Current state:** phases 1 to 3 built and verified live (`docs/findings/phase1.md`, `phase2.md`, `phase3.md`). Phase 3 keeps per-user credentials in an encrypted Azure Table (`src/BrokerForge.Auth/Credentials`, D-25) and serves the connect page from the host (`src/BrokerForge.Host/Connect`, D-26); the Tickets mock (`/tk`) is the per-user source, as `TicketsAPI` (OAuth) and `TicketsEntraAPI` (Impersonate). Contract tests that use the credential store start Azurite through `npx`. `solution/BrokerForgeCore` is the core solution as unpacked source (`tools/dev/export-core.sh`). Check `git ls-files` before assuming a project is there. Config still comes from host settings plus `descriptors/` and `schemas/` files (HLD D-10, D-16); the Dataverse config tables are phase 2b. `src/BrokerForge.Abstractions` holds the host-side shared interfaces (D-13). Phase 2 added `src/BrokerForge.Schema` (table schema files, spec 14), `src/BrokerForge.Generator`, `tools/SwaggerForge` and the `bf` CLI in `tools/BrokerForge.Cli` (D-22).

## What this is

BrokerForge surfaces external data sources in Microsoft Dataverse the way K2 service brokers surface them through SmartObjects.

| K2 | BrokerForge |
| --- | --- |
| Service Broker | Adapter implementing `IBroker` (REST first; SQL, OData, SOAP, GraphQL later) |
| Service Instance | `bf_serviceinstance` row: endpoint, authentication mode, Key Vault references |
| Service Object | Discovered schema + operation descriptors (YAML) |
| SmartObject | Virtual table (CRUD) + Custom APIs (all other methods) |
| Host server | Broker host: ASP.NET Core service on Azure Container Apps |

It generalises **RESTVTP**, an existing config-driven REST virtual table provider. RESTVTP code is not available to this repo, so the REST adapter, SwaggerForge and the Node mock API are built fresh from `docs/hld.md` and `docs/descriptor-spec.md`.

## Request pipeline and boundaries

Every message takes the same path (HLD 4, 10):

1. Plug-in turns the Dataverse message into the canonical request (HLD 7.1) and calls the host with a managed identity token.
2. Host ingress validates the token, audience, tenant and environment ID.
3. Router resolves `bf_serviceinstance` and the approved `bf_operation` descriptor (cached from Dataverse).
4. Descriptor engine checks filters/sort/paging against the descriptor (fail here if undeclared), then binds values and renders the request.
5. `IAuthProvider` for the instance's mode returns an `IAuthenticatedChannel`.
6. `IBroker` adapter executes the rendered request over that channel and returns the raw result.
7. Descriptor engine maps rows, applies error maps, encodes keys (Keys project), and the host replies with `rows`/`next`, `record` or `outputs`.

Boundaries that span projects: adapters never read secrets or descriptors; hooks never read secrets; only the host resolves `secret` bindings; the plug-in holds no descriptor or source logic.

## Non-negotiable principles

1. **Fail loudly.** Never return partial, approximate or client-side-filtered results. Unsupported filter, operator, sort, missing credential, unmapped status code or response shape mismatch is a typed exception naming the cause. No silent fallbacks anywhere.
2. **No fallback between authentication modes.** A per-user mode with no credential throws `ConnectionRequiredException`. It never retries as the service account (equivalent to K2's Enforce Impersonation always on).
3. **Configure before code.** A new REST source must be onboardable with config and descriptors only. Code hooks exist, but adding one is a last resort and needs a comment saying why the descriptor could not express it.
4. **No secrets** in Dataverse rows, descriptors, logs, traces, test fixtures or exception messages. Secrets are Key Vault references or live in the host's encrypted store.
5. **Thin plug-in.** The Dataverse plug-in translates messages into the canonical request and back. No source logic, no descriptor logic, no credentials in the plug-in.
6. **Honest constraints.** If something cannot be done on the platform, stop and report it rather than approximating. Record it in `docs/hld.md` section 15 or 16.

## Verify before building on it

Phase 0 verified these on 31 October 2026. Read `docs/findings/phase0.md` before touching registration, the provider plug-in or host ingress.

- [x] How the `entitydataprovider` table is queried for the virtual table data provider registration.
- [x] How `IEntityDataSourceRetrieverService` is resolved inside the provider plug-in to read the data source record.
- [x] Plug-in to host call using Power Platform managed identity (`IManagedIdentityService.AcquireToken`) and host-side validation of that token (audience, tenant; environment only through the plug-in identity, see the findings).

Still unverified (listed under "Still open" in the findings): CA-issued certificate subjects, `AcquireToken(Guid, …)`. The `netstandard2.0` loading question no longer applies: the plug-in compiles Contracts source in (HLD D-08).

## Stack

| Part | Choice | Notes |
| --- | --- | --- |
| Contracts | `netstandard2.0` class library | Shared DTOs for canonical request/response. Must compile for the plug-in. |
| Plug-in | .NET Framework 4.6.2, `Microsoft.CrmSdk.CoreAssemblies` | Dataverse plug-in requirement. Signed. Plug-in package if dependencies are needed. |
| Broker host | .NET 8, ASP.NET Core minimal APIs | Assumption: C# throughout. HLD 16 lists host language as open; confirm before phase 1. |
| Descriptors | YAML (YamlDotNet), JSONPath, JSONata for transforms | Schema in `docs/descriptor-spec.md` |
| Mock API | Node (from RESTVTP) | Orders API used by all contract tests |
| Infra | Bicep | Container App, Key Vault, App Insights; APIM and Relay as optional modules |
| Dataverse ALM | `pac` CLI, unpacked solution source | Core solution + one solution per source |

## Repository layout

```
brokerforge/
  CLAUDE.md
  docs/
    hld.md                      # high-level design (source of truth for architecture)
    descriptor-spec.md          # descriptor schema and rules (source of truth for the engine)
    findings/                   # phase 0 results, spikes
  src/
    BrokerForge.Contracts/      # netstandard2.0: canonical request/response, exceptions, enums
    BrokerForge.Plugin/         # net462: generic data provider + Custom API handler
    BrokerForge.Host/           # net8.0: ingress, trust, router, endpoints
    BrokerForge.Descriptors/    # net8.0: model, parser, validator, renderer, response mapper
    BrokerForge.Auth/           # net8.0: IAuthProvider implementations, token/credential store
    BrokerForge.Adapters.Rest/  # net8.0: IBroker for REST
    BrokerForge.Keys/           # net8.0: key strategies (native GUID, reversible, key map)
  tools/
    SwaggerForge/               # OpenAPI -> draft descriptors + table schema (library; `bf draft`)
    BrokerForge.Cli/            # `bf validate | draft | generate`
    mock-api/                   # Node mock: Orders (/v2) and Inventory (/inv/v1)
  solution/                     # Dataverse solution source (pac)
  descriptors/
    OrdersAPI/                  # one YAML file per operation
  schemas/                      # one table schema file per service instance (descriptor spec 14)
  infra/                        # Bicep
  tests/
    BrokerForge.Descriptors.Tests/
    BrokerForge.Keys.Tests/
    BrokerForge.Contract.Tests/ # host + mock API, every descriptor feature
```

## Naming

- Dataverse publisher prefix for framework tables: `bf_`. Generated source tables use the client's prefix (examples use `ext_`).
- Exceptions live in Contracts and end in `Exception`: `UnsupportedQueryException`, `ConnectionRequiredException`, `SourceException`, `SourceTimeoutException`, `MappingException`, `BrokerUnavailableException`.
- Every request and log line carries `correlationId`.

## Phases and acceptance criteria

Work one phase at a time. A phase is done only when every criterion passes. Stop after each phase and report, so the conversation can be compacted before the next.

### Phase 0 — Spike

- Provider plug-in registered against a test virtual table; RetrieveMultiple returns a hard-coded row.
- The two SDK surfaces above verified and written up.
- Plug-in acquires a managed identity token and calls a stub host endpoint that validates it.
- **Done when:** `docs/findings/phase0.md` records all three with working code references.

### Phase 1 — Core

- Contracts: canonical request/response as in HLD 7.1.
- Host: ingress and trust, router, descriptor engine (request, response, errors, paging), REST adapter.
- Auth: Service Account and Static modes.
- Keys: native GUID and reversible encoding, with round-trip tests.
- Plug-in: all five messages mapped to the canonical request.
- **Done when:** the Orders mock API supports list (with declared filters and paging), retrieve, create, update and delete through a virtual table; an undeclared filter throws `UnsupportedQueryException`; an unmapped 500 throws `SourceException` with correlation ID.

### Phase 2 — Tooling

- SwaggerForge emits descriptors that pass the validator, plus a table schema.
- Validator runs in CI over `descriptors/`.
- Generator creates tables, columns and Custom APIs through the Web API.
- **Done when:** a second mock API is onboarded with no new code.

### Phase 3 — Per-user security

- OAuth, SSO and Impersonate modes; token and credential store; connect page; async context rule.
- **Done when:** a per-user source returns different rows for two users; a user with no credential gets `ConnectionRequiredException` with a connect URL; no path falls back to the service account (test it).

### Phase 4 — More adapters

- SQL via Azure Relay, then OData; SOAP and GraphQL on demand.
- **Done when:** each adapter passes the shared contract tests.

### Phase 5 — Hardening

- Response caching, throttling, key map, admin app, runbooks.
- **Done when:** NFR targets in HLD 3 are measured against a real source.

## Testing rules

- Every descriptor feature in `docs/descriptor-spec.md` has a contract test against the mock API.
- Every error row in HLD 11 has a test that triggers it.
- Key strategies: property-based round-trip tests.
- No test uses a real secret. Static-mode tests read a dummy value from a test Key Vault emulator or environment variable.

## Commands

```
dotnet build BrokerForge.slnx
dotnet test BrokerForge.slnx                    # contract tests start tools/mock-api with node
dotnet test tests/BrokerForge.Descriptors.Tests --filter "FullyQualifiedName~<TestName>"   # single test
cd tools/mock-api && npm test                   # MOCK_API_KEY=<any> npm start to run it
tools/spike/sign-plugin.sh                      # Release build, strong name + Authenticode (osslsigncode) -> out/
tools/dev/deploy.sh                             # Azure dev env (infra/main.bicep), images, Key Vault secrets
tools/dev/setup-dataverse.sh                    # sign + register core: assembly, both plug-in types, provider, bf_brokerdatasource, bf_HostUrl/bf_HostScope
tools/dev/generate.sh OrdersAPI BrokerForgeOrders [--dry-run] [--confirm <fp>]   # bf generate: table, columns, form and view, role, Custom APIs; exit 4: confirm the printed changes
dotnet run --project tools/BrokerForge.Cli -- validate descriptors --schemas schemas   # what CI runs
dotnet run --project tools/BrokerForge.Cli -- draft <openapi> --instance <Name> --prefix ext --out out [--answers a.json]   # exit 3: fill out/<Name>-questions.json, run again
tools/dev/generate-in-host.sh WarehouseAPI BrokerForgeWarehouse [--preview | --confirm <fp> | --with-role]   # Generate as the host from the published design; exit 3: the role needs --with-role (runs locally as you). exec is rate-limited: ~8 calls, then 10 minutes
tools/dev/acceptance.sh                         # Orders: phase 1 done criteria + option set writes, lines read back
tools/dev/acceptance_inventory.sh               # Inventory: the phase 2 onboarding acceptance
tools/dev/setup-connect-app.sh                  # phase 3 Entra apps (connect app + Tickets API), before deploy.sh; TICKETS_BOB_OID=<second user>; the connect app also has Dynamics user_impersonation for the role page
/usr/bin/python3 tools/dev/setup_dataverse.py --admin-app   # only the admin app (bf_connection, review page, admin Custom APIs, administrator role, BrokerForge app) and the host user
tools/dev/export-core.sh                        # BrokerForgeCore from dev -> solution/BrokerForgeCore (unpacked source) + out/BrokerForgeCore_managed.zip (needs pac)
tools/dev/install-core.sh <org url> <env id>    # managed core into another environment, then its own identity, host variables and host user
node --test 'tests/webresources/*.test.js'      # the admin pages' logic (src/BrokerForge.WebResources)
                                                # per-user live acceptance is manual: docs/findings/phase3.md (connect at {host}/connect/<instance>)
source tools/spike/dv.sh && dv GET ext_orders   # Web API call as the az CLI user
```

The solution file is `BrokerForge.slnx` (.NET 10 SDK format). macOS notes: `net462` builds through `Microsoft.NETFramework.ReferenceAssemblies`; signing material lives in `~/.brokerforge/spike-signing/` (outside the repo), the app registration, environment and managed identity IDs in `~/.brokerforge/spike-ids.env` and dev IDs in `~/.brokerforge/dev-ids.env`. The dev apps scale to zero between sessions, so the first call after a pause takes about 25 seconds; `deploy.sh` sets the host and mock back to one replica and waits until both run the new image alone. CI builds the plug-in unsigned (`-p:SignAssembly=false`); signed builds come only from `sign-plugin.sh`. Use `/usr/bin/python3` for the dev scripts (the python.org build has no CA certificates). Signing uses the PEM certificate and key (`cert.pem`, `cert.key`); signing from the PFX failed intermittently with a passphrase prompt. The scripts still retry the timestamp server. In zsh, never name a shell variable `path` (it is tied to `$PATH`).

## Working agreement

- Ask before changing a contract in `BrokerForge.Contracts` or the descriptor schema: both are shared surfaces. Update the docs in the same change.
- Record design changes in `docs/hld.md` section 16 (decisions) rather than only in code.
- If a requirement cannot be met as written, say so and propose the closest honest alternative.
