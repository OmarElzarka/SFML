#!/usr/bin/env bash
# ==============================================================================
# SFML 2.6.2 Online IDE - Azure Production Deployment Script
# Designed for Azure Cloud Shell & Azure CLI (Contributor role compatible)
# ==============================================================================
set -euo pipefail

TARGET_SUBSCRIPTION="${TARGET_SUBSCRIPTION:-4329056b-c3be-43dd-858f-86ecb8b64598}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-sfml-prod}"
LOCATION="${LOCATION:-westeurope}"
SQL_LOCATION="${SQL_LOCATION:-swedencentral}"
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
                         sqlLocation="${SQL_LOCATION}" \
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

echo "  Executing Bicep deployment in '${SELECTED_LOCATION}' (SQL in '${SQL_LOCATION}') with VM SKU: ${SELECTED_VM_SIZE}..."
DEPLOY_OUTPUT=$(az deployment group create \
    --resource-group "${RESOURCE_GROUP}" \
    --template-file "${SCRIPT_DIR}/main.bicep" \
    --parameters location="${SELECTED_LOCATION}" \
                 sqlLocation="${SQL_LOCATION}" \
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
systemctl stop sfml-api 2>/dev/null || true
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

# 8. Configure HTTPS with Let's Encrypt for custom domain
DOMAIN_NAME="sfml.omarelzarka.com"
RESOLVED_IP=$(getent ahosts "${DOMAIN_NAME}" 2>/dev/null | awk '{print $1}' | head -n1 || true)
if [ "${RESOLVED_IP}" = "${VM_PUBLIC_IP}" ]; then
    echo "Domain ${DOMAIN_NAME} resolves to this VM (${VM_PUBLIC_IP}). Ensuring SSL certificate and HTTPS configuration..."
    mkdir -p /var/sfml/frontend/.well-known/acme-challenge
    if [ ! -d "/etc/letsencrypt/live/${DOMAIN_NAME}" ]; then
        certbot certonly --webroot -w /var/sfml/frontend -d "${DOMAIN_NAME}" --non-interactive --agree-tos -m admin@omarelzarka.com --no-eff-email || true
    fi
    if [ -d "/etc/letsencrypt/live/${DOMAIN_NAME}" ]; then
        cat << 'NGINX_EOF' > /etc/nginx/sites-available/sfml.conf
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    listen [::]:80;
    server_name sfml.omarelzarka.com 20.61.216.36;

    location /.well-known/acme-challenge/ {
        root /var/sfml/frontend;
        try_files $uri =404;
    }

    location / {
        return 301 https://sfml.omarelzarka.com$request_uri;
    }
}

server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name sfml.omarelzarka.com;

    ssl_certificate /etc/letsencrypt/live/sfml.omarelzarka.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/sfml.omarelzarka.com/privkey.pem;

    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_prefer_server_ciphers on;
    ssl_ciphers ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:DHE-RSA-AES128-GCM-SHA256:DHE-RSA-AES256-GCM-SHA384;
    ssl_session_cache shared:SSL:10m;
    ssl_session_timeout 1d;
    ssl_session_tickets off;

    add_header X-Content-Type-Options "nosniff" always;
    add_header X-XSS-Protection "1; mode=block" always;
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;

    client_max_body_size 50M;

    gzip on;
    gzip_vary on;
    gzip_min_length 1024;
    gzip_proxied expired no-cache no-store private auth;
    gzip_types text/plain text/css text/xml text/javascript application/x-javascript application/xml application/javascript application/json image/svg+xml;

    root /var/sfml/frontend;
    index index.html;

    location ~* \.(?:ico|css|js|gif|jpe?g|png|woff2?|eot|ttf|svg)$ {
        expires 6M;
        access_log off;
        add_header Cache-Control "public, max-age=15552000, immutable";
        try_files $uri =404;
    }

    location / {
        try_files $uri $uri/ /index.html;
    }

    location ^~ /api/ {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
    }

    location ^~ /health {
        proxy_pass http://127.0.0.1:5000/health;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
    }

    location ^~ /ws/ {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location ^~ /vnc/ {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_buffering off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }
}
NGINX_EOF
        mkdir -p /etc/letsencrypt/renewal-hooks/deploy
        cat << 'RENEW_EOF' > /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh
#!/bin/sh
nginx -s reload
RENEW_EOF
        chmod +x /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh
        nginx -t && systemctl reload nginx
        echo "HTTPS enabled with Let's Encrypt for ${DOMAIN_NAME}!"
    fi
fi

echo '=== VM Deployment Complete ==='
"

echo "Verifying deployment health..."
HEALTH_URL="https://sfml.omarelzarka.com/health"
for i in {1..30}; do
    if curl -s -f -k "${HEALTH_URL}" > /dev/null 2>&1 || curl -s -f "http://${VM_PUBLIC_IP}/health" > /dev/null 2>&1; then
        echo "✅ Health check PASSED"
        break
    fi
    echo "  Waiting for health check (attempt ${i}/30)..."
    sleep 5
done

echo ""
echo "====================================================="
echo " 🎉 SFML 2.6.2 Online IDE Deployed Successfully!     "
echo "====================================================="
echo " Production Web IDE: https://sfml.omarelzarka.com"
echo " HTTP Redirect:      http://sfml.omarelzarka.com"
echo " Health Endpoint:    https://sfml.omarelzarka.com/health"
echo " VM Public IP:       ${VM_PUBLIC_IP}"
echo " DNS FQDN:           http://${VM_FQDN}"
echo "====================================================="
