# Phase 3 spike findings (1 November 2026)

Probe: `tools/spike/phase3/probe_caller.py`. It creates a temporary unbound Custom API `bf_SpikeAsync` (allowed custom processing step type 2, sync and async), registers `BrokerForge.Plugin.CustomApiPlugin` on it as an async post-operation step and then as a sync one, calls it through the Web API as the az CLI user, reads the host's execute log scope (`userObjectId`, `async`, added in this phase) and deletes what it created. The host refuses the unknown API name after writing the scope, which is all the probe needs.

| Call | Plug-in mode | `caller.async` at the host | `caller.userObjectId` at the host |
| --- | --- | --- | --- |
| Web API, async post-operation step | 1 | `true` | the calling user (`50b98b16-…`) |
| Web API, sync post-operation step | 0 | `false` | the calling user |

So `context.Mode == 1` is a usable async signal (HLD D-29): an async step reaches the host with `async = true` and the user who triggered it, which is what `TriggeringUser` needs.

Not yet verified, carried to the live acceptance (Task 10), because each needs a person or a second user:

- `CallerObjectId` impersonation: the environment has no second enabled user yet, so whether `InitiatingUserAzureActiveDirectoryObjectId` stays the real caller under impersonation (D-27) is checked once the second test user exists.
- A Power Automate cloud flow calling a Custom API: needs a flow connection, which needs an interactive sign-in.
- How the `ConnectionRequiredException` text and URL render in a model-driven view and a canvas gallery: needs a browser.
- The connect app's managed identity federated credential (D-26): exercised when the connect app is created and the host redeems its first code (Tasks 9 and 10). If Entra refuses it, the fallback is a client secret in Key Vault.

The provider plug-in is always registered synchronously, so virtual table messages always arrive with `async = false`.
