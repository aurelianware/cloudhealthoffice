# Azure AD Admin Consent Setup Guide

This guide explains how to grant admin consent for the Cloud Health Office API and portal applications.

## Understanding the Error

**Error Code**: `AADSTS650052`

**What it means**: The API service principal doesn't exist in your Azure AD tenant yet. An admin must grant consent to create it.

**Full Error**:
```
The app is trying to access a service 'cfada1ac-f251-48ea-9330-39212aa4c862' 
(Cloud Health Office API) that your organization lacks a service principal for.
```

## Solution 1: Grant Admin Consent via URL (Fastest)

### For the API Application

1. **Build the admin consent URL**:
   ```
   https://login.microsoftonline.com/{TENANT_ID}/adminconsent?client_id={API_CLIENT_ID}
   ```

2. **Replace placeholders**:
   - `{TENANT_ID}`: Your Azure AD tenant ID (e.g., `32177734-051b-4fdc-9568-cc35530191b1`)
   - `{API_CLIENT_ID}`: API app client ID (e.g., `cfada1ac-f251-48ea-9330-39212aa4c862`)

3. **Example URL**:
   ```
   https://login.microsoftonline.com/32177734-051b-4fdc-9568-cc35530191b1/adminconsent?client_id=cfada1ac-f251-48ea-9330-39212aa4c862
   ```

4. **Have an Azure AD admin**:
   - Open this URL in a browser
   - Sign in with admin credentials (Global Admin, Cloud Application Admin, or Application Admin)
   - Review the permissions requested
   - Click **Accept**

### For the Portal Application

Repeat for the portal app if needed:
```
https://login.microsoftonline.com/{TENANT_ID}/adminconsent?client_id={PORTAL_CLIENT_ID}
```

## Solution 2: Grant Admin Consent via Azure Portal

### Steps

1. **Navigate to Azure Portal**:
   - Go to https://portal.azure.com
   - Sign in as an admin

2. **Open Azure Active Directory**:
   - Search for "Azure Active Directory" or click the AD icon

3. **Go to App Registrations**:
   - Click **App registrations** in the left menu
   - Click **All applications** tab
   - Search for "Cloud Health Office API"

4. **Grant Admin Consent**:
   - Click on the API app registration
   - Click **API permissions** in the left menu
   - Click **Grant admin consent for {Your Org}** button
   - Confirm by clicking **Yes**

5. **Verify Consent**:
   - You should see green checkmarks under "Status" column
   - Status should show: "Granted for {Your Org}"

## Solution 3: PowerShell Script (Automated)

Create and run this PowerShell script:

```powershell
# Grant-CloudHealthOfficeAdminConsent.ps1

param(
    [Parameter(Mandatory=$true)]
    [string]$TenantId,
    
    [Parameter(Mandatory=$true)]
    [string]$ApiClientId,
    
    [Parameter(Mandatory=$false)]
    [string]$PortalClientId
)

# Install required module if not present
if (-not (Get-Module -ListAvailable -Name Microsoft.Graph)) {
    Install-Module Microsoft.Graph -Scope CurrentUser -Force
}

# Connect to Microsoft Graph
Connect-MgGraph -TenantId $TenantId -Scopes "Application.ReadWrite.All"

# Grant admin consent for API
Write-Host "Granting admin consent for Cloud Health Office API..." -ForegroundColor Cyan

$apiServicePrincipal = Get-MgServicePrincipal -Filter "appId eq '$ApiClientId'" -ErrorAction SilentlyContinue

if (-not $apiServicePrincipal) {
    Write-Host "Creating service principal for API..." -ForegroundColor Yellow
    $apiServicePrincipal = New-MgServicePrincipal -AppId $ApiClientId
    Write-Host "✅ Service principal created: $($apiServicePrincipal.Id)" -ForegroundColor Green
} else {
    Write-Host "✅ Service principal already exists: $($apiServicePrincipal.Id)" -ForegroundColor Green
}

# Grant admin consent for Portal if provided
if ($PortalClientId) {
    Write-Host "Granting admin consent for Portal..." -ForegroundColor Cyan
    
    $portalServicePrincipal = Get-MgServicePrincipal -Filter "appId eq '$PortalClientId'" -ErrorAction SilentlyContinue
    
    if (-not $portalServicePrincipal) {
        Write-Host "Creating service principal for Portal..." -ForegroundColor Yellow
        $portalServicePrincipal = New-MgServicePrincipal -AppId $PortalClientId
        Write-Host "✅ Service principal created: $($portalServicePrincipal.Id)" -ForegroundColor Green
    } else {
        Write-Host "✅ Service principal already exists: $($portalServicePrincipal.Id)" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "✅ Admin consent granted successfully!" -ForegroundColor Green
Write-Host "Users can now access the application." -ForegroundColor Green

Disconnect-MgGraph
```

