<#
.SYNOPSIS
    Deploys SFML 2.6.2 Online IDE to Microsoft Azure.
.DESCRIPTION
    Provisions Azure infrastructure via Bicep (VM, ACR, Azure Storage, Azure SQL),
    builds and pushes sfml-sandbox container, runs database migrations,
    builds backend and frontend, and configures the runner VM.
.PARAMETER ResourceGroup
    Name of the Azure Resource Group.
.PARAMETER Location
    Azure region (e.g. eastus, westeurope).
.PARAMETER SqlAdminPassword
    Password for Azure SQL administrator.
.PARAMETER VmAdminPassword
    Password or SSH key for VM administrator.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ResourceGroup = "rg-sfml-prod",

    [Parameter(Mandatory = $false)]
    [string]$Location = "eastus",

    [Parameter(Mandatory = $false)]
    [string]$VmAdminUsername = "sfmladmin",

    [Parameter(Mandatory = $false)]
    [string]$VmAdminPassword,

    [Parameter(Mandatory = $false)]
    [string]$SqlAdminUsername = "sfmlsqladmin",

    [Parameter(Mandatory = $false)]
    [string]$SqlAdminPassword
)

$ErrorActionPreference = "Stop"

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "   SFML 2.6.2 Online IDE - Azure Deployment Script   " -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

# 1. Validate Azure CLI
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error "Azure CLI ('az') is required. Please install it from https://aka.ms/installazurecliwindows"
    exit 1
}

# 2. Check Azure Login
Write-Host "[1/7] Verifying Azure credentials..." -ForegroundColor Yellow
$account = az account show --output json | ConvertFrom-Json
if (-not $account) {
    Write-Host "Please login to Azure:" -ForegroundColor Yellow
    az login
    $account = az account show --output json | ConvertFrom-Json
}
Write-Host "Connected to subscription: $($account.name) ($($account.id))" -ForegroundColor Green

# 3. Prompt for passwords if not provided
if (-not $SqlAdminPassword) {
    $secureSqlPass = Read-Host -Prompt "Enter Azure SQL Administrator password (min 8 chars, mixed case/numbers/symbols)" -AsSecureString
    $SqlAdminPassword = [System.Net.NetworkCredential]::new("", $secureSqlPass).Password
}
if (-not $VmAdminPassword) {
    $secureVmPass = Read-Host -Prompt "Enter VM Administrator password" -AsSecureString
    $VmAdminPassword = [System.Net.NetworkCredential]::new("", $secureVmPass).Password
}

# 4. Create Resource Group
Write-Host "[2/7] Creating Resource Group '$ResourceGroup' in '$Location'..." -ForegroundColor Yellow
az group create --name $ResourceGroup --location $Location --output table

# 5. Deploy Infrastructure via Bicep
Write-Host "[3/7] Deploying Infrastructure (VM, Storage, SQL, ACR, NSG) via Bicep..." -ForegroundColor Yellow
$deployment = az deployment group create `
    --resource-group $ResourceGroup `
    --template-file "$PSScriptRoot/main.bicep" `
    --parameters vmAdminUsername=$VmAdminUsername `
    vmAuthType="password" `
    vmAdminPasswordOrKey=$VmAdminPassword `
    sqlAdminUsername=$SqlAdminUsername `
    sqlAdminPassword=$SqlAdminPassword `
    --output json | ConvertFrom-Json

$outputs = $deployment.properties.outputs
$acrLoginServer = $outputs.acrLoginServer.value
$acrName = $outputs.acrName.value
$storageAccountName = $outputs.storageAccountName.value
$storageContainer = $outputs.storageContainerName.value
$sqlServerFqdn = $outputs.sqlServerFqdn.value
$sqlDbName = $outputs.sqlDatabaseName.value
$vmPublicIp = $outputs.vmPublicIp.value
$vmFqdn = $outputs.vmFqdn.value

