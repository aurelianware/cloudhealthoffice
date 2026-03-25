#!/bin/bash
# Configure Logic Apps SFTP API Connection
# Cloud Health Office - Post-Kubernetes SFTP Deployment

set -euo pipefail

echo "=========================================="
echo "SFTP API Connection Configuration"
echo "=========================================="
echo ""

# Get SFTP service details from Kubernetes
echo "📡 Retrieving SFTP server details from Kubernetes..."
SFTP_IP=$(kubectl get svc sftp-service -n cho-sftp -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null || echo "")

if [ -z "$SFTP_IP" ]; then
    echo "❌ SFTP LoadBalancer IP not found."
    echo ""
    echo "Check if the SFTP server is deployed:"
    echo "   kubectl get svc -n cho-sftp"
    echo ""
    echo "If the service shows <pending>, your cluster may not have a LoadBalancer provisioner."
    echo "Options:"
    echo "   1. Use kubectl port-forward for local testing"
    echo "   2. Deploy MetalLB or similar LoadBalancer controller"
    echo "   3. Use NodePort service type instead"
    exit 1
fi

SFTP_PORT=22
echo "✅ SFTP Server: $SFTP_IP:$SFTP_PORT"
echo ""

# Prompt for Azure resource details
read -p "Azure Resource Group [rg-hipaa-logic-apps]: " RESOURCE_GROUP
RESOURCE_GROUP=${RESOURCE_GROUP:-rg-hipaa-logic-apps}

read -p "Azure Location [westus2]: " LOCATION
LOCATION=${LOCATION:-westus2}

read -p "Connection Name [cho-sftp]: " CONNECTION_NAME
CONNECTION_NAME=${CONNECTION_NAME:-cho-sftp}

echo ""
echo "SFTP Credentials (from infrastructure/k8s/sftp-server-deployment.yaml):"
read -p "Username [cho-edi]: " SFTP_USER
SFTP_USER=${SFTP_USER:-cho-edi}

read -sp "Password [changeme123]: " SFTP_PASS
SFTP_PASS=${SFTP_PASS:-changeme123}
echo ""

echo ""
echo "=========================================="
echo "Configuration Summary"
echo "=========================================="
echo "SFTP Host:        $SFTP_IP"
echo "SFTP Port:        $SFTP_PORT"
echo "Username:         $SFTP_USER"
echo "Resource Group:   $RESOURCE_GROUP"
echo "Location:         $LOCATION"
echo "Connection Name:  $CONNECTION_NAME"
echo ""

read -p "Create/update this API connection? (y/n) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Aborted."
    exit 0
fi

echo ""
echo "🔧 Creating/updating API connection..."

# Create the connection using Azure CLI
# Note: This uses the sftpwithssh connector (SSH-based SFTP)
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file - \
  --parameters \
    connectionName="$CONNECTION_NAME" \
    sftpHost="$SFTP_IP" \
    sftpPort="$SFTP_PORT" \
    sftpUsername="$SFTP_USER" \
    sftpPassword="$SFTP_PASS" \
    location="$LOCATION" \
  <<'EOF'
{
  "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "connectionName": {
      "type": "string"
    },
    "sftpHost": {
      "type": "string"
    },
    "sftpPort": {
      "type": "int",
      "defaultValue": 22
    },
    "sftpUsername": {
      "type": "string"
    },
    "sftpPassword": {
      "type": "securestring"
    },
    "location": {
      "type": "string"
    }
  },
  "resources": [
    {
      "type": "Microsoft.Web/connections",
      "apiVersion": "2016-06-01",
      "name": "[parameters('connectionName')]",
      "location": "[parameters('location')]",
      "properties": {
        "displayName": "[parameters('connectionName')]",
        "api": {
          "id": "[subscriptionResourceId('Microsoft.Web/locations/managedApis', parameters('location'), 'sftpwithssh')]"
        },
        "parameterValues": {
          "hostName": "[parameters('sftpHost')]",
          "portNumber": "[parameters('sftpPort')]",
          "userName": "[parameters('sftpUsername')]",
          "password": "[parameters('sftpPassword')]",
          "acceptAnySshHostKey": true,
          "disableUploadFilesResumeCapability": true
        }
      }
    }
  ],
  "outputs": {
    "connectionId": {
      "type": "string",
      "value": "[resourceId('Microsoft.Web/connections', parameters('connectionName'))]"
    }
  }
}
EOF

if [ $? -eq 0 ]; then
    echo ""
    echo "=========================================="
    echo "✅ SFTP API Connection Configured!"
    echo "=========================================="
    echo ""
    echo "Connection ID:"
    CONNECTION_ID=$(az resource show \
      --resource-group "$RESOURCE_GROUP" \
      --resource-type "Microsoft.Web/connections" \
      --name "$CONNECTION_NAME" \
      --query id -o tsv)
    echo "$CONNECTION_ID"
    echo ""
    echo "📋 Next Steps:"
    echo "1. Update infra/main.parameters.json with SFTP host:"
    echo "   \"sftpHost\": { \"value\": \"$SFTP_IP\" }"
    echo ""
    echo "2. Update Logic Apps connections.parameters.json:"
    echo "   Update the connectionId for sftp-ssh connection"
    echo ""
    echo "3. Re-deploy Logic Apps workflows:"
    echo "   ./scripts/deploy-workflows.sh"
    echo ""
    echo "🧪 Test the connection:"
    echo "   az resource invoke-action \\"
    echo "     --resource-group $RESOURCE_GROUP \\"
    echo "     --resource-type Microsoft.Web/connections \\"
    echo "     --name $CONNECTION_NAME \\"
    echo "     --action testConnection \\"
    echo "     --api-version 2016-06-01"
else
    echo ""
    echo "❌ Failed to create API connection. Check errors above."
    exit 1
fi