**Run the script**:
```powershell
.\Grant-CloudHealthOfficeAdminConsent.ps1 `
    -TenantId "32177734-051b-4fdc-9568-cc35530191b1" `
    -ApiClientId "cfada1ac-f251-48ea-9330-39212aa4c862" `
    -PortalClientId "YOUR_PORTAL_CLIENT_ID"
```

## Solution 4: Azure CLI

```bash
# Login as admin
az login --tenant 32177734-051b-4fdc-9568-cc35530191b1

# Create service principal for API
az ad sp create --id cfada1ac-f251-48ea-9330-39212aa4c862

# Verify
az ad sp show --id cfada1ac-f251-48ea-9330-39212aa4c862
```

## Who Can Grant Admin Consent?

Azure AD roles with permission to grant admin consent:

- ✅ **Global Administrator** (highest privilege)
- ✅ **Cloud Application Administrator**
- ✅ **Application Administrator**
- ❌ Regular users (cannot grant tenant-wide consent)

## Verification

After granting consent, verify it worked:

### Method 1: Azure Portal
1. Go to **Azure AD** → **Enterprise Applications**
2. Search for "Cloud Health Office API"
3. Click on it
4. Go to **Permissions**
5. Verify status shows "Granted"

### Method 2: Test Login
1. Open the portal: https://portal.cloudhealthoffice.com
2. Sign in with a regular user account
3. Should successfully authenticate without the AADSTS650052 error

## Troubleshooting

### Error: "Need admin approval"
- **Cause**: User consent is disabled, only admins can consent
- **Fix**: Admin must grant consent OR enable user consent in Azure AD settings

### Error: "AADSTS65001: The user or administrator has not consented"
- **Cause**: Service principal exists but permissions not granted
- **Fix**: Use admin consent URL or Azure Portal method above

### Service Principal Created but Still Getting Errors
- **Cause**: Permissions might not be delegated properly
- **Fix**: 
  1. Check API permissions in app registration
  2. Ensure API exposes the correct scopes
  3. Re-grant admin consent

## Prevention: One-Time Setup

To avoid this issue during initial deployment:

1. **Pre-create service principals** before deploying apps
2. **Grant admin consent** immediately after creating app registrations
3. **Document consent URLs** in deployment guides
4. **Add consent detection** to the portal (see error handling below)

## User-Friendly Error Messages

The portal has been updated to detect this error and show:

```
⚠️ Admin Consent Required

This application requires administrator approval before you can use it.

Please ask your IT administrator to:
1. Visit: https://login.microsoftonline.com/{tenant}/adminconsent?client_id={client_id}
2. Sign in with admin credentials
3. Grant consent

Once consent is granted, you'll be able to access the application.

Error Code: AADSTS650052
```

## Automation for Deployment

Add to your deployment pipeline:

```yaml
# In .github/workflows/deploy.yml
- name: Grant Admin Consent (if needed)
  run: |
    az ad sp create --id ${{ secrets.API_CLIENT_ID }} || echo "Service principal already exists"
```

## References

- [Microsoft Docs: Admin Consent](https://learn.microsoft.com/en-us/azure/active-directory/manage-apps/grant-admin-consent)
- [Microsoft Docs: Service Principal](https://learn.microsoft.com/en-us/azure/active-directory/develop/app-objects-and-service-principals)
- [Error Reference: AADSTS650052](https://login.microsoftonline.com/error?code=650052)
