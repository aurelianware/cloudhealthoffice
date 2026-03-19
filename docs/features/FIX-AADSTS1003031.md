# Fix AADSTS1003031: Configure Portal API Permissions

## Error Explanation

**Error Code**: AADSTS1003031  
**Message**: "Misconfigured required resource access in client application registration"

**What it means**: The portal app registration doesn't have the Cloud Health Office API added as a required resource with the necessary permissions.

## Quick Fix: Azure Portal Method

### Step 1: Open Portal App Registration

1. Go to [Azure Portal](https://portal.azure.com)
2. Navigate to **Azure Active Directory** → **App registrations**
3. Find and click **"Cloud Health Office Portal"** (or your portal app name)

### Step 2: Add API Permissions

1. Click **API permissions** in the left menu
2. Click **+ Add a permission**
3. Click **My APIs** tab
4. Select **"Cloud Health Office API"**
5. Select **Delegated permissions**
6. Check the following permissions:
   - ☑️ `user_impersonation` (or your API's exposed scope)
   - ☑️ `access_as_user` (if available)
7. Click **Add permissions**

### Step 3: Grant Admin Consent

1. Still in **API permissions** page
2. Click **Grant admin consent for {Your Organization}**
3. Click **Yes** to confirm
4. Verify green checkmarks appear under "Status" column

### Step 4: Verify Configuration

Your API permissions should now show:
- ✅ Microsoft Graph → User.Read (Delegated) - *Granted*
- ✅ Cloud Health Office API → user_impersonation (Delegated) - *Granted*

## PowerShell Script (Automated)

Save this as `Configure-PortalApiPermissions.ps1`:

```powershell
#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Configure portal app to call Cloud Health Office API

.PARAMETER TenantId
    Azure AD tenant ID

.PARAMETER PortalClientId
    Portal app registration client ID

.PARAMETER ApiClientId
    API app registration client ID

.EXAMPLE
    .\Configure-PortalApiPermissions.ps1 -TenantId "32177734-..." -PortalClientId "portal-client-id" -ApiClientId "cfada1ac-f251-48ea-9330-39212aa4c862"
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$TenantId,
    
    [Parameter(Mandatory=$true)]
    [string]$PortalClientId,
    
    [Parameter(Mandatory=$true)]
    [string]$ApiClientId
)

# Install required module
if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Applications)) {
    Install-Module Microsoft.Graph.Applications -Scope CurrentUser -Force
}

# Connect to Microsoft Graph
Connect-MgGraph -TenantId $TenantId -Scopes "Application.ReadWrite.All"

Write-Host "Configuring Portal API permissions..." -ForegroundColor Cyan

# Get the API service principal to find its scopes
$apiSp = Get-MgServicePrincipal -Filter "appId eq '$ApiClientId'"

if (-not $apiSp) {
    Write-Host "❌ API service principal not found. Run Grant-AdminConsent.ps1 first." -ForegroundColor Red
    exit 1
}

# Get the portal app registration
$portalApp = Get-MgApplication -Filter "appId eq '$PortalClientId'"

if (-not $portalApp) {
    Write-Host "❌ Portal app registration not found." -ForegroundColor Red
    exit 1
}

# Find the user_impersonation scope from the API
$userImpersonationScope = $apiSp.Oauth2PermissionScopes | Where-Object { $_.Value -eq "user_impersonation" }

if (-not $userImpersonationScope) {
    Write-Host "⚠️  API doesn't expose 'user_impersonation' scope. Creating default scope..." -ForegroundColor Yellow
    
    # Get the API app registration to add the scope
    $apiApp = Get-MgApplication -Filter "appId eq '$ApiClientId'"
    
    $newScope = @{
        AdminConsentDescription = "Allow the application to access Cloud Health Office API on behalf of the signed-in user"
        AdminConsentDisplayName = "Access Cloud Health Office API"
        Id = (New-Guid).Guid
        IsEnabled = $true
        Type = "User"
        UserConsentDescription = "Allow the application to access Cloud Health Office API on your behalf"
        UserConsentDisplayName = "Access Cloud Health Office API"
        Value = "user_impersonation"
    }
    
    $api = @{
        Oauth2PermissionScopes = @($newScope)
    }
    
    Update-MgApplication -ApplicationId $apiApp.Id -Api $api
    
    Write-Host "✅ Created 'user_impersonation' scope on API" -ForegroundColor Green
    
    # Refresh the service principal
    Start-Sleep -Seconds 5
    $apiSp = Get-MgServicePrincipal -Filter "appId eq '$ApiClientId'"
    $userImpersonationScope = $apiSp.Oauth2PermissionScopes | Where-Object { $_.Value -eq "user_impersonation" }
}

# Configure required resource access
$requiredResourceAccess = @(
    @{
        ResourceAppId = $ApiClientId
        ResourceAccess = @(
            @{
                Id = $userImpersonationScope.Id
                Type = "Scope"  # Delegated permission
            }
        )
    }
)

# Update the portal app registration
Update-MgApplication -ApplicationId $portalApp.Id -RequiredResourceAccess $requiredResourceAccess

Write-Host "✅ API permissions added to portal app" -ForegroundColor Green

# Grant admin consent
Write-Host "Granting admin consent..." -ForegroundColor Yellow

$portalSp = Get-MgServicePrincipal -Filter "appId eq '$PortalClientId'"

# Create OAuth2 permission grant (admin consent)
$oauth2Grant = @{
    ClientId = $portalSp.Id
    ConsentType = "AllPrincipals"  # Admin consent for all users
    PrincipalId = $null
    ResourceId = $apiSp.Id
    Scope = "user_impersonation"
}

try {
    New-MgOauth2PermissionGrant -BodyParameter $oauth2Grant -ErrorAction SilentlyContinue
    Write-Host "✅ Admin consent granted" -ForegroundColor Green
}
catch {
    if ($_.Exception.Message -like "*already exists*") {
        Write-Host "✅ Admin consent already granted" -ForegroundColor Green
    }
    else {
        Write-Host "⚠️  Could not grant admin consent automatically: $_" -ForegroundColor Yellow
        Write-Host "Please grant consent manually in Azure Portal" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "✅ Configuration complete!" -ForegroundColor Green
Write-Host "Portal app can now call Cloud Health Office API" -ForegroundColor Green

Disconnect-MgGraph
```

## Azure CLI Method

```bash
# Login
az login --tenant YOUR_TENANT_ID

# Get API app details
API_APP_ID=$(az ad app show --id cfada1ac-f251-48ea-9330-39212aa4c862 --query id -o tsv)
API_SCOPE_ID=$(az ad app show --id $API_APP_ID --query "api.oauth2PermissionScopes[0].id" -o tsv)

# Get portal app details
PORTAL_APP_ID=$(az ad app show --id YOUR_PORTAL_CLIENT_ID --query id -o tsv)

# Add API permission to portal
az ad app permission add \
  --id $PORTAL_APP_ID \
  --api cfada1ac-f251-48ea-9330-39212aa4c862 \
  --api-permissions $API_SCOPE_ID=Scope

# Grant admin consent
az ad app permission admin-consent --id $PORTAL_APP_ID
```

## Manual Configuration Details

### What Needs to Be Configured

**Portal App Registration → API Permissions:**

```json
{
  "requiredResourceAccess": [
    {
      "resourceAppId": "cfada1ac-f251-48ea-9330-39212aa4c862",  // API Client ID
      "resourceAccess": [
        {
          "id": "user_impersonation_scope_id",  // API's exposed scope ID
          "type": "Scope"  // Delegated permission
        }
      ]
    }
  ]
}
```

### API Must Expose Scopes

The API app registration must have exposed API scopes:

1. Go to API app registration
2. Click **Expose an API**
3. Verify **Application ID URI** is set (e.g., `api://cfada1ac-f251-48ea-9330-39212aa4c862`)
4. Add a scope if missing:
   - **Scope name**: `user_impersonation`
   - **Who can consent**: Admins and users
   - **Admin consent display name**: Access Cloud Health Office API
   - **Admin consent description**: Allow the application to access Cloud Health Office API on behalf of the signed-in user
   - **State**: Enabled

## Verification

After configuration:

1. **Check API Permissions**:
   - Portal app → API permissions → Should show Cloud Health Office API

2. **Check Admin Consent Status**:
   - Status column should have green checkmark
   - Should say "Granted for {Your Org}"

3. **Test Login**:
   - Open portal: https://portal.cloudhealthoffice.com
   - Sign in
   - Should successfully authenticate without AADSTS1003031 error

## Common Issues

### Issue: "API doesn't appear in My APIs"
- **Cause**: API app registration doesn't exist or no exposed scopes
- **Fix**: Create API app registration first, add exposed API scopes

### Issue: "Permission already exists" error
- **Cause**: Permission was already added
- **Fix**: This is OK, just grant admin consent

### Issue: "User cannot consent" error
- **Cause**: Scope requires admin consent
- **Fix**: Admin must grant consent (green button in API permissions)

## Architecture

```
User → Portal (Client App)
         ↓ (requests access token)
      Azure AD
         ↓ (issues token with API scope)
      Portal receives token
         ↓ (calls API with Bearer token)
      API validates token
         ↓ (checks audience and scope)
      API responds
```

**Required Configuration:**
- ✅ Portal app has API permission configured
- ✅ Admin consent granted for the permission
- ✅ API exposes scopes
- ✅ API validates tokens with correct audience

## Complete Setup Checklist

- [ ] API app registration created
- [ ] API exposes scopes (user_impersonation)
- [ ] Portal app registration created
- [ ] Portal has API permission added
- [ ] Admin consent granted for API access
- [ ] Both service principals exist
- [ ] Test login successful
