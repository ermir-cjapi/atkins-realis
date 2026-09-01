targetScope = 'resourceGroup'

@description('Name of the existing Key Vault.')
param keyVaultName string

@description('Principal ID that requires secret-read access.')
param principalId string

@description('Stable principal name used for deterministic role assignment naming.')
param principalName string

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: keyVaultName
}

resource keyVaultSecretsUserRbac 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, principalName, 'Key Vault Secrets User')
  scope: keyVault
  properties: {
    description: 'Allow the HR API to resolve ServiceNow OAuth credentials.'
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86a8191'
    )
  }
}
