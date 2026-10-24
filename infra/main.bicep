// BrokerForge broker host infrastructure (HLD 14). Skeleton: not yet deployed or validated with `az bicep build`.
// One host per client tenant. API Management and Azure Relay are optional modules (HLD D-07).

targetScope = 'resourceGroup'

@description('Short prefix for resource names, e.g. bf-acme-dev.')
param namePrefix string

param location string = resourceGroup().location

@description('Container image for the broker host, e.g. myacr.azurecr.io/brokerforge-host:1.0.0.')
param hostImage string

@description('Put Azure API Management in front of the host (client standard, VNet-private host, or other consumers).')
param deployApim bool = false

@description('Deploy Azure Relay Hybrid Connections for on-prem sources (phase 4).')
param deployRelay bool = false

module monitoring 'modules/monitoring.bicep' = {
  name: '${namePrefix}-monitoring'
  params: {
    namePrefix: namePrefix
    location: location
  }
}

module host 'modules/containerapp.bicep' = {
  name: '${namePrefix}-host'
  params: {
    namePrefix: namePrefix
    location: location
    image: hostImage
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
  }
}

module vault 'modules/keyvault.bicep' = {
  name: '${namePrefix}-vault'
  params: {
    namePrefix: namePrefix
    location: location
    hostPrincipalId: host.outputs.principalId
  }
}

module apim 'modules/apim.bicep' = if (deployApim) {
  name: '${namePrefix}-apim'
  params: {
    namePrefix: namePrefix
    location: location
    backendUrl: host.outputs.url
  }
}

module relay 'modules/relay.bicep' = if (deployRelay) {
  name: '${namePrefix}-relay'
  params: {
    namePrefix: namePrefix
    location: location
  }
}

output hostUrl string = host.outputs.url
output keyVaultUri string = vault.outputs.uri
