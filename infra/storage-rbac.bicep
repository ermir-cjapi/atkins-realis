targetScope = 'resourceGroup'

@description('Existing storage account name.')
param storageAccountName string

@description('Managed identity principal ID receiving access.')
param principalId string

@description('Stable name used to seed the role assignment GUID.')
param principalName string

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: storageAccountName
}

resource blobDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, principalName, 'Storage Blob Data Contributor')
  scope: storageAccount
  properties: {
    description: 'Allow the HR API to persist Agents SDK authorization state.'
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    )
  }
}
