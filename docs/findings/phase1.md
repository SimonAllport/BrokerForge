# Phase 1 findings

Verified 31 October 2026 in the dev environment (`orgeb8d0d95.crm17.dynamics.com`, environment ID `a4c0d8aa-c6c6-e9a3-bc54-8725982bb449`) against the Orders mock, through a Dataverse virtual table. Everything below was observed, not inferred, unless marked **Not verified**.

## Result

All seven acceptance assertions in `tools/dev/acceptance.sh` pass. They are the phase 1 "done when" criteria in CLAUDE.md.

| # | Assertion | Observed |
| --- | --- | --- |
| 1 | `ext_status eq 100000000` with `odata.maxpagesize=25`, following `@odata.nextLink` | 40 open orders over 2 pages, no duplicates |
| 2 | Retrieve by the id from 1 | Same `ext_name` |
| 3 | `POST ext_orders` | 204 with `OData-EntityId`; the row reads back |
| 4 | `PATCH ext_customerref` | 204; the new value reads back |
| 5 | `DELETE`, then GET | Delete 204; the GET fails with `NotFound: …` and the correlation ID (HTTP 400, see finding 2) |
| 6 | `$filter=ext_name eq 'REF-0001'` | HTTP 400, `UnsupportedQueryException: Filter on column ext_name with operator eq is not supported …` with the correlation ID |
| 7 | `$filter=ext_customerref eq '__fail500'` | HTTP 400, `SourceException: Source returned HTTP 500 for operation listOrders …` with the correlation ID |

## What the platform did differently from the plan

1. **A Web API PATCH queries the virtual table by primary key first.** A plain `PATCH ext_orders(<id>)` is an upsert. Before sending Update, Dataverse sent the provider a RetrieveMultiple with the single condition `ext_orderid eq <id>`, which the host refused as an undeclared filter. With `If-Match: *` (update only) the PATCH went straight to Update. The host now answers a RetrieveMultiple whose only condition is the primary key with the table's Retrieve operation (HLD D-14). Any other condition beside the key is still refused.
2. **Provider error codes are not passed through.** The plug-in raises `InvalidPluginExecutionException(OperationStatus.Failed, <code>, message)` with `0x80040217` for not found, but the Web API returned HTTP 400 with code `0x80040265` for every provider error. The message reaches the caller intact, so the BrokerForge kind (`NotFound`, `Conflict`, `UnsupportedQueryException` and so on) and the correlation ID are always visible. **Not verified:** whether throwing `FaultException<OrganizationServiceFault>`, or leaving `BusinessEntity` unset, would give a real 404. A short experiment with the latter changed nothing, but the trace logs for it never appeared, so it is inconclusive. Recorded in HLD 15.
3. **`IPluginExecutionContext6.EnvironmentId` is populated** in the provider context, with the Power Platform environment ID that the host's caller check (D-09) expects. The data source record does not need an environment column.
4. **The spike assembly could not be updated in place.** Dataverse refuses an assembly update that drops a registered plug-in type, and `SpikeRetrieveMultiplePlugin` is still used by the phase 0 table. The phase 1 plug-in is registered side by side as version `0.1.0.0` with the same name and key. Updating within `0.1.x` replaces it in place.
5. **Two host revisions served traffic briefly after a redeploy.** One call reached the old image. `activeRevisionsMode: 'Single'` is now explicit in Bicep.

## Latency (NFR-01: host overhead 150 ms at p95)

End to end through the Web API, warm, measured by `acceptance.sh` from the developer's machine (n=10 each):

| Call | Median | p95 |
| --- | --- | --- |
| Retrieve | 207 ms | 254 ms |
| RetrieveMultiple `$top=10` | 224 ms | 262 ms |

Inside the host, the `Completed … in {ElapsedMs}` log line covers routing, rendering, the source call and mapping. The source is the mock in the same Container Apps environment. Median: 2 ms for Retrieve and RetrieveMultiple, 1–3 ms for Create, Update and Delete. The RetrieveMultiple p95 was 193 ms over 26 calls, from the first calls on a fresh revision while .NET was still warming up. Host overhead is inside NFR-01; almost all of the end-to-end time is in Dataverse and the plug-in sandbox. The first call after a new plug-in assembly took 4.65 s.

With `minReplicas: 1` there was no host cold start in these runs. Phase 0 measured 23.5 s for a cold host.

## Known gaps carried into phase 2

- Writing an option set column (Create or Update) is refused with `UnsupportedQueryException` until write descriptors can reverse-map option sets. Before the final review the integer was sent to the source silently.
- A Create or Update that supplies a column no `col` binding sends is refused with a Validation error naming the column, so a form save can never appear to succeed while dropping a value. On `ext_order` that means Status, Reference and Placed cannot be edited in phase 1.
- Dataverse asking for page N > 1 without a paging cookie (a jump to page) is refused. Back-navigation with a lower page number was not tested.
- `ext_lines_json` is written on Create but not read back, because the spec has no map type that serialises a JSON subtree to a string.
- Application Insights is deployed but the host does not send to it yet. Console logs, which carry the `correlationId` scope, go to Log Analytics (`ContainerAppConsoleLogs_CL`).

## Deployed resources

- Resource group `rg-brokerforge-dev` (Switzerland North): Container Apps environment `bf-dev-env`, apps `bf-dev-host` (external, min 1 replica) and `bf-dev-orders-mock` (internal, 1 replica), registry `bfdevacry6gnmaq6yl7ho`, Key Vault `bf-dev-kv-y6gnma` (secrets `orders-api-key`, `page-token-key`, generated by `deploy.sh` and never printed), user-assigned identity `bf-dev-id`, Log Analytics `bf-dev-logs`, Application Insights `bf-dev-insights`.
- Host: `https://bf-dev-host.bravebay-a6104a1b.switzerlandnorth.azurecontainerapps.io`.
- Dataverse: solutions `BrokerForgeCore` (publisher `brokerforge`, prefix `bf`) and `BrokerForgeOrders` (publisher `brokerforgeext`, prefix `ext`); assembly `BrokerForge.Plugin` 0.1.0.0 (`49664812-30d5-f111-84ea-002248db5cf2`) bound to the phase 0 managed identity; plug-in type `BrokerForge.Plugin.ProviderPlugin` (`4c664812-30d5-f111-84ea-002248db5cf2`); data provider `BrokerForge` (`4c75d224-30d5-f111-84ea-002248db5cf2`); data-source table `bf_brokerdatasource` with record "Orders host" (`4f75d224-30d5-f111-84ea-002248db5cf2`); virtual table `ext_order`.
- IDs are saved in `~/.brokerforge/dev-ids.env`.
