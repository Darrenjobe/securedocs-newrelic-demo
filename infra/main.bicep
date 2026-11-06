// SecureDocs demo: Linux App Service plan with two apps (portal + API) and an Azure SQL database,
// both apps instrumented with the New Relic .NET agent (bundled in the app via the NewRelic.Agent NuGet package).

@description('Azure region for the App Service plan and web apps')
param location string = resourceGroup().location

@description('Azure region for Azure SQL (can differ from the web apps, e.g. when a region is not accepting new SQL servers)')
param sqlLocation string = location

@description('Short prefix used in resource names')
@maxLength(12)
param namePrefix string = 'securedocs'

@description('App Service plan SKU: B1 (default), S1, P0v3, P0v4, P1v4 ... or F1 (free, limited). Use a tier your subscription has quota for (Quotas → App Service).')
param appServiceSku string = 'B1'

@description('Azure SQL database SKU (S2 = 50 DTU; S0 saturates under Auto traffic + SQL monitoring)')
param sqlSkuName string = 'S2'

@description('SQL administrator login')
param sqlAdminLogin string = 'securedocsadmin'

@secure()
@description('SQL administrator password')
param sqlAdminPassword string

@secure()
@description('New Relic ingest license key')
param newRelicLicenseKey string

@secure()
@description('Optional New Relic User API key (NRAK-...). When set, turning on a Fault Control switch records a deployment marker on the affected app.')
param newRelicUserApiKey string = ''

@description('Optional New Relic account ID that owns the APM apps (used to find them for deployment markers)')
param newRelicAccountId string = ''

@description('Shared secret the portal sends to the API to change fault settings')
@secure()
param faultControlKey string = newGuid()

var suffix = uniqueString(resourceGroup().id)
// Free/Shared tiers don't support Always On.
var alwaysOn = !contains(['F1', 'D1', 'FREE', 'SHARED'], toUpper(appServiceSku))
var planName = '${namePrefix}-plan-${suffix}'
var portalName = '${namePrefix}-portal-${suffix}'
var apiName = '${namePrefix}-api-${suffix}'
// Region is part of the name so a failed attempt in another region can't block the name.
var sqlServerName = '${namePrefix}-sql-${uniqueString(resourceGroup().id, sqlLocation)}'
var sqlDbName = 'SecureDocs'

// New Relic .NET agent on Linux App Service: the agent files ship inside the app under /newrelic.
var newRelicSettings = [
  { name: 'CORECLR_ENABLE_PROFILING', value: '1' }
  { name: 'CORECLR_PROFILER', value: '{36032161-FFC0-4B61-B559-F6C5D41BAE5A}' }
  { name: 'CORECLR_PROFILER_PATH', value: '/home/site/wwwroot/newrelic/libNewRelicProfiler.so' }
  { name: 'CORECLR_NEWRELIC_HOME', value: '/home/site/wwwroot/newrelic' }
  { name: 'NEW_RELIC_HOME', value: '/home/site/wwwroot/newrelic' }
  { name: 'NEWRELIC_LOG_DIRECTORY', value: '/home/LogFiles/NewRelic' }
  { name: 'NEW_RELIC_LICENSE_KEY', value: newRelicLicenseKey }
  { name: 'NEW_RELIC_DISTRIBUTED_TRACING_ENABLED', value: 'true' }
  { name: 'NEW_RELIC_LABELS', value: 'environment:demo;owner:securedocs' }
  // Deployment markers for Fault Control switches (off when the key is empty).
  { name: 'NewRelic__UserApiKey', value: newRelicUserApiKey }
  { name: 'NewRelic__AccountId', value: newRelicAccountId }
]

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  kind: 'linux'
  sku: { name: appServiceSku }
  properties: { reserved: true }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: sqlLocation
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// Lets Azure services (the App Service apps) reach the server.
resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDbName
  location: sqlLocation
  sku: { name: sqlSkuName }
}

var sqlConnection = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${sqlDbName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;'

resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: apiName
  location: location
  kind: 'app,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet SecureDocs.Api.dll'
      alwaysOn: alwaysOn
      healthCheckPath: alwaysOn ? '/health' : null
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: concat(newRelicSettings, [
        { name: 'NEW_RELIC_APP_NAME', value: 'SecureDocs-API' }
        { name: 'Database__Provider', value: 'SqlServer' }
        { name: 'Faults__ControlKey', value: faultControlKey }
        { name: 'Ftp__InboxPath', value: '/home/data/securedocs/ftp-inbox' }
      ])
      connectionStrings: [
        { name: 'SecureDocs', connectionString: sqlConnection, type: 'SQLAzure' }
      ]
    }
  }
}

resource portal 'Microsoft.Web/sites@2023-12-01' = {
  name: portalName
  location: location
  kind: 'app,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet SecureDocs.Web.dll'
      alwaysOn: alwaysOn
      healthCheckPath: alwaysOn ? '/health' : null
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: concat(newRelicSettings, [
        { name: 'NEW_RELIC_APP_NAME', value: 'SecureDocs-Portal' }
        { name: 'Api__BaseUrl', value: 'https://${api.properties.defaultHostName}' }
        { name: 'Api__PublicUrl', value: 'https://${api.properties.defaultHostName}' }
        { name: 'Faults__ControlKey', value: faultControlKey }
      ])
    }
  }
}

output portalAppName string = portal.name
output apiAppName string = api.name
output portalUrl string = 'https://${portal.properties.defaultHostName}'
output apiUrl string = 'https://${api.properties.defaultHostName}'
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDbName
