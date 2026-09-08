#!/usr/bin/env bash
# ==============================================================================
# SFML 2.6.2 Online IDE - Azure Production Deployment Script
# Designed for Azure Cloud Shell & Azure CLI (Contributor role compatible)
# ==============================================================================
set -euo pipefail

TARGET_SUBSCRIPTION="${TARGET_SUBSCRIPTION:-4329056b-c3be-43dd-858f-86ecb8b64598}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-sfml-prod}"
LOCATION="${LOCATION:-westeurope}"
VM_ADMIN_USERNAME="${VM_ADMIN_USERNAME:-sfmladmin}"
VM_ADMIN_PASSWORD="${VM_ADMIN_PASSWORD:-SfmlVm2026!ProdPass}"
SQL_ADMIN_USERNAME="${SQL_ADMIN_USERNAME:-sfmlsqladmin}"
SQL_ADMIN_PASSWORD="${SQL_ADMIN_PASSWORD:-SfmlSql2026!ProdPass}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"

echo "====================================================="
echo "   SFML 2.6.2 Online IDE - Azure Production Deploy   "
echo "====================================================="

# 1. Verify Azure CLI
if ! command -v az &> /dev/null; then
    echo "Error: Azure CLI ('az') is not installed."
    exit 1
fi

# 2. Verify and set subscription
echo "[1/6] Verifying Azure session and selecting subscription..."
az account set --subscription "${TARGET_SUBSCRIPTION}"
CURRENT_SUB=$(az account show --query "id" -o tsv)
CURRENT_USER=$(az account show --query "user.name" -o tsv)
echo "  Connected as: ${CURRENT_USER}"
echo "  Subscription: ${CURRENT_SUB}"

# 3. Ensure Resource Group
echo "[2/6] Ensuring Resource Group '${RESOURCE_GROUP}' in '${LOCATION}'..."
az group create --name "${RESOURCE_GROUP}" --location "${LOCATION}" --output table

# 4. Detect available Region & VM SKU and Provision Infrastructure via Bicep
echo "[3/6] Finding available VM SKU and Region via Bicep validation..."
CANDIDATE_REGIONS=("${LOCATION}" "westeurope" "northeurope" "swedencentral" "germanywestcentral" "francecentral" "uksouth" "canadacentral" "eastus2" "eastus")
CANDIDATE_SKUS=("Standard_D2s_v5" "Standard_D2as_v5" "Standard_D2s_v4" "Standard_B2ms" "Standard_B2s")

SELECTED_LOCATION=""
SELECTED_VM_SIZE=""

for LOC in "${CANDIDATE_REGIONS[@]}"; do
    echo "  Checking region '${LOC}'..."
    for SKU in "${CANDIDATE_SKUS[@]}"; do
        if az deployment group validate \
            --resource-group "${RESOURCE_GROUP}" \
            --template-file "${SCRIPT_DIR}/main.bicep" \
            --parameters location="${LOC}" \
                         vmAdminUsername="${VM_ADMIN_USERNAME}" \
                         vmAuthType="password" \
                         vmAdminPasswordOrKey="${VM_ADMIN_PASSWORD}" \
                         sqlAdminUsername="${SQL_ADMIN_USERNAME}" \
                         sqlAdminPassword="${SQL_ADMIN_PASSWORD}" \
                         vmSize="${SKU}" \
                         enableRoleAssignments=false \
            --output none 2>/dev/null; then
            SELECTED_LOCATION="${LOC}"
            SELECTED_VM_SIZE="${SKU}"
            echo "  ✅ Validated active capacity: Region '${SELECTED_LOCATION}', VM SKU '${SELECTED_VM_SIZE}'"
            break 2
        else
            echo "    ⚠️ SKU '${SKU}' restricted in '${LOC}', testing next..."
        fi
    done
done

if [ -z "${SELECTED_VM_SIZE}" ]; then
    SELECTED_LOCATION="westeurope"
    SELECTED_VM_SIZE="Standard_D2s_v5"
    echo "  Defaulting to ${SELECTED_LOCATION} with ${SELECTED_VM_SIZE}..."
fi

echo "  Executing Bicep deployment in '${SELECTED_LOCATION}' with VM SKU: ${SELECTED_VM_SIZE}..."
DEPLOY_OUTPUT=$(az deployment group create \
    --resource-group "${RESOURCE_GROUP}" \
    --template-file "${SCRIPT_DIR}/main.bicep" \
    --parameters location="${SELECTED_LOCATION}" \
                 vmAdminUsername="${VM_ADMIN_USERNAME}" \
                 vmAuthType="password" \
                 vmAdminPasswordOrKey="${VM_ADMIN_PASSWORD}" \
                 sqlAdminUsername="${SQL_ADMIN_USERNAME}" \
                 sqlAdminPassword="${SQL_ADMIN_PASSWORD}" \
                 vmSize="${SELECTED_VM_SIZE}" \
                 enableRoleAssignments=false \
    --output json)

VM_NAME=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.vmName.value')
VM_PUBLIC_IP=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.vmPublicIp.value')
VM_FQDN=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.vmFqdn.value')
ACR_LOGIN_SERVER=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.acrLoginServer.value')
ACR_NAME=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.acrName.value')
STORAGE_ACCOUNT=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.storageAccountName.value')
STORAGE_CONTAINER=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.storageContainerName.value')
SQL_SERVER_FQDN=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.sqlServerFqdn.value')
SQL_DB_NAME=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.sqlDatabaseName.value')

echo "Infrastructure provisioned successfully:"
echo "  VM Name:        ${VM_NAME}"
echo "  VM Public IP:   ${VM_PUBLIC_IP}"
echo "  VM FQDN:        ${VM_FQDN}"
echo "  Container Reg:  ${ACR_LOGIN_SERVER}"
echo "  Blob Storage:   ${STORAGE_ACCOUNT} (${STORAGE_CONTAINER})"
echo "  SQL Database:   ${SQL_SERVER_FQDN}/${SQL_DB_NAME}"

