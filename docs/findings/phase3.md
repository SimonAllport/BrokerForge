# Phase 3 findings: per-user security

Live results against the dev environment (`rg-brokerforge-dev`, org `orgeb8d0d95`), 2 November 2026.
Plan: `docs/superpowers/plans/2026-11-01-phase3-per-user-security.md`. Decisions: HLD D-25 to D-33.
The async signal was verified separately on 1 November (`docs/findings/phase3-spike.md`).

Two Entra users took part. User A is Simon (`50b98b16-…`), System Administrator; the mock maps him to `alice`.
User B is Test Me (`testme@cloudfish.solutions`, `20304250-…`) with Basic User and the "BrokerForge Tickets user"
role (6); the mock maps them to `bob`. Two sources share the Tickets mock: `TicketsAPI` (OAuth against the mock's
own authorisation server, table `ext_ticket`) and `TicketsEntraAPI` (Impersonate with an Entra token for the
"BrokerForge Tickets mock API (dev)" app, table `ext_entraticket`).

## 1. Setup

`tools/dev/setup-connect-app.sh` created the two app registrations and the `bf-host-mi` federated credential on
the connect app. Admin consent was not granted from the CLI, so each user approved the consent prompt at first
sign-in; that is the documented fallback and needed nothing else. `tools/dev/deploy.sh` added the storage account,
the `bf-dataprotection` key, the role assignments, the external mock and the three Tickets secrets, and waited for
image 9b08540. `tools/dev/generate.sh` created both tables (7 writes each); both dry runs then reported "nothing to
change". The unauthenticated `GET /connect/TicketsAPI` redirected to Entra with the connect app's client ID.

## 2. No credential fails loudly, before the source is called

Before anyone connected, A's reads of both tables failed with `ConnectionRequiredException`, naming the mode and
the connect URL, with a correlation ID:

> Service instance TicketsAPI uses OAuth and there is no credential for this user; connect your account and try
> again. Connect at https://bf-dev-host…/connect/TicketsAPI.

The host failed them in 84 ms and 6 ms, which is the router refusing before any source call. As section 15 already
records, Dataverse surfaces the host's 401 to Web API callers as HTTP 400 / `0x80040265`.

## 3. Two users, the same tables, different rows (the phase 3 done criterion)

| User | `ext_tickets` (OAuth) | `ext_entratickets` (Impersonate) |
| --- | --- | --- |
| A | Ticket 1 to Ticket 7 | Ticket 1 to Ticket 7 |
| B | Ticket 8 to Ticket 12 | Ticket 8 to Ticket 12 |

Host logs show every read carrying the caller's own object ID with `async=False`. B's reads took 1,029 ms (OAuth,
including the mock waking from zero) and 365 ms (Impersonate). Tokens refreshed live: the host logged refreshes of
A's Impersonate token at 10:10 and of both of A's tokens at 16:22, each followed by a successful read.

## 4. No fallback, live

B disconnected `TicketsAPI` at 16:22:04. B's next read of `ext_tickets` failed with the same
`ConnectionRequiredException` (correlation ID `79553a9e-…`) while A was still connected and the host's service
identity was available. Nothing was returned as A, as the service account or from a cache. The contract tests
(`NoFallbackTests`) cover the remaining paths, including the async rule.

## 5. No token reaches the logs

The host wrote 127 console log lines in the 12 hours covering the run: every connect, refresh, read and the
disconnect. A Log Analytics query found none containing `eyJ` (any JWT), `Bearer `, `access_token`, `refresh_token`,
`code_verifier`, `client_secret`, or a 43-character base64url string (the shape of the mock's tokens and codes).

## 6. A per-user source needs a security role the generator does not create

Virtual tables are organisation-owned, so a user needs Read (and Create, for writes) on each generated table.
`bf generate` creates tables, columns and Custom APIs but no role, so B could not read the tables until a
"BrokerForge Tickets user" role was added by hand (Read and Create at organisation depth on `ext_ticket` and
`ext_entraticket`, in solution `BrokerForgeTickets`). Dataverse adds its usual default privileges to any new role.
This applies to every source, not only per-user ones; phases 1 and 2 did not notice because A is an administrator.
Recorded in HLD section 15.

## 7. The connect page and Dataverse can be signed in as different people

B's first attempt opened the connect pages and the Dataverse Web API in one browser window. The connect pages
stored credentials for B, but the Web API read ran as A, from an existing Microsoft sign-in in that window, and so
returned A's rows. Nothing leaked: each credential is keyed by the Entra object ID that signed in to the connect
page, and each read used the caller's own. The confusion is real, though: the connect page does not show which
account is signed in, so a user can connect the wrong account without noticing. Recorded in HLD section 15 as a
follow-up (show the signed-in account on the connect page).

A Web API URL opened in a fresh window returns 401 rather than a sign-in page; opening the environment's root URL
first creates the session.

## 8. Phases 1 and 2 still pass

`tools/dev/acceptance.sh` (Orders) and `tools/dev/acceptance_inventory.sh` (Inventory) passed unchanged against the
phase 3 host. Orders warm retrieve through the Web API: median 222 ms, p95 307 ms (n=10).

## 9. How the run was done, and what it does not cover

The plan called for `tools/dev/acceptance_peruser.sh` driving both users through separate `az` profiles. The run was
done by hand instead: B's reads had to come from B's own browser session, and the az CLI here holds only A. The
evidence is the host's logs, filtered by object ID, plus the responses B's browser returned. A scripted version
would need a second az profile signed in as B.

Not covered live: SSO (contract tests only, D-32) and `Auth:AsyncContext: ServiceIdentity`, which no dev source
configures. The async signal itself (`async=true` with the triggering user) was verified live in the spike.
Also not verified: a Power Automate flow calling a per-user Custom API (it needs a flow connection signed in
interactively), and how the connect error renders in a model-driven view or canvas gallery. A flow that reads a
per-user virtual table never gets the async rule: the data provider plug-in runs synchronously, so `async` is false
and the flow's connection user needs their own credential (HLD 9.3).

## 10. Impersonation borrowed the impersonated user's credential (found live, fixed)

With the second user available, A read `ext_entratickets` through the Web API with the header
`CallerObjectId: 20304250-…` (B). The host received B's object ID and the read returned B's rows (Ticket 8 to 12)
through B's stored Impersonate credential: under `CallerObjectId` the platform sets
`InitiatingUserAzureActiveDirectoryObjectId` to the impersonated user, not the caller. Anyone with
`prvActOnBehalfOfAnotherUser` (System Administrators, integration application users granted it) could call a
per-user source as any connected user, which D-27 rules out.

Fix (2ac9cc9): the plug-in sends the object ID only when `IPluginExecutionContext3.AuthenticatedUserId` equals
`InitiatingUserId`, and sends none otherwise, so per-user modes refuse the call with `ConnectionRequiredException`.
Service account and Static modes do not use the ID and are unaffected; no contract changed. Verified live after
re-registering the plug-in and redeploying the host:

| Call | Host saw | Result |
| --- | --- | --- |
| A reads `ext_entratickets` impersonating B | no object ID | `ConnectionRequiredException` ("an impersonated call sends none") |
| A reads `ext_entratickets` | A | Ticket 1 to Ticket 7 |
| Async step on a Custom API (spike probe), plain | A, `async=true` | the async rule still gets the triggering user |
| Async step, impersonating B | no object ID, `async=true` | refused |
| Sync step, plain / impersonating B | A / no object ID, `async=false` | as above |

Orders and Inventory acceptance pass again on the redeployed host.
