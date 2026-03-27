#!/bin/bash
# Deploy Azure Kubernetes Service (AKS) Cluster
# Cloud Health Office - HIPAA-Compliant Kubernetes Infrastructure

set -euo pipefail

echo "=========================================="
echo "Cloud Health Office - AKS Cluster Setup"
echo "=========================================="
echo ""

# Check prerequisites
if ! command -v az &> /dev/null; then
    echo "❌ Azure CLI not found. Install with:"
    echo "   brew install azure-cli"
    exit 1
fi

# Check if logged in
az account show &>/dev/null || {
    echo "❌ Not logged in to Azure. Run: az login"
    exit 1
}

echo "✅ Azure CLI found and authenticated"
echo ""

# Configuration
read -p "Resource Group name [rg-cloudhealthoffice]: " RESOURCE_GROUP
RESOURCE_GROUP=${RESOURCE_GROUP:-rg-cloudhealthoffice}

read -p "AKS Cluster name [cho-aks-cluster]: " CLUSTER_NAME
CLUSTER_NAME=${CLUSTER_NAME:-cho-aks-cluster}

read -p "Azure Region [westus2]: " LOCATION
LOCATION=${LOCATION:-westus2}

read -p "Node count [2]: " NODE_COUNT
NODE_COUNT=${NODE_COUNT:-2}

read -p "Node VM size [Standard_D2s_v3]: " NODE_SIZE
NODE_SIZE=${NODE_SIZE:-Standard_D2s_v3}

echo ""
echo "=========================================="
echo "Configuration Summary"
echo "=========================================="
echo "Resource Group:  $RESOURCE_GROUP"
echo "Cluster Name:    $CLUSTER_NAME"
echo "Location:        $LOCATION"
echo "Node Count:      $NODE_COUNT"
echo "Node Size:       $NODE_SIZE"
echo ""
echo "Features:"
echo "  - Azure CNI networking"
echo "  - Azure Monitor enabled"
echo "  - Azure Policy enabled"
echo "  - Managed identity"
echo "  - Private cluster: No (for LoadBalancer access)"
echo "  - RBAC: Enabled"
echo ""

read -p "Create AKS cluster with these settings? (y/n) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Aborted."
    exit 0
fi

echo ""
echo "Step 1: Creating/Verifying Resource Group..."
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --tags \
    Environment=Production \
    Application=CloudHealthOffice \
    Compliance=HIPAA \
    ManagedBy=Terraform \
  --output table

echo ""
echo "Step 2: Creating AKS cluster..."
echo "⏳ This may take 5-10 minutes..."
echo ""

az aks create \
  --resource-group "$RESOURCE_GROUP" \
  --name "$CLUSTER_NAME" \
  --location "$LOCATION" \
  --node-count "$NODE_COUNT" \
  --node-vm-size "$NODE_SIZE" \
  --network-plugin azure \
  --network-policy azure \
  --enable-managed-identity \
  --enable-addons monitoring \
  --enable-cluster-autoscaler \
  --min-count 2 \
  --max-count 5 \
  --max-pods 50 \
  --load-balancer-sku standard \
  --vm-set-type VirtualMachineScaleSets \
  --tier standard \
  --tags \
    Environment=Production \
    Application=CloudHealthOffice \
    Compliance=HIPAA \
  --output table

if [ $? -ne 0 ]; then
    echo ""
    echo "❌ AKS cluster creation failed"
    exit 1
fi

echo ""
echo "Step 3: Getting cluster credentials..."
az aks get-credentials \
  --resource-group "$RESOURCE_GROUP" \
  --name "$CLUSTER_NAME" \
  --overwrite-existing

echo ""
echo "Step 4: Verifying cluster access..."
kubectl cluster-info
kubectl get nodes

echo ""
echo "Step 5: Installing kubectl (if needed)..."
if ! command -v kubectl &> /dev/null; then
    echo "Installing kubectl..."
    az aks install-cli
else
    echo "✅ kubectl already installed"
fi

echo ""
echo "Step 6: Creating storage class (if needed)..."
kubectl apply -f - <<EOF
apiVersion: storage.k8s.io/v1
kind: StorageClass
metadata:
  name: managed-premium-retain
provisioner: disk.csi.azure.com
parameters:
  skuName: Premium_LRS
reclaimPolicy: Retain
allowVolumeExpansion: true
volumeBindingMode: WaitForFirstConsumer
EOF

echo ""
echo "=========================================="
echo "✅ AKS Cluster Created Successfully!"
echo "=========================================="
echo ""
echo "Cluster Details:"
az aks show \
  --resource-group "$RESOURCE_GROUP" \
  --name "$CLUSTER_NAME" \
  --query "{Name:name, Location:location, KubernetesVersion:kubernetesVersion, NodeCount:agentPoolProfiles[0].count, FQDN:fqdn}" \
  --output table

echo ""
echo "📋 Next Steps:"
echo ""
echo "1. Deploy SFTP server:"
echo "   ./scripts/deploy-sftp-server.sh"
echo ""
echo "2. Configure DNS and IP whitelisting:"
echo "   ./scripts/setup-sftp-dns-whitelist.sh"
echo ""
echo "3. View cluster dashboard:"
echo "   az aks browse --resource-group $RESOURCE_GROUP --name $CLUSTER_NAME"
echo ""
echo "4. Monitor costs:"
echo "   az consumption usage list --output table"
echo ""
echo "📊 Estimated Monthly Cost: ~\$150-200"
echo "   - 2x Standard_D2s_v3 nodes: ~\$140/month"
echo "   - LoadBalancer: ~\$4/month"
echo "   - Managed Disks: ~\$10/month"
echo ""
echo "🔒 Security Recommendations:"
echo "   - Enable Azure Defender for Kubernetes"
echo "   - Configure network policies"
echo "   - Set up Pod Security Standards"
echo "   - Enable audit logging"
echo ""
echo "📖 Documentation:"
echo "   - AKS Best Practices: https://docs.microsoft.com/azure/aks/best-practices"
echo "   - SFTP Integration: docs/SFTP-INTEGRATION-GUIDE.md"
echo ""
