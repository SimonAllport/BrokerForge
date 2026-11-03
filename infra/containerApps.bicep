// Container Apps environment, the BrokerForge host and two mocks from one image. The Orders and Inventory mock is
// internal; the Tickets mock is external, because the browser must reach its sign-in page (HLD D-31), and serves
// the Tickets API alone (MOCK_APIS), so nothing else of the mock is reachable from outside.
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
param orgUrl string
param credentialTableEndpoint string
param keyRingBlobUri string
param dataProtectionKeyUri string
param connectClientId string
param ticketsApiAppId string
param ticketsAliceOid string
param ticketsBobOid string

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
// Container Apps names are fixed, so the public URLs are known before the apps exist.
var hostUrl = 'https://${prefix}-host.${environment.properties.defaultDomain}'
var ticketsUrl = 'https://${prefix}-tickets-mock.${environment.properties.defaultDomain}'
var perUser = !empty(connectClientId)
var login = az.environment().authentication.loginEndpoint
var entraUsers = join(filter([ empty(ticketsAliceOid) ? '' : '${ticketsAliceOid}:alice', empty(ticketsBobOid) ? '' : '${ticketsBobOid}:bob' ], u => !empty(u)), ',')

// In-memory state: one replica each, so creates and deletes are seen by every call.
resource mock 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-orders-mock'
  location: location
  identity: identities
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: false, targetPort: 8080, transport: 'http' }
      registries: registries
      secrets: [
        { name: 'mock-api-key', keyVaultUrl: '${secretUri}/orders-api-key', identity: identityId }
        { name: 'inventory-api-key', keyVaultUrl: '${secretUri}/inventory-api-key', identity: identityId }
      ]
    }
    template: {
      containers: [
        {
          name: 'mock'
          image: mockImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'PORT', value: '8080' }
            { name: 'MOCK_APIS', value: 'orders,inventory' }
            { name: 'MOCK_API_KEY', secretRef: 'mock-api-key' }
            { name: 'MOCK_INVENTORY_KEY', secretRef: 'inventory-api-key' }
            { name: 'MOCK_SLOW_MS', value: '100000' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource ticketsMock 'Microsoft.App/containerApps@2024-03-01' = if (perUser) {
  name: '${prefix}-tickets-mock'
  location: location
  identity: identities
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'http' }
      registries: registries
      secrets: [
        // The mock refuses to start without an API key; MOCK_APIS keeps the Orders routes it guards switched off.
        { name: 'mock-api-key', keyVaultUrl: '${secretUri}/orders-api-key', identity: identityId }
        { name: 'tickets-client-secret', keyVaultUrl: '${secretUri}/tickets-client-secret', identity: identityId }
        { name: 'tickets-alice-password', keyVaultUrl: '${secretUri}/tickets-alice-password', identity: identityId }
        { name: 'tickets-bob-password', keyVaultUrl: '${secretUri}/tickets-bob-password', identity: identityId }
      ]
    }
    template: {
      containers: [
        {
          name: 'mock'
          image: mockImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'PORT', value: '8080' }
            { name: 'MOCK_APIS', value: 'tickets' }
            { name: 'MOCK_API_KEY', secretRef: 'mock-api-key' }
            { name: 'MOCK_TK_CLIENT_SECRET', secretRef: 'tickets-client-secret' }
            { name: 'MOCK_TK_ALICE_PASSWORD', secretRef: 'tickets-alice-password' }
            { name: 'MOCK_TK_BOB_PASSWORD', secretRef: 'tickets-bob-password' }
            { name: 'MOCK_TK_REDIRECT_URIS', value: '${hostUrl}/connect/callback' }
            // v2 tokens carry the app ID as aud, v1 tokens the identifier URI.
            { name: 'MOCK_ENTRA_AUDIENCE', value: empty(ticketsApiAppId) ? '' : '${ticketsApiAppId},api://${ticketsApiAppId}' }
            { name: 'MOCK_ENTRA_JWKS_URL', value: '${login}${tenantId}/discovery/v2.0/keys' }
            { name: 'MOCK_ENTRA_USERS', value: entraUsers }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

var orders = 'Instances__OrdersAPI__'
var map = 'ObjectMaps__ext_order__'
var inventory = 'Instances__InventoryAPI__'
var itemMap = 'ObjectMaps__ext_item__'
var tickets = 'Instances__TicketsAPI__'
var ticketMap = 'ObjectMaps__ext_ticket__'
var entraTickets = 'Instances__TicketsEntraAPI__'
var entraTicketMap = 'ObjectMaps__ext_entraticket__'

// Per-user sources (phase 3) need the connect app, so they are added only once it exists.
var perUserEnv = [
  { name: 'Connect__PublicBaseUrl', value: hostUrl }
  { name: 'Connect__ClientId', value: connectClientId }
  { name: 'OAuthResources__Tickets__AuthorizeUrl', value: '${ticketsUrl}/tk/oauth/authorize' }
  { name: 'OAuthResources__Tickets__TokenUrl', value: '${ticketsUrl}/tk/oauth/token' }
  { name: 'OAuthResources__Tickets__Scopes__0', value: 'tickets.read' }
  { name: 'OAuthResources__Tickets__ClientId', value: 'bf-tickets' }
  { name: 'OAuthResources__Tickets__ClientSecretRef', value: 'kv:${secretUri}/tickets-client-secret' }
  { name: 'OAuthResources__Entra__AuthorizeUrl', value: '${login}${tenantId}/oauth2/v2.0/authorize' }
  { name: 'OAuthResources__Entra__TokenUrl', value: '${login}${tenantId}/oauth2/v2.0/token' }
  { name: 'OAuthResources__Entra__Scopes__0', value: 'api://${ticketsApiAppId}/user_impersonation' }
  { name: 'OAuthResources__Entra__Scopes__1', value: 'offline_access' }
  { name: 'OAuthResources__Entra__UsesConnectApp', value: 'true' }
  { name: '${tickets}BrokerType', value: 'rest' }
  { name: '${tickets}BaseUrl', value: ticketsUrl }
  { name: '${tickets}Auth__Mode', value: 'OAuth' }
  { name: '${tickets}Auth__OAuthResource', value: 'Tickets' }
  { name: '${ticketMap}ServiceInstance', value: 'TicketsAPI' }
  { name: '${ticketMap}KeyStrategy', value: 'reversible' }
  { name: '${ticketMap}KeyKind', value: 'String' }
  { name: '${ticketMap}Operations__RetrieveMultiple', value: 'listTickets' }
  { name: '${ticketMap}Operations__Retrieve', value: 'getTicket' }
  { name: '${ticketMap}Operations__Create', value: 'createTicket' }
  { name: '${entraTickets}BrokerType', value: 'rest' }
  { name: '${entraTickets}BaseUrl', value: ticketsUrl }
  { name: '${entraTickets}Auth__Mode', value: 'Impersonate' }
  { name: '${entraTickets}Auth__OAuthResource', value: 'Entra' }
  { name: '${entraTicketMap}ServiceInstance', value: 'TicketsEntraAPI' }
  { name: '${entraTicketMap}KeyStrategy', value: 'reversible' }
  { name: '${entraTicketMap}KeyKind', value: 'String' }
  { name: '${entraTicketMap}Operations__RetrieveMultiple', value: 'listEntraTickets' }
  { name: '${entraTicketMap}Operations__Retrieve', value: 'getEntraTicket' }
  { name: '${entraTicketMap}Operations__Create', value: 'createEntraTicket' }
]

// minReplicas 1: phase 0 measured a 23.5 s cold start (HLD 15).
resource host 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-host'
  location: location
  identity: identities
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'auto' }
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'host'
          image: hostImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat([
            { name: 'AZURE_CLIENT_ID', value: identityClientId }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
            { name: 'Trust__TenantId', value: tenantId }
            { name: 'Trust__Audience', value: hostAudience }
            { name: 'Trust__AllowedCallers__0__ClientId', value: pluginClientId }
            { name: 'Trust__AllowedCallers__0__EnvironmentId', value: environmentId }
            { name: 'Trust__AllowedCallers__0__OrgUrl', value: orgUrl }
            // Connection designs (HLD D-34) are read from that environment; their keys live in the credential table.
            { name: 'Config__Designs__Enabled', value: 'true' }
            { name: 'Credentials__TableEndpoint', value: credentialTableEndpoint }
            { name: 'DataProtection__BlobUri', value: keyRingBlobUri }
            { name: 'DataProtection__KeyId', value: dataProtectionKeyUri }
            { name: 'Host__TimeoutSeconds', value: '90' }
            { name: 'Host__PageTokenKeyRef', value: 'kv:${secretUri}/page-token-key' }
            { name: 'Config__DescriptorsPath', value: '/app/descriptors' }
            { name: 'Config__SchemasPath', value: '/app/schemas' }
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
            { name: '${inventory}BrokerType', value: 'rest' }
            { name: '${inventory}BaseUrl', value: 'https://${mock.properties.configuration.ingress.fqdn}' }
            { name: '${inventory}Auth__Mode', value: 'Static' }
            { name: '${inventory}Auth__StaticKind', value: 'apiKeyQuery' }
            { name: '${inventory}Auth__QueryParam', value: 'api_key' }
            { name: '${inventory}Auth__SecretRef', value: 'kv:${secretUri}/inventory-api-key' }
            { name: '${itemMap}ServiceInstance', value: 'InventoryAPI' }
            { name: '${itemMap}KeyStrategy', value: 'reversible' }
            { name: '${itemMap}KeyKind', value: 'Int32' }
            { name: '${itemMap}Operations__RetrieveMultiple', value: 'listItems' }
            { name: '${itemMap}Operations__Retrieve', value: 'getItem' }
            { name: '${itemMap}Operations__Create', value: 'createItem' }
            { name: '${itemMap}Operations__Update', value: 'updateItem' }
            { name: '${itemMap}Operations__Delete', value: 'deleteItem' }
          ], perUser ? perUserEnv : [])
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
