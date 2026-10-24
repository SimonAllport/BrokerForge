# Phase 0 findings

Fill in each section as it is verified in a real environment. Each needs: what was tested, the exact API usage, the result, and a source link. Until all three are done, nothing builds on these assumptions (CLAUDE.md).

Environment: `<org URL, region, date>`

## 1. Provider registration (`entitydataprovider`)

- **Question:** how the generic provider is registered and how the `entitydataprovider` row is created and queried for Retrieve/RetrieveMultiple/Create/Update/Delete.
- **Test:** register `BrokerForge.Plugin.SpikeRetrieveMultiplePlugin` against a test virtual table; RetrieveMultiple returns the hard-coded row.
- **Result:** _pending_
- **Code:** _pending_
- **Source:** https://learn.microsoft.com/power-apps/developer/data-platform/virtual-entities/sample-ve-provider-crud-operations

## 2. Reading the data source record (`IEntityDataSourceRetrieverService`)

- **Question:** does `serviceProvider.GetService(typeof(IEntityDataSourceRetrieverService))` resolve inside the provider plug-in, and does `RetrieveEntityDataSource()` return the bound data source row with custom columns?
- **Used in:** `src/BrokerForge.Plugin/Settings.cs` (`DataSourceSettings.Resolve`).
- **Result:** _pending_
- **Source:** https://learn.microsoft.com/dotnet/api/microsoft.xrm.sdk.ientitydatasourceretrieverservice.retrieveentitydatasource

## 3. Plug-in → host trust (`IManagedIdentityService`)

- **Question:** token acquisition with `IManagedIdentityService.AcquireToken(scopes)`; which claims arrive at the host (iss, aud, appid/azp, tid, environment); how the host validates them.
- **Used in:** `src/BrokerForge.Plugin/HostClient.cs` (`ManagedIdentityTokenSource`), `src/BrokerForge.Host/Trust.cs` (`ManagedIdentityRequestTrust`, currently denies every call).
- **Result:** _pending_ (record a decoded sample token with values redacted)
- **Source:** https://learn.microsoft.com/power-platform/admin/set-up-managed-identity

## Also confirm

- `Microsoft.CrmSdk.CoreAssemblies` version that contains both interfaces above (currently pinned to 9.0.2.60).
- Whether `IPluginExecutionContext2.InitiatingUserAzureActiveDirectoryObjectId` is populated for provider plug-ins.
- The right source for `environmentId` (data source column, organisation ID, or a token claim).
