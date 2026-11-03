# Connection design format

A connection design is one JSON document per service instance. It holds everything the host needs to build an instance except secrets and per-environment values. DataverseToCode reads the same documents from unpacked solutions, so the format is stable: **format version 1; any breaking change bumps it.**

Code: `src/BrokerForge.Connections` (`ConnectionDesign`, `DesignSerializer`, `DesignCarrier`).

## Carrier

The design is stored in a Dataverse web resource of type 4 (XML), named `<prefix>_/brokerforge/<Instance>/design.xml` (for example `bf_/brokerforge/OrdersAPI/design.xml`). The body is:

```xml
<brokerforgeDesign formatVersion="1"><![CDATA[ ...design JSON... ]]></brokerforgeDesign>
```

`DesignCarrier.Wrap` and `Unwrap` are strict: the root must be `brokerforgeDesign` with exactly the attribute `formatVersion="1"` and exactly one CDATA section. Any other child node, attribute or value throws `DesignFormatException` naming the source. The reasoning is in `docs/findings/portal-carrier.md`.

### The `]]>` rule

CDATA ends at the first `]]>`, and OpenAPI or YAML text can contain it. `DesignSerializer.Serialize` therefore replaces every `]]>` in the JSON with `]]>`. That sequence can only occur inside a JSON string, and it decodes to the same text, so the text survives Serialize, Wrap, Unwrap and Parse unchanged. `Wrap` refuses JSON that still contains `]]>`. The serializer otherwise uses relaxed escaping, so `<`, `>`, `&` and quotes in YAML stay readable.

## Serialization rules

- camelCase keys, indented with two spaces, `\n` line endings, null values omitted.
- Object keys in the declaration order below; dictionary keys sorted ordinally. Re-saving an unchanged design produces no diff.
- `Parse(json, source)` throws `DesignFormatException` naming `source` and the JSON path for an unknown key, a missing required key, a wrong type, a `formatVersion` other than 1, and any property under `auth` named `secret`, `password`, `clientSecret`, `apiKeyValue` or `value` (the message never includes the value).

## Keys

Root:

| Key | Type | Required | Notes |
| --- | --- | --- | --- |
| `formatVersion` | integer | yes | Always 1. |
| `instance` | string | yes | Service instance name, for example `OrdersAPI`. |
| `version` | integer | yes | Design revision, for the portal's own use. |
| `brokerType` | string | yes | Adapter type, for example `rest`. |
| `openApi` | string | yes | OpenAPI document text (YAML or JSON). |
| `answers` | object, string to string | yes | The wizard answers used to draft descriptors. |
| `auth` | object | yes | See below. |
| `objectMap` | object | no | Table and operation mapping. |
| `schema` | string | yes | Table schema YAML text (descriptor spec 14). |
| `descriptors` | object, operation name to string | yes | One YAML text per operation. |

`auth` (shape only: no values, no secret references):

| Key | Type | Required | Notes |
| --- | --- | --- | --- |
| `mode` | string | yes | Authentication mode. |
| `staticKind` | string | no | Static mode: how the secret is sent. |
| `headerName` | string | no | Static header name. |
| `queryParam` | string | no | Static query parameter name. |
| `username` | string | no | Basic-style user name (not a secret). |
| `scope` | string | no | Service-account scope. |
| `ssoKind` | string | no | SSO mode kind. |
| `asyncContext` | string | no | Async context rule. |
| `oauth` | object | no | See below. |
| `secretNames` | array of string | yes | Names of the instance secrets the design needs, for example `["apiKey"]`. |

`auth.oauth`:

| Key | Type | Required | Notes |
| --- | --- | --- | --- |
| `authorizeUrl` | string | yes | |
| `tokenUrl` | string | yes | |
| `scopes` | array of string | yes | |
| `clientId` | string | no | |
| `usesConnectApp` | boolean | yes | True when the host's connect app is the OAuth client. |

`objectMap`:

| Key | Type | Required | Notes |
| --- | --- | --- | --- |
| `table` | string | yes | Logical table name. |
| `keyStrategy` | string | yes | Key strategy name. |
| `keyKind` | string | yes | Key kind. |
| `operations` | object, string to string | yes | Table message to operation name. |

## Per-environment values

No key in the design is per-environment. Those values are `EnvironmentSettings(BaseUrl, AuthOverrides)` and are supplied as Dataverse environment variables:

- `bf_<Instance>_BaseUrl`: the source base URL.
- `bf_<Instance>_Auth`: JSON object of `AuthOverrides` (string to string).

Secret values are never part of either; they stay in Key Vault or the host's encrypted store.
