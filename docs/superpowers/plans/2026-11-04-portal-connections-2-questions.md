# Portal connections plan 2: SwaggerForge questions and the draft service

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** SwaggerForge stops drafting defaults. Every gap becomes a question with fixed choices, answers are applied, and a draft service turns OpenAPI text plus answers into a review: the proposed table, one row per endpoint with its status, the open questions, and the schema and descriptors once every ticked endpoint is ready.

**Architecture:** `DraftService.Review(text, options, answers)` in `tools/SwaggerForge` parses the pasted text without loading external references and runs the drafter with an `Answers` lookup. Wherever the drafter used a default or wrote a "review" note, it now asks a `Question`. An endpoint whose questions are all answered gets its descriptor, which is then validated. Anything open or invalid withholds that endpoint, and with it the whole result. The host Custom APIs that call this service come in plan 4. In this plan the caller is `bf draft --answers`.

**Tech Stack:** .NET 8, Microsoft.OpenApi.Readers 1.6.30, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-03-portal-connections-design.md` (sections 4, 5 screen 2, 6 steps 1–2). Plan 1, with the five-plan table: `docs/superpowers/plans/2026-11-03-portal-connections-1-config.md`.

## Global Constraints

- "BrokerForge guessing" is an anti-goal: "Every gap is a question the admin answers, or the endpoint stays out." No default may reach emitted YAML. That rules out string length 100, precision 2, page size 100, filter `eq`, the first string as primary name, `sortStable: false`, and the only collection taken as the table.
- "Fail loudly." A definition that cannot be read, an external `$ref`, or an answer that isn't one of the choices is reported, never worked round.
- "No secrets in Dataverse rows, descriptors, logs, traces, test fixtures or exception messages." Answers are not secrets. The service never echoes the definition text into problems; it names line and pointer only.
- "Ask before changing a contract in `BrokerForge.Contracts` or the descriptor schema." This plan changes neither. The design format keeps `answers` as string → string at format version 1; this plan documents the keys.
- The repo is public: fixtures use `contoso` and mock names only.
- `bf validate` and the contract tests still load `descriptors/` and `schemas/` from files, unchanged.

## Answer keys (the stored format, documented in task 5)

`<path>` is a JSONPath from the drafter, such as `$.reference` or `$.state.code`. `<op>` is the operation name: the operationId with a lower-case first letter.

| Key | Kind | Asked when | Choices / range |
| --- | --- | --- | --- |
| `table.collection` | Choice | more than one collection has an item path | each collection path |
| `table.name` | Text, optional | always (proposed `<prefix>_<singular collection>`) | `^<prefix>_[a-z0-9]+$`, at most 50 characters |
| `table.displayName`, `table.collectionName` | Text, optional | always (proposed humanised singular and plural) | 1–100 characters |
| `table.key` | Choice | the item has no scalar `id` | the item's top-level scalar paths |
| `table.primaryName` | Choice | no string property named `name` | the string columns' source paths |
| `column.<path>.name` | Text, optional | every column | as `table.name`; unique; not `<table>id` |
| `column.<path>.displayName` | Text, optional | every column | 1–100 characters |
| `column.<path>.maxLength` | Number | string column with no `maxLength` | 1–4000 |
| `column.<path>.precision` | Number | number column whose `multipleOf` is not 1, 0.1, 0.01 … 0.0000000001 | 0–10 |
| `op.<op>.use` | Choice, optional | every drafted endpoint | `yes` (default), `no` |
| `op.<op>.paging` | Choice | every list | the candidates the shape supports, from `cursor`, `offset`, `pageNumber` and `link`, plus `none` |
| `op.<op>.pageBase` | Choice | `pageNumber` chosen and `page` declares no `minimum` | `0`, `1` |
| `op.<op>.pageSizeMax` | Number | the chosen page size parameter has no `maximum` | 1–5000 |
| `op.<op>.filter.<param>` | Choice | every query parameter that matches a column, apart from paging and sort parameters | string: `none`, `eq`, `in`, `eq,in`, `like`; enum: `none`, `eq`, `in`, `eq,in`; other types: `none`, `eq` |
| `op.<op>.sort` | Choice | a query parameter named `sort`, `orderBy` or `order_by` | `none`, `asc`, `-{field}`, `{field} desc` |
| `op.<op>.sort.<path>` | Text, optional | `op.<op>.sort` is not `none`; once per column | the source's sort field; 1–100 characters, no whitespace |
| `op.<op>.sortStable` | Choice | every list | `yes`, `no` |
| `op.<op>.value.<param>` | Text | a path parameter that isn't the record ID, or a required query parameter that matches no column | 1–200 characters; sent as `{ static: … }` |
| `op.<op>.error.<status>` | Choice | a documented status other than 2xx, `default`, 403, 404, 409 or 422 | `NotFound`, `Conflict`, `Forbidden`, `Validation`, `Fail` |
| `op.<op>.error.<status>.message` | Choice | the status maps to `Validation` and its JSON schema has string paths (up to two levels) | each string path, `none` |

Kept as fixed mappings with no question, because the definition states them: 403 → Forbidden, 404 → NotFound, 409 → Conflict, 422 → Validation; `$.total` (integer) as the offset total; `page.minimum` as the page base; a declared `maxLength` or `maximum`; `multipleOf` as precision; the column types in descriptor spec 14; option set values from `100000000`.

## Review Focus

1. **Wrong kind of text pasted** (empty, HTML, a Postman collection, truncated YAML): the review has one problem naming what is wrong (line and pointer from the reader), no endpoints and no result. It never throws. Test: task 1 `Review_NotOpenApi_OneProblemNoResult`.
2. **External `$ref`** (`https://…`, `http://127.0.0.1:1/…`, `../secrets.yaml`): never fetched or read; a problem names the reference; no result. Test: task 1 `Review_ExternalRef_ProblemAndNothingFetched`.
3. **Stale answers after the definition changes** (a removed property, a filter choice the new type no longer allows): unknown keys go in `UnusedAnswers` and are never applied. An answer that isn't valid for the question reopens it with an `Error`, and the endpoint isn't Ready. Test: task 1 `Review_UnknownAnswer_ListedUnusedNotApplied`; task 3 `Review_FilterAnswerNotAllowedForType_QuestionReopenedWithError`.
4. **Column renames that collide** (two columns given one name, a name without the prefix, `<table>id`): the question gets an `Error`, no result, and nothing throws from the validator. Test: task 2 `Review_ColumnRenameCollision_ErrorOnBothNoResult`.
5. **Determinism**, which plan 3's "Read again" diff depends on: the same text and answers give byte-identical schema and descriptors and the same question order. Test: task 5 `Review_SameInput_SameOutputAndQuestionOrder`.

