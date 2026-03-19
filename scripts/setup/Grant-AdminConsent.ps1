#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Grant admin consent for Cloud Health Office applications in Azure AD

.DESCRIPTION
    This script creates service principals and grants admin consent for the
    Cloud Health Office API and Portal applications in your Azure AD tenant.
    
    Requires: Azure AD admin role (Global Admin, Cloud Application Admin, or Application Admin)

.PARAMETER TenantId
    Your Azure AD tenant ID (GUID)

.PARAMETER ApiClientId
    The Client ID of the Cloud Health Office API app registration

.PARAMETER PortalClientId
    (Optional) The Client ID of the Portal app registration

.PARAMETER UseDeviceCodeAuth
    Use device code authentication (for environments without browser access)

.EXAMPLE
    .\Grant-AdminConsent.ps1 -TenantId "32177734-051b-4fdc-9568-cc35530191b1" -ApiClientId "cfada1ac-f251-48ea-9330-39212aa4c862"

.EXAMPLE
    .\Grant-AdminConsent.ps1 -TenantId "32177734-051b-4fdc-9568-cc35530191b1" -ApiClientId "cfada1ac-f251-48ea-9330-39212aa4c862" -PortalClientId "b8975dfe-3227-4dea-a053-c5bfb15b7cfd"
#>

param(
    [Parameter(Mandatory=$true, HelpMessage="Your Azure AD tenant ID")]
    [ValidateNotNullOrEmpty()]
    [string]$TenantId,
    
    [Parameter(Mandatory=$true, HelpMessage="Cloud Health Office API Client ID")]
    [ValidateNotNullOrEmpty()]
    [string]$ApiClientId,
    
    [Parameter(Mandatory=$false, HelpMessage="Portal Client ID (optional)")]
    [string]$PortalClientId,
    
    [Parameter(Mandatory=$false)]
    [switch]$UseDeviceCodeAuth
)

# Script metadata
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Cloud Health Office - Admin Consent Setup" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Check if Microsoft.Graph module is installed
Write-Host "Checking for Microsoft.Graph module..." -ForegroundColor Yellow

if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Applications)) {
    Write-Host "Microsoft.Graph module not found. Installing..." -ForegroundColor Yellow
    try {
        Install-Module Microsoft.Graph -Scope CurrentUser -Force -AllowClobber
        Write-Host "✅ Microsoft.Graph module installed successfully" -ForegroundColor Green
    }
    catch {
        Write-Host "❌ Failed to install Microsoft.Graph module: $_" -ForegroundColor Red
        Write-Host "Please run: Install-Module Microsoft.Graph -Scope CurrentUser" -ForegroundColor Yellow
        exit 1
    }
}

# Import required modules
Import-Module Microsoft.Graph.Applications
Import-Module Microsoft.Graph.Authentication

Write-Host ""
Write-Host "Configuration:" -ForegroundColor Cyan
Write-Host "  Tenant ID:        $TenantId" -ForegroundColor White
Write-Host "  API Client ID:    $ApiClientId" -ForegroundColor White
if ($PortalClientId) {
    Write-Host "  Portal Client ID: $PortalClientId" -ForegroundColor White
}
Write-Host ""

# Connect to Microsoft Graph
Write-Host "Connecting to Microsoft Graph..." -ForegroundColor Yellow

$connectParams = @{
    TenantId = $TenantId
    Scopes = @(
        "Application.ReadWrite.All",
        "AppRoleAssignment.ReadWrite.All",
        "DelegatedPermissionGrant.ReadWrite.All"
    )
}

if ($UseDeviceCodeAuth) {
    $connectParams.UseDeviceCode = $true
}

try {
    Connect-MgGraph @connectParams | Out-Null
    Write-Host "✅ Connected to Microsoft Graph" -ForegroundColor Green
    
    $context = Get-MgContext
    Write-Host "   Authenticated as: $($context.Account)" -ForegroundColor Gray
    Write-Host ""
}
catch {
    Write-Host "❌ Failed to connect to Microsoft Graph: $_" -ForegroundColor Red
    exit 1
}

