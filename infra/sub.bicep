/*
  Deploy resources that make up the Assured Computing Environment - Landing Zones - Subscription infrastructure.
*/

/********
Scope
*********/

targetScope = 'subscription'

/********
Parameters
*********/

@description('Environment code.')
param environment string

@description('Enable Teams delegated SSO after all environment-specific identity prerequisites exist.')
param teamsSsoEnabled bool = false

/********
Variables
*********/

@description('Solution code. DO NOT CHANGE')
var solution = 'orbauto'

/********
Resources
*********/

// Resource Group - agents-hr
module rg_agents_hr 'rg-agents-hr.bicep' = {
  name: '${deployment().name}_ah'
  scope: resourceGroup('${solution}-agents-rg-hr-xx-${environment}')
  params: {
    solution: solution
    environment: environment
    teamsSsoEnabled: teamsSsoEnabled
  }
}