---

### Task 1: Review model, `DraftService`, answers lookup

**Files:**
- Create: `tools/SwaggerForge/Review.cs`, `tools/SwaggerForge/Answers.cs`, `tools/SwaggerForge/DraftService.cs`
- Modify: `tools/SwaggerForge/Drafter.cs` (the entry point takes `Answers` and returns `DraftReview`; per-operation status; `op.<op>.use`), `tools/SwaggerForge/DraftResult.cs` (`DraftOptions` loses `Table`)
- Test: `tests/SwaggerForge.Tests/DraftServiceTests.cs`

**Interfaces:**
- Produces, in namespace `BrokerForge.SwaggerForge`:
  - `public sealed record DraftOptions(string ServiceInstance, string Prefix)`
  - `public enum QuestionKind { Choice, Number, Text }`
  - `public sealed record Choice(string Value, string Label)`
  - `public sealed record Question(string Key, string Text, QuestionKind Kind, IReadOnlyList<Choice> Choices, bool Required, string? Proposed, string? Answer, string? Error)`. `Answer` is the answer used (null when unanswered or invalid); `Error` says why a given answer was refused.
  - `public enum EndpointStatus { Ready, NeedsAnswers, NotUsed, Unsupported, Invalid }`
  - `public sealed record EndpointReview(string Operation, string Method, string Path, string Becomes, EndpointStatus Status, string? Reason, IReadOnlyList<Question> Questions, IReadOnlyList<string> Problems)`. `Becomes` is one of `List`, `Open`, `New`, `Save`, `Delete` or `Action: <display name>`. `Reason` is the skip reason for Unsupported. `Problems` holds validator errors for Invalid.
  - `public sealed record ColumnProposal(string SourcePath, string LogicalName, string DisplayName, string Type)`
  - `public sealed record TableProposal(string Collection, string LogicalName, string DisplayName, string CollectionName, string? KeyPath, string? PrimaryName, IReadOnlyList<ColumnProposal> Columns, IReadOnlyList<Question> Questions)`
  - `public sealed record DraftReview(TableProposal? Table, IReadOnlyList<EndpointReview> Endpoints, IReadOnlyList<string> Problems, IReadOnlyList<string> UnusedAnswers, DraftResult? Result) { public bool Ready => Result != null; }`
  - `public static class DraftService { public const int MaxDefinitionLength = 2_000_000; public static DraftReview Review(string definition, DraftOptions options, IReadOnlyDictionary<string, string> answers); }`
  - `internal sealed class Answers`: `Answers(IReadOnlyDictionary<string,string>)`, `Question Choice(string key, string text, IReadOnlyList<Choice> choices, bool required = true, string? proposed = null)`, `Question Number(string key, string text, int min, int max)`, `Question Text(string key, string text, bool required, string? proposed, Func<string, string?> check)`, `IReadOnlyList<string> Unused()`. Each call records the key as asked and returns the question with `Answer`/`Error` filled. `check` returns an error message or null.
