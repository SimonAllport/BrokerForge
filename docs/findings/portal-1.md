# Portal connections, plan 1: a source served from a connection design, live in dev

Run 3 November 2026 against the dev environment (`rg-brokerforge-dev` and its Dataverse environment), on branch
`portal-connections`, host image built from cc4c6a8 plus this change. Everything below was observed, not inferred,
unless marked **Not verified**. Design: HLD D-34, `docs/connection-design-format.md`, `docs/findings/portal-carrier.md`.

## Result

`WarehouseAPI`, a copy of the Inventory source, is served from a connection design that the host reads from
Dataverse with its own identity. Inventory stays file-based. Both existing acceptance scripts pass unchanged on the
same host.

| Check | Result |
| --- | --- |
| `GET ext_stockitems` before the key is set | HTTP 400, `ConfigurationError: Service instance WarehouseAPI needs its key `apiKey` in this environment: an administrator sets it on the BrokerForge secret page.` |
| `set-instance-secret.sh WarehouseAPI apiKey inventory-api-key` | `Set instance secret `apiKey` of WarehouseAPI in environment <id> from kv:<vault>/secrets/inventory-api-key.` (12 s, most of it the exec session) |
| `GET ext_stockitems` (page size 50) | HTTP 200, the 45 mock items; `ext_state eq 100000001` returns 9; host logs `Executing RetrieveMultiple on ext_stockitem with listItems v1 (design v1)` |
| Custom API `ext_AdjustStockItem` +2 then -2 on item 3 | HTTP 200, OnHand 23 then 21 (back to where it was); logged `(design v1)` |
| Current value of `bf_WarehouseAPI_BaseUrl` deleted (no default exists) | Served from the cache until the 300 s TTL ran out, then HTTP 400, `ConfigurationError: Service instance WarehouseAPI needs its base URL in this environment: set environment variable bf_WarehouseAPI_BaseUrl.` |
| Value restored (`publish-design.sh` again) | HTTP 200 again once the cached store expired (693 ms for the reload call) |
| `tools/dev/acceptance.sh` (Orders) | all assertions passed |
| `tools/dev/acceptance_inventory.sh` | all assertions passed |
| Log scan (phase 3 findings 5 method) | see section 5 |

## 1. What was set up

1. **Host application user.** `setup_dataverse.py --host-user` (also part of the full run) creates the application
   user for the host managed identity's client ID in the root business unit, with one role, `BrokerForge host`
   (in `BrokerForgeCore`), holding organisation-level Read on web resources and environment variable definitions.
   Nothing else.
2. **Copy source.** In a scratch folder, as `tools/media/capture.mjs` does: `bf draft openapi/WarehouseAPI.yaml
   --instance WarehouseAPI --prefix ext --table ext_stockitem --out drafts` from `tools/mock-api/inventory-openapi.yaml`;
   the same review edits (`sortStable: true`, display names Stock item / Stock items / SKU); then
   `bf validate descriptors --schemas schemas`, which reported 6 descriptors valid. One extra edit is needed (section 4).
3. **Table.** `bf generate` from the scratch folder into solution `BrokerForgeWarehouse` (publisher `brokerforgeext`):
   data source record, virtual table `ext_stockitem`, 5 columns, the Custom API. 10 writes.
4. **Design.** `tools/dev/publish-design.sh WarehouseAPI BrokerForgeWarehouse --root <scratch> --settings-from
   InventoryAPI --secret-name apiKey`. This takes Inventory's live host settings (`Static`, `apiKeyQuery`, `api_key`,
   object map `reversible`/`Int32`, base URL the internal mock) and writes:
   - the web resource `ext_/brokerforge/WarehouseAPI/design.xml` (type 4, 9,463 bytes), then `PublishXml` for it;
   - `bf_WarehouseAPI_BaseUrl` (String) and `bf_WarehouseAPI_Auth` (JSON), both in the solution with no default;
   - dev's base URL as the current value, created **without** the solution header, so dev's URL does not travel.

   A second run with nothing changed keeps version 1 and writes the same content. The live web resource parses with
   `DesignCarrier.Unwrap` and `DesignSerializer.Parse`.
5. **Host.** `infra/containerApps.bicep` adds `Trust__AllowedCallers__0__OrgUrl` (new `orgUrl` parameter;
   `deploy.sh` passes `DV_ORG` from `tools/spike/dv.sh`) and `Config__Designs__Enabled=true`. Every existing setting is
   kept. The credential table and Data Protection settings moved from the per-user list to the main list, because a
   host with designs refuses to start without them. `deploy.sh` ran and waited for the new image.
6. **Key.** `tools/dev/set-instance-secret.sh WarehouseAPI apiKey inventory-api-key`, section 3.

## 2. Timings

