// Optional (phase 4): Azure Relay Hybrid Connections for on-prem SQL.
param namePrefix string
param location string

resource relay 'Microsoft.Relay/namespaces@2021-11-01' = {
  name: '${namePrefix}-relay'
  location: location
  sku: { name: 'Standard' }
}

// TODO phase 4: one hybrid connection per on-prem service instance.
output namespaceName string = relay.name
