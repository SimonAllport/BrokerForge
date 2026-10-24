# CLAUDE.md — BrokerForge

Handover for Claude Code. Read this first, then `docs/hld.md` and `docs/descriptor-spec.md`.

## What this is

BrokerForge surfaces external data sources in Microsoft Dataverse the way K2 service brokers surface them through SmartObjects.

| K2 | BrokerForge |
| --- | --- |
| Service Broker | Adapter implementing `IBroker` (REST first; SQL, OData, SOAP, GraphQL later) |
| Service Instance | `bf_serviceinstance` row: endpoint, authentication mode, Key Vault references |
| Service Object | Discovered schema + operation descriptors (YAML) |
| SmartObject | Virtual table (CRUD) + Custom APIs (all other methods) |
| Host server | Broker host: ASP.NET Core service on Azure Container Apps |

It generalises **RESTVTP**, an existing config-driven REST virtual table provider. RESTVTP code is the starting point for the REST adapter, SwaggerForge and the Node mock API.

- RESTVTP repo: `<set path before starting>`

## Non-negotiable principles

1. **Fail loudly.** Never return partial, approximate or client-side-filtered results. Unsupported filter, operator, sort, missing credential, unmapped status code or response shape mismatch is a typed exception naming the cause. No silent fallbacks anywhere.
2. **No fallback between authentication modes.** A per-user mode with no credential throws `ConnectionRequiredException`. It never retries as the service account (equivalent to K2's Enforce Impersonation always on).
3. **Configure before code.** A new REST source must be onboardable with config and descriptors only. Code hooks exist, but adding one is a last resort and needs a comment saying why the descriptor could not express it.
4. **No secrets** in Dataverse rows, descriptors, logs, traces, test fixtures or exception messages. Secrets are Key Vault references or live in the host's encrypted store.
5. **Thin plug-in.** The Dataverse plug-in translates messages into the canonical request and back. No source logic, no descriptor logic, no credentials in the plug-in.
6. **Honest constraints.** If something cannot be done on the platform, stop and report it rather than approximating. Record it in `docs/hld.md` section 15 or 16.

## Verify before building on it

These are unverified. Phase 0 exists to confirm them. Do not build adapters on assumptions about them.

- [ ] How the `entitydataprovider` table is queried for the virtual table data provider registration.
- [ ] How `IEntityDataSourceRetrieverService` is resolved inside the provider plug-in to read the data source record.
- [ ] Plug-in to host call using Power Platform managed identity (`IManagedIdentityService.AcquireToken`) and host-side validation of that token (audience, tenant, environment).

When verified, write the finding, the exact API usage and a link to the source in `docs/findings/phase0.md`.

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
    BrokerForge.Abstractions/   # net8.0: IBroker, IAuthProvider, ServiceInstance, ObjectMap (HLD D-08)
    BrokerForge.Plugin/         # net462: generic data provider + Custom API handler
    BrokerForge.Host/           # net8.0: ingress, trust, router, endpoints
    BrokerForge.Descriptors/    # net8.0: model, parser, validator, renderer, response mapper
    BrokerForge.Auth/           # net8.0: IAuthProvider implementations, token/credential store
    BrokerForge.Adapters.Rest/  # net8.0: IBroker for REST
    BrokerForge.Keys/           # net8.0: key strategies (native GUID, reversible, key map)
  tools/
    SwaggerForge/               # OpenAPI -> draft descriptors + table schema
    mock-api/                   # Node Orders mock
  solution/                     # Dataverse solution source (pac)
  descriptors/
    OrdersAPI/                  # one YAML file per operation
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
dotnet build BrokerForge.sln                 # warnings are errors (Directory.Build.props)
dotnet test BrokerForge.sln                  # includes the descriptor validator gate over descriptors/
cd tools/mock-api && npm start               # Orders mock on http://localhost:4010, X-Api-Key: dev-key
cd tools/mock-api && npm run check           # mock smoke test
ASPNETCORE_ENVIRONMENT=Development ORDERS_API_KEY=dev-key dotnet run --project src/BrokerForge.Host
pac solution pack --folder solution/BrokerForgeCore --zipfile out/BrokerForgeCore.zip   # once the solution exists
```

CI (`.github/workflows/ci.yml`) runs restore, build, test and the mock smoke test on every push.

## Current state

Scaffolded; nothing is deployed and phase 0 has not started. Every stub throws `NotImplementedException` naming its phase, and the host turns that into a typed `Configuration` error rather than a 500.

| Area | State |
| --- | --- |
| Contracts | Done: canonical request/response, exceptions with error round-trip, `CanonicalJson` |
| Keys | Native GUID and reversible encoding done with property tests; key map is a phase 5 stub |
| Descriptors | Model, parser, validator (all 12 rules) and query capability check done; renderer and response mapper are phase 1 stubs |
| Auth | Static and Service Account done; Impersonate, SSO, OAuth are phase 3 stubs |
| REST adapter | HTTP transport done; discovery is phase 2 (SwaggerForge) |
| Host | Router, endpoints, appsettings-backed config, file-backed descriptors done; managed identity trust denies every call until phase 0; Dataverse-backed stores are phase 1 |
| Plug-in | Request builder, QueryExpression translator, entity mapping, host client done; data source and managed identity calls are unverified (phase 0); Custom API routing is undecided (HLD 16) |
| Mock API | Done, with test hooks for 500, slow and malformed responses |
| Infra, solution | Skeletons |

## Working agreement

- Ask before changing a contract in `BrokerForge.Contracts` or the descriptor schema: both are shared surfaces. Update the docs in the same change.
- Record design changes in `docs/hld.md` section 16 (decisions) rather than only in code.
- If a requirement cannot be met as written, say so and propose the closest honest alternative.
