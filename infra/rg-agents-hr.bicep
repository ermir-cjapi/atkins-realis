/********
Scope
*********/

targetScope = 'resourceGroup'

/********
Parameters
*********/

@description('Solution code.')
param solution string

@description('Environment code.')
param environment string

/********
Variables
*********/

// Resource ID of the Log Analytics workspace that App Insights will use.
var logAnalyticsWorkspaceResourceId = resourceId(
  '${solution}-shared-rg-xx-${environment}',
  'Microsoft.OperationalInsights/workspaces',
  '${solution}-shared-log-xx-${environment}'
)

/********
Resources
*********/

// Get reference to existing shared app service plan.
resource asp 'Microsoft.Web/serverfarms@2024-11-01' existing = {
  name: '${solution}-shared-asp-xx-${environment}'
  scope: resourceGroup('${solution}-shared-rg-xx-${environment}')
}

resource ase 'Microsoft.Web/hostingEnvironments@2024-11-01' existing = {
  name: '${solution}-shared-ase-xx-${environment}'
  scope: resourceGroup('${solution}-shared-rg-xx-${environment}')
}

// Existing shared Key Vault containing the ServiceNow OAuth credentials.
resource sharedKeyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: '${solution}sharedkvxx${environment}'
  scope: resourceGroup('${solution}-shared-rg-xx-${environment}')
}

// Get reference to existing storage account for the logic app.
resource st 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: '${solution}sharedstxx${environment}'
  scope: resourceGroup('${solution}-shared-rg-xx-${environment}')
}

// Get the bot for HR.
resource botHr 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: '${solution}-bots-id-hr-xx-${environment}'
  scope: resourceGroup('${solution}-bots-rg-xx-${environment}')
}

// Setup container for authorization data.
module stContainerAuthorization 'storage-container.bicep' = {
  name: '${deployment().name}_authorization_storage'
  scope: resourceGroup('${solution}-shared-rg-xx-${environment}')
  params: {
    storageAccountName: st.name
    containerName: 'agents-hr-authorization'
  }
}

// API.
var apiAppiName = '${solution}-agents-hr-appi-api-xx-${environment}'
module apiAppi 'br/public:avm/res/insights/component:0.7.1' = {
  name: '${deployment().name}_api_appi'
  params: {
    name: apiAppiName
    workspaceResourceId: logAnalyticsWorkspaceResourceId
    applicationType: 'web'
    disableIpMasking: false
    disableLocalAuth: true
    enableTelemetry: false
    kind: 'web'
  }
}

var apiAppName = '${solution}-agents-hr-app-api-xx-${environment}'

module apiApp 'br/public:avm/res/web/site:0.19.4' = {
  name: '${deployment().name}_api_app'
  params: {
    kind: 'app'
    name: apiAppName
    serverFarmResourceId: asp.id
    appServiceEnvironmentResourceId: ase.id
    enableTelemetry: false
    managedIdentities: {
      systemAssigned: true
      userAssignedResourceIds: [
        botHr.id
      ]
    }
    // Required to allow internal networks to access the app (ASE ensures it is not public).
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      http20Enabled: true
      minTlsVersion: '1.3'
      netFrameworkVersion: 'v10.0'
      scmMinTlsVersion: '1.3'
      use32BitWorkerProcess: false
    }
    configs: [
      {
        name: 'appsettings'
        applicationInsightResourceId: apiAppi.outputs.resourceId
        retainCurrentAppSettings: false
        properties: {
          APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'Authorization=AAD'
          Teams__Enabled: 'true'
          Teams__AppId: botHr.properties.clientId
          Teams__TenantId: subscription().tenantId
          Teams__AuthorizationHandler: 'hr'
          Teams__StorageContainerUri: stContainerAuthorization.outputs.containerUri
          AgentApplication__UserAuthorization__DefaultHandlerName: 'hr'
          AgentApplication__UserAuthorization__SignInFailedMessage: 'Sign-in failed. Please try again or contact support.'
          AgentApplication__UserAuthorization__Handlers__hr__Settings__AzureBotOAuthConnectionName: 'user.read'
          AgentApplication__UserAuthorization__Handlers__hr__Settings__EnableSso: 'true'
          AgentApplication__UserAuthorization__Handlers__hr__Settings__Title: 'Sign in to Assist | HR'
          AgentApplication__UserAuthorization__Handlers__hr__Settings__Text: 'Sign in with your work account to continue.'
          Connections__ServiceConnection__Assembly: 'Microsoft.Agents.Authentication.Msal'
          Connections__ServiceConnection__Type: 'MsalAuth'
          Connections__ServiceConnection__Settings__AuthType: 'UserManagedIdentity'
          Connections__ServiceConnection__Settings__ClientId: botHr.properties.clientId
          Connections__ServiceConnection__Settings__Scopes__0: 'https://api.botframework.com/.default'
          ConnectionsMap__0__ServiceUrl: '*'
          ConnectionsMap__0__Connection: 'ServiceConnection'
          CloudAdapterOptions__ValidateServiceUrl: 'true'
          // Non-secret ServiceNow resilience/cache defaults remain in appsettings.json until an
          // environment-specific limit is owner-approved. OAuth secrets remain Key Vault references.
          ServiceNow__ClientSecret: '@Microsoft.KeyVault(SecretUri=${sharedKeyVault.properties.vaultUri}secrets/agents-hr-servicenow-aisearch-clientsecret/)'
          WEBSITE_VNET_ROUTE_ALL: '1'
        }
      }
    ]
  }
}

