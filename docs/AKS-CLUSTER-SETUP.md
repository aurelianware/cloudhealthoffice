# AKS Cluster Setup Guide

## Quick Start

```bash
# 1. Create AKS cluster (interactive)
./scripts/deploy-aks-cluster.sh

# 2. Deploy SFTP server
./scripts/deploy-sftp-server.sh

# 3. Configure DNS and IP whitelisting
./scripts/setup-sftp-dns-whitelist.sh
```

## What Gets Created

### Azure Resources

- **AKS Cluster**: 2-node Kubernetes cluster with autoscaling (2-5 nodes)
- **Virtual Network**: Dedicated VNet with Azure CNI networking
- **Load Balancer**: Standard SKU for SFTP external access
- **Managed Identity**: For secure Azure resource access
- **Log Analytics Workspace**: Azure Monitor integration
- **Azure Policy**: Compliance and governance

### Kubernetes Components

- **StorageClass**: `managed-premium-retain` for persistent storage
- **RBAC**: Azure RBAC integration enabled
- **Network Policy**: Azure Network Policy for pod-to-pod security
- **Cluster Autoscaler**: Automatically scales nodes based on demand

## Configuration Options

### Production Configuration

```bash
./scripts/deploy-aks-cluster.sh
# When prompted:
Resource Group: rg-hipaa-logic-apps
Cluster Name: cho-aks-prod
Region: westus2
Node Count: 3
Node Size: Standard_D4s_v3
```

### Development Configuration

```bash
# Smaller, cheaper cluster for development
Resource Group: rg-cho-dev
Cluster Name: cho-aks-dev
Region: westus2
Node Count: 1
Node Size: Standard_B2s  # ~$30/month
```

## Node VM Sizes

| Size | vCPU | RAM | Monthly Cost* | Use Case |
|------|------|-----|---------------|----------|
| Standard_B2s | 2 | 4GB | ~$30 | Development |
| Standard_D2s_v3 | 2 | 8GB | ~$70 | Small production |
| Standard_D4s_v3 | 4 | 16GB | ~$140 | Production |
| Standard_D8s_v3 | 8 | 32GB | ~$280 | High-volume |

*Approximate costs for West US 2 region

## Post-Deployment Setup

### Install kubectl

**macOS:**
```bash
brew install kubectl
```

**Linux:**
```bash
az aks install-cli
```

**Windows:**
```powershell
az aks install-cli
```

### Connect to Cluster

```bash
# Get credentials
az aks get-credentials \
  --resource-group rg-hipaa-logic-apps \
  --name cho-aks-cluster

# Verify connection
kubectl get nodes
kubectl cluster-info
```

### View Dashboard

```bash
# Start dashboard proxy
az aks browse \
  --resource-group rg-hipaa-logic-apps \
  --name cho-aks-cluster
```

## Security Hardening

### Enable Azure Defender

```bash
az security pricing create \
  --name KubernetesService \
  --tier Standard
```

### Configure Network Policies

```bash
# Deploy default deny policy
kubectl apply -f - <<EOF
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: default-deny-all
  namespace: default
spec:
  podSelector: {}
  policyTypes:
  - Ingress
  - Egress
EOF
```

### Enable Pod Security Standards

```bash
kubectl label namespace cho-sftp \
  pod-security.kubernetes.io/enforce=baseline \
  pod-security.kubernetes.io/audit=restricted \
  pod-security.kubernetes.io/warn=restricted
```

### Enable Audit Logging

```bash
az aks update \
  --resource-group rg-hipaa-logic-apps \
  --name cho-aks-cluster \
  --enable-azure-monitor-metrics
```

## Cost Optimization

### Use Spot Instances (Non-Production)

```bash
az aks nodepool add \
  --resource-group rg-hipaa-logic-apps \
  --cluster-name cho-aks-cluster \
  --name spotpool \
  --priority Spot \
  --eviction-policy Delete \
  --spot-max-price -1 \
  --node-count 2 \
  --node-vm-size Standard_D2s_v3

# ~70% cost savings compared to regular nodes
```

### Stop Cluster When Not in Use (Dev Only)

```bash
# Stop cluster
az aks stop \
  --resource-group rg-cho-dev \
  --name cho-aks-dev

# Start cluster
az aks start \
  --resource-group rg-cho-dev \
  --name cho-aks-dev
```

### Monitor Costs

```bash
# View consumption
az consumption usage list \
  --start-date 2026-02-01 \
  --end-date 2026-02-04 \
  --output table

# Set budget alert
az consumption budget create \
  --budget-name aks-monthly-budget \
  --amount 200 \
  --time-grain Monthly \
  --resource-group rg-hipaa-logic-apps
```

## Upgrade Cluster

