# Phase 3 Per-User Security Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A source protected per user (OAuth, Impersonate or SSO) returns each Dataverse user their own rows. A user with no stored credential gets `ConnectionRequiredException` with a connect URL, and no path ever falls back to the service account.

**Architecture:**
- The host gains a credential store: Azure Table Storage rows encrypted with ASP.NET Core Data Protection, whose key ring is wrapped by a Key Vault key.
- It also gains a connect page under `/connect`. Users sign in with Entra ID there, then grant OAuth consent or enter SSO credentials. The page keys what it stores by the user's Entra object ID, the same ID the plug-in already sends as `caller.userObjectId`.
- Three new `IAuthProvider` implementations read the store. A small selector applies the instance's async context rule before choosing the provider.
- A third mock source (Tickets, with its own OAuth server) proves per-user rows in contract tests and live.

**Tech Stack:**
- .NET 8 host with cookie and OpenID Connect authentication, Azure.Data.Tables and ASP.NET Core Data Protection (Azure Blob key storage plus a Key Vault key).
- Node 22 mock, built-ins only.
- Bicep, and `az` for the connect app registration.

**Spec:** `docs/hld.md` 6, 7.3, 9.1–9.5, 10, 11, 15, 16 (open questions on the token store and the connect page); `CLAUDE.md` phase 3 criteria and principles 1, 2 and 4.

## Global Constraints

- Phase 3 is done when:
  - a per-user source returns different rows for two users;
  - a user with no credential gets `ConnectionRequiredException` with a connect URL;
  - no path falls back to the service account, and a test proves it.
- No fallback between authentication modes (principle 2, D-03). A per-user mode with no usable credential throws `ConnectionRequiredException`. The only other identity a call may use is the one an async context rule names explicitly, and only when `caller.async` is true.
- No secrets (tokens, refresh tokens, SSO passwords, client secrets, authorisation codes, PKCE verifiers) in Dataverse rows, descriptors, logs, traces, exception messages, test fixtures or HTML responses. Stored credentials are encrypted at rest.
- Thin plug-in: no plug-in change beyond what Task 1 proves necessary.
- Ask before changing `BrokerForge.Contracts` or the descriptor schema. This plan expects to change neither (P9).
- Every request and log line carries `correlationId`. Connect page log lines carry the instance and the user object ID, never a credential.
- Dev scripts run with `/usr/bin/python3`. Never name a zsh variable `path`.

## Review Focus

1. **Two calls for the same user arrive together after the access token expired, and the source rotates refresh tokens.** Expected: one refresh, both calls succeed, the new refresh token is saved. Test in Task 4: `OAuth_ConcurrentExpiredCalls_RefreshOnce`.
2. **The source refuses the refresh (`invalid_grant`: revoked or expired).** Expected: `ConnectionRequiredException`, the row is marked `expired`, and the next call fails the same way without contacting the token endpoint. Test in Task 4: `OAuth_RefreshRejected_MarksExpiredAndRequiresConnect`.
3. **An OAuth callback arrives with state issued to another user, another instance, already used, or older than 10 minutes.** Expected: refused, nothing stored. Test in Task 7: `Callback_StateForOtherUserOrReplayed_Refused`.
4. **The token endpoint returns an error whose body echoes the code or refresh token.** Expected: the exception message and every log line contain no credential value. Test in Task 4: `OAuth_TokenEndpointFailure_NoCredentialInMessageOrLogs`.
5. **An instance's async rule is `ServiceIdentity`, and a synchronous call arrives from a user with no credential.** Expected: `ConnectionRequiredException`, because the rule does not leak into sync calls. Test in Task 6: `AsyncRule_ServiceIdentity_SyncCallStillPerUser`.

---

### Task 0: Decisions to confirm before Task 1

Simon confirms or changes each. Record the answers in HLD 16 as D-25 onwards, close the two open questions they answer, and update HLD 6, 9.1 and 9.3.

- **P1 Credential store.**
  - **Storage:** an Azure Table Storage table `bfusercredentials`. PartitionKey is the service instance; RowKey is the user's Entra object ID.
  - **Columns:** kind (`oauth`, `impersonate`, `sso`), status (`connected`, `expired`, `revoked`), connected-at, last-used, expires-at, and one `Payload` column. Payload holds the tokens or the SSO credential, encrypted with Data Protection (purpose `BrokerForge.UserCredential.v1`). The key ring lives in a blob and is wrapped by a Key Vault key.
  - **Not Key Vault per user:** one secret per user and source hits vault throttling (about 4,000 operations per 10 seconds per vault) and spreads secrets out of reach of any review.
  - Data Protection also protects the connect page cookie and OAuth state across replicas.
