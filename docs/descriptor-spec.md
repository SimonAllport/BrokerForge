# BrokerForge — Operation Descriptor Specification

Version 0.1 · Source of truth for `BrokerForge.Descriptors` (parser, validator, renderer, response mapper).

An operation descriptor describes one call to a source: how to build the request from Dataverse inputs, and how to turn the response back into Dataverse rows, records or outputs. One descriptor per file, under `descriptors/<ServiceInstance>/<operation>.yaml`.

## 1. Top-level structure

```yaml
operation: listOrders            # required, unique within the service instance, camelCase
version: 1                       # required, integer, bumped on any change
binds: RetrieveMultiple          # required, see 2
table: ext_order                 # required for virtual table messages; omitted for Custom APIs
request:   { ... }               # required, see 4
response:  { ... }               # required except where the message returns nothing, see 6
paging:    { ... }               # required for RetrieveMultiple, see 7
errors:    { ... }               # optional, see 8
steps:     [ ... ]               # optional, see 9
hook:      { ... }               # optional, see 10
cache:     { ... }               # optional, see 11
retry:     { ... }               # optional, see 11
```

Unknown keys anywhere are a validation error, not ignored.

## 2. `binds`

| Value | Inputs available | Must return |
| --- | --- | --- |
| `RetrieveMultiple` | query columns, filter values, sort, page | `response.rows`, `response.key` |
| `Retrieve` | `id` (decoded external key) | `response.rows` (single), `response.key` |
| `Create` | record columns | `response.key` (new external key) |
| `Update` | `id`, changed record columns only | nothing |
| `Delete` | `id` | nothing |
| `customapi:<UniqueName>` | Custom API input parameters | `response.outputs` |

## 3. Value bindings

Every value in path, query, headers or body is either a literal (YAML scalar) or a binding object with exactly one key from this table. No other sources exist.

| Binding | Example | Resolves to | Allowed in |
| --- | --- | --- | --- |
| `static` | `{ static: "2024-06" }` | The literal value | all |
| `instance` | `{ instance: tenantId }` | Key from `bf_serviceinstance` instance config | all |
| `secret` | `{ secret: partnerKey }` | Named Key Vault reference on the service instance, resolved in the host | headers, query only |
| `context` | `{ context: userObjectId }` | `userObjectId`, `correlationId`, `environmentId`, `async` | all |
| `col` | `{ col: ext_status }` | Record column value | Create, Update |
| `id` | `{ id: true }` or `{ id: orderId }` | Decoded external key, or a named part of a composite key | Retrieve, Update, Delete, Custom API with bound id |
| `filter` | `{ filter: ext_status, ops: [eq, in] }` | Value from the query filter on that column | RetrieveMultiple query and path |
| `sort` | `{ sort: { ext_name: name, createdon: created } }` | Source sort field for the requested column | RetrieveMultiple query |
| `param` | `{ param: Reason }` | Custom API input parameter | Custom API |
| `computed` | `{ computed: uuid }` | `uuid`, `utcnow` (ISO 8601) | all |
| `paging` | `{ paging: pageSize, max: 100 }` | `pageSize`, `token`, `offset`, `pageNumber` | RetrieveMultiple query |

Binding modifiers (optional, on the same object):

- `required: true` — missing value is `UnsupportedQueryException` (for filters) or a validation error at call time (for others). Default: required for `id`, `col` on Create where the column is required, `param` where the Custom API parameter is required.
- `omitIfNull: true` — drop the query parameter or header when the value is null. Default true for query and headers.
- `format` — `iso8601`, `date`, `epochSeconds`, `epochMillis`, `lower`, `upper`, `csv` (for `in` arrays).

`secret` is never allowed in body or path, so secrets cannot be echoed back by a source that reflects input.

### Path templates

Path parameters use `{name}` placeholders and a `pathParams` map:

```yaml
request:
  method: GET
  path: /v2/accounts/{accountId}/orders/{orderId}
  pathParams:
    accountId: { instance: accountId }
    orderId:   { id: true }
```

Every placeholder must have a `pathParams` entry and every entry must be used. Values are URL-encoded.

## 4. `request`

```yaml
request:
  method: POST                   # GET, POST, PUT, PATCH, DELETE
  path: /v2/accounts/{accountId}/orders
  pathParams: { accountId: { instance: accountId } }
  query:
    dryRun: { static: false }
  headers:
    X-Api-Version:   { static: "2024-06" }
    X-Correlation:   { context: correlationId }
    Idempotency-Key: { computed: uuid }
  body:
    contentType: application/json     # application/json (default), application/x-www-form-urlencoded, text/xml
    template:                         # structured template; leaves are literals or bindings
      customer: { col: ext_customerref }
      lines:    { col: ext_lines_json, format: json }
      placedBy: { context: userObjectId }
```

Rules:

