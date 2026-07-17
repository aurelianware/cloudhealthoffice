# Testing Guide: Cloud Health Office Smart Routing

## Quick Test Commands

### 1. Run All Tests
```bash
./scripts/test-smart-routing.sh
```

### 2. Test Individual Routes

Set `PORTAL_BASE_URL` to your local or customer-deployed portal URL:

```bash
export PORTAL_BASE_URL="${PORTAL_BASE_URL:-http://localhost:5026}"
```

**Demo Page (No Auth Required):**
```bash
curl -I "$PORTAL_BASE_URL/demo"
# Should return: HTTP 200

# Or open in browser:
open "$PORTAL_BASE_URL/demo"
```

**Root Route (Unauthenticated):**
```bash
curl -I "$PORTAL_BASE_URL/"
# Should redirect to /welcome or Azure AD login
```

**Signup Page:**
```bash
open "$PORTAL_BASE_URL/signup"
```

### 3. Check Portal Logs (Real-time)
```bash
# Watch all portal logs
kubectl logs -f -n cloudhealthoffice -l app=portal

# Filter for routing decisions
kubectl logs -n cloudhealthoffice -l app=portal --tail=100 | grep -i "routing\|tenant\|authenticated"

# Check Cosmos DB queries
kubectl logs -n cloudhealthoffice -l app=portal --tail=100 | grep -i "cosmos\|subscription"
```

## Creating Test Data in Cosmos DB

### Option 1: Azure Portal (Easiest)

1. **Open Cosmos DB Data Explorer:**
   ```bash
   # Get direct link
   echo "https://portal.azure.com/#@32177734-051b-4fdc-9568-cc35530191b1/resource/subscriptions/caf68aff-3bee-40e3-bf26-c4166efa952b/resourceGroups/prod-cloudhealthoffice-rg/providers/Microsoft.DocumentDB/databaseAccounts/cloudhealthoffice-cosmos/dataExplorer"
   ```

2. **Navigate:** CloudHealthOffice → Tenants → New Item

3. **Add Test Tenant:**
   ```json
   {
     "id": "test-org-001",
     "tenantId": "test-org-001",
     "azureTenantId": "32177734-051b-4fdc-9568-cc35530191b1",
     "organizationName": "My Test Organization",
     "subscriptionStatus": "Active",
     "tier": "professional",
     "isDemo": false,
     "stripeCustomerId": "cus_test123",
     "stripeSubscriptionId": "sub_test123",
     "trialEndsAt": "2026-03-15T00:00:00Z",
     "createdAt": "2026-02-08T00:00:00Z",
     "updatedAt": "2026-02-08T00:00:00Z",
     "adminEmails": [
       "your.email@example.com"
     ]
   }
   ```

4. **Add Test Member (for authorized access):**
   - Go to Members container → New Item
   ```json
   {
     "id": "member-001",
     "tenantId": "test-org-001",
     "email": "your.email@example.com",
     "firstName": "Test",
     "lastName": "User",
     "role": "Admin",
     "status": "Active",
     "createdAt": "2026-02-08T00:00:00Z"
   }
   ```

### Option 2: Using Azure CLI (Limited)

**Query existing tenants:**
```bash
# Check Cosmos DB connection
az cosmosdb show -n cloudhealthoffice-cosmos -g prod-cloudhealthoffice-rg --query "documentEndpoint"

# List containers
az cosmosdb sql container list \
  --account-name cloudhealthoffice-cosmos \
  --resource-group prod-cloudhealthoffice-rg \
  --database-name CloudHealthOffice \
  --query "[].{Name:name, PartitionKey:resource.partitionKey.paths[0]}" -o table
```

## Testing Scenarios

### Scenario 1: Demo Mode ✅
**Setup:** No account needed  
**Action:** Visit `/demo`  
**Expected:** See read-only demo dashboard with sample data

**Test:**
```bash
curl -I "$PORTAL_BASE_URL/demo"
# Should return HTTP 200
```

