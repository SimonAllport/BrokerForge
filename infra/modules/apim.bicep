// Optional (HLD D-07). Gateway-level JWT validation repeats the host's own check; the host never relies on APIM alone.
param namePrefix string
param location string
param backendUrl string

@description('Consumption is cheapest but has no VNet integration; use Developer/Standard v2 or Premium for a private host.')
param skuName string = 'Consumption'

param publisherEmail string = 'noreply@example.com'
param publisherName string = 'BrokerForge'

resource apim 'Microsoft.ApiManagement/service@2023-05-01-preview' = {
  name: '${namePrefix}-apim'
  location: location
  sku: { name: skuName, capacity: skuName == 'Consumption' ? 0 : 1 }
  properties: {
    publisherEmail: publisherEmail
    publisherName: publisherName
  }
}

// TODO phase 1: API definition for /v1/execute with validate-jwt policy and backend = backendUrl.
output gatewayUrl string = apim.properties.gatewayUrl
output backend string = backendUrl
