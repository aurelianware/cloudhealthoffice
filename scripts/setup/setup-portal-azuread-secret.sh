#!/bin/bash
set -e

echo "=========================================="
echo "Portal Azure AD Secret Configuration"
echo "=========================================="
echo ""
echo "This script will configure the Azure AD authentication secret for the portal."
echo ""

# Check if kubectl is available
if ! command -v kubectl &> /dev/null; then
    echo "❌ kubectl is not installed or not in PATH"
    exit 1
fi

# Prompt for values
echo "📋 Please provide the following Azure AD values:"
echo ""

read -p "Azure AD Tenant ID: " TENANT_ID
if [ -z "$TENANT_ID" ]; then
    echo "❌ Tenant ID cannot be empty"
    exit 1
fi

echo ""
echo "Client ID (current: 54f3419d-0d69-4b06-939a-c1a260596556)"
read -p "Azure AD Client ID [press Enter to use current]: " CLIENT_ID
if [ -z "$CLIENT_ID" ]; then
    CLIENT_ID="54f3419d-0d69-4b06-939a-c1a260596556"
fi

echo ""
echo "⚠️  IMPORTANT: You need the CLIENT SECRET VALUE, not the Secret ID!"
echo ""
echo "To get this from Azure Portal:"
echo "  1. Go to Azure Portal → Azure Active Directory → App registrations"
echo "  2. Find your app (Client ID: $CLIENT_ID)"
echo "  3. Go to 'Certificates & secrets'"
echo "  4. Copy the SECRET VALUE (looks like 'abc~123XYZ...')"
echo "  5. NOT the Secret ID (which looks like a GUID)"
echo ""
read -sp "Azure AD Client Secret VALUE: " CLIENT_SECRET
echo ""

if [ -z "$CLIENT_SECRET" ]; then
    echo "❌ Client Secret cannot be empty"
    exit 1
fi

# Validate secret is not a GUID (common mistake)
if [[ $CLIENT_SECRET =~ ^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$ ]]; then
    echo ""
    echo "❌ ERROR: You provided a Client Secret ID (GUID), not the secret value!"
    echo "   The secret value should be a long random string like 'abc~123XYZ...'"
    echo "   Please go back to Azure Portal and copy the VALUE, not the ID."
    exit 1
fi

echo ""
echo "Creating/updating Kubernetes secret..."

# Create or update the secret
kubectl -n cloudhealthoffice create secret generic azure-ad-config \
  --from-literal=TenantId="$TENANT_ID" \
  --from-literal=ClientId="$CLIENT_ID" \
  --from-literal=ClientSecret="$CLIENT_SECRET" \
  --dry-run=client -o yaml | kubectl apply -f -

echo "✅ Secret 'azure-ad-config' created/updated successfully in namespace 'cloudhealthoffice'"
echo ""
echo "Restarting portal pods to pick up new configuration..."

kubectl -n cloudhealthoffice rollout restart deployment/portal

echo "✅ Portal deployment restarted"
echo ""
echo "Monitor rollout status with:"
echo "  kubectl -n cloudhealthoffice rollout status deployment/portal"
echo ""
echo "Check logs with:"
echo "  kubectl -n cloudhealthoffice logs -l app=portal --tail=50 -f"
echo ""
echo "=========================================="
echo "✅ Configuration Complete!"
echo "=========================================="
