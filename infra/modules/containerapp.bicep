param namePrefix string
param location string
param image string
param logAnalyticsWorkspaceId string
param appInsightsConnectionString string

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: last(split(logAnalyticsWorkspaceId, '/'))
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-env'
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

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-host'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
    }
    template: {
      containers: [
        {
          name: 'host'
          image: image
          env: [
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            // BrokerForge__Trust__TenantId, __Audience and __AllowedAppIds__0 are set per client.
          ]
          probes: [
            { type: 'Liveness', httpGet: { path: '/health', port: 8080 } }
          ]
        }
      ]
      scale: {
        minReplicas: 2 // HLD 15: avoid a single point of failure for every source
        maxReplicas: 10
      }
    }
  }
}

output principalId string = app.identity.principalId
output url string = 'https://${app.properties.configuration.ingress.fqdn}'
