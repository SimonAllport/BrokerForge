// Container Apps environment, the BrokerForge host (external) and the Orders mock (internal only).
param location string
param prefix string
param identityId string
param identityClientId string
param registryServer string
param workspaceName string
param appInsightsConnectionString string
param vaultUri string
param hostImage string
param mockImage string
param tenantId string
param hostAudience string
param pluginClientId string
param environmentId string

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace.properties.customerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
  }
}

var identities = { type: 'UserAssigned', userAssignedIdentities: { '${identityId}': {} } }
var registries = [ { server: registryServer, identity: identityId } ]
var secretUri = '${vaultUri}secrets'

// In-memory state: one replica so creates and deletes are seen by every call.
resource mock 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-orders-mock'
  location: location
  identity: identities
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: { external: false, targetPort: 8080, transport: 'http' }
      registries: registries
      secrets: [ { name: 'mock-api-key', keyVaultUrl: '${secretUri}/orders-api-key', identity: identityId } ]
    }
    template: {
      containers: [
        {
          name: 'mock'
          image: mockImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'PORT', value: '8080' }
            { name: 'MOCK_API_KEY', secretRef: 'mock-api-key' }
            { name: 'MOCK_SLOW_MS', value: '100000' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

var orders = 'Instances__OrdersAPI__'
var map = 'ObjectMaps__ext_order__'

// minReplicas 1: phase 0 measured a 23.5 s cold start (HLD 15).
resource host 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-host'
  location: location
  identity: identities
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: { external: true, targetPort: 8080, transport: 'auto' }
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'host'
          image: hostImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'AZURE_CLIENT_ID', value: identityClientId }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
            { name: 'Trust__TenantId', value: tenantId }
            { name: 'Trust__Audience', value: hostAudience }
            { name: 'Trust__AllowedCallers__0__ClientId', value: pluginClientId }
            { name: 'Trust__AllowedCallers__0__EnvironmentId', value: environmentId }
            { name: 'Host__TimeoutSeconds', value: '90' }
            { name: 'Host__PageTokenKeyRef', value: 'kv:${secretUri}/page-token-key' }
            { name: 'Config__DescriptorsPath', value: '/app/descriptors' }
            { name: '${orders}BrokerType', value: 'rest' }
            { name: '${orders}BaseUrl', value: 'https://${mock.properties.configuration.ingress.fqdn}' }
            { name: '${orders}Auth__Mode', value: 'Static' }
            { name: '${orders}Auth__StaticKind', value: 'apiKeyHeader' }
            { name: '${orders}Auth__HeaderName', value: 'X-Api-Key' }
            { name: '${orders}Auth__SecretRef', value: 'kv:${secretUri}/orders-api-key' }
            { name: '${orders}Config__accountId', value: 'acc-1' }
            { name: '${map}ServiceInstance', value: 'OrdersAPI' }
            { name: '${map}KeyStrategy', value: 'reversible' }
            { name: '${map}KeyKind', value: 'String' }
            { name: '${map}Operations__RetrieveMultiple', value: 'listOrders' }
            { name: '${map}Operations__Retrieve', value: 'getOrder' }
            { name: '${map}Operations__Create', value: 'createOrder' }
            { name: '${map}Operations__Update', value: 'updateOrder' }
            { name: '${map}Operations__Delete', value: 'deleteOrder' }
          ]
          probes: [
            { type: 'Readiness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 10 }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

output hostUrl string = 'https://${host.properties.configuration.ingress.fqdn}'
