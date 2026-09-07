# Azure Deployment Guide — SFML 2.6.2 Online Playground

This document details recommended strategies for deploying the SFML Online Playground to Microsoft Azure without databases or persistent storage.

---

## Recommended Deployment Architectures

### Option 1: Azure Kubernetes Service (AKS) — Recommended for High Scale
Because every running student session spins up an isolated sandbox container with its own virtual display and VNC socket, AKS offers native container lifecycle management and dynamic pod scheduling.

```text
                  Internet
                     │
                     ▼
        Azure Application Gateway / Ingress-NGINX
                     │
         ┌───────────┴───────────┐
         ▼                       ▼
   Frontend Pods           Backend API Pods
   (Angular / Nginx)       (ASP.NET Core 10)
                                 │
                                 ▼
                     Kubernetes API Client
                                 │
                                 ▼
                    Dynamically Scheduled Pods
                    (sfml-sandbox:latest)
```

1. **Backend Privileges**: Backend pod is granted a Kubernetes ServiceAccount with RBAC rights to create and delete ephemeral sandbox pods in an isolated namespace.
2. **Dynamic Ingress**: Use Ingress-NGINX or Traefik with WebSocket support enabled.
3. **Session Routing**: The sandbox pod is exposed via a ClusterIP service or headless service, and websockify traffic is routed through the Ingress controller.

---

### Option 2: Azure Container Apps (ACA) / Azure VM Scale Set — For Lightweight Deployments

For smaller educational environments, deploy the Backend on an Azure VM Scale Set (Ubuntu Linux with Docker Engine installed):

1. **Frontend**: Deploy static Angular build to **Azure Static Web Apps** or an Azure Storage Static Website with Azure Front Door.
2. **Backend**: Run ASP.NET Core Web API inside a Linux VM or Azure Container Instance (ACI) with access to the Docker daemon.
3. **Sandbox Runners**: Spawned directly by the backend via the local Docker socket on the VM.
4. **Port Range**: Configure Azure Network Security Group (NSG) to allow inbound TCP traffic on a designated high port range (e.g., 5000-6000) for websockify connections, or proxy them through the ASP.NET Core backend or Nginx.

---

## Azure Resource Checklist

| Component | Recommended Azure Service | SKU / Sizing |
| :--- | :--- | :--- |
| **Frontend** | Azure Static Web Apps | Free / Standard |
| **Backend API** | Azure App Service (Linux) or AKS | B2s / D2s_v5 |
| **Sandbox Execution** | AKS Node Pool or Azure Linux VM | Standard_D4s_v5 (4 vCPU, 16GB RAM) |
| **Container Registry** | Azure Container Registry (ACR) | Basic |
| **Ingress / SSL** | Azure Application Gateway | Standard_v2 |

---

## Security Best Practices on Azure

1. **Network Isolation**: Run sandbox runner pods/containers in a dedicated subnet without outbound internet access (`Egress: DenyAll`) to prevent students from making external network requests or mining cryptocurrency.
2. **Resource Quotas**: Enforce Kubernetes `ResourceQuotas` and `LimitRanges` (e.g., max 512MB RAM, 0.5 CPU per runner pod).
3. **Ephemeral Disks**: Utilize Azure Ephemeral OS Disks for instant container teardown and zero persistent trace.
4. **No Database Needed**: Confirm application settings require zero connection strings, databases, or cloud storage accounts.
