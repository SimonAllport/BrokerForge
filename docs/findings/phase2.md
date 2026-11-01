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