- `Authorization` and any header the service instance's auth mode sets are reserved. Declaring them is a validation error.
- GET and DELETE must not have a body.
- `body.template` is a YAML structure mirrored into JSON. A leaf with `format: json` parses a JSON string column into a nested value; invalid JSON at call time is `MappingException`.
- `body.raw` (alternative to `template`) is a string with `{{ binding }}` placeholders for XML and other formats. Placeholders use the same binding names: `{{ col.ext_status }}`, `{{ context.correlationId }}`. Values are XML-escaped when `contentType` is `text/xml`.
- On Update, only changed columns are bound. A `col` binding for an unchanged column is omitted unless `alwaysSend: true`.

## 5. Filters

Filters in a RetrieveMultiple query are honoured only if a `filter` binding declares the column and operator.

| Operator | Dataverse condition | Typical source form |
| --- | --- | --- |
| `eq` | Equal | `?status=open` |
| `ne` | NotEqual | `?status_ne=open` |
| `gt`, `ge`, `lt`, `le` | GreaterThan … LessEqual | `?created_from=` |
| `in` | In | `?status=open,closed` (`format: csv`) |
| `like` | Like (begins with only) | `?q=` |
| `null` | Null / NotNull | `?hasCustomer=false` |

Rules:

- A query condition on an undeclared column or operator raises `UnsupportedQueryException` naming both. The host never filters client-side.
- `or` groups are rejected unless the descriptor declares `filters.allowOr: true` and the source supports it via `filters.orStyle` (only `in`-collapsible ORs on one column are supported in v1).
- Link-entity (joined) conditions are rejected in v1.
- Sorting on an undeclared column raises `UnsupportedQueryException`. A query with no sort uses the source's default order; the descriptor must declare `sortStable: true|false` so paging behaviour is known.

## 6. `response`

```yaml
response:
  rows: $.data[*]                # JSONPath to the row array (RetrieveMultiple) or single object (Retrieve)
  key: $.id                      # JSONPath to the external key, or a map for composite keys
  map:                           # column logical name -> JSONPath (or transform)
    ext_name:   $.reference
    ext_status: $.state.code
    ext_total:  { path: $.totals.gross, type: decimal }
    ext_placed: { path: $.placedAt, type: datetime }
    ext_label:  { jsonata: "$uppercase(state.code) & ' / ' & reference" }
  outputs:                       # Custom API only: output parameter -> JSONPath
    SubmittedAt: $.submittedAt
```

Rules:

- `rows` matching nothing returns an empty set only when the path exists and is an empty array. A missing path is `MappingException`.
- Every column in the table's generated schema must appear in `map` or be explicitly listed under `response.unmapped: [ ... ]`. Silent gaps are a validation error.
- `type` coerces: `string`, `int`, `decimal`, `bool`, `datetime`, `date`, `guid`, `optionset:<mapName>`. Coercion failure is `MappingException` naming column and path.
- Composite keys: `key: { accountId: $.accountId, orderId: $.id }`. The object map's key strategy must be `keymap` or reversible if it fits.
- Create must return a key. If the source returns 201 with a `Location` header only, use `key: { header: Location, pattern: "/orders/(?<id>[^/]+)$" }`.

### Option set maps

```yaml
optionsets:
  orderStatus:
    open: 100000000
    shipped: 100000001
    cancelled: 100000002
    _unknown: error            # error (default) | null
```

An unmapped source value is `MappingException` unless `_unknown: null`.

## 7. `paging` (RetrieveMultiple)

```yaml
paging:
  strategy: cursor               # cursor | offset | pageNumber | link | none
  pageSize: { param: limit, max: 100 }
  token:    { param: cursor, next: $.meta.nextCursor }
```

| Strategy | Request | Next page detection |
| --- | --- | --- |
| `cursor` | `token.param` = previous `next` | `token.next` JSONPath non-null |
| `offset` | `offset.param` = page × size | returned row count = page size, or `total` path |
| `pageNumber` | `page.param` = page index (base declared by `page.base: 0|1`) | as offset |
| `link` | URL from previous response | `Link` header `rel="next"` |
| `none` | one call | never; requesting page 2 is `UnsupportedQueryException` |

The host translates Dataverse paging cookies to and from the source token. If the source page size is smaller than Dataverse requested, the host makes further calls to fill the page, up to `paging.maxCallsPerPage` (default 5); exceeding it is `SourceException`, never a short page presented as complete.

## 8. `errors`

```yaml
errors:
  404: NotFound
  409: Conflict
  422: { exception: Validation, message: $.error.detail }
  default: Fail
```

| Value | Raised as |
| --- | --- |
| `NotFound` | Dataverse record-not-found |
| `Conflict` | Dataverse duplicate/concurrency error |
| `Forbidden` | Dataverse privilege error |
| `Validation` | Dataverse invalid-argument error, message from the JSONPath if given (redacted, max 500 chars) |
| `Fail` | `SourceException` with status and correlation ID |

`default` must be `Fail`. Any status not listed is `SourceException`. 2xx responses are success unless `errors.successBodyCheck` declares a JSONPath and expected value for APIs that return errors inside 200.

## 9. `steps` (multi-call operations)

For a lookup before the main call, or async 202 polling. Maximum 3 steps in v1.

