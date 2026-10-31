# Phase 0 findings

Verified 31 October 2026 against a real environment. Everything below was observed, not inferred, unless marked **Not verified**.

| Item | Value |
| --- | --- |
| Environment | `dev-eu-8831fe17`, `https://orgeb8d0d95.crm17.dynamics.com`, geo CHE, version 9.2.26103.145 |
| Environment ID | `a4c0d8aa-c6c6-e9a3-bc54-8725982bb449` |
| SDK | `Microsoft.CrmSdk.CoreAssemblies` 9.0.2.60, plug-in targets `net462` |
| Stub host | Azure Container Apps, Switzerland North, `rg-brokerforge-spike` |
| Solution | `BrokerForgeSpike`, publisher `brokerforge` (prefix `bf`) |

Code: [SpikeRetrieveMultiplePlugin.cs](../../src/BrokerForge.Plugin/SpikeRetrieveMultiplePlugin.cs), [host Program.cs](../../src/BrokerForge.Host/Program.cs), [sign-plugin.sh](../../tools/spike/sign-plugin.sh), [dv.sh](../../tools/spike/dv.sh), [metadata payloads](../../tools/spike/metadata/).

## Result

`GET /api/data/v9.2/bf_spikerows` runs the provider plug-in, which reads its data source record, acquires a managed identity token, calls the stub host, and returns one hard-coded row carrying the host's validated claims. Every acceptance criterion for phase 0 passes.

| Criterion | Status |
| --- | --- |
| Provider registered against a test virtual table; RetrieveMultiple returns a hard-coded row | Pass |
| `entitydataprovider` registration understood and written up | Pass (section 1) |
| `IEntityDataSourceRetrieverService` resolution understood and written up | Pass (section 2) |
| Plug-in acquires a managed identity token and calls a host that validates it | Pass (section 3) |

## 1. Data provider registration (`entitydataprovider`)

Everything was done through the Web API. No Plug-in Registration Tool, no Windows.

### Order of operations

Dependencies force this order. Deleting goes in reverse: a data-source table cannot be deleted while an `entitydataprovider` row names it.

1. **Plug-in assembly.** `POST pluginassemblies` with `content` (base64 DLL), `name`, `version`, `culture`, `publickeytoken`, `isolationmode: 2` (sandbox), `sourcetype: 0` (database). First-time registration works by API; `pac plugin push` only updates an existing assembly.
2. **Plug-in type.** `POST plugintypes` with `typename`, `name`, `friendlyname`, `pluginassemblyid@odata.bind`. No step is registered: the provider runs in stage 30, which steps cannot use.
3. **Data-source table.** `POST EntityDefinitions`, see [datasource-table.py](../../tools/spike/metadata/datasource-table.py). It must be a *virtual* table with `DataProviderId = b2112a7e-b26c-42f7-9b63-9a809a9d716f`, the platform's data-source provider. Platform tables such as `msdyn_componentlayerdatasource` have the same shape.
4. **Primary key external name.** `PUT EntityDefinitions(LogicalName='bf_spikedatasource')/Attributes(LogicalName='bf_spikedatasourceid')` with the full attribute metadata, `@odata.type: Microsoft.Dynamics.CRM.AttributeMetadata` and `ExternalName: "id"`. Without it, creating a data source record fails with `ExternalName is null or empty for attribute 'bf_spikedatasourceid'`. There is no `UniqueidentifierAttributeMetadata` type in the Web API model.
5. **Data provider.** `POST entitydataproviders` with `name`, `datasourcelogicalname: "bf_spikedatasource"` and `retrievemultipleplugin: <plugintypeid>`. The `*plugin` columns take **plug-in type** IDs.
6. **Data source record.** `POST bf_spikedatasources` with the column values.
7. **Virtual table.** `POST EntityDefinitions` with `DataProviderId: <entitydataproviderid>` and `DataSourceId: <data source record id>`, see [virtual-table.py](../../tools/spike/metadata/virtual-table.py).

### What the platform does