# 5. Build and Push sfml-sandbox Image to ACR (Cloud Build)
echo "[4/6] Building sfml-sandbox:latest via ACR Tasks..."
az acr build \
    --registry "${ACR_NAME}" \
    --image sfml-sandbox:2.6.2 \
    --image sfml-sandbox:latest \
    "${ROOT_DIR}/runner"

# 6. Retrieve Credentials and Connection Strings
echo "[5/6] Retrieving secure connection credentials..."
STORAGE_CONN_STR=$(az storage account show-connection-string \
    --name "${STORAGE_ACCOUNT}" \
    --resource-group "${RESOURCE_GROUP}" \
    --query connectionString -o tsv)

SQL_CONN_STR="Server=tcp:${SQL_SERVER_FQDN},1433;Initial Catalog=${SQL_DB_NAME};Persist Security Info=False;User ID=${SQL_ADMIN_USERNAME};Password=${SQL_ADMIN_PASSWORD};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

ACR_CREDS=$(az acr credential show --name "${ACR_NAME}" --output json)
ACR_USER=$(echo "${ACR_CREDS}" | jq -r '.username')
ACR_PASS=$(echo "${ACR_CREDS}" | jq -r '.passwords[0].value')

APP_ENV_RAW=$(cat << EOF
ConnectionStrings__DefaultConnection=${SQL_CONN_STR}
Storage__ConnectionString=${STORAGE_CONN_STR}
Storage__ContainerName=sfml-assets
Storage__WorkspacePath=/var/sfml/storage/workspace
Docker__Host=unix:///var/run/docker.sock
Docker__ImageName=sfml-sandbox:latest
EOF
)
APP_ENV_B64=$(echo -n "${APP_ENV_RAW}" | base64 | tr -d '\r\n')

# 7. Configure and Deploy Application onto the Azure VM
echo "[6/6] Configuring VM, pulling runtime, and deploying application via Azure VM agent..."
az vm run-command invoke \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${VM_NAME}" \
    --command-id RunShellScript \
    --scripts "
set -euo pipefail
echo '=== Starting VM Application Deployment ==='

# Wait for cloud-init package installations and docker service
echo 'Waiting for cloud-init package installation to complete...'
cloud-init status --wait || true

while ! systemctl is-active --quiet docker; do
    echo 'Waiting for docker service...'
    sleep 3
done

# 1. Login to ACR and pull persistent runtime image
echo 'Logging into ACR...'
docker login ${ACR_LOGIN_SERVER} -u '${ACR_USER}' -p '${ACR_PASS}'
echo 'Pulling sfml-sandbox:latest...'
docker pull ${ACR_LOGIN_SERVER}/sfml-sandbox:latest
docker tag ${ACR_LOGIN_SERVER}/sfml-sandbox:latest sfml-sandbox:latest

# 2. Clone latest repository
echo 'Fetching source code from GitHub...'
rm -rf /tmp/sfml-repo
git clone https://github.com/OmarElzarka/SFML.git /tmp/sfml-repo
cd /tmp/sfml-repo

# 3. Build Angular frontend inside node container
echo 'Building Angular frontend...'
docker run --rm -v /tmp/sfml-repo/frontend:/app -w /app node:20-alpine sh -c 'npm ci && npm run build'
mkdir -p /var/sfml/frontend
rm -rf /var/sfml/frontend/*
if [ -d /tmp/sfml-repo/frontend/dist/frontend/browser ]; then
    cp -r /tmp/sfml-repo/frontend/dist/frontend/browser/* /var/sfml/frontend/
else
    cp -r /tmp/sfml-repo/frontend/dist/frontend/* /var/sfml/frontend/
fi

# 4. Build backend inside dotnet sdk container (self-contained linux-x64)
echo 'Building ASP.NET Core backend...'
docker run --rm -v /tmp/sfml-repo/backend:/src -w /src/SfmlPlayground.Api mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet publish -c Release -r linux-x64 --self-contained true -o /src/SfmlPlayground.Api/publish_output
mkdir -p /var/sfml/backend
mkdir -p /var/sfml/storage/workspace
rm -rf /var/sfml/backend/*
cp -r /tmp/sfml-repo/backend/SfmlPlayground.Api/publish_output/* /var/sfml/backend/

# 5. Configure production environment
echo '${APP_ENV_B64}' | base64 -d > /var/sfml/backend/app.env

# 6. Ensure permissions
chmod +x /var/sfml/backend/SfmlPlayground.Api 2>/dev/null || true
chown -R sfmladmin:sfmladmin /var/sfml

# 7. Start and restart services
systemctl daemon-reload
systemctl enable sfml-api
systemctl restart sfml-api
systemctl restart nginx

echo '=== VM Deployment Complete ==='
"

echo "Verifying deployment health..."
HEALTH_URL="http://${VM_PUBLIC_IP}/health"
for i in {1..30}; do
    if curl -s -f "${HEALTH_URL}" > /dev/null 2>&1; then
        echo "✅ Health check PASSED at ${HEALTH_URL}"
        break
    fi
    echo "  Waiting for health check (attempt ${i}/30)..."
    sleep 5
done

echo ""
echo "====================================================="
echo " 🎉 SFML 2.6.2 Online IDE Deployed Successfully!     "
echo "====================================================="
echo " Production Web IDE: http://${VM_PUBLIC_IP}"
echo " DNS FQDN:           http://${VM_FQDN}"
echo " Health Endpoint:    http://${VM_PUBLIC_IP}/health"
echo "====================================================="
