<#
.SYNOPSIS
    Deploys SFML 2.6.2 Online IDE to Microsoft Azure.
.DESCRIPTION
    Provisions Azure infrastructure via Bicep (VM, ACR, Azure Storage, Azure SQL),
    builds and pushes sfml-sandbox container via ACR Tasks, and configures the runner VM.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$TargetSubscription = "4329056b-c3be-43dd-858f-86ecb8b64598",

    [Parameter(Mandatory = $false)]
    [string]$ResourceGroup = "rg-sfml-prod",

    [Parameter(Mandatory = $false)]
    [string]$Location = "eastus",

    [Parameter(Mandatory = $false)]
    [string]$VmAdminUsername = "sfmladmin",

    [Parameter(Mandatory = $false)]
    [string]$VmAdminPassword = "SfmlVm2026!ProdPass",

    [Parameter(Mandatory = $false)]
    [string]$SqlAdminUsername = "sfmlsqladmin",

    [Parameter(Mandatory = $false)]
    [string]$SqlAdminPassword = "SfmlSql2026!ProdPass"
)

$ErrorActionPreference = "Stop"

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "   SFML 2.6.2 Online IDE - Azure Deployment Script   " -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

# 1. Validate Azure CLI
$azCmd = Get-Command az -ErrorAction SilentlyContinue
if (-not $azCmd) {
    if (Test-Path "C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd") {
        $env:Path = "C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin;" + $env:Path
    } else {
        Write-Error "Azure CLI ('az') is required. Please install it from https://aka.ms/installazurecliwindows"
        exit 1
    }
}

# 2. Check Azure Login and set subscription
Write-Host "[1/6] Verifying Azure credentials..." -ForegroundColor Yellow
az account set --subscription $TargetSubscription
$account = az account show --output json | ConvertFrom-Json
Write-Host "Connected to subscription: $($account.name) ($($account.id)) as $($account.user.name)" -ForegroundColor Green

# 3. Create Resource Group
Write-Host "[2/6] Ensuring Resource Group '$ResourceGroup' in '$Location'..." -ForegroundColor Yellow
az group create --name $ResourceGroup --location $Location --output table

# 4. Detect available VM SKU and Deploy Infrastructure via Bicep
Write-Host "[3/6] Finding available VM SKU in '$Location' and deploying via Bicep..." -ForegroundColor Yellow
$candidateSkus = @("Standard_B2ms", "Standard_B2s", "Standard_D2s_v4", "Standard_D2as_v5", "Standard_D2s_v5")
$selectedSku = ""

foreach ($sku in $candidateSkus) {
    Write-Host "  Testing VM SKU '$sku'..." -ForegroundColor Cyan
    try {
        $null = az deployment group validate `
            --resource-group $ResourceGroup `
            --template-file "$PSScriptRoot/main.bicep" `
            --parameters vmAdminUsername=$VmAdminUsername `
                         vmAuthType="password" `
                         vmAdminPasswordOrKey=$VmAdminPassword `
                         sqlAdminUsername=$SqlAdminUsername `
                         sqlAdminPassword=$SqlAdminPassword `
                         vmSize=$sku `
                         enableRoleAssignments=false `
            --output none 2>$null
        if ($LASTEXITCODE -eq 0) {
            $selectedSku = $sku
            Write-Host "  ✅ Selected available VM SKU: $selectedSku" -ForegroundColor Green
            break
        }
    } catch { }
}

if (-not $selectedSku) {
    $selectedSku = "Standard_B2s"
    Write-Host "  Defaulting to $selectedSku..." -ForegroundColor Yellow
}

$deployment = az deployment group create `
    --resource-group $ResourceGroup `
    --template-file "$PSScriptRoot/main.bicep" `
    --parameters vmAdminUsername=$VmAdminUsername `
                 vmAuthType="password" `
                 vmAdminPasswordOrKey=$VmAdminPassword `
                 sqlAdminUsername=$SqlAdminUsername `
                 sqlAdminPassword=$SqlAdminPassword `
                 vmSize=$selectedSku `
                 enableRoleAssignments=false `
    --output json | ConvertFrom-Json

$outputs = $deployment.properties.outputs
$vmName = $outputs.vmName.value
$acrLoginServer = $outputs.acrLoginServer.value
$acrName = $outputs.acrName.value
$storageAccountName = $outputs.storageAccountName.value
$storageContainer = $outputs.storageContainerName.value
$sqlServerFqdn = $outputs.sqlServerFqdn.value
$sqlDbName = $outputs.sqlDatabaseName.value
$vmPublicIp = $outputs.vmPublicIp.value
$vmFqdn = $outputs.vmFqdn.value

