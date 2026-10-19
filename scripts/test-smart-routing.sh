#!/bin/bash
set -e

# Cloud Health Office - Smart Routing Test Script
# Tests the intelligent tenant routing and its MongoDB-backed tenant lookup

PORTAL_URL="https://portal.cloudhealthoffice.com"

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

# Test 3: Check the MongoDB connection secret
echo "✅ Test 3: MongoDB (Azure DocumentDB) Kubernetes Secret"
MONGO_SECRET=$(kubectl get secret database-secret -n cloudhealthoffice -o jsonpath='{.data.connectionString}' 2>/dev/null | base64 -d)
if [ -n "$MONGO_SECRET" ]; then
  echo "   ✓ PASSED - database-secret has a connectionString"
else
  echo "   ✗ FAILED - database-secret/connectionString not found"
fi
echo ""

# Test 4: Check Portal Pods
echo "✅ Test 4: Portal Deployment Status"
PORTAL_READY=$(kubectl get pods -n cloudhealthoffice -l app=portal --no-headers 2>/dev/null | grep -c "Running" || echo "0")
if [ "$PORTAL_READY" -gt 0 ]; then
  echo "   ✓ PASSED - $PORTAL_READY portal pod(s) running"
  kubectl get pods -n cloudhealthoffice -l app=portal --no-headers | awk '{print "   Pod:", $1, "-", $3}'
else
  echo "   ✗ FAILED - No portal pods running"
fi
echo ""

# Test 5: Check Portal Logs for tenant routing
echo "✅ Test 5: Portal Tenant Routing Logs"
TENANT_LOGS=$(kubectl logs -n cloudhealthoffice -l app=portal --tail=100 2>/dev/null | grep -i "tenant" | head -5 || echo "")
if [ -n "$TENANT_LOGS" ]; then
  echo "   ✓ PASSED - Found tenant logs:"
  echo "$TENANT_LOGS" | sed 's/^/   /'
else
  echo "   ℹ INFO - No tenant logs found (may be normal if no requests yet)"
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
echo "   → But user NOT a member of that tenant"
echo "   → Should route to /request-access"
echo ""
echo "To create a test tenant:"
echo "   1. Get your Azure Tenant ID:"
echo "      az account show --query tenantId -o tsv"
echo ""
echo "   2. Create the tenant through the portal signup flow ($PORTAL_URL/signup)"
echo "      or tenant-service"
echo ""