### Scenario 2: New Organization (Signup)
**Setup:** Sign in with Microsoft account from tenant NOT in Cosmos DB  
**Action:** Visit `/` while authenticated  
**Expected:** Route to `/signup`

**Verify Routing:**
```bash
# Check logs after signing in
kubectl logs -n cloudhealthoffice -l app=portal --tail=50 | grep "No subscription found"
# Should see: "No subscription found for tenant {id}, redirecting to signup"
```

### Scenario 3: Existing Subscription + Authorized
**Setup:** 
1. Create tenant in Cosmos DB with your Azure Tenant ID
2. Add your email to Members container
3. Sign in

**Action:** Visit `/`  
**Expected:** Route to `/dashboard`

**Verify Routing:**
```bash
kubectl logs -n cloudhealthoffice -l app=portal --tail=50 | grep "authorized"
# Should see: "User {email} authorized, redirecting to dashboard"
```

### Scenario 4: Existing Subscription + NOT Authorized
**Setup:**
1. Create tenant in Cosmos DB
2. DON'T add your email to Members
3. Sign in

**Action:** Visit `/`  
**Expected:** Route to `/request-access`

**Verify Routing:**
```bash
kubectl logs -n cloudhealthoffice -l app=portal --tail=50 | grep "not authorized"
# Should see: "User {email} not authorized for tenant {id}, redirecting to request access"
```

### Scenario 5: Expired Subscription
**Setup:**
1. Create tenant with `"subscriptionStatus": "Expired"`
2. Sign in

**Action:** Visit `/`  
**Expected:** Route to `/billing`

## Debugging Tips

### 1. Check Cosmos DB Connection
```bash
# View Cosmos secret in Kubernetes
kubectl get secret cosmos-secret -n cloudhealthoffice -o jsonpath='{.data.COSMOS_ENDPOINT}' | base64 -d
kubectl get secret cosmos-secret -n cloudhealthoffice -o jsonpath='{.data.COSMOS_KEY}' | base64 -d | head -c 20

# Check if portal has environment variables
kubectl exec -n cloudhealthoffice deployment/portal -- env | grep COSMOS
```

### 2. Monitor Real-time Routing
```bash
# Tail logs and watch routing decisions
kubectl logs -f -n cloudhealthoffice -l app=portal | grep -E "(Looking up subscription|Found subscription|routing|redirecting)"
```

### 3. Check Azure AD Token Claims
When signed in, check browser console → Network → signin-oidc → Headers to see:
- `tid` (Tenant ID)
- `upn` or `preferred_username` (Email)

### 4. Verify Cosmos Queries
```bash
# Check for Cosmos query logs
kubectl logs -n cloudhealthoffice -l app=portal --tail=200 | grep -A 2 "Looking up subscription"
# Should show: Tenant ID being queried
```

## Common Issues

**Issue:** Always redirecting to /signup even with tenant in Cosmos DB
**Fix:** Verify `azureTenantId` in Cosmos matches your Azure AD tenant ID:
```bash
az account show --query tenantId -o tsv
```

**Issue:** Cosmos DB connection errors
**Fix:** Check secret exists:
```bash
kubectl get secret cosmos-secret -n cloudhealthoffice
```

**Issue:** Not finding members
**Fix:** Ensure email in Members container matches exactly (case-insensitive):
```bash
# Your Azure AD email
az ad signed-in-user show --query mail -o tsv
```

## Success Criteria

✅ `/demo` accessible without authentication  
✅ Root route redirects based on auth state  
✅ Cosmos DB containers exist (Tenants, Members)  
✅ Portal pods running with Cosmos secrets  
✅ Logs show "Looking up subscription for Azure Tenant ID"  
✅ Smart routing to /signup, /dashboard, or /request-access works

## Next Steps

1. Add your organization's tenant to Cosmos DB
2. Test signup flow creates tenant automatically
3. Add team members to Members container
4. Test demo mode with colleagues
5. Monitor routing in production logs