- `DraftResult` stays as it is: schema, descriptors, skipped, notes. Notes now carry only information ("property X is not drafted"), never "review".

- [ ] **Step 1: Write the failing tests** in `DraftServiceTests`:
  - `Review_NotOpenApi_OneProblemNoResult`: for each of `""`, `"<html><body>hi</body></html>"` and `"openapi: 3.0.3\npaths:\n  /x: [unclosed"`, `Review` returns `Problems.Count == 1`, `Endpoints` empty, `Result` null.
  - `Review_TooLong_Problem`: text of `MaxDefinitionLength + 1` characters gives a problem containing `"2000000"` and no endpoints.
  - `Review_ExternalRef_ProblemAndNothingFetched`: a copy of `orders-openapi.yaml` whose `Order` schema is `$ref: "http://127.0.0.1:1/order.yaml"`, and a second copy with `$ref: "../secrets.yaml"`. Each gives a problem containing the reference text and `Result == null`. The test sets up an `HttpListener` on a free loopback port, uses that URL in a third copy, and asserts the listener received no request.
  - `Review_OperationUnticked_NotUsedAndLeftOut`: answers `{ "op.deleteOrder.use": "no" }` plus the Orders answers (task 5 adds the full set; for now, the subset this task's drafter needs) make `deleteOrder` `NotUsed`, and when the result is ready it has no `deleteOrder` descriptor.
  - `Review_UnknownAnswer_ListedUnusedNotApplied`: `{ "column.$.nothere.name": "ext_x" }` appears in `UnusedAnswers`, and no column is named `ext_x`.
  - `Review_SkippedOperation_Unsupported`: `unsupported.yaml` yields `Unsupported` endpoints whose `Reason` matches today's skip reasons (`oneOf`, `/widgets/{widgetId}/variant`).
  - `Review_NoEndpointTicked_NotReady`: every op `use: no` gives `Result == null`.
- [ ] **Step 2: Run** `dotnet test tests/SwaggerForge.Tests --filter FullyQualifiedName~DraftServiceTests`. Expected: build fails because `DraftService` doesn't exist.
- [ ] **Step 3: Implement.** Read with `new OpenApiStringReader(new OpenApiReaderSettings { LoadExternalRefs = false, ReferenceResolution = ReferenceResolutionSetting.ResolveLocalReferences })`. Treat any diagnostic error as a problem. Treat any `Reference.IsExternal` found by an `OpenApiWalker` visitor over schemas, parameters, responses and request bodies as a problem `"external reference <ref> is not read; paste one self-contained definition"`. A document with no paths is the problem `"no paths: this is not an OpenAPI definition"`. `Drafter` becomes `internal static DraftReview Draft(OpenApiDocument doc, DraftOptions o, Answers answers)`. `Result` is non-null only when there are no problems, at least one endpoint is used, and every used endpoint is `Ready`. Update `DrafterTests` and `CliDraftTests` to compile: until tasks 2–4 replace the defaults, they pass the answers they need.
- [ ] **Step 4: Run** the SwaggerForge tests. Expected: PASS.
- [ ] **Step 5: Commit** `feat(swaggerforge): draft service with review, answers and endpoint status`.

### Task 2: Table questions

**Files:**
- Modify: `tools/SwaggerForge/Drafter.cs` (`FindResource`, `Fields`, `SchemaYaml`), `tools/SwaggerForge/Heuristics.cs` (remove `DefaultMaxLength`, `DefaultPrecision`; add `PrecisionOf(OpenApiSchema) -> int?` from `multipleOf`)
- Test: `tests/SwaggerForge.Tests/TableQuestionTests.cs`

**Interfaces:**
- Consumes: `Answers`, `Question`, `TableProposal`, `ColumnProposal` (task 1).
- Produces: column logical names after renames. Tasks 3 and 4 refer to columns by `Field.SourcePath`, never by logical name.

- [ ] **Step 1: Failing tests:**
  - `Review_TwoCollections_AsksWhichAndProposesNothingUntilAnswered`: a definition with `/widgets/{id}` and `/gadgets/{id}` asks `table.collection` with choices `/gadgets` and `/widgets` (ordinal order). Table-bound endpoints are `NeedsAnswers`. Answering `/widgets` proposes `ext_widget`.
  - `Review_StringWithoutMaxLength_Asked`: Orders' `$.customer.ref` has `maxLength: 100`, so it isn't asked. A copy with the `maxLength` removed asks `column.$.customer.ref.maxLength`. Answering `"0"` or `"4001"` sets `Error`; answering `"60"` gives schema `maxLength: 60`.
  - `Review_NumberPrecision_AskedUnlessMultipleOf`: Orders asks `column.$.totals.gross.precision`. With `multipleOf: 0.01` added it isn't asked and the schema has precision 2.
  - `Review_PrimaryName`: Orders has no `name` property, so it asks `table.primaryName` with every string column's path as a choice, and `$.reference` makes the schema `primaryName` that column. Inventory (`tools/mock-api/inventory-openapi.yaml`, copied into the test output as a fixture) isn't asked.
  - `Review_NoScalarId_AsksKey`: an item whose key property is `code` asks `table.key` with the top-level scalar paths. `$.code` gives `key: $.code`, and `code` doesn't become a column.
  - `Review_ColumnRename_AppliedEverywhere`: `column.$.reference.name = ext_name` with `column.$.reference.displayName = Reference` puts `ext_name` in the schema, in every map, and in the create and update templates.
  - `Review_ColumnRenameCollision_ErrorOnBothNoResult`: giving two paths the same name sets `Error` on both, and so does `order_ref` (no prefix) or `ext_orderid`. `Result` is null and nothing throws.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~TableQuestionTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** One candidate collection is taken without a question; several ask. Propose table names from the collection, through `Singular` and `Heuristics.Humanise`. Unanswered required table questions mark every table-bound endpoint `NeedsAnswers`. Their questions are on `TableProposal.Questions`, not repeated on the endpoints. An unanswered value never reaches YAML: build with the answer, or withhold the descriptor.
- [ ] **Step 4: Run** the SwaggerForge tests. Expected: PASS.
- [ ] **Step 5: Commit** `feat(swaggerforge): table, key, primary name and column questions`.

### Task 3: List questions: paging, filters, sort, stable order

**Files:**
- Modify: `tools/SwaggerForge/Drafter.cs` (`List`, `Paging`, `FilterColumn`); remove `Heuristics.DefaultPageSizeMax`
- Test: `tests/SwaggerForge.Tests/ListQuestionTests.cs`

**Interfaces:**
- Consumes: task 1 and 2 types. Columns are found by source path.

- [ ] **Step 1: Failing tests:**
  - `Review_Paging_AlwaysAskedWithCandidates`: Orders' `op.listOrders.paging` has choices `cursor` (label names `limit`, `cursor` and `$.meta.nextCursor`) and `none`, with no answer by default. `cursor` gives today's `token: { param: cursor, next: $.meta.nextCursor }` with `pageSize.max: 100`.
  - `Review_PagingNotStated_QuestionAndNotReady` (spec done criterion 3): a list with no paging parameters asks `op.<op>.paging` with only `none`. Until it's answered the endpoint is `NeedsAnswers` and `Result` is null. `none` gives `strategy: none`.
  - `Review_PageNumberBase_AskedOnlyWithoutMinimum` and `Review_PageSizeMax_AskedOnlyWithoutMaximum`.
  - `Review_Filters_AskedPerParameter`: Orders asks `op.listOrders.filter.status` (enum choices `none`, `eq`, `in`, `eq,in`) and `op.listOrders.filter.customer` (string choices). Answering `eq,in` and `eq` gives `status: { filter: ext_status, ops: [eq, in], format: csv }` and `customer: { filter: <customer.ref column>, ops: [eq] }`, plus `filters: { allowOr: true, orStyle: in }`. `none` declares no filter.
  - `Review_FilterAnswerNotAllowedForType_QuestionReopenedWithError`: `like` on the enum `status` sets `Error` and leaves the endpoint `NeedsAnswers`.
  - `Review_Sort`: Orders asks `op.listOrders.sort`. `-{field}` together with `op.listOrders.sort.$.reference = reference` and `op.listOrders.sort.$.placedAt = placedAt` gives `sort: { sort: { <reference col>: reference, <placedAt col>: placedAt }, descFormat: "-{field}" }`. `asc` gives no `descFormat`. A sort that's used but names no column is an `Error` on `op.listOrders.sort`.
  - `Review_SortStable_Asked`: no answer means `NeedsAnswers`, and `yes` gives `sortStable: true`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~ListQuestionTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Paging candidates use today's detection rules, but only as choices. `link` is offered when the 200 response declares a `Link` header. A missing parameter for the chosen strategy can't happen, because only supported strategies are offered.
- [ ] **Step 4: Run** the SwaggerForge tests. Expected: PASS.
- [ ] **Step 5: Commit** `feat(swaggerforge): paging, filter, sort and stable-order questions`.

### Task 4: Values, errors and validator problems

**Files:**
- Modify: `tools/SwaggerForge/Drafter.cs` (`CheckPathParams`, `Errors`, `CustomApi`, `Write`, `Verify`)
- Test: `tests/SwaggerForge.Tests/OperationQuestionTests.cs`

**Interfaces:**
- Consumes: earlier tasks. `Verify` stops throwing. A validator error becomes `EndpointStatus.Invalid` with the errors in `Problems`, and a schema error goes into `DraftReview.Problems`.

- [ ] **Step 1: Failing tests:**
  - `Review_NonIdPathParameter_AsksFixedValue`: Orders' `op.createOrder.value.accountId`. `acc-1` gives `pathParams: { accountId: { static: acc-1 } }`, and no descriptor contains an `instance:` binding.
  - `Review_RequiredQueryParameterMatchingNoColumn_AsksFixedValue`: it is sent as `{ static: … }`.
  - `Review_DocumentedStatus_AskedUnlessFixed`: adding a documented `400` to `createOrder` asks `op.createOrder.error.400`, while 404 and 422 aren't asked. `Validation` gives `400: Validation`.
  - `Review_ValidationMessage_AskedWithStringPaths`: `op.createOrder.error.422.message` has choices `$.error.detail` and `none`. `$.error.detail` gives `422: { exception: Validation, message: $.error.detail }`; `none` gives `422: Validation`.
  - `Review_CustomApiErrors_SameQuestions`: Inventory's `adjustStock` asks `op.adjustStock.error.422.message`.
  - `Review_ReservedParameter_NotAskedSentByAuth`: `DraftOptions` gains `IReadOnlyList<string>? ReservedNames = null`, the header and query names the connection's authentication sends. A required query parameter `apiKey` with `ReservedNames = ["apiKey"]` is not asked or declared, and a note says the authentication sends it. Drafts are validated with `ReservedNames.For(options.ReservedNames ?? [])`.
  - `Review_ValidatorError_EndpointInvalidNotThrown`: the internal `Drafter.Check(string schemaYaml, IReadOnlyDictionary<string,string> descriptors, DraftOptions o)`, which returns `IReadOnlyDictionary<string, IReadOnlyList<string>>`, gets a hand-written descriptor that maps a column missing from the schema. It returns that operation's validator errors and doesn't throw. The review marks such an endpoint `Invalid` with the errors in `Problems`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~OperationQuestionTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the SwaggerForge tests. Expected: PASS.
- [ ] **Step 5: Commit** `feat(swaggerforge): fixed values, error mappings and validator problems`.

### Task 5: Reproduce today's sources, `bf draft --answers`, docs

**Files:**
- Create: `tests/SwaggerForge.Tests/fixtures/orders-answers.json`, `tests/SwaggerForge.Tests/fixtures/inventory-answers.json`, `tests/SwaggerForge.Tests/ReproduceTests.cs`, `docs/findings/portal-2.md`
- Modify: `tools/BrokerForge.Cli/DraftCommand.cs`, `tests/SwaggerForge.Tests/CliDraftTests.cs`, `tests/SwaggerForge.Tests/SwaggerForge.Tests.csproj` (copy `tools/mock-api/inventory-openapi.yaml` into `fixtures/`), `docs/connection-design-format.md` (answer keys section, from the table above), `docs/hld.md` (D-35: every gap is a question, so SwaggerForge drafts no defaults; `bf draft` needs answers)

**Interfaces:**
- `bf draft <openapi> --instance <name> --prefix <p> --out <dir> [--answers <json>] [--root <dir>]`. `--table` goes; its replacement is the answer `table.name`. When the review isn't ready, it writes `<out>/<instance>-questions.json` (an object of every open question's key → `""`, plus `"_about"` lines with each question's text and choices). It also writes the draft report with endpoint statuses and problems, writes no schema or descriptors, and returns 3. When the review is ready, it behaves as today (writes, refuses to overwrite, returns 0). Exit 1 means problems.

- [ ] **Step 1: Failing tests:**
  - `Reproduce_Inventory_ExactlyTheCommittedDescriptors`: `inventory-answers.json` (paging `offset`, filters `state`/`name` `eq`, `sortStable` yes, `price` precision 2, `sku` display name as in `schemas/InventoryAPI.yaml`, the 422 messages `$.error.detail`) gives descriptors whose parsed YAML trees equal `descriptors/InventoryAPI/*.yaml`, file for file. Compare canonical JSON from YamlDotNet's representation model, so quoting and flow style don't matter. The schema equals `schemas/InventoryAPI.yaml` the same way.
  - `Reproduce_Orders_ListGetDeleteExactly`: `orders-answers.json` gives `listOrders`, `getOrder` and `deleteOrder` trees equal to `descriptors/OrdersAPI/`. For `createOrder` and `updateOrder`, the `body.template` column bindings, the `optionsets` and the `errors` present in the fixture are equal. The fixture has no headers, `placedBy` or `retry`, and plan 5 decides those.
  - `Review_SameInput_SameOutputAndQuestionOrder`: two `Review` calls give byte-identical `SchemaYaml` and descriptors, and identical question key sequences on an unanswered review.
  - `Cli_Draft_WithoutAnswers_WritesQuestionsAndExit3`, and `Cli_Draft_WithAnswers_WritesAndValidates`, which is the existing write test rewritten to run `ValidateCommand` on the output.
- [ ] **Step 2: Run** `dotnet test tests/SwaggerForge.Tests`. Expected: FAIL. If an Inventory difference can't be expressed by any answer key, that's a plan defect: rule on it in the ledger before going on.
- [ ] **Step 3: Implement and write the docs.** `portal-2.md` records the question set, the reproduction results, and what Orders still lacks for plan 5 (headers, `placedBy`, `retry`).
- [ ] **Step 4: Run** `dotnet test BrokerForge.slnx` and `dotnet run --project tools/BrokerForge.Cli -- validate descriptors --schemas schemas`. Expected: both exit 0.
- [ ] **Step 5: Commit** `feat(cli): bf draft takes answers; docs: answer keys, D-35, portal-2 findings`.
