@description('Azure location.')
param location string = resourceGroup().location

@description('Storage SKU.')
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'
  'Standard_RAGRS'
])
param sku string = 'Standard_ZRS'

@description('Log Analytics workspace resource ID for diagnostics.')
param logAnalyticsWorkspaceId string

@description('Subnet resource ID for private endpoint (if enabled).')
param privateEndpointSubnetId string

@description('Optional tags applied to all resources.')
param tags object = {}

@description('Toggle diagnostic settings creation.')
param enableDiagnostics bool = true
param enablePrivateEndpoint bool


resource __saFriendlyId__ 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: sku
  }
  kind: 'StorageV2'
  tags: tags
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    publicNetworkAccess: enablePrivateEndpoint ? 'Disabled' : 'Enabled'
    supportsHttpsTrafficOnly: true
    encryption: {
      services: {
        file: {
          keyType: 'Account'
        }
        blob: {
          keyType: 'Account'
        }
      }
      keySource: 'Microsoft.Storage'
    }
  }
}

// Use underscores in symbol names to avoid invalid identifier characters
resource __saFriendlyId___diag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (enableDiagnostics) {
  name: '${storageAccountName}-diag'
  scope: __saFriendlyId__
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      {
        category: 'StorageRead'
        enabled: true
      }
      {
        category: 'StorageWrite'
        enabled: true
      }
      {
        category: 'StorageDelete'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

var __saFriendlyId___pe_services = [
  'blob'
  'file'
  'queue'
  'table'
  'dfs'
]

resource __saFriendlyId___pe 'Microsoft.Network/privateEndpoints@2025-01-01' = [
  for svc in __saFriendlyId___pe_services: if (enablePrivateEndpoint) {
    name: '${storageAccountName}-pe-${svc}'
    location: location
    properties: {
      subnet: {
        id: privateEndpointSubnetId
      }
      privateLinkServiceConnections: [
        {
          name: '${storageAccountName}-${svc}'
          properties: {
            groupIds: [
              svc
            ]
            privateLinkServiceId: __saFriendlyId__.id
          }
        }
      ]
    }
  }
]

output resourceId string = __saFriendlyId__.id
output primaryEndpoints object = __saFriendlyId__.properties.primaryEndpoints