```bash
# Check available versions
az aks get-upgrades \
  --resource-group rg-hipaa-logic-apps \
  --name cho-aks-cluster \
  --output table

# Upgrade cluster
az aks upgrade \
  --resource-group rg-hipaa-logic-apps \
  --name cho-aks-cluster \
  --kubernetes-version 1.29
```

## Troubleshooting

### Cluster Creation Failed

**Check quota:**
```bash
az vm list-usage \
  --location westus2 \
  --output table | grep -i "standard d"
```

**Request quota increase:**
```bash
# Via Azure Portal: Support → New Support Request → Quota
# Or use Azure CLI:
az support tickets create \
  --ticket-name "AKS-quota-increase" \
  --title "Increase AKS node quota" \
  --severity minimal \
  --description "Need quota for Standard_D2s_v3 VMs"
```

### Can't Connect to Cluster

```bash
# Regenerate credentials
az aks get-credentials \
  --resource-group rg-hipaa-logic-apps \
  --name cho-aks-cluster \
  --overwrite-existing \
  --admin

# Check RBAC permissions
az aks show \
  --resource-group rg-hipaa-logic-apps \
  --name cho-aks-cluster \
  --query aadProfile
```

### Nodes Not Ready

```bash
# Check node status
kubectl get nodes
kubectl describe node <node-name>

# Check system pods
kubectl get pods -n kube-system

# View node logs
az aks show \
  --resource-group rg-hipaa-logic-apps \
  --name cho-aks-cluster \
  --query agentPoolProfiles
```

### LoadBalancer Stuck Pending

```bash
# Check service
kubectl get svc -n cho-sftp
kubectl describe svc sftp-service -n cho-sftp

# Check Azure LoadBalancer
az network lb list \
  --resource-group MC_rg-hipaa-logic-apps_cho-aks-cluster_westus2 \
  --output table

# Check NSG rules
az network nsg list \
  --resource-group MC_rg-hipaa-logic-apps_cho-aks-cluster_westus2 \
  --output table
```

## Disaster Recovery

### Backup Cluster Configuration

```bash
# Export all resources
kubectl get all --all-namespaces -o yaml > cluster-backup.yaml

# Backup persistent volumes
kubectl get pvc --all-namespaces -o yaml > pvc-backup.yaml
```

### Snapshot Disks

```bash
# Create snapshot of SFTP data disk
DISK_ID=$(az disk list \
  --resource-group MC_rg-hipaa-logic-apps_cho-aks-cluster_westus2 \
  --query "[?contains(name, 'pvc')].id" -o tsv | head -1)

az snapshot create \
  --resource-group rg-hipaa-logic-apps \
  --name sftp-data-snapshot-$(date +%Y%m%d) \
  --source "$DISK_ID"
```

### Multi-Region Deployment

Deploy to secondary region for HA:

```bash
# Create cluster in East US
./scripts/deploy-aks-cluster.sh
# When prompted:
# Region: eastus2
# Cluster Name: cho-aks-east

# Set up geo-replication for SFTP data
# Use Azure Files or blob storage with geo-redundancy
```

## Monitoring

### View Metrics

```bash
# CPU/Memory usage
kubectl top nodes
kubectl top pods -n cho-sftp

# Azure Monitor
az monitor metrics list \
  --resource <cluster-resource-id> \
  --metric "node_cpu_usage_percentage" \
  --output table
```

### Set Up Alerts

```bash
# Alert on high CPU
az monitor metrics alert create \
  --name aks-high-cpu \
  --resource-group rg-hipaa-logic-apps \
  --scopes <cluster-resource-id> \
  --condition "avg node_cpu_usage_percentage > 80" \
  --window-size 5m \
  --evaluation-frequency 1m
```

## Cleanup

### Delete Cluster

```bash
# Warning: This deletes all data!
az aks delete \
  --resource-group rg-hipaa-logic-apps \
  --name cho-aks-cluster \
  --yes --no-wait

# Delete resource group (if no other resources)
az group delete \
  --name rg-hipaa-logic-apps \
  --yes --no-wait
```

## Cost Summary

**Production (3 nodes, Standard_D4s_v3):**
- Compute: ~$420/month
- LoadBalancer: ~$4/month
- Storage: ~$20/month
- **Total: ~$444/month**

**Development (1 node, Standard_B2s):**
- Compute: ~$30/month
- LoadBalancer: ~$4/month
- Storage: ~$5/month
- **Total: ~$39/month**

## Related Documentation

- [SFTP Integration Guide](./SFTP-INTEGRATION-GUIDE.md)
- [SFTP Architecture](./SFTP-ARCHITECTURE.md)
- [DNS and IP Whitelisting](./SFTP-DNS-SETUP.md)
- [Multi-Cloud Deployment](./MULTI-CLOUD-DEPLOYMENT.md)