- **P2 Connect page.**
  - **Location:** served by the host at `{Connect:PublicBaseUrl}/connect/{instance}`. That URL is the `connectUrl` in every `ConnectionRequiredException`.
  - **Sign-in:** Entra OpenID Connect through a new app registration, "BrokerForge Connect". It authenticates with the host's managed identity as a federated credential, so there is no client secret. If Task 1 finds that refused, the fallback is a client secret in Key Vault, recorded as a ruling.
  - **Reaching it:** users reach the page through the URL in the error text. A connect prompt inside model-driven or canvas apps stays the HLD 15 mitigation for a later phase. This closes the open question "How users reach the connect page" for phase 3.
- **P3 Which user.**
  - **Identity:** credentials are keyed by `InitiatingUserAzureActiveDirectoryObjectId`, which the plug-in already sends, not by the impersonated `UserId`. A Dataverse app user impersonating someone through `CallerObjectId` cannot borrow that person's source credential.
  - **No object ID:** a call with no object ID to a per-user mode throws `ConnectionRequiredException`. The message says the caller has no Entra identity.
- **P4 Mode semantics.**
  - **OAuth:** authorisation code with PKCE (S256) against an OAuth resource declared in host settings, `OAuthResources:<name>` (HLD `bf_oauthresource`): AuthorizeUrl, TokenUrl, Scopes, ClientId, ClientSecretRef (a `kv:` or `env:` reference). The access token is sent as a bearer token. It is refreshed when it has less than 60 seconds left, and a rotated refresh token is saved.
  - **Impersonate:** the same flow against Entra, using the connect app and the source API's delegated scope. The host stores Entra's refresh token and redeems it for access tokens. There is no on-behalf-of flow, because the plug-in never holds the user's token (HLD 9.1).
  - **SSO:** the user enters a username and secret on the connect page. They are sent as HTTP Basic or an API key header, per the instance's `Auth:SsoKind` (`basic` or `apiKeyHeader` with `HeaderName`).
- **P5 Async context rule.**
  - **Values:** `Auth:AsyncContext` is `TriggeringUser` (the default) or `ServiceIdentity`.
  - **`ServiceIdentity`:** needs an explicit `Auth:AsyncAuth` block whose mode is ServiceAccount or Static. Only calls with `caller.async = true` use it. `TriggeringUser` with an `AsyncAuth` block fails at startup.
  - **The signal:** `caller.async` is `context.Mode == 1`, as now. Task 1 checks live which callers set it. If Power Automate calls run synchronously as the connection owner, that is recorded as the behaviour: flows use the connection owner's own credential.
- **P6 Tables stay in host settings.** `bf_userconnection` status lives in the store row and `bf_oauthresource` in host settings until phase 2b (D-16). No Dataverse row ever holds a credential.
- **P7 Third mock source: Tickets.**
  - **Paths:** `/tk/v1` in the existing mock. Tickets are owned by mock users `alice` and `bob`. The mock also runs an OAuth server at `/tk/oauth/authorize` (a sign-in form) and `/tk/oauth/token`, which handles `authorization_code` with PKCE and rotating `refresh_token`.
  - **Credentials accepted:** an OAuth bearer, Basic with per-user passwords (SSO), or an Entra bearer validated against Entra's signing keys (Impersonate). Built-ins only. Contract tests sign test JWTs with a key the test supplies.
  - **Ingress:** the mock's ingress becomes external (dev only), because the user's browser must reach `/tk/oauth/authorize`. Every data path still requires a credential.
- **P8 Live acceptance.**
  - **Coverage:** OAuth and Impersonate, with two real Entra users. SSO is covered by contract tests only, since nothing in it depends on platform behaviour.
  - **What Simon provides:** a second dev-tenant user with Dataverse access and a role that can read `ext_ticket`. Simon does the browser sign-ins on the connect page; I can't sign in as anyone.
