// BrokerForge dev environment (phase 1). Two passes from tools/dev/deploy.sh:
//   deployApps=false: identity, registry, Key Vault, monitoring (images and secrets do not exist yet)
//   deployApps=true:  Container Apps environment, host and Orders mock
targetScope = 'resourceGroup'

param location string = resourceGroup().location
param prefix string = 'bf-dev'
param deployApps bool = false
@description('Object ID of the user running the deployment; gets Key Vault Secrets Officer to write the generated secrets.')
param deployerObjectId string
param hostImage string = ''
param mockImage string = ''
param tenantId string = subscription().tenantId
@description('Host app registration ID: the audience of the plug-in token.')
param hostAudience string = ''
@description('Plug-in app registration ID and the one environment it may call for (D-09).')
param pluginClientId string = ''
param environmentId string = ''

var suffix = uniqueString(resourceGroup().id)

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-id'
  location: location
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: replace('${prefix}acr${suffix}', '-', '')
  location: location
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, identity.id, 'acrpull')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

module vault 'keyVault.bicep' = {
  name: 'vault'
  params: {
    location: location
    name: '${prefix}-kv-${take(suffix, 6)}'
    readerPrincipalId: identity.properties.principalId
    officerPrincipalId: deployerObjectId
  }
}

module monitoring 'monitoring.bicep' = {
  name: 'monitoring'
  params: { location: location, prefix: prefix }
}

module apps 'containerApps.bicep' = if (deployApps) {
  name: 'apps'
  params: {
    location: location
    prefix: prefix
    identityId: identity.id
    identityClientId: identity.properties.clientId
    registryServer: registry.properties.loginServer
    workspaceName: monitoring.outputs.workspaceName
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    vaultUri: vault.outputs.vaultUri
    hostImage: hostImage
    mockImage: mockImage
    tenantId: tenantId
    hostAudience: hostAudience
    pluginClientId: pluginClientId
    environmentId: environmentId
  }
}

output registryName string = registry.name
output vaultName string = vault.outputs.name
output vaultUri string = vault.outputs.vaultUri
output hostUrl string = deployApps ? apps.outputs.hostUrl : ''
