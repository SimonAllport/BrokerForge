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

CDATA ends at the first `]]>`, and OpenAPI or YAML text can contain it. `DesignSerializer.Serialize` therefore writes the `>` of every `]]>` as the JSON escape for U+003E: the two characters `]]` followed by the six characters backslash, `u`, `0`, `0`, `3`, `E` (in C# source `"]]\\u003E"`). That sequence can only occur inside a JSON string. `Parse` accepts it because any JSON parser decodes the escape back to `>`, so the text survives Serialize, Wrap, Unwrap and Parse unchanged. `Wrap` refuses JSON that still contains a raw `]]>`. The serializer otherwise uses relaxed escaping, so `<`, `>`, `&` and quotes in YAML stay readable.

## Serialization rules

- camelCase keys, indented with two spaces, `\n` line endings, null values omitted.
- Object keys in the declaration order below; dictionary keys sorted ordinally. Re-saving an unchanged design produces no diff.
- `Parse(json, source)` throws `DesignFormatException` naming `source` and the JSON path for an unknown key, a missing required key, a wrong type, a `formatVersion` other than 1, a duplicate key in any object or dictionary, and any property under `auth` named `secret`, `password`, `clientSecret`, `apiKeyValue` or `value` (the message never includes the value).

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
| `mode` | string | yes | Authentication mode: exactly `ServiceAccount`, `Static`, `Impersonate`, `Sso` or `OAuth` (case-sensitive, no spaces, no numbers). |
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
| `authorizeUrl` | string | no | The source's own authorize endpoint, https. Must be absent when `usesConnectApp` is true: the host then uses `https://login.microsoftonline.com/<Trust:TenantId>/oauth2/v2.0/authorize`. |
| `tokenUrl` | string | no | The source's own token endpoint, https. Must be absent when `usesConnectApp` is true: the host then uses `https://login.microsoftonline.com/<Trust:TenantId>/oauth2/v2.0/token`. Only the design's own `clientSecret` instance secret is ever sent to it. |
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
- `bf_<Instance>_Auth`: JSON object of `AuthOverrides` (string to string). Each key replaces the `auth` string field of the same name: `staticKind`, `headerName`, `queryParam`, `username`, `scope` or `ssoKind`. `mode` and `asyncContext` cannot be overridden: who the source sees the call as belongs to the design that was approved, not to an environment variable. Any other key, `mode` and `asyncContext` included, is a configuration problem naming the key and the instance, and the design is left out of that environment.

Secret values are never part of either; they stay in Key Vault or the host's encrypted store.

## Tokens minted through the host

ServiceAccount (the host's managed identity) and Impersonate (a user's Entra token through the host's connect app) send a token to the design's base URL. A design may use either only with a scope that the host lists for its environment in `Trust:AllowedCallers[i]:DesignScopes`; the list is empty by default, so such a design is refused until the host's administrator adds the scope. For ServiceAccount the scope is `auth.scope` (after any `bf_<Instance>_Auth` override); for Impersonate every scope in `auth.oauth.scopes` except the sign-in scopes `openid`, `profile`, `email` and `offline_access`, and at least one other scope is required. Scopes for Azure management, Key Vault, Storage, Dataverse and Microsoft Graph are always refused, and a bare scope such as `User.Read` counts as Microsoft Graph. Each scope must be an absolute `https://` or `api://` URI with no user info, query or fragment; any other spelling (a bare host, a bare app ID, another scheme) is refused. A refused design is a configuration problem naming the instance, the scope and the environment.