- **P9 No shared-surface change.** `CallerContext` already carries `userObjectId` and `async`. `ErrorPayload` already carries `connectUrl`, and the plug-in already appends "Connect at <url>." Descriptors need no new binding source, because credentials attach in the channel, as for Static. If a task finds otherwise, it stops and asks.

- [ ] **Step 1:** Get Simon's answers and write D-25 to D-33 into HLD 16. Update HLD 6 (`bf_userconnection` and `bf_oauthresource` stay in settings until 2b), 9.1, 9.3 and the open questions. Commit: `docs: phase 3 decisions`.

---

### Task 1: Live spike: async signal, error rendering, connect app

**Files:** Create `docs/findings/phase3-spike.md` and `tools/spike/phase3/` (a throwaway async step and an async classic workflow definition).

- [ ] **Step 1:** The provider step is always synchronous, so the probe is a temporary Custom API `bf_SpikeWhoAmI` whose plug-in traces `Mode`, `Depth`, `InitiatingUserAzureActiveDirectoryObjectId`, `UserAzureActiveDirectoryObjectId`, `ParentContext?.MessageName`. Call it four ways: Web API as Simon, Web API with `CallerObjectId` impersonating user B, a Power Automate cloud flow ("Perform an unbound action"), and an async classic workflow or async step. Record the trace for each.
- [ ] **Step 2:** Raise `ConnectionRequiredException` text through `ext_order` in a model-driven app view and in a canvas app gallery. Record whether the URL is shown and whether it is clickable.
- [ ] **Step 3:** Create the "BrokerForge Connect" app registration with a federated credential for the host's managed identity (issuer `https://login.microsoftonline.com/{tenant}/v2.0`, subject = MI principal ID, audience `api://AzureADTokenExchange`). From the host container, get a client assertion with `ManagedIdentityCredential` and redeem a code. Expected: tokens issued. If refused, record why and switch to a client secret (ruling).
- [ ] **Step 4:** Write the findings, remove the spike API and steps, commit: `docs: phase 3 spike findings`. Later tasks read P3 and P5 against these results; any conflict is a ruling.

---

### Task 2: Credential store

**Files:** Create `src/BrokerForge.Auth/Credentials/` (`UserCredential.cs`, `IUserCredentialStore.cs`, `InMemoryUserCredentialStore.cs`, `TableUserCredentialStore.cs`). Test: `tests/BrokerForge.Auth.Tests/CredentialStoreTests.cs`. Packages: `Azure.Data.Tables`, `Microsoft.AspNetCore.DataProtection` (Auth stays a class library).

**Interfaces:**
- Produces: `enum CredentialKind { OAuth, Impersonate, Sso }`, `enum CredentialStatus { Connected, Expired, Revoked }`.
- Produces: `sealed record UserCredential(CredentialKind Kind, CredentialStatus Status, string? AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string? Username, string? Secret, DateTimeOffset ConnectedAt)`. Its `ToString()` prints kind, status and expiry only.
- Produces: `interface IUserCredentialStore { Task<UserCredential?> GetAsync(string instance, string userObjectId, CancellationToken ct); Task SaveAsync(string instance, string userObjectId, UserCredential credential, CancellationToken ct); Task SetStatusAsync(string instance, string userObjectId, CredentialStatus status, CancellationToken ct); Task DeleteAsync(string instance, string userObjectId, CancellationToken ct); }`.
- Produces: `TableUserCredentialStore(TableClient table, IDataProtectionProvider protection)`. The payload is JSON of the token fields, protected with purpose `BrokerForge.UserCredential.v1`.