| Call | Host time | End to end through the Web API |
| --- | --- | --- |
| First call after the deploy (cold design load: two Dataverse reads with a new token, then the store miss) | 763 ms | 5,748 ms (first call to a fresh revision and plug-in) |
| First call with the key (instance build, Key Vault untouched: `store:` reads the table) | 399 ms | 1,010 ms |
| Warm calls | 10 to 20 ms | 270 to 550 ms |
| Reload after the TTL (design read again) | 1,065 ms | 75,542 ms, see below |

The 75 s call: the host received it 74 s after the client sent it and answered in 1,065 ms (log timestamps). The
delay was on the Dataverse side (platform or plug-in sandbox) before the plug-in called the host; the next call, 30 s
later, took 315 ms. It happened once and its cause was not found. **Not verified** as linked to the design reload.

## 3. Setting the key with the host's own identity

`set-instance-secret.sh` runs the new host admin command inside the running host container through
`az containerapp exec`:

```
dotnet /app/BrokerForge.Host.dll set-instance-secret --environment <id> --instance WarehouseAPI --name apiKey --from kv:<vault uri>secrets/inventory-api-key
```

The command builds the host's own services (same configuration, managed identity, credential table and Data
Protection key ring), reads the value itself from the Key Vault reference, writes it with `TableInstanceSecretStore`,
reads back that the row exists, and prints only the reference. The value is never on a command line, never on the
workstation and never printed. It refuses an environment that is not in `Trust:AllowedCallers`, a `store:` source,
and incomplete arguments (`SetInstanceSecretCommandTests`). The script checks that the Key Vault secret exists by
listing names, so it does not fetch the value either.

This differs from the plan, which named a local `dotnet run --project src/BrokerForge.Host` against the dev table.
That would need Simon's account to hold the table data role and the Key Vault key role for the Data Protection key
ring, which were not to be granted. `az containerapp exec` needs a terminal, so the script uses `script(1)` when
run from another tool.

## 4. Differences from the plan

1. **The Custom API is renamed for the copy.** The draft binds `adjustStock` to `ext_AdjustStock`, which Inventory
   already owns in this environment. One Custom API cannot serve two instances, so the WarehouseAPI copy binds
   `customapi:ext_AdjustStockItem` (descriptor and schema `customApis`). Validation and generate passed after this.
2. **No privilege exists for environment variable values.** `privileges` has `prvReadEnvironmentVariableDefinition`
   but no `prvReadEnvironmentVariableValue`. The role holds Read on web resources and on definitions only. The
   host's `$expand` of `environmentvariabledefinition_environmentvariablevalue` still returned the value, so values
   are read through the definition's privilege.
3. **The key secret is Inventory's.** The copy calls the same mock, which accepts only `inventory-api-key`, so
   `set-instance-secret.sh` takes the Key Vault secret name as a third argument.
4. **Settings source.** WarehouseAPI has no host settings of its own, so `publish-design.sh` takes
   `--settings-from InventoryAPI`. It refuses settings the format cannot carry (OAuth resources, `Config`,
   `Secrets`, unknown auth fields) rather than dropping them.
5. Both OData queries from the task 6 report ran live under the application user: `contains`/`endswith` on
   `webresourceset.name`, and `$filter` with `$expand` on `environmentvariabledefinitions`.

## 5. Log scan

All Container Apps console lines from the deploy (22:10 UTC) to the end of the run: 152 from the host, 1 from each
mock, 154 in total. None contains the test key (compared in memory with the Key Vault value, which was not printed),
`eyJ` (any JWT) or `Bearer `. 14 host lines carry `(design v1)`. The App Insights tables in the workspace returned no
rows for the window, so only the console logs were scanned, as in phase 3.

## 6. WarehouseAPI stays; how to remove it

WarehouseAPI is the test source for plan 2 onwards, so it is left in place and working. To remove it:

1. Delete the instance secret row in the host's credential table (`bfusercredentials`): partition
   `instancesecret_<environment ID, N form>`, row `WarehouseAPI_apiKey`. Do it from the host identity, as
   `set-instance-secret.sh` does, or with the plan 4 page once it exists.
2. In Dataverse, delete the solution's components and then the solution `BrokerForgeWarehouse`: the web resource
   `ext_/brokerforge/WarehouseAPI/design.xml`, the value of `bf_WarehouseAPI_BaseUrl`, the definitions
   `bf_WarehouseAPI_BaseUrl` and `bf_WarehouseAPI_Auth`, the Custom API `ext_AdjustStockItem` with its parameters,
   the virtual table `ext_stockitem`, and the `bf_brokerdatasource` record for WarehouseAPI.
3. Within 5 minutes (the cache TTL) the host stops serving it. No redeploy is needed.

The host application user and the `BrokerForge host` role stay: every environment with designs needs them.
