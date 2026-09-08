@description('Location for all resources.')
param location string = resourceGroup().location

@description('Prefix for resource names.')
@minLength(3)
@maxLength(15)
param namePrefix string = 'sfml'

@description('Environment name (e.g. dev, prod).')
param environment string = 'prod'

@description('Admin username for the Linux Virtual Machine.')
param vmAdminUsername string = 'sfmladmin'

@description('SSH public key or secure password for the Linux Virtual Machine.')
@secure()
param vmAdminPasswordOrKey string

@description('Authentication type for the Linux Virtual Machine: sshPublicKey or password.')
@allowed([
  'sshPublicKey'
  'password'
])
param vmAuthType string = 'sshPublicKey'

@description('Size of the Linux Virtual Machine (Standard_D2s_v5 has 2 vCPU and 8GB RAM, ideal for SFML + clangd).')
param vmSize string = 'Standard_D2s_v5'

@description('Administrator login for Azure SQL Database.')
param sqlAdminUsername string = 'sfmlsqladmin'

@description('Administrator password for Azure SQL Database.')
@secure()
param sqlAdminPassword string

@description('SKU for Azure SQL Database (Basic is $5/month, 5 DTU, 2GB storage, ideal for 2-7 students).')
param sqlSkuName string = 'Basic'

@description('Optional custom domain or DNS prefix for the Public IP.')
param dnsLabelPrefix string = ''

@description('Whether to create RBAC role assignments (requires Owner/User Access Administrator role). Set false for Contributor role.')
param enableRoleAssignments bool = false

var uniqueSuffix = uniqueString(resourceGroup().id)
var cleanPrefix = take(replace(toLower(namePrefix), '-', ''), 8)
var acrName = '${cleanPrefix}acr${take(uniqueSuffix, 10)}'
var storageAccountName = '${cleanPrefix}stg${take(uniqueSuffix, 10)}'
var sqlServerName = '${namePrefix}sql-${uniqueSuffix}'
var sqlDatabaseName = 'SfmlPlayground'
var vnetName = '${namePrefix}-vnet-${environment}'
var subnetName = 'default'
var nsgName = '${namePrefix}-nsg-${environment}'
var publicIpName = '${namePrefix}-pip-${environment}'
var nicName = '${namePrefix}-nic-${environment}'
var vmName = '${namePrefix}-vm-${environment}'
var computedDnsPrefix = empty(dnsLabelPrefix) ? '${namePrefix}-${uniqueSuffix}' : dnsLabelPrefix

// ==========================================
// 1. Azure Container Registry (ACR)
// ==========================================
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
}

// ==========================================
// 2. Azure Storage Account (Blob Storage)
// ==========================================
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    accessTier: 'Hot'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

resource assetContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'sfml-assets'
  properties: {
    publicAccess: 'None'
  }
}

// ==========================================
// 3. Azure SQL Database
// ==========================================
resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminUsername
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource allowAzureIps 'Microsoft.Sql/servers/firewallRules@2023-05-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServicesAndResources'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  sku: {
    name: sqlSkuName
    tier: sqlSkuName == 'Basic' ? 'Basic' : 'Standard'
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 2147483648 // 2 GB
  }
}

// ==========================================
// 4. Virtual Network & NSG
// ==========================================
resource nsg 'Microsoft.Network/networkSecurityGroups@2023-09-01' = {
  name: nsgName
  location: location
  properties: {
    securityRules: [
      {
        name: 'Allow-SSH'
        properties: {
          priority: 1000
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '22'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
      {
        name: 'Allow-HTTP'
        properties: {
          priority: 1010
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '80'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
      {
        name: 'Allow-HTTPS'
        properties: {
          priority: 1020
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2023-09-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
    subnets: [
      {
        name: subnetName
        properties: {
          addressPrefix: '10.0.1.0/24'
          networkSecurityGroup: {
            id: nsg.id
          }
        }
      }
    ]
  }
}

resource publicIp 'Microsoft.Network/publicIPAddresses@2023-09-01' = {
  name: publicIpName
  location: location
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    dnsSettings: {
      domainNameLabel: computedDnsPrefix
    }
  }
}

resource nic 'Microsoft.Network/networkInterfaces@2023-09-01' = {
  name: nicName
  location: location
  properties: {
    ipConfigurations: [
      {
        name: 'ipconfig1'
        properties: {
          subnet: {
            id: resourceId('Microsoft.Network/virtualNetworks/subnets', vnetName, subnetName)
          }
          privateIPAllocationMethod: 'Dynamic'
          publicIPAddress: {
            id: publicIp.id
          }
        }
      }
    ]
  }
  dependsOn: [
    vnet
  ]
}

// ==========================================
// 5. Ubuntu 22.04 LTS Virtual Machine
// ==========================================
resource vm 'Microsoft.Compute/virtualMachines@2023-09-01' = {
  name: vmName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    hardwareProfile: {
      vmSize: vmSize
    }
    storageProfile: {
      imageReference: {
        publisher: 'Canonical'
        offer: '0001-com-ubuntu-server-jammy'
        sku: '22_04-lts-gen2'
        version: 'latest'
      }
      osDisk: {
        name: '${vmName}-osdisk'
        caching: 'ReadWrite'
        createOption: 'FromImage'
        managedDisk: {
          storageAccountType: 'StandardSSD_LRS'
        }
        diskSizeGB: 64
      }
    }
    osProfile: {
      computerName: vmName
      adminUsername: vmAdminUsername
      adminPassword: vmAuthType == 'password' ? vmAdminPasswordOrKey : null
      linuxConfiguration: vmAuthType == 'sshPublicKey' ? {
        disablePasswordAuthentication: true
        ssh: {
          publicKeys: [
            {
              path: '/home/${vmAdminUsername}/.ssh/authorized_keys'
              keyData: vmAdminPasswordOrKey
            }
          ]
        }
      } : null
    }
    networkProfile: {
      networkInterfaces: [
        {
          id: nic.id
        }
      ]
    }
  }
}

// ==========================================
// 6. Role Assignments (Managed Identity -> ACR Pull)
// ==========================================
// AcrPull role definition ID: 7f951dda-4ed3-4680-a7ca-43fe172d538d
resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableRoleAssignments) {
  name: guid(acr.id, vm.id, 'AcrPull')
  scope: acr
  properties: {
    principalId: vm.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
}

// ==========================================
// 7. Outputs
// ==========================================
output vmName string = vm.name
output vmPublicIp string = publicIp.properties.ipAddress
output vmFqdn string = publicIp.properties.dnsSettings.fqdn
output acrLoginServer string = acr.properties.loginServer
output acrName string = acr.name
output storageAccountName string = storageAccount.name
output storageContainerName string = assetContainer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name
output connectionStringTemplate string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${sqlDatabase.name};Persist Security Info=False;User ID=${sqlAdminUsername};Password=<PASSWORD>;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
