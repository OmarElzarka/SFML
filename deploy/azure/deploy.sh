#!/usr/bin/env bash
# ==============================================================================
# SFML 2.6.2 Online IDE - Azure Production Deployment Script
# ==============================================================================
set -euo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-sfml-prod}"
LOCATION="${LOCATION:-eastus}"
VM_ADMIN_USERNAME="${VM_ADMIN_USERNAME:-sfmladmin}"
SQL_ADMIN_USERNAME="${SQL_ADMIN_USERNAME:-sfmlsqladmin}"
SQL_ADMIN_PASSWORD="${SQL_ADMIN_PASSWORD:-}"
VM_ADMIN_KEY="${VM_ADMIN_KEY:-}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"

echo "=== SFML 2.6.2 Online IDE Azure Deployment ==="

# 1. Verify az CLI
if ! command -v az &> /dev/null; then
    echo "Error: Azure CLI ('az') is not installed."
    exit 1
fi

# 2. Verify az login
echo "[1/7] Verifying Azure login..."
az account show --output none || az login

# 3. Handle Credentials
if [ -z "${SQL_ADMIN_PASSWORD}" ]; then
    read -rsp "Enter Azure SQL Administrator Password: " SQL_ADMIN_PASSWORD
    echo
fi

if [ -z "${VM_ADMIN_KEY}" ]; then
    if [ -f "${HOME}/.ssh/id_rsa.pub" ]; then
        VM_ADMIN_KEY="$(cat "${HOME}/.ssh/id_rsa.pub")"
    else
        read -rp "Enter SSH Public Key or Password for VM: " VM_ADMIN_KEY
    fi
fi

# 4. Create Resource Group
echo "[2/7] Ensuring Resource Group '${RESOURCE_GROUP}' in '${LOCATION}'..."
az group create --name "${RESOURCE_GROUP}" --location "${LOCATION}" --output table

# 5. Provision Infrastructure via Bicep
echo "[3/7] Provisioning Azure Infrastructure via Bicep..."
AUTH_TYPE="sshPublicKey"
if [[ "${VM_ADMIN_KEY}" != ssh-* ]]; then
    AUTH_TYPE="password"
fi

DEPLOY_OUTPUT=$(az deployment group create \
    --resource-group "${RESOURCE_GROUP}" \
    --template-file "${SCRIPT_DIR}/main.bicep" \
    --parameters vmAdminUsername="${VM_ADMIN_USERNAME}" \
                 vmAuthType="${AUTH_TYPE}" \
                 vmAdminPasswordOrKey="${VM_ADMIN_KEY}" \
                 sqlAdminUsername="${SQL_ADMIN_USERNAME}" \
                 sqlAdminPassword="${SQL_ADMIN_PASSWORD}" \
    --output json)

ACR_LOGIN_SERVER=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.acrLoginServer.value')
ACR_NAME=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.acrName.value')
STORAGE_ACCOUNT=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.storageAccountName.value')
STORAGE_CONTAINER=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.storageContainerName.value')
SQL_SERVER_FQDN=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.sqlServerFqdn.value')
SQL_DB_NAME=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.sqlDatabaseName.value')
VM_PUBLIC_IP=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.vmPublicIp.value')
VM_FQDN=$(echo "${DEPLOY_OUTPUT}" | jq -r '.properties.outputs.vmFqdn.value')

echo "Infrastructure deployed successfully:"
echo "  VM Public IP:   ${VM_PUBLIC_IP}"
echo "  VM FQDN:        ${VM_FQDN}"
echo "  ACR:            ${ACR_LOGIN_SERVER}"
echo "  Storage:        ${STORAGE_ACCOUNT}"
echo "  SQL Database:   ${SQL_SERVER_FQDN}/${SQL_DB_NAME}"

# 6. Retrieve Storage Connection String
echo "[4/7] Retrieving Storage Connection String..."
STORAGE_CONN_STR=$(az storage account show-connection-string \
    --name "${STORAGE_ACCOUNT}" \
    --resource-group "${RESOURCE_GROUP}" \
    --query connectionString -o tsv)

SQL_CONN_STR="Server=tcp:${SQL_SERVER_FQDN},1433;Initial Catalog=${SQL_DB_NAME};Persist Security Info=False;User ID=${SQL_ADMIN_USERNAME};Password=${SQL_ADMIN_PASSWORD};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

# 7. Build and Push sfml-sandbox Docker Image
echo "[5/7] Building and pushing sfml-sandbox:2.6.2..."
az acr login --name "${ACR_NAME}"
docker build -t "${ACR_LOGIN_SERVER}/sfml-sandbox:2.6.2" -t "${ACR_LOGIN_SERVER}/sfml-sandbox:latest" -t "sfml-sandbox:latest" -f "${ROOT_DIR}/runner/Dockerfile" "${ROOT_DIR}/runner"
docker push "${ACR_LOGIN_SERVER}/sfml-sandbox:2.6.2"
docker push "${ACR_LOGIN_SERVER}/sfml-sandbox:latest"

# 8. Database Migrations
echo "[6/7] Applying Database Migrations..."
MY_IP=$(curl -s https://api.ipify.org || echo "")
if [ -n "${MY_IP}" ]; then
    SQL_SERVER_PREFIX=$(echo "${SQL_SERVER_FQDN}" | cut -d'.' -f1)
    az sql server firewall-rule create \
        --resource-group "${RESOURCE_GROUP}" \
        --server "${SQL_SERVER_PREFIX}" \
        --name "TempDeployRule" \
        --start-ip-address "${MY_IP}" \
        --end-ip-address "${MY_IP}" --output none 2>/dev/null || true
fi

if command -v sqlcmd &> /dev/null; then
    sqlcmd -S "${SQL_SERVER_FQDN}" -d "${SQL_DB_NAME}" -U "${SQL_ADMIN_USERNAME}" -P "${SQL_ADMIN_PASSWORD}" -i "${SCRIPT_DIR}/sql/migrate.sql"
else
    echo "Running EF Core migration via dotnet CLI..."
    ConnectionStrings__DefaultConnection="${SQL_CONN_STR}" dotnet ef database update --project "${ROOT_DIR}/backend/SfmlPlayground.Api"
fi

if [ -n "${MY_IP}" ]; then
    az sql server firewall-rule delete \
        --resource-group "${RESOURCE_GROUP}" \
        --server "${SQL_SERVER_PREFIX}" \
        --name "TempDeployRule" --output none 2>/dev/null || true
fi

# 9. Build and Package Backend & Frontend
echo "[7/7] Compiling backend and frontend bundles..."
PUBLISH_DIR="${ROOT_DIR}/dist/backend"
rm -rf "${PUBLISH_DIR}"
dotnet publish "${ROOT_DIR}/backend/SfmlPlayground.Api/SfmlPlayground.Api.csproj" -c Release -o "${PUBLISH_DIR}"

cd "${ROOT_DIR}/frontend"
npm ci
npm run build
cd "${ROOT_DIR}"

echo "====================================================="
echo " Deployment Complete!"
echo " Web UI:     http://${VM_PUBLIC_IP} (or http://${VM_FQDN})"
echo " Health:     http://${VM_PUBLIC_IP}/health"
echo "====================================================="