- [ ] **Step 1:** Write these tests. Run them against both implementations: `TableUserCredentialStore` against Azurite through a fixture, skipped with a reason when Azurite is missing, and run in CI.
  - `Store_RoundTrip_ReturnsSavedCredential`
  - `Store_IsolatesUsersAndInstances` (alice@Tickets, bob@Tickets and alice@Other are three separate rows)
  - `Store_SetStatus_KeepsPayload`
  - `Table_PayloadIsCiphertext` (the raw entity's `Payload` contains neither the access token nor the refresh token)
  - `UserCredential_ToString_HasNoSecrets`
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Auth.Tests`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement. Keys are validated: the object ID must parse as a GUID and the instance must match `^[A-Za-z0-9]+$`, otherwise `ArgumentException`. Table keys forbid `/ \ # ?`.
- [ ] **Step 4:** Run. Expected: PASS. Add an Azurite service to `.github/workflows/ci.yml`.
- [ ] **Step 5:** Commit: `feat(auth): encrypted per-user credential store`.

---

### Task 3: Host settings for per-user modes

**Files:** Modify `src/BrokerForge.Abstractions/ServiceInstance.cs`, `src/BrokerForge.Host/Config/HostConfigStore.cs`, `src/BrokerForge.Auth/AuthReservedNames.cs`. Create `src/BrokerForge.Host/Config/OAuthResourceOptions.cs`. Test: `tests/BrokerForge.Contract.Tests/PerUserConfigTests.cs`.

**Interfaces:**
- Produces: `AuthConfig` gains `string? OAuthResource`, `string? SsoKind`, `AsyncContext AsyncContext = AsyncContext.TriggeringUser` and `AuthConfig? AsyncAuth`. Also `enum AsyncContext { TriggeringUser, ServiceIdentity }`.
- Produces: `sealed record OAuthResource(string Name, string AuthorizeUrl, string TokenUrl, IReadOnlyList<string> Scopes, string ClientId, string? ClientSecretRef, bool UsesConnectApp)`. For Impersonate the resource is Entra and `UsesConnectApp` is true.
- Produces: `HostConfigStore.OAuthResource(string name) -> OAuthResource` and `HostConfigStore.ConnectUrl(string instance) -> string` (`{Connect:PublicBaseUrl}/connect/{instance}`).
- Startup problems:
  - an unknown or missing `OAuthResource` for OAuth or Impersonate;
  - `SsoKind` not `basic` or `apiKeyHeader`, or `apiKeyHeader` without `HeaderName`;
  - `ServiceIdentity` without `AsyncAuth`, or `AsyncAuth` whose mode is per-user;
  - `TriggeringUser` with an `AsyncAuth` block;
  - any per-user instance while `Connect:PublicBaseUrl` is unset.
  - The "not supported until phase 3" check is removed.
- Reserved names: SSO `apiKeyHeader` reserves its `HeaderName`; OAuth, Impersonate and SSO basic reserve `Authorization`.

- [ ] **Step 1:** Write `Config_PerUserModesLoad`, plus one test per startup problem above, each asserting the problem text names the setting path (for example `ServiceInstances:Tickets:Auth:AsyncAuth`).
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Contract.Tests --filter PerUserConfig`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run, then the whole contract suite. Expected: PASS.
- [ ] **Step 5:** Commit: `feat(host): per-user auth settings and OAuth resources`.

---

### Task 4: Per-user auth providers

**Files:** Create `src/BrokerForge.Auth/PerUser/` (`DelegatedTokenAuthProvider.cs`, `SsoAuthProvider.cs`, `TokenEndpointClient.cs`, `RefreshGate.cs`, `ConnectionRequired.cs`). Test: `tests/BrokerForge.Auth.Tests/PerUserProviderTests.cs` (a fake token endpoint as an `HttpMessageHandler`).

**Interfaces:**
- Consumes: `IUserCredentialStore` (Task 2), `AuthConfig` and `OAuthResource` (Task 3).
- Produces: `DelegatedTokenAuthProvider(AuthMode mode, IUserCredentialStore store, Func<string, OAuthResource> resources, TokenEndpointClient tokens, Func<string, string> connectUrl, HttpClient http, ILogger log)`. Registered twice, for `AuthMode.OAuth` and `AuthMode.Impersonate`.
- Produces: `SsoAuthProvider(IUserCredentialStore store, Func<string, string> connectUrl, HttpClient http)`.
- Produces: `TokenEndpointClient.RedeemCodeAsync(OAuthResource r, string code, string verifier, string redirectUri, CancellationToken ct)` and `RefreshAsync(OAuthResource r, string refreshToken, CancellationToken ct)`. Both return `TokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)`. `invalid_grant` throws `RefreshRejectedException`; any other failure throws `CredentialUnavailableException` naming the token URL and the status, never the body. The Task 6 callback uses this client too.
- Behaviour:
  - No object ID, no row, or status not `Connected`: `ConnectionRequiredException(message, correlationId, connectUrl(instance))`. Messages name the instance and the reason ("no credential", "expired", "revoked"). The row's kind must match the mode, otherwise the same exception with reason "connected for another mode".
  - `RefreshGate` holds one `SemaphoreSlim` per (instance, user), and the token is re-read after the semaphore is taken. Channel `CredentialValues` hold the access token, or for SSO the secret and its Basic encoding.

- [ ] **Step 1:** Write these tests.
  - `OAuth_NoCredential_ConnectionRequiredWithUrl`
  - `OAuth_NoUserObjectId_ConnectionRequired`
  - `OAuth_ValidToken_AttachedAsBearer`
  - `OAuth_ExpiringToken_RefreshedAndSaved` (the rotated refresh token is persisted)
  - `OAuth_ConcurrentExpiredCalls_RefreshOnce` (two parallel calls, a token endpoint that counts calls and accepts each refresh token once; expected 1 call, both succeed)
  - `OAuth_RefreshRejected_MarksExpiredAndRequiresConnect` (a second call makes 0 token endpoint calls)
  - `OAuth_TokenEndpointFailure_NoCredentialInMessageOrLogs` (the endpoint returns 500 with the refresh token in its body; the exception message and a captured `ILogger` hold no token text)
  - `OAuth_ExpiredStatus_ConnectionRequired`
  - `OAuth_KindMismatch_ConnectionRequired`
  - `Sso_Basic_AttachesStoredCredential`
  - `Sso_ApiKeyHeader_AttachesStoredSecret`
  - `Sso_NoCredential_ConnectionRequired`
  - `Impersonate_UsesConnectAppResource`
- [ ] **Step 2:** Run `dotnet test tests/BrokerForge.Auth.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement, and register both providers in `Program.cs`.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5:** Commit: `feat(auth): OAuth, Impersonate and SSO providers`.