- **Unset message plug-ins fail loudly.** Every `*plugin` column not supplied defaults to `c1919979-0021-4f11-a587-a8f904bdfdf9`, which is `Microsoft.Crm.Common.ObjectModel.Plugin.ThrowSdkMessageNotImplementedPlugin`. This covers Retrieve, Create, Update, Delete, the `*Multiple` variants, Upsert, archive and retention.
- **`DataProviderId` is dropped silently unless the table passes virtual-table validation.** A create carrying only `DataProviderId: b2112a7e…` returned 201 as `TableType: Standard` with `DataProviderId: null`. With the flags below it became `TableType: Virtual`. The generator must read back `TableType` after create and fail if it isn't `Virtual`.
- **Virtual-table validation reports one rule per attempt.** Each was found by a failed create. A virtual or data-source table needs:
  - `ChangeTrackingEnabled: false` and managed property `CanChangeTrackingBeEnabled: false` (`CanChangeTrackingBeEnabled can not be active for … virtual Entity`)
  - managed property `CanCreateCharts: false` (`Cannot create charts for virtual Entity`)
  - `IsAuditEnabled` and `IsDuplicateDetectionEnabled` managed properties false (set together with charts; the platform did not report them individually)
  - `ExternalCollectionName` (`you must specify a value for the External Collection Name property`)
  - `ExternalName` on the primary name and, for data-source tables, on the primary key
- **`DataSourceId` must be an `entitydatasource` component.** A row in a standard table is refused with `The dependent component EntityDataSource (Id=…) does not exist`.
- **Data source records live in `entitydatasource`.** Writing to the virtual `bf_spikedatasource` creates an `entitydatasource` row with the same ID. `_entitydataproviderid_value` is filled in from `datasourcelogicalname`. Column values are serialised as JSON in `connectiondefinition`, keyed by each column's `ExternalName`:

  ```json
  { "hosturl": "https://bf-spike-host…azurecontainerapps.io", "name": "Spike host",
    "scope": "api://532b2886-…/.default", "id": "44336a82-…" }
  ```

  Columns marked `IsDataSourceSecret` go into `connectiondefinitionsecrets` (here `{}`). **Not verified:** how secret columns are encrypted and whether they come back from `RetrieveEntityDataSource()`. BrokerForge does not need this: principle 4 keeps secrets out of Dataverse rows, so data source records hold only the host URL and scope.

### Querying it

```
GET entitydataproviders(<id>)                                  # provider row, plug-in type per message
GET entitydatasources?$filter=_entitydataproviderid_value eq <id>
GET EntityDefinitions(LogicalName='<table>')?$select=TableType,DataProviderId,DataSourceId
```

## 2. `IEntityDataSourceRetrieverService`

Signature, read from the `Microsoft.Xrm.Sdk.dll` 9.0.2.60 metadata:

```csharp
namespace Microsoft.Xrm.Sdk
public interface IEntityDataSourceRetrieverService { Entity RetrieveEntityDataSource(); }
```

Resolution inside the provider plug-in ([SpikeRetrieveMultiplePlugin.cs](../../src/BrokerForge.Plugin/SpikeRetrieveMultiplePlugin.cs)):

```csharp
var retriever = (IEntityDataSourceRetrieverService)serviceProvider.GetService(typeof(IEntityDataSourceRetrieverService));
var dataSource = retriever.RetrieveEntityDataSource();
```

Observed return value, from the plug-in trace log:

```
dataSource logicalName=bf_spikedatasource id=44336a82-0fd5-f111-84ea-002248db5cf2 attributes=bf_hosturl,bf_name,bf_scope,bf_spikedatasourceid
```

The `Entity` uses the data-source table's logical name and **Dataverse logical column names**, not the `ExternalName` keys used in storage. It is the record bound through the virtual table's `DataSourceId`. No query, no extra privileges.

Microsoft documents the method with no description or sample: <https://learn.microsoft.com/en-us/dotnet/api/microsoft.xrm.sdk.ientitydatasourceretrieverservice.retrieveentitydatasource>.

## 3. Managed identity, plug-in to host

### SDK

```csharp
namespace Microsoft.Xrm.Sdk
public interface IManagedIdentityService
{
    string AcquireToken(IEnumerable<string> scopes);
    string AcquireToken(Guid managedIdentityId, IEnumerable<string> scopes);
}
```

