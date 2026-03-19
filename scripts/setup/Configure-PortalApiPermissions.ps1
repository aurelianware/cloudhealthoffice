#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Configure Cloud Health Office Portal to call the API with proper permissions

.DESCRIPTION
    Fixes AADSTS1003031 error by:
    1. Adding API permissions to portal app registration
    2. Ensuring API exposes the required scopes
    3. Granting admin consent for the permissions

.PARAMETER TenantId
    Your Azure AD tenant ID

.PARAMETER PortalClientId
    Portal app registration client ID

.PARAMETER ApiClientId
    API app registration client ID

.EXAMPLE
    .\Configure-PortalApiPermissions.ps1 -TenantId "32177734-051b-4fdc-9568-cc35530191b1" -PortalClientId "abc123..." -ApiClientId "cfada1ac-f251-48ea-9330-39212aa4c862"
#>

param(
    [Parameter(Mandatory=$true, HelpMessage="Azure AD tenant ID")]
    [ValidateNotNullOrEmpty()]
    [string]$TenantId,
    
    [Parameter(Mandatory=$true, HelpMessage="Portal app registration client ID")]
    [ValidateNotNullOrEmpty()]
    [string]$PortalClientId,
    
    [Parameter(Mandatory=$true, HelpMessage="API app registration client ID")]
    [ValidateNotNullOrEmpty()]
    [string]$ApiClientId
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Configure Portal API Permissions" -ForegroundColor Cyan
Write-Host "Fix for AADSTS1003031" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Install required modules
Write-Host "Checking for Microsoft.Graph modules..." -ForegroundColor Yellow

$requiredModules = @(
    "Microsoft.Graph.Applications",
    "Microsoft.Graph.Authentication"
)

foreach ($module in $requiredModules) {
    if (-not (Get-Module -ListAvailable -Name $module)) {
        Write-Host "Installing $module..." -ForegroundColor Yellow
        Install-Module $module -Scope CurrentUser -Force -AllowClobber
    }
}

Import-Module Microsoft.Graph.Applications
Import-Module Microsoft.Graph.Authentication

Write-Host "✅ Modules loaded" -ForegroundColor Green
Write-Host ""

# Connect to Microsoft Graph
Write-Host "Connecting to Microsoft Graph..." -ForegroundColor Yellow

try {
    Connect-MgGraph -TenantId $TenantId -Scopes @(
        "Application.ReadWrite.All",
        "DelegatedPermissionGrant.ReadWrite.All"
    ) | Out-Null
    
    $context = Get-MgContext
    Write-Host "✅ Connected as: $($context.Account)" -ForegroundColor Green
    Write-Host ""
}
catch {
    Write-Host "❌ Failed to connect: $_" -ForegroundColor Red
    exit 1
}

# Step 1: Verify API app registration and get/create scope
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "Step 1: Verify API Configuration" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

try {
    $apiApp = Get-MgApplication -Filter "appId eq '$ApiClientId'"
    
    if (-not $apiApp) {
        Write-Host "❌ API app registration not found" -ForegroundColor Red
        Write-Host "   Client ID: $ApiClientId" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "✅ API app found: $($apiApp.DisplayName)" -ForegroundColor Green
    Write-Host "   Object ID: $($apiApp.Id)" -ForegroundColor Gray
    
    # Check if API exposes scopes
    $scopes = $apiApp.Api.Oauth2PermissionScopes
    
    if (-not $scopes -or $scopes.Count -eq 0) {
        Write-Host "⚠️  API doesn't expose any scopes. Creating default scope..." -ForegroundColor Yellow
        
        $scopeId = (New-Guid).Guid
        $newScope = @{
            AdminConsentDescription = "Allow the application to access Cloud Health Office API on behalf of the signed-in user"
            AdminConsentDisplayName = "Access Cloud Health Office API"
            Id = $scopeId
            IsEnabled = $true
            Type = "User"
            UserConsentDescription = "Allow the application to access Cloud Health Office API on your behalf"
            UserConsentDisplayName = "Access Cloud Health Office API"
            Value = "user_impersonation"
        }
        
        $apiUpdate = @{
            Api = @{
                Oauth2PermissionScopes = @($newScope)
            }
            IdentifierUris = @("api://$ApiClientId")
        }
        
        Update-MgApplication -ApplicationId $apiApp.Id -BodyParameter $apiUpdate
        
        Write-Host "✅ Created 'user_impersonation' scope" -ForegroundColor Green
        Write-Host "   Scope ID: $scopeId" -ForegroundColor Gray
        
        # Refresh app registration
        Start-Sleep -Seconds 3
        $apiApp = Get-MgApplication -ApplicationObjectId $apiApp.Id
        $userImpersonationScopeId = $scopeId
    }
    else {
        $userImpersonationScope = $scopes | Where-Object { $_.Value -eq "user_impersonation" }
        
        if ($userImpersonationScope) {
            Write-Host "✅ Found 'user_impersonation' scope" -ForegroundColor Green
            Write-Host "   Scope ID: $($userImpersonationScope.Id)" -ForegroundColor Gray
            $userImpersonationScopeId = $userImpersonationScope.Id
        }
        else {
            Write-Host "⚠️  'user_impersonation' scope not found. Using first available scope..." -ForegroundColor Yellow
            $userImpersonationScopeId = $scopes[0].Id
            Write-Host "   Using scope: $($scopes[0].Value)" -ForegroundColor Gray
        }
    }
    
    Write-Host ""
}
catch {
    Write-Host "❌ Error: $_" -ForegroundColor Red
    exit 1
}

# Step 2: Configure portal app permissions
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "Step 2: Configure Portal API Permissions" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

try {
    $portalApp = Get-MgApplication -Filter "appId eq '$PortalClientId'"
    
    if (-not $portalApp) {
        Write-Host "❌ Portal app registration not found" -ForegroundColor Red
        Write-Host "   Client ID: $PortalClientId" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "✅ Portal app found: $($portalApp.DisplayName)" -ForegroundColor Green
    Write-Host "   Object ID: $($portalApp.Id)" -ForegroundColor Gray
    
    # Build required resource access
    $requiredResourceAccess = @(
        @{
            ResourceAppId = $ApiClientId
            ResourceAccess = @(
                @{
                    Id = $userImpersonationScopeId
                    Type = "Scope"
                }
            )
        }
    )
    
    # Check if permission already exists
    $existingPermission = $portalApp.RequiredResourceAccess | Where-Object { $_.ResourceAppId -eq $ApiClientId }
    
    if ($existingPermission) {
        Write-Host "⚠️  API permission already configured" -ForegroundColor Yellow
        Write-Host "   Updating..." -ForegroundColor Yellow
    }
    
    # Update portal app
    $portalUpdate = @{
        RequiredResourceAccess = $requiredResourceAccess
    }
    
    Update-MgApplication -ApplicationId $portalApp.Id -BodyParameter $portalUpdate
    
    Write-Host "✅ API permission added to portal app" -ForegroundColor Green
    Write-Host "   Resource: Cloud Health Office API" -ForegroundColor Gray
    Write-Host "   Permission: user_impersonation (Delegated)" -ForegroundColor Gray
    Write-Host ""
}
catch {
    Write-Host "❌ Error: $_" -ForegroundColor Red
    exit 1
}

# Step 3: Grant admin consent
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "Step 3: Grant Admin Consent" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

try {
    # Get service principals
    $portalSp = Get-MgServicePrincipal -Filter "appId eq '$PortalClientId'" -ErrorAction SilentlyContinue
    $apiSp = Get-MgServicePrincipal -Filter "appId eq '$ApiClientId'" -ErrorAction SilentlyContinue
    
    # Create service principals if they don't exist
    if (-not $portalSp) {
        Write-Host "Creating portal service principal..." -ForegroundColor Yellow
        $portalSp = New-MgServicePrincipal -AppId $PortalClientId
        Write-Host "✅ Portal service principal created" -ForegroundColor Green
    }
    
    if (-not $apiSp) {
        Write-Host "Creating API service principal..." -ForegroundColor Yellow
        $apiSp = New-MgServicePrincipal -AppId $ApiClientId
        Write-Host "✅ API service principal created" -ForegroundColor Green
    }
    
    # Grant OAuth2 permission (admin consent)
    $oauth2Grant = @{
        ClientId = $portalSp.Id
        ConsentType = "AllPrincipals"
        ResourceId = $apiSp.Id
        Scope = "user_impersonation"
    }
    
    try {
        New-MgOauth2PermissionGrant -BodyParameter $oauth2Grant -ErrorAction Stop | Out-Null
        Write-Host "✅ Admin consent granted" -ForegroundColor Green
    }
    catch {
        if ($_.Exception.Message -like "*Permission being granted*already exists*") {
            Write-Host "✅ Admin consent already granted" -ForegroundColor Green
        }
        else {
            throw $_
        }
    }
    
    Write-Host ""
}
catch {
    Write-Host "⚠️  Could not grant admin consent automatically" -ForegroundColor Yellow
    Write-Host "   Error: $_" -ForegroundColor Gray
    Write-Host ""
    Write-Host "   Please grant consent manually:" -ForegroundColor Yellow
    Write-Host "   https://login.microsoftonline.com/$TenantId/adminconsent?client_id=$PortalClientId" -ForegroundColor White
    Write-Host ""
}

# Verification
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "Verification" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

$portalApp = Get-MgApplication -ApplicationObjectId $portalApp.Id
$apiPermission = $portalApp.RequiredResourceAccess | Where-Object { $_.ResourceAppId -eq $ApiClientId }

if ($apiPermission) {
    Write-Host "✅ Portal app has API permission configured" -ForegroundColor Green
}
else {
    Write-Host "❌ API permission not found on portal app" -ForegroundColor Red
}

Write-Host ""

# Next steps
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "Next Steps" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""
Write-Host "1. Verify in Azure Portal:" -ForegroundColor Yellow
Write-Host "   Portal App → API Permissions → Should show Cloud Health Office API" -ForegroundColor White
Write-Host ""
Write-Host "2. Test user login:" -ForegroundColor Yellow
Write-Host "   https://portal.cloudhealthoffice.com" -ForegroundColor White
Write-Host ""
Write-Host "3. If issues persist, manually grant consent:" -ForegroundColor Yellow
Write-Host "   https://portal.azure.com → Portal App → API Permissions → Grant admin consent" -ForegroundColor White
Write-Host ""

Disconnect-MgGraph | Out-Null

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "✅ Configuration Complete!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
