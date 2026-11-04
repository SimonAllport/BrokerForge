# Portal connections plan 2: SwaggerForge questions

Results of 4 November 2026, local only. This plan has no Dataverse or host part; the host Custom APIs that call the draft service come in plan 4. Plan: `docs/superpowers/plans/2026-11-04-portal-connections-2-questions.md`. Decision: HLD D-35.

## 1. What changed

SwaggerForge no longer fills gaps with defaults. Before, it drafted a string length of 100, two decimal places, a page size of 100, filter `eq` on every matching parameter, `sortStable: false`, the first string as the primary name, the only collection as the table, and `instance:` config for path parameters. Each of those is now a question, and an endpoint stays out of the result until its questions are answered.

`DraftService.Review(text, options, answers)` (`tools/SwaggerForge/DraftService.cs`) returns a `DraftReview`:

- the proposed table, with every column's source path, logical name, display name and type, and the table questions;
- one `EndpointReview` per operation, giving what it becomes (List, Open, New, Save, Delete, Action), its status (Ready, NeedsAnswers, NotUsed, Unsupported, Invalid), the skip reason or validator errors, and its questions;
- problems with the definition itself;
- answers no question asked;
- the schema and descriptors, only when nothing is open.

The answer keys are documented in `docs/connection-design-format.md` ("Answer keys"). They are the design's `answers` field, so the format stays at version 1.

The service never throws for pasted text. Empty text, HTML, broken YAML, a document with no paths, or text over 2,000,000 characters each give one problem and no endpoints. External `$ref`s (URLs or relative files) are never loaded; each is a problem naming the reference. A test with a live loopback listener confirms no request is made.

## 2. Today's sources, re-drafted from their OpenAPI files plus answers

**Inventory, exact.** `tools/mock-api/inventory-openapi.yaml` with the nine answers in `tests/SwaggerForge.Tests/fixtures/inventory-answers.json` drafts the six committed descriptors and `schemas/InventoryAPI.yaml`. Run through `bf draft`, the descriptor files are byte-identical (`diff -r` is empty) and `bf validate` passes. Without answers, `bf draft` exits 3 and lists six open questions:

- the price's decimal places;
- the list's paging;
- the `state` filter;
- the `name` filter;
- stable order;
- the create's 422 message.

The other three answers are optional: the SKU display name, and the 422 message paths of `updateItem` and `adjustStock`, which document no body.

**Orders, as far as the definition goes.** `tests/SwaggerForge.Tests/fixtures/orders-openapi.yaml` with `orders-answers.json` (22 answers, most of them column renames and display names) gives:

- `listOrders` and `getOrder` equal to the committed files, including `eq,in` with `csv`, `filters: { allowOr: true, orStyle: in }`, the sort map with `-{field}`, cursor paging at `$.meta.nextCursor` and the nested paths;
- `deleteOrder` equal apart from its `X-Correlation` header;
- `createOrder` and `updateOrder` with the same body templates, option sets and the error mappings the definition documents.

Option set map names are compared by position: the drafter names a map after its column (`status`), while the committed files say `orderStatus`. The name is internal to a descriptor and changes no behaviour.

Still missing from the Orders definition, for plan 5 to decide:

- the `X-Correlation` and `Idempotency-Key` headers;
- `placedBy: { context: userObjectId }`;
- `retry`;
- `updateOrder`'s 422 mapping.

The definition doesn't state any of these, and no answer key adds them. The fixture now declares `required` on the nested `state`, `customer` and `totals` objects. The mock always returns them, and without it their columns drafted as optional.

## 3. Calls made while building (from the ledger)

- A Validation status with no documented body still asks for its message, as an optional JSON path. Inventory's committed files needed this.
- Logical names may contain underscores after the prefix (`ext_lines_json`).
- Lists use the same error questions as other operations.
- Optional query parameters of retrieve, write and delete operations aren't sent. Required ones ask for a fixed value. A parameter named in `DraftOptions.ReservedNames` is left to the authentication.
- Sort field questions appear only once a sort style is chosen.

## 4. Tests

`dotnet test tests/SwaggerForge.Tests`: 58 tests, including `DraftServiceTests`, `TableQuestionTests`, `ListQuestionTests`, `OperationQuestionTests`, `ReproduceTests` and the CLI tests. The full solution and `bf validate descriptors --schemas schemas` pass (see the plan 2 ledger completion line).
