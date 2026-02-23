#!/bin/bash
set -e

# Cloud Health Office - Smart Routing Test Script
# Tests the intelligent tenant routing and Cosmos DB integration

PORTAL_URL="https://portal.cloudhealthoffice.com"
COSMOS_ACCOUNT="cloudhealthoffice-cosmos"
COSMOS_RG="prod-cloudhealthoffice-rg"
COSMOS_DB="CloudHealthOffice"

echo "🧪 Testing Cloud Health Office Smart Routing System"
echo "=================================================="
echo ""

# Test 1: Demo Page (Anonymous)
echo "✅ Test 1: Demo Page (Anonymous Access)"
echo "   URL: $PORTAL_URL/demo"
DEMO_STATUS=$(curl -s -o /dev/null -w "%{http_code}" $PORTAL_URL/demo)
if [ "$DEMO_STATUS" = "200" ]; then
  echo "   ✓ PASSED - Demo page accessible without auth (HTTP $DEMO_STATUS)"
else
  echo "   ✗ FAILED - Expected 200, got HTTP $DEMO_STATUS"
fi
echo ""

# Test 2: Root Route (Unauthenticated)
echo "✅ Test 2: Root Route Redirect (Unauthenticated)"
echo "   URL: $PORTAL_URL/"
ROOT_REDIRECT=$(curl -s -o /dev/null -w "%{redirect_url}" -L $PORTAL_URL/)
if [[ "$ROOT_REDIRECT" == *"/welcome"* ]] || [[ "$ROOT_REDIRECT" == *"login.microsoftonline.com"* ]]; then
  echo "   ✓ PASSED - Redirects to /welcome or Azure AD login"
  echo "   Redirect: $ROOT_REDIRECT"
else
  echo "   ✗ Unexpected redirect: $ROOT_REDIRECT"
fi
echo ""

# Test 3: Check Cosmos DB Connection
echo "✅ Test 3: Cosmos DB Connectivity"
COSMOS_ENDPOINT=$(az cosmosdb show -n $COSMOS_ACCOUNT -g $COSMOS_RG --query "documentEndpoint" -o tsv 2>/dev/null)
if [ -n "$COSMOS_ENDPOINT" ]; then
  echo "   ✓ PASSED - Cosmos DB accessible"
  echo "   Endpoint: $COSMOS_ENDPOINT"
else
  echo "   ✗ FAILED - Cannot access Cosmos DB"
fi
echo ""

# Test 4: Check Tenants Container
echo "✅ Test 4: Tenants Container Exists"
TENANT_CONTAINER=$(az cosmosdb sql container show \
  --account-name $COSMOS_ACCOUNT \
  --resource-group $COSMOS_RG \
  --database-name $COSMOS_DB \
  --name Tenants \
  --query "id" -o tsv 2>/dev/null)
if [ -n "$TENANT_CONTAINER" ]; then
  echo "   ✓ PASSED - Tenants container exists"
else
  echo "   ✗ FAILED - Tenants container not found"
fi
echo ""

# Test 5: Check Members Container
echo "✅ Test 5: Members Container Exists"
MEMBER_CONTAINER=$(az cosmosdb sql container show \
  --account-name $COSMOS_ACCOUNT \
  --resource-group $COSMOS_RG \
  --database-name $COSMOS_DB \
  --name Members \
  --query "id" -o tsv 2>/dev/null)
if [ -n "$MEMBER_CONTAINER" ]; then
  echo "   ✓ PASSED - Members container exists"
else
  echo "   ✗ FAILED - Members container not found"
fi
echo ""

# Test 6: Check Portal Pods
echo "✅ Test 6: Portal Deployment Status"
PORTAL_READY=$(kubectl get pods -n cloudhealthoffice -l app=portal --no-headers 2>/dev/null | grep -c "Running" || echo "0")
if [ "$PORTAL_READY" -gt 0 ]; then
  echo "   ✓ PASSED - $PORTAL_READY portal pod(s) running"
  kubectl get pods -n cloudhealthoffice -l app=portal --no-headers | awk '{print "   Pod:", $1, "-", $3}'
else
  echo "   ✗ FAILED - No portal pods running"
fi
echo ""

# Test 7: Check Cosmos Secret
echo "✅ Test 7: Cosmos DB Kubernetes Secret"
COSMOS_SECRET=$(kubectl get secret cosmos-secret -n cloudhealthoffice -o jsonpath='{.data.COSMOS_ENDPOINT}' 2>/dev/null | base64 -d)
if [ -n "$COSMOS_SECRET" ]; then
  echo "   ✓ PASSED - cosmos-secret exists in Kubernetes"
  echo "   Endpoint: $COSMOS_SECRET"
else
  echo "   ✗ FAILED - cosmos-secret not found"
fi
echo ""

# Test 8: Check Portal Logs for Cosmos
echo "✅ Test 8: Portal Cosmos DB Integration Logs"
COSMOS_LOGS=$(kubectl logs -n cloudhealthoffice -l app=portal --tail=100 2>/dev/null | grep -i "cosmos\|tenant" | head -5 || echo "")
if [ -n "$COSMOS_LOGS" ]; then
  echo "   ✓ PASSED - Found Cosmos/Tenant logs:"
  echo "$COSMOS_LOGS" | sed 's/^/   /'
else
  echo "   ℹ INFO - No Cosmos/Tenant logs found (may be normal if no requests yet)"
fi
echo ""

echo "=================================================="
echo "🎯 Testing Summary"
echo "=================================================="
echo ""
echo "To manually test routing scenarios:"
echo ""
echo "1️⃣  Demo Mode:"
echo "   → Visit: $PORTAL_URL/demo"
echo "   → Should show demo dashboard without login"
echo ""
echo "2️⃣  No Subscription (Signup):"
echo "   → Sign in with account from tenant without subscription"
echo "   → Should route to /signup"
echo ""
echo "3️⃣  Has Subscription (Authorized):"
echo "   → Sign in with account that has tenant + is member"
echo "   → Should route to /dashboard"
echo ""
echo "4️⃣  Request Access (Unauthorized):"
echo "   → Sign in with account from tenant with subscription"
echo "   → But user NOT in Members container"
echo "   → Should route to /request-access"
echo ""
echo "To create a test tenant:"
echo "   1. Get your Azure Tenant ID:"
echo "      az account show --query tenantId -o tsv"
echo ""
echo "   2. Use Azure Portal Data Explorer to add to Tenants container:"
echo "      https://portal.azure.com -> Cosmos DB -> Data Explorer"
echo ""
echo "   3. Or use the Azure CLI (when supported)"
echo ""