```yaml
steps:
  - name: resolveAccount
    request:
      method: GET
      path: /v2/accounts
      query: { ref: { col: ext_accountref } }
    capture:
      accountId: $.data[0].id          # missing capture is MappingException
  - name: main                         # the request block of the descriptor, with step captures available as { step: resolveAccount.accountId }
  - name: poll
    when: { status: 202 }
    request:
      method: GET
      path: "{location}"
      pathFrom: { header: Location }
    until: { path: $.status, equals: complete }
    every: 2s
    timeout: 60s                       # must leave headroom inside the 90s host limit
```

## 10. `hook` (escape hatch)

```yaml
hook:
  pre: BrokerForge.Hooks.Orders.SignRequest       # IPreRequestHook implementation
  post: BrokerForge.Hooks.Orders.FlattenLines     # IPostResponseHook implementation
  reason: "Partner API needs a body hash header computed over the canonical JSON"
```

`reason` is required. Hooks receive the rendered request or raw response and may not read secrets directly; they use the same `IAuthenticatedChannel`.

## 11. `cache` and `retry`

```yaml
cache:
  ttl: 60s                 # off when absent
  perUser: true            # forced true when the service instance uses a per-user mode
retry:
  idempotent: true         # required true for automatic retry; Create needs an Idempotency-Key binding
  attempts: 3
  on: [429, 503, network]
```

## 12. Validation rules summary

The validator rejects a descriptor when any of these hold:

1. Unknown key, or a binding object with zero or more than one source key.
2. A binding source not allowed for the field (table in 3) or for the message (`binds`).
3. A path placeholder without a `pathParams` entry, or an unused entry.
4. A reserved header declared.
5. GET or DELETE with a body.
6. RetrieveMultiple without `paging`, or Retrieve/RetrieveMultiple/Create without `response.key`.
7. A table column neither mapped nor listed in `response.unmapped`.
8. `errors.default` other than `Fail`.
9. `retry` without `idempotent: true`, or Create retry without an idempotency key binding.
10. A `hook` without `reason`.
11. `steps` over 3, or a poll timeout leaving under 20 seconds of the 90-second host limit.
12. `secret` used in path or body.

## 13. Worked examples (Orders mock API)

### List

```yaml
operation: listOrders
version: 1
binds: RetrieveMultiple
table: ext_order
request:
  method: GET
  path: /v2/orders
  query:
    status:   { filter: ext_status, ops: [eq, in], format: csv }
    customer: { filter: ext_customerref, ops: [eq] }
    sort:     { sort: { ext_name: reference, ext_placed: placedAt } }
paging:
  strategy: cursor
  pageSize: { param: limit, max: 100 }
  token:    { param: cursor, next: $.meta.nextCursor }
response:
  rows: $.data[*]
  key: $.id
  map:
    ext_name:        $.reference
    ext_status:      { path: $.state.code, type: optionset:orderStatus }
    ext_customerref: $.customer.ref
    ext_placed:      { path: $.placedAt, type: datetime }
optionsets:
  orderStatus: { open: 100000000, shipped: 100000001, cancelled: 100000002 }
errors:
  default: Fail
sortStable: true
```

### Retrieve

```yaml
operation: getOrder
version: 1
binds: Retrieve
table: ext_order
request:
  method: GET
  path: /v2/orders/{orderId}
  pathParams: { orderId: { id: true } }
response:
  rows: $
  key: $.id
  map:
    ext_name:        $.reference
    ext_status:      { path: $.state.code, type: optionset:orderStatus }
    ext_customerref: $.customer.ref
    ext_placed:      { path: $.placedAt, type: datetime }
optionsets:
  orderStatus: { open: 100000000, shipped: 100000001, cancelled: 100000002 }
errors:
  404: NotFound
  default: Fail
```

### Create

```yaml
operation: createOrder
version: 1
binds: Create
table: ext_order
request:
  method: POST
  path: /v2/accounts/{accountId}/orders
  pathParams: { accountId: { instance: accountId } }
  headers:
    X-Correlation:   { context: correlationId }
    Idempotency-Key: { computed: uuid }
  body:
    template:
      customer: { ref: { col: ext_customerref } }
      lines:    { col: ext_lines_json, format: json }
      placedBy: { context: userObjectId }
response:
  key: $.id
  unmapped: [ext_name, ext_status, ext_placed]
errors:
  409: Conflict
  422: { exception: Validation, message: $.error.detail }
  default: Fail
retry:
  idempotent: true
  attempts: 3
  on: [429, 503, network]
```

### Custom API

```yaml
operation: submitOrder
version: 1
binds: customapi:ext_SubmitOrder
request:
  method: POST
  path: /v2/orders/{orderId}/submit
  pathParams: { orderId: { param: OrderId } }
  headers:
    X-Reason: { param: Reason, omitIfNull: true }
  body:
    template:
      notify: { param: NotifyCustomer }
response:
  outputs:
    SubmittedAt: { path: $.submittedAt, type: datetime }
    Status:      $.state.code
errors:
  404: NotFound
  409: Conflict
  default: Fail
```

The Custom API `ext_SubmitOrder` is generated with inputs `OrderId` (string, required), `Reason` (string, optional), `NotifyCustomer` (bool, required) and outputs `SubmittedAt` (datetime), `Status` (string).
