targetScope = 'resourceGroup'

@description('Existing shared storage account name.')
param storageAccountName string

@description('Blob container to create.')
param containerName string

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: storageAccountName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' existing = {
  parent: storageAccount
  name: 'default'
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobService
  name: containerName
  properties: {
    publicAccess: 'None'
  }
}

output containerUri string = 'https://${storageAccount.name}.blob.${environment().suffixes.storage}/${container.name}'