resource apiAppiResource 'Microsoft.Insights/components@2020-02-02' existing = {
  name: apiAppiName
}

resource apiAppiRbac 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(apiAppiResource.id, apiAppName, 'Monitoring Metrics Publisher')
  scope: apiAppiResource
  properties: {
    description: 'Allow API to publish metrics to App Insights.'
    principalId: apiApp.outputs.systemAssignedMIPrincipalId!
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '3913510d-42f4-4e42-8a64-420c390055eb'
    )
  }
}

module apiKeyVaultRbac 'key-vault-rbac.bicep' = {
  name: '${deployment().name}_api_kv_rbac'
  scope: resourceGroup('${solution}-shared-rg-xx-${environment}')
  params: {
    keyVaultName: sharedKeyVault.name
    principalId: apiApp.outputs.systemAssignedMIPrincipalId!
    principalName: apiAppName
  }
}

module apiStorageRbac 'storage-rbac.bicep' = {
  name: '${deployment().name}_api_storage_rbac'
  scope: resourceGroup('${solution}-shared-rg-xx-${environment}')
  params: {
    storageAccountName: st.name
    principalId: apiApp.outputs.systemAssignedMIPrincipalId!
    principalName: apiAppName
  }
}

// Indexer.
var indexerAppiName = '${solution}-agents-hr-appi-indexer-xx-${environment}'
module indexerAppi 'br/public:avm/res/insights/component:0.7.1' = {
  name: '${deployment().name}_indexer_appi'
  params: {
    name: indexerAppiName
    workspaceResourceId: logAnalyticsWorkspaceResourceId
    applicationType: 'web'
    disableIpMasking: false
    disableLocalAuth: true
    enableTelemetry: false
    kind: 'web'
  }
}

var indexerName = '${solution}-agents-hr-func-indexer-xx-${environment}'
module indexer 'br/public:avm/res/web/site:0.19.4' = {
  name: '${deployment().name}_indexer'
  params: {
    kind: 'functionapp'
    name: indexerName
    serverFarmResourceId: asp.id
    appServiceEnvironmentResourceId: ase.id
    enableTelemetry: false
    managedIdentities: {
      systemAssigned: true
    }
    // Required to allow internal networks to access the app (ASE ensures it is not public).
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      http20Enabled: true
      minTlsVersion: '1.3'
      netFrameworkVersion: 'v10.0'
      scmMinTlsVersion: '1.3'
      use32BitWorkerProcess: false
    }
    configs: [
      {
        name: 'appsettings'
        storageAccountUseIdentityAuthentication: true
        storageAccountResourceId: st.id
        applicationInsightResourceId: indexerAppi.outputs.resourceId
        retainCurrentAppSettings: false
        properties: {
          APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'Authorization=AAD'
          AzureWebJobsStorage__credential: 'managedidentity'
          FUNCTIONS_EXTENSION_VERSION: '~4'
          FUNCTIONS_WORKER_RUNTIME: 'dotnet-isolated'
          WEBSITE_VNET_ROUTE_ALL: '1'
        }
      }
    ]
  }
}

resource indexerAppiResource 'Microsoft.Insights/components@2020-02-02' existing = {
  name: indexerAppiName
}

resource indexerAppiRbac 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(indexerAppiResource.id, indexerName, 'Monitoring Metrics Publisher')
  scope: indexerAppiResource
  properties: {
    description: 'Allow indexer to publish metrics to App Insights.'
    principalId: indexer.outputs.systemAssignedMIPrincipalId!
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '3913510d-42f4-4e42-8a64-420c390055eb'
    )
  }
}
