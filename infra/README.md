# Infrastructure

Bicep for the broker host (HLD 14). **Skeleton:** written against current resource API versions but not yet built with `az bicep build` or deployed.

| File | Deploys |
| --- | --- |
| `main.bicep` | Entry point; composes the modules below |
| `modules/monitoring.bicep` | Log Analytics + Application Insights |
| `modules/containerapp.bicep` | Container Apps environment + host app (system-assigned identity, 2–10 replicas) |
| `modules/keyvault.bicep` | Key Vault (RBAC) with Secrets User for the host identity |
| `modules/apim.bicep` | Optional API Management (`deployApim=true`) |
| `modules/relay.bicep` | Optional Azure Relay for on-prem sources (`deployRelay=true`, phase 4) |

```
az deployment group create -g <rg> -f infra/main.bicep -p namePrefix=bf-acme-dev hostImage=<image>
```