---

### Task 5: Tickets mock and OAuth server

Tasks 6 and 7 test against this mock.

**Files:** Create `tools/mock-api/tickets.js` and `tools/mock-api/tickets-openapi.yaml`. Modify `server.js`, `server.test.js` and `Dockerfile`.

**Interfaces:**
- Produces:
  - `GET /tk/v1/tickets?status=&limit=&cursor=`: the caller's tickets only, cursor paging.
  - `GET /tk/v1/tickets/{id}`: 404 for another user's ticket, the same as one that doesn't exist.
  - `POST /tk/v1/tickets`: owner = the caller.
- Seed data: alice owns `TK-1`..`TK-7`, bob owns `TK-8`..`TK-12`.
- Credentials accepted:
  - `Authorization: Bearer <mock token>`;
  - `Basic alice:<MOCK_TK_ALICE_PASSWORD>`;
  - an Entra JWT (RS256, `aud` = `MOCK_ENTRA_AUDIENCE`, keys from `MOCK_ENTRA_JWKS_URL` or `MOCK_ENTRA_JWKS_FILE`), with `oid` mapped by `MOCK_ENTRA_USERS=oid:alice,oid:bob`.
  - Anything else gets 401 `{"error":"unauthorized"}`.
- OAuth endpoints:
  - `GET /tk/oauth/authorize`: a form choosing alice or bob plus their password, then redirects with `code` and `state`.
  - `POST /tk/oauth/token`: `authorization_code` (PKCE S256 required; the client secret is `MOCK_TK_CLIENT_SECRET`) and `refresh_token`, single-use with rotation. Lifetime is `MOCK_TK_TOKEN_SECONDS` (default 3600).
  - `POST /tk/oauth/revoke`.

- [ ] **Step 1:** Write these Node tests.
  - `tickets: each user sees only their own`
  - `tickets: other user's id is 404`
  - `oauth: code requires matching PKCE verifier`
  - `oauth: refresh token is single-use`
  - `oauth: revoked refresh token gives invalid_grant`
  - `entra: wrong audience is 401`
  - `no credential is 401`
- [ ] **Step 2:** Run `cd tools/mock-api && npm test`. Expected: FAIL.
- [ ] **Step 3:** Implement with built-ins only, using `crypto.createPublicKey({ key: jwk, format: 'jwk' })` and `crypto.verify`.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5:** Commit: `feat(mock): Tickets API with OAuth server`.

---

### Task 6: Async context rule and the no-fallback suite