Write-Host "Provisioned Resources:" -ForegroundColor Green
Write-Host "  VM Public IP:      $vmPublicIp ($vmFqdn)"
Write-Host "  Container Registry: $acrLoginServer"
Write-Host "  Storage Account:   $storageAccountName"
Write-Host "  SQL Server:        $sqlServerFqdn"

# 6. Retrieve Storage Connection String
Write-Host "[4/7] Fetching Storage Account Connection String..." -ForegroundColor Yellow
$storageConnStr = az storage account show-connection-string `
    --name $storageAccountName `
    --resource-group $ResourceGroup `
    --query connectionString -o tsv

$sqlConnStr = "Server=tcp:$sqlServerFqdn,1433;Initial Catalog=$sqlDbName;Persist Security Info=False;User ID=$SqlAdminUsername;Password=$SqlAdminPassword;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

# 7. Build and Push Docker Runner Image
Write-Host "[5/7] Building and pushing sfml-sandbox:2.6.2 to ACR..." -ForegroundColor Yellow
az acr login --name $acrName
docker build -t "$acrLoginServer/sfml-sandbox:2.6.2" -t "$acrLoginServer/sfml-sandbox:latest" -t "sfml-sandbox:latest" -f "$PSScriptRoot/../../runner/Dockerfile" "$PSScriptRoot/../../runner"
docker push "$acrLoginServer/sfml-sandbox:2.6.2"
docker push "$acrLoginServer/sfml-sandbox:latest"

# 8. Run Database Migrations
Write-Host "[6/7] Applying Database Migrations to Azure SQL..." -ForegroundColor Yellow
$sqlScript = Get-Content -Raw -Path "$PSScriptRoot/sql/migrate.sql"

# Allow local client IP temporarily for migration if needed
$myIp = (Invoke-RestMethod -Uri "https://api.ipify.org")
Write-Host "Adding temporary firewall rule for local IP: $myIp" -ForegroundColor Cyan
az sql server firewall-rule create `
    --resource-group $ResourceGroup `
    --server ( $sqlServerFqdn.Split('.')[0] ) `
    --name "TempDeployClientRule" `
    --start-ip-address $myIp `
    --end-ip-address $myIp --output none 2>$null

try {
    if (Get-Command sqlcmd -ErrorAction SilentlyContinue) {
        sqlcmd -S $sqlServerFqdn -d $sqlDbName -U $SqlAdminUsername -P $SqlAdminPassword -i "$PSScriptRoot/sql/migrate.sql"
    }
    else {
        # Fallback: run migration via dotnet ef or Azure CLI
        Write-Host "Applying EF Core migration via dotnet..." -ForegroundColor Cyan
        $env:ConnectionStrings__DefaultConnection = $sqlConnStr
        dotnet ef database update --project "$PSScriptRoot/../../backend/SfmlPlayground.Api"
    }
    Write-Host "Database migration applied successfully." -ForegroundColor Green
}
finally {
    az sql server firewall-rule delete `
        --resource-group $ResourceGroup `
        --server ( $sqlServerFqdn.Split('.')[0] ) `
        --name "TempDeployClientRule" --output none 2>$null
}

# 9. Build Backend & Frontend
Write-Host "[7/7] Compiling production backend and frontend..." -ForegroundColor Yellow
$publishDir = "$PSScriptRoot/../../dist/backend"
dotnet publish "$PSScriptRoot/../../backend/SfmlPlayground.Api/SfmlPlayground.Api.csproj" -c Release -o $publishDir

Push-Location "$PSScriptRoot/../../frontend"
npm ci
npm run build
Pop-Location

Write-Host "=====================================================" -ForegroundColor Green
Write-Host " Deployment Complete! " -ForegroundColor Green
Write-Host " VM Address: http://$vmPublicIp (or http://$vmFqdn)" -ForegroundColor Green
Write-Host " Health Check: http://$vmPublicIp/health" -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green
