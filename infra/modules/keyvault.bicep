param namePrefix string
param location string
param hostPrincipalId string

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take(replace('${namePrefix}-kv', '_', '-'), 24)
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
  }
}

// Key Vault Secrets User for the host's managed identity.
resource hostSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, hostPrincipalId, 'secrets-user')
  scope: vault
  properties: {
    principalId: hostPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

output uri string = vault.properties.vaultUri