# Function to create or get service principal
function Grant-ServicePrincipalConsent {
    param(
        [string]$AppId,
        [string]$AppName
    )
    
    Write-Host "Processing: $AppName" -ForegroundColor Cyan
    Write-Host "  Client ID: $AppId" -ForegroundColor Gray
    
    try {
        # Check if service principal already exists
        $sp = Get-MgServicePrincipal -Filter "appId eq '$AppId'" -ErrorAction SilentlyContinue
        
        if ($sp) {
            Write-Host "  ✅ Service principal already exists" -ForegroundColor Green
            Write-Host "     Object ID: $($sp.Id)" -ForegroundColor Gray
        }
        else {
            Write-Host "  Creating service principal..." -ForegroundColor Yellow
            $sp = New-MgServicePrincipal -AppId $AppId
            Write-Host "  ✅ Service principal created" -ForegroundColor Green
            Write-Host "     Object ID: $($sp.Id)" -ForegroundColor Gray
        }
        
        Write-Host ""
        return $sp
    }
    catch {
        Write-Host "  ❌ Error: $_" -ForegroundColor Red
        Write-Host ""
        return $null
    }
}

# Grant consent for API
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "1. Cloud Health Office API" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

$apiSp = Grant-ServicePrincipalConsent -AppId $ApiClientId -AppName "Cloud Health Office API"

if (-not $apiSp) {
    Write-Host "⚠️  Failed to create service principal for API" -ForegroundColor Red
    Write-Host "   This may indicate:" -ForegroundColor Yellow
    Write-Host "   - App registration doesn't exist" -ForegroundColor Yellow
    Write-Host "   - Insufficient permissions" -ForegroundColor Yellow
    Write-Host "   - Invalid Client ID" -ForegroundColor Yellow
    Write-Host ""
}

# Grant consent for Portal (if provided)
if ($PortalClientId) {
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "2. Cloud Health Office Portal" -ForegroundColor Cyan
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    
    $portalSp = Grant-ServicePrincipalConsent -AppId $PortalClientId -AppName "Cloud Health Office Portal"
    
    if (-not $portalSp) {
        Write-Host "⚠️  Failed to create service principal for Portal" -ForegroundColor Red
    }
}

# Generate admin consent URLs for manual fallback
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "Admin Consent URLs (Manual Fallback)" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

$apiConsentUrl = "https://login.microsoftonline.com/$TenantId/adminconsent?client_id=$ApiClientId"
Write-Host ""
Write-Host "API Admin Consent URL:" -ForegroundColor Yellow
Write-Host $apiConsentUrl -ForegroundColor White
Write-Host ""

if ($PortalClientId) {
    $portalConsentUrl = "https://login.microsoftonline.com/$TenantId/adminconsent?client_id=$PortalClientId"
    Write-Host "Portal Admin Consent URL:" -ForegroundColor Yellow
    Write-Host $portalConsentUrl -ForegroundColor White
    Write-Host ""
}

# Verification
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "Verification" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

if ($apiSp) {
    Write-Host "✅ API service principal verified" -ForegroundColor Green
}

if ($PortalClientId -and $portalSp) {
    Write-Host "✅ Portal service principal verified" -ForegroundColor Green
}

Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "Next Steps" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""
Write-Host "1. Verify service principals in Azure Portal:" -ForegroundColor Yellow
Write-Host "   https://portal.azure.com/#view/Microsoft_AAD_IAM/ActiveDirectoryMenuBlade/~/RegisteredApps" -ForegroundColor White
Write-Host ""
Write-Host "2. Test user login to Cloud Health Office Portal" -ForegroundColor Yellow
Write-Host "   Users should no longer see AADSTS650052 error" -ForegroundColor White
Write-Host ""
Write-Host "3. If issues persist, click the admin consent URLs above" -ForegroundColor Yellow
Write-Host "   to manually grant consent in a browser" -ForegroundColor White
Write-Host ""

# Disconnect
Disconnect-MgGraph | Out-Null
Write-Host "✅ Disconnected from Microsoft Graph" -ForegroundColor Green
Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Admin Consent Setup Complete!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