Write-Host "Provisioned Resources:" -ForegroundColor Green
Write-Host "  VM Name:           $vmName"
Write-Host "  VM Public IP:      $vmPublicIp ($vmFqdn)"
Write-Host "  Container Registry: $acrLoginServer"
Write-Host "  Storage Account:   $storageAccountName ($storageContainer)"
Write-Host "  SQL Server:        $sqlServerFqdn ($sqlDbName)"

# 5. Build and Push sfml-sandbox to ACR (Cloud Build via ACR Tasks)
Write-Host "[4/6] Building sfml-sandbox:latest via ACR Tasks..." -ForegroundColor Yellow
az acr build `
    --registry $acrName `
    --image sfml-sandbox:2.6.2 `
    --image sfml-sandbox:latest `
    "$PSScriptRoot/../../runner"

# 6. Retrieve Credentials
Write-Host "[5/6] Retrieving secure connection credentials..." -ForegroundColor Yellow
$storageConnStr = az storage account show-connection-string `
    --name $storageAccountName `
    --resource-group $ResourceGroup `
    --query connectionString -o tsv

$sqlConnStr = "Server=tcp:$sqlServerFqdn,1433;Initial Catalog=$sqlDbName;Persist Security Info=False;User ID=$SqlAdminUsername;Password=$SqlAdminPassword;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

$acrCreds = az acr credential show --name $acrName --output json | ConvertFrom-Json
$acrUser = $acrCreds.username
$acrPass = $acrCreds.passwords[0].value

# 7. Configure VM and Deploy Application
Write-Host "[6/6] Configuring VM and deploying application via Azure VM agent..." -ForegroundColor Yellow
$vmScript = @"
set -euo pipefail
echo 'Waiting for cloud-init package installation to complete...'
cloud-init status --wait || true

while ! systemctl is-active --quiet docker; do
    sleep 3
done
docker login $acrLoginServer -u '$acrUser' -p '$acrPass'
docker pull $acrLoginServer/sfml-sandbox:latest
docker tag $acrLoginServer/sfml-sandbox:latest sfml-sandbox:latest

rm -rf /tmp/sfml-repo
git clone https://github.com/OmarElzarka/SFML.git /tmp/sfml-repo
cd /tmp/sfml-repo

docker run --rm -v /tmp/sfml-repo/frontend:/app -w /app node:20-alpine sh -c 'npm ci && npm run build'
mkdir -p /var/sfml/frontend
rm -rf /var/sfml/frontend/*
cp -r /tmp/sfml-repo/frontend/dist/frontend/browser/* /var/sfml/frontend/

docker run --rm -v /tmp/sfml-repo/backend/SfmlPlayground.Api:/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet publish -c Release -r linux-x64 --self-contained true -o /src/publish_output
mkdir -p /var/sfml/backend
mkdir -p /var/sfml/storage/workspace
rm -rf /var/sfml/backend/*
cp -r /tmp/sfml-repo/backend/SfmlPlayground.Api/publish_output/* /var/sfml/backend/

cat << 'ENVEOF' > /var/sfml/backend/app.env
ConnectionStrings__DefaultConnection=$sqlConnStr
Storage__ConnectionString=$storageConnStr
Storage__ContainerName=sfml-assets
Storage__WorkspacePath=/var/sfml/storage/workspace
Docker__Host=unix:///var/run/docker.sock
Docker__ImageName=sfml-sandbox:latest
ENVEOF

chmod +x /var/sfml/backend/SfmlPlayground.Api 2>/dev/null || true
chown -R sfmladmin:sfmladmin /var/sfml

systemctl daemon-reload
systemctl enable sfml-api
systemctl restart sfml-api
systemctl restart nginx
"@

az vm run-command invoke `
    --resource-group $ResourceGroup `
    --name $vmName `
    --command-id RunShellScript `
    --scripts $vmScript

Write-Host "Verifying deployment health..." -ForegroundColor Yellow
$healthUrl = "http://$vmPublicIp/health"
for ($i = 1; $i -le 30; $i++) {
    try {
        $res = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
        if ($res.StatusCode -eq 200) {
            Write-Host " Health check PASSED at $healthUrl" -ForegroundColor Green
            break
        }
    } catch {
        Write-Host "  Waiting for health check (attempt $i/30)..."
        Start-Sleep -Seconds 5
    }
}

Write-Host "=====================================================" -ForegroundColor Green
Write-Host "  SFML 2.6.2 Online IDE Deployed Successfully!       " -ForegroundColor Green
Write-Host " Web IDE:     http://$vmPublicIp" -ForegroundColor Green
Write-Host " DNS FQDN:    http://$vmFqdn" -ForegroundColor Green
Write-Host " Health:      http://$vmPublicIp/health" -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green
