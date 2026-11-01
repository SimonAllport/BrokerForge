# Phase 2 findings: tooling

Live results against the dev environment (`rg-brokerforge-dev`, org `orgeb8d0d95`), 1 November 2026.
Plan: `docs/superpowers/plans/2026-10-31-phase2-tooling.md`. Decisions: HLD D-16 to D-24.

## 1. The generator adopts the phase 1 `ext_order` table unchanged

`tools/dev/setup-dataverse.sh` now registers core components only (assembly, both plug-in types,
`bf_brokerdatasource`, the BrokerForge provider, `bf_HostUrl` and `bf_HostScope`). `tools/dev/generate.sh
OrdersAPI BrokerForgeOrders` then compared `schemas/OrdersAPI.yaml` with the table setup created in phase 1
and reported "nothing to change": same column types, lengths, precision, option values and external names,
and the existing "Orders host" data source record was found by `bf_serviceinstance`.

## 2. The platform adds a derived `<column>name` attribute for every option set column

The first dry run refused: "ext_order.ext_statusname exists but is not in the schema". Dataverse creates
`ext_statusname` (AttributeType Virtual, `AttributeOf: ext_status`) alongside the picklist. The generator
now ignores attributes with `AttributeOf` set; the test fake models the attribute so the re-run test covers it.

## 3. Orders acceptance with the new descriptor features

`tools/dev/acceptance.sh`: all phase 1 assertions plus 8 (option set written on create and update reads
back as the Dataverse value) and 9 (`ext_lines_json` read back through `type: json`) pass. Warm retrieve
through the Web API: median 224 ms, p95 247 ms (n=10).

## 4. Microsoft.OpenApi.Readers 1.6.22 has a high-severity advisory

GHSA-7pxr-59rr-hqj2 (denial of service through YAML alias expansion) affects `Microsoft.OpenApi.Readers`
before 1.6.30. SwaggerForge reads OpenAPI files supplied by sources, so it now uses 1.6.30.

## 5. Inventory onboarded with configuration only (the phase 2 done criterion)

`bf draft tools/mock-api/inventory-openapi.yaml --instance InventoryAPI --prefix ext --table ext_item --out out`
drafted six descriptors (list, retrieve, create, update, delete and the `adjustStock` action as Custom API
`ext_AdjustStock`) and `schemas/InventoryAPI.yaml`. It skipped nothing and left three notes: decimal
precision 2 for `ext_price` (kept), `ext_name` as the primary name (kept) and `sortStable: false` (set to true:
the source returns items in id order). Two more hand edits, both YAML: the `ext_sku` display name, and
`error.detail` as the message on 422 mappings. `bf validate` passed all 11 descriptors.

The onboarding commit (e86f999) adds `schemas/InventoryAPI.yaml`, `descriptors/InventoryAPI/*.yaml`, the
InventoryAPI instance and `ext_item` object map in `infra/containerApps.bicep` (Static `apiKeyQuery`,
reversible Int32 keys) and the acceptance scripts. `git diff --stat 90b57a7..HEAD -- src/` is empty.

`tools/dev/acceptance_inventory.sh` passes: 45 items over three 20-row offset pages, each once; an option set
filter reverse-mapped to the source value; retrieve by integer key; create, update and delete; `ext_AdjustStock`
through the Web API returning typed `OnHand` and `AdjustedAt`, with a source 422 surfaced as `Validation`;
an undeclared filter refused with `UnsupportedQueryException`; a missing required input refused.

Time against NFR-06 (under one day for a well-specified OpenAPI): from the mock commit (10:11) to the passing
onboarding acceptance (10:29), 18 minutes. That includes two deploys and two fixes found on the way (6 and 7),
which were made before the onboarding commit's base so the no-code check covers only the onboarding.

## 6. Tooling bugs the live onboarding found

- **Custom API plug-in binding.** The generator posted `plugintypeid@odata.bind`; the Web API refused it
  ("undeclared property 'plugintypeid'"). The navigation property on `customapi` is `PluginTypeId`, and
  binding names are case-sensitive. Fixed in 90b57a7; the test fake now uses the platform's name. The first
  run had already created `ext_item` and its columns; the re-run found them matching the schema and wrote
  only the Custom API, which is the re-run behaviour the generator promises.
- **Mock revision overlap.** The mock Dockerfile copied an explicit file list without `inventory.js`, so the
  new mock revision never became healthy while the old one kept answering (the phase 1 host overlap, finding
  8, now for the mock). `deploy.sh` waits for host and mock to run the new image alone (24985af).

## 7. A missing required Custom API input is refused by the platform first

With `isoptional: false` on the request parameter, Dataverse rejects a call without `Delta` before the plug-in
runs ("Error identified in Payload provided by the user"). The host's own check
(`CustomApi_MissingRequiredInput_400Validation`) stays as the second line, for callers that reach the host
directly and for optional-in-Dataverse parameters the schema marks required.

## Still open

- The CI workflow (`.github/workflows/ci.yml`) has not run on GitHub: nothing has been pushed. Each step ran locally.
- Dataverse config tables for service instances, object maps and descriptors (the rest of D-10) are phase 2b (D-16).
- `FaultException<OrganizationServiceFault>` for provider errors (HLD 15) was not verified; errors still reach Web API callers as 400 / `0x80040265`.