**Files:** Create `src/BrokerForge.Host/Routing/AuthSelector.cs`. Modify `src/BrokerForge.Host/Routing/RequestRouter.cs:115-123`. Test: `tests/BrokerForge.Contract.Tests/NoFallbackTests.cs`.

**Interfaces:**
- Produces: `AuthSelector.Select(ServiceInstance instance, CallerContext caller) -> (AuthMode Mode, ServiceInstance EffectiveInstance)`. The result uses the instance's own `Auth` unless `caller.Async && instance.Auth.AsyncContext == ServiceIdentity`; then it is `instance with { Auth = instance.Auth.AsyncAuth }`. This is the only place a call's identity can differ from the instance's mode.

- [ ] **Step 1:** Write these tests through the host, against the Tickets mock (Task 5). Each test registers a recording `TokenCredential` and a recording static `ISecretResolver`.
  - `PerUser_NoCredential_SyncCall_ConnectionRequired_NoServiceCredentialUsed` (the recorders saw 0 calls, the mock saw 0 data requests, and the reply is 401 `ConnectionRequiredException` with `connectUrl`)
  - `PerUser_NoCredential_AsyncTriggeringUser_ConnectionRequired`
  - `AsyncRule_ServiceIdentity_SyncCallStillPerUser`
  - `AsyncRule_ServiceIdentity_AsyncCallUsesAsyncAuth`
  - `PerUser_ExpiredToken_RefreshRejected_NoRetryAsAnotherMode`
  - `PerUser_SourceReturns401_SourceExceptionNotRetried` (a 401 from the source with a valid stored token is a `SourceException` or mapped error, never a retry)
  - `TwoUsers_SameQuery_DifferentRows` (alice and bob stored, `caller.userObjectId` differs, the row sets don't overlap)
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement `AuthSelector` and use it in `RequestRouter`.
- [ ] **Step 4:** Run the whole contract suite. Expected: PASS.
- [ ] **Step 5:** Commit: `feat(host): async context rule; no-fallback tests`.

---

### Task 7: Connect page

**Files:** Create `src/BrokerForge.Host/Connect/` (`ConnectEndpoints.cs`, `ConnectState.cs`, `ConnectPages.cs` for minimal HTML with every value HTML-encoded). Modify `Program.cs`: add a cookie scheme `Connect`, OpenID Connect `ConnectOidc`, and Data Protection persisted to blob and wrapped by a Key Vault key. In tests it uses an ephemeral key ring. Test: `tests/BrokerForge.Contract.Tests/ConnectTests.cs`, with a test authentication handler standing in for OIDC that signs in a given `oid`.

**Interfaces:**
- Consumes: Tasks 2–4.
- Routes. All except the callback require the `Connect` cookie scheme; the existing `/execute` endpoint keeps the JWT bearer scheme only.
  - `GET /connect/{instance}`: status (connected, expired, revoked or none), with Connect and Disconnect buttons.
  - `POST /connect/{instance}/start`: for OAuth or Impersonate, redirects to the AuthorizeUrl with `state` and PKCE S256. For SSO, shows a form.
  - `GET /connect/callback`: redeems the code through `TokenEndpointClient` and saves a `Connected` credential.
  - `POST /connect/{instance}/sso`: saves the username and secret.
  - `POST /connect/{instance}/disconnect`: deletes the row.
  - POSTs require antiforgery.
- Produces: `ConnectState`, protected with Data Protection purpose `BrokerForge.ConnectState.v1`. It holds instance, user object ID, PKCE verifier, nonce and issued-at; it is valid for 10 minutes and used once (the nonce is tracked in a cookie).

- [ ] **Step 1:** Write these tests.
  - `Connect_Unauthenticated_ChallengesOidc`
  - `Connect_OAuthFlow_StoresConnectedCredential` (follows the redirects against the Tickets mock OAuth server)
  - `Callback_StateForOtherUserOrReplayed_Refused` (four cases: another oid, another instance, reused, 11 minutes old; nothing stored in each)
  - `Connect_Sso_StoresCredential_NeverEchoesSecret` (no response body contains the secret)
  - `Disconnect_RemovesCredential_NextCallConnectionRequired`
  - `Connect_UnknownOrNonPerUserInstance_404`
  - `Execute_WithConnectCookieOnly_401` (the cookie does not open `/execute`)
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the whole suite. Expected: PASS.
- [ ] **Step 5:** Commit: `feat(host): connect page`.

---

### Task 8: Tickets descriptors and contract coverage

**Files:**
- `schemas/TicketsAPI.yaml` and `descriptors/TicketsAPI/` (listTickets, getTicket, createTicket), drafted with `bf draft` from `tickets-openapi.yaml` and reviewed by hand. Instance `TicketsAPI` is OAuth.
- Contract test settings for three instances over the same descriptors: `TicketsOAuth`, `TicketsSso` (basic) and `TicketsImpersonate` (test-signed JWTs).
- Test: `tests/BrokerForge.Contract.Tests/PerUserEndToEndTests.cs`.

- [ ] **Step 1:** Write these tests, each with two users.
  - `OAuth_TwoUsers_DifferentRows`
  - `Sso_TwoUsers_DifferentRows`
  - `Impersonate_TwoUsers_DifferentRows`
  - `PerUser_Create_OwnedByCaller`
  - `PerUser_RetrieveOtherUsersTicket_NotFound`
- [ ] **Step 2:** Run. Expected: FAIL (no descriptors).
- [ ] **Step 3:** Add the schema, descriptors and test settings. Run `bf validate descriptors --schemas schemas`. Expected: exit 0.
- [ ] **Step 4:** Run the whole suite. Expected: PASS.
- [ ] **Step 5:** Commit: `feat: TicketsAPI descriptors; per-user end-to-end tests`.

---

### Task 9: Infrastructure and dev setup

**Files:**
- Modify `infra/main.bicep` and `infra/containerApps.bicep`:
  - a storage account with table `bfusercredentials` and blob container `dataprotection`;
  - a Key Vault key `bf-dataprotection`;
  - host managed identity roles: Storage Table Data Contributor, Storage Blob Data Contributor, Key Vault Crypto User;
  - external ingress for the mock;
  - host settings for `TicketsAPI`, `OAuthResources:Tickets`, `OAuthResources:Entra*` and `Connect:*`;
  - the mock's Tickets environment and its secrets.
- Create `tools/dev/setup-connect-app.sh` (the connect app registration, its redirect URI `{host}/connect/callback` and `/signin-oidc`, and the federated credential from Task 1). It writes IDs to `~/.brokerforge/dev-ids.env`.
- Modify `tools/dev/deploy.sh` (the Tickets secrets: client secret and user passwords, generated into Key Vault and never printed).

- [ ] **Step 1:** Run `az bicep build infra/main.bicep`. Expected: no errors. Run `tools/dev/deploy.sh`. Expected: the host and mock run the new image alone, and `/healthz` returns ok.
- [ ] **Step 2:** Run `tools/dev/generate.sh TicketsAPI BrokerForgeTickets`. Expected: `ext_ticket` created, then a dry run reports "nothing to change".
- [ ] **Step 3:** Commit: `feat(infra): credential store, connect app, Tickets source`.

---

### Task 10: Live acceptance, findings, docs

**Files:** Create `tools/dev/acceptance_peruser.py` and `.sh`, and `docs/findings/phase3.md`. Modify `CLAUDE.md` (current state, commands) and `docs/hld.md` (15, 16, 9.3 from the Task 1 findings).

The script runs each user through `AZURE_CONFIG_DIR` profiles, `~/.brokerforge/az-userA` and `az-userB`. It pauses for the browser steps, which Simon does.

- [ ] **Step 1:** Run `tools/dev/acceptance_peruser.sh`. Expected, in order:
  1. A queries `ext_tickets` with no credential: an error containing `ConnectionRequiredException` and `/connect/TicketsAPI`.
  2. After A connects: A sees exactly `TK-1`..`TK-7`.
  3. B still gets `ConnectionRequiredException`. This is the no-fallback check live.
  4. After B connects: B sees `TK-8`..`TK-12`.
  5. A disconnects: A gets `ConnectionRequiredException` again.
  6. The Impersonate instance repeats steps 1–4 with Entra sign-in.
  7. No host log line in the run window contains a stored token (a Log Analytics query for the token prefixes the mock issues).
- [ ] **Step 2:** Re-run `tools/dev/acceptance.sh` and `acceptance_inventory.sh`. Expected: still pass.
- [ ] **Step 3:** Write the findings and doc updates. Scale the dev apps to zero. Commit: `docs: phase 3 findings`.
