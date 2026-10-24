# Dataverse solution source

Unpacked solution source managed with `pac`. Empty until phase 0/1.

Planned contents (HLD 5.1, 6, 14):

| Solution | Contents |
| --- | --- |
| `BrokerForgeCore` | Plug-in package (BrokerForge.Plugin + Contracts), `entitydataprovider` row for the generic provider, BrokerForge data source table (`bf_hosturl`, `bf_hostscope`, `bf_serviceinstance`, `bf_environmentid`), config tables (`bf_serviceinstance`, `bf_operation`, `bf_objectmap`, `bf_keymap`, `bf_userconnection`, `bf_oauthresource`), admin app, environment variables |
| One per source, e.g. `BrokerForgeOrders` | Generated virtual tables, Custom APIs, data source record |

```
pac solution init --publisher-name BrokerForge --publisher-prefix bf --outputDirectory solution/BrokerForgeCore
pac solution pack --folder solution/BrokerForgeCore --zipfile out/BrokerForgeCore.zip
```