It is in `Microsoft.CrmSdk.CoreAssemblies` 9.0.2.60. The spike calls `AcquireToken(new[] { "api://<hostAppId>/.default" })`. **Not verified:** the overload taking `managedIdentityId`.

### Setup (version 2, per <https://learn.microsoft.com/en-us/power-platform/admin/set-up-managed-identity>)

1. **Entra.** App registration `bf-spike-plugin` (the plug-in's identity) and `bf-spike-host` (the audience). The host app has identifier URI `api://<appId>` and `api.requestedAccessTokenVersion = 2`.
2. **Signing on macOS.** Strong-name key generated with `RSACryptoServiceProvider.ExportCspBlob(true)`, because `sn.exe` is unavailable. Self-signed RSA-3072 code-signing certificate made with `openssl`. Authenticode signature with **`osslsigncode` 2.14** (SHA-256, DigiCert timestamp) in place of `signtool`. Dataverse accepted it. Signing material lives in `~/.brokerforge/spike-signing/`, outside the repo. See [sign-plugin.sh](../../tools/spike/sign-plugin.sh).
3. **Federated credential** on `bf-spike-plugin`:
   - issuer `https://login.microsoftonline.com/{tenantId}/v2.0`
   - audience `api://AzureADTokenExchange`
   - subject `/eid1/c/pub/t/{encodedTenantId}/a/qzXoWDkuqUa3l6zM5mM0Rw/n/plugin/e/{environmentId}/h/{hash}`
     - `{encodedTenantId}` is `Guid.ToByteArray()` as Base64URL without padding. In Python that's `uuid.UUID(t).bytes_le`.
     - `{hash}` for a self-signed certificate is the **lowercase hex** SHA-256 of the DER `.cer`, the same as `shasum -a 256 cert.cer`. This worked. The `/i/{issuerHash}/s/{subjectHash}` Base64URL form is for CA-issued certificates.
4. **Dataverse.** `POST managedidentities` with `applicationid`, `tenantid`, `credentialsource: 2`, `subjectscope: 1`, `version: 2`. Then `PATCH pluginassemblies(<id>)` with `managedidentityid@odata.bind`.

### Token the host receives

```json
{ "aud": "<host app id>", "iss": "https://login.microsoftonline.com/<tenant>/v2.0", "ver": "2.0",
  "tid": "<tenant>", "azp": "<plug-in app id>", "azpacr": "2",
  "oid": "<plug-in service principal>", "sub": "<same as oid>" }
```

The token has **no environment claim**. The environment is bound only on the Entra side, by the `/e/{environmentId}` segment of the federated credential subject.

### Host validation

See [Program.cs](../../src/BrokerForge.Host/Program.cs). The host checks:
- signature against the tenant's v2 metadata
- issuer (v1 and v2 forms allowed)
- `aud` = host app ID
- `tid` = tenant
- `azp`/`appid` = the allowed plug-in app ID

Tests:

| Test | Result |
| --- | --- |
| No token | 401 |
| Real Entra token for the wrong audience (Dataverse) | 401, logged `IDX10214 Audience validation failed` with the correlation ID |
| Valid plug-in, but host's allowed client ID changed to a dummy | Dataverse returns HTTP 400 `Broker host returned 401 (correlationId …)`. The caller sees an error, not an empty view. |
| Valid plug-in | 200, row returned |

### Timing

| Call | Plug-in execution |
| --- | --- |
| First call (host scaled to zero, first token exchange) | 23.5 s |
| Warm calls | 31–46 ms, one at 1.4 s |

## 4. Paging (verified 31 October 2026, phase 1 Task 1)

The spike plug-in was changed to report `QueryExpression.PageInfo` in every row and to return three pages, setting `PagingCookie = "bf-test-<page>"` and `MoreRecords = true` on pages 1 and 2 (`src/BrokerForge.Plugin/SpikeRetrieveMultiplePlugin.cs`, commit with this section).

| Call | What the provider received | What the caller got |
| --- | --- | --- |
| `GET bf_spikerows`, `Prefer: odata.maxpagesize=2` | `QueryExpression`, `PageNumber=1`, `Count=2`, `PagingCookie=null` | 2 rows and an `@odata.nextLink` whose `$skiptoken` is `<cookie pagenumber="2" pagingcookie="bf-test-1" istracking="False" />` |
| Follow that `nextLink` | `PageNumber=2`, `Count=2`, `PagingCookie="bf-test-1"` | 2 rows, next link carrying `bf-test-2` |
| Follow again | `PageNumber=3`, `PagingCookie="bf-test-2"` | 2 rows, no next link (`MoreRecords=false`) |
| `$top=3` | `PageNumber=1`, `Count=3`, `TopCount=null` | 3 rows and no next link, even though the provider said `MoreRecords=true` |
| No page size | `Count=5000` | 5000 rows and a next link |
| FetchXML `count="2" page="2" paging-cookie="bf-test-1"` | `QueryExpression` (FetchXML is converted), `PageNumber=2`, `PagingCookie="bf-test-1"` | 2 rows, `@Microsoft.Dynamics.CRM.morerecords: true`, `fetchxmlpagingcookie` carrying `bf-test-2` |
| Page 1 returns `Count - 1` rows with `MoreRecords=true` | `Count=3` | 2 rows and a next link. A short page with more to follow is accepted |

What this means for the design:

- The provider's `PagingCookie` string comes back verbatim on the next page, through both the Web API and FetchXML. The host's page token can travel in it, so the host stays stateless (HLD 12).
- The platform puts the cookie in an XML attribute inside `$skiptoken`, and the user can see and edit it. The host must treat the token as untrusted input (descriptor spec 7: operation, version and link checks).
- `$top` arrives as `PageInfo.Count`, not `TopCount`, and the platform suppresses the next link itself. The plug-in needs no special case.
- With no page size the provider is asked for 5000 rows. This confirms G9 / D-12: a page bounded by `maxCallsPerPage` must return a next token, not fail.
- `ReturnTotalRecordCount` was false in every call; FetchXML reports `totalrecordcount: 0`. Phase 1 does not return totals.

## Consequences for the design

These need decisions; they are recorded in `docs/hld.md` sections 15 and 16.

1. **Environment trust must come from identity, not from the payload.** The token carries no environment ID. If one plug-in identity had federated credentials for several environments, the host could not tell them apart, and the `environmentId` in the canonical request would rest only on the plug-in's word. To keep HLD 9.2's "validate environment ID", each environment needs **its own plug-in identity**, with the host mapping `azp` to an allowed environment. That means one app registration per environment, each with one federated credential.
2. **Cold start is far over budget.** 23.5 s against NFR-01's 150 ms, and a real risk against the 2-minute sandbox limit when stacked with source time. The host needs `minReplicas >= 1` in any environment users touch.
3. **The generator must verify what it created.** The platform silently drops `DataProviderId` (see section 1). The generator reads back `TableType`, `DataProviderId` and `DataSourceId`, and fails if they don't match.
4. **The provider must set the primary-key attribute,** not only `Entity.Id`. The spike set only `Entity.Id`, and the Web API returned `bf_spikerowid: null`.
5. **Data source records are config.** They hold the host URL and scope only. HLD 14 already says data source records are per-source solution components; they serialise into `entitydatasource`, so they can be shipped that way. **Not verified:** that they import cleanly in a solution.

## Still open

- **Not verified:** whether `netstandard2.0` Contracts referenced from the `net462` plug-in load in the sandbox without a plug-in package. This affects the phase 1 project structure; test it first in phase 1.
- **Not verified:** whether a CA-issued certificate works with the `/i/…/s/…` subject. Production needs this.
- **Not verified:** what `AcquireToken(Guid, …)` does.

## Spike resources

These are for cleanup once phase 1 has its own environment setup.

- Entra apps `bf-spike-host` (`532b2886-…`) and `bf-spike-plugin` (`f79121d7-…`)
- Azure resource group `rg-brokerforge-spike`: Container App, environment, ACR, Log Analytics
- Dataverse:
  - solution `BrokerForgeSpike` and publisher `brokerforge`
  - tables `bf_spikerow` and `bf_spikedatasource`
  - provider, managed identity and assembly rows
  - plug-in trace logging, set to All for this environment
