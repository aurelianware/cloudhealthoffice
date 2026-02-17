# OAuth 2.0 / OpenID Connect Authentication Setup

## Overview

Cloud Health Office uses **Azure AD multi-tenant authentication** with JWT bearer tokens. Frontend applications obtain tokens via OIDC, then pass them to backend microservices for validation.

**Architecture:**
- **Frontend**: Blazor portal authenticates users via MSAL.js or Microsoft.Identity.Web
- **Token Flow**: Frontend passes JWT access token in `Authorization: Bearer <token>` header
- **Microservices**: Each service independently validates JWT signature, issuer, audience, and lifetime
- **Multi-Tenant Isolation**: `tenant_id` claim from JWT ensures data partitioning by organization

---

## Azure AD App Registration

### 1. Create Multi-Tenant App Registration

**IMPORTANT**: Use a **single app registration** for all microservices, not one per service.

```bash
# Create ONE app registration for Cloud Health Office
az ad app create \
  --display-name "Cloud Health Office API" \
  --sign-in-audience "AzureADMultipleOrgs" \
  --identifier-uris "api://cloudhealthoffice" \
  --web-redirect-uris "https://cloudhealthoffice.com/signin-oidc" \
  --enable-id-token-issuance true \
  --enable-access-token-issuance true
```

**Sign-in audience**: `AzureADMultipleOrgs` (any organizational Azure AD tenant)  
**Application ID URI**: `api://cloudhealthoffice` (same for all services)

### 2. Expose API Scopes

Create scopes for different operations across all microservices:

**Authorization Service:**
```
api://cloudhealthoffice/Authorization.Read
api://cloudhealthoffice/Authorization.ReadWrite
```

**Attachment Service:**
```
api://cloudhealthoffice/Attachments.Upload
api://cloudhealthoffice/Attachments.Download
api://cloudhealthoffice/Attachments.ReadWrite
```

**Eligibility Service:**
```
api://cloudhealthoffice/Eligibility.Query
```

**Claims Service:**
```
api://cloudhealthoffice/Claims.Submit
api://cloudhealthoffice/Claims.Read
```

**Why Single App Registration?**
- ✅ One token contains all scopes user needs
- ✅ Single consent screen for users
- ✅ Simpler credential management
- ✅ Standard Microsoft pattern for microservices
- ✅ Each service validates same token, checks for its required scopes

### 3. Add App Roles

Define roles for RBAC:

```json
{
  "allowedMemberTypes": ["User"],
  "description": "Can manage prior authorizations",
  "displayName": "Prior Auth Manager",
  "id": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "isEnabled": true,
  "value": "PriorAuthManager"
}
```

**Roles:**
- `Administrator` - Full access
- `PriorAuthManager` - Manage 278 authorizations
- `ClaimsProcessor` - Submit/view 837 claims
- `AttachmentUploader` - Upload 275 attachments
- `EligibilityVerifier` - Query 270/271 eligibility

### 4. Add Custom Claims

Configure `tenant_id` claim mapping:

1. Go to **Token configuration**
2. Add optional claim: `extension_tenant_id` (custom attribute)
3. Map Azure AD `tid` → custom `tenant_id` claim via:
   - Azure AD B2C user flows
   - Custom claims transformation policy

---

## Service Configuration

**All services use the SAME ClientId and Audience** (from single app registration).

### authorization-service

**appsettings.json:**
```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "common",
    "ClientId": "<YOUR_CLIENT_ID>",
    "Audience": "api://cloudhealthoffice"
  }
}
```

**Scope validation in code:**
```csharp
[Authorize(Policy = "RequireAuthorizationReadWrite")]
public async Task<ActionResult> SubmitAuthorization(...)
```

**Kubernetes Secret:**
```bash
kubectl -n cloudhealthoffice create secret generic azure-ad-config \
  --from-literal=AzureAd__ClientId='<CLIENT_ID>' \
  --from-literal=AzureAd__TenantId='common' \
  --from-literal=AzureAd__Audience='api://cloudhealthoffice'
```

### attachment-service

**appsettings.json:**
```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "common",
    "ClientId": "<SAME_CLIENT_ID>",
    "Audience": "api://cloudhealthoffice"
  }
}
```

**Scope validation in code:**
```csharp
[Authorize(Policy = "RequireAttachmentUpload")]
public async Task<ActionResult> UploadAttachment(...)
```

---

## Token Claims

### Standard Claims (Azure AD)
```json
{
  "iss": "https://login.microsoftonline.com/{tenant-id}/v2.0",
  "aud": "api://cloudhealthoffice",
  "sub": "user-object-id",
  "tid": "organization-azure-ad-tenant-id",
  "oid": "user-object-id",
  "name": "John Doe",
  "preferred_username": "john.doe@payer.com",
  "roles": ["PriorAuthManager", "AttachmentManager"],
  "scp": "Authorization.ReadWrite Attachments.Upload Eligibility.Query",
  "exp": 1738800000,
  "nbf": 1738796400,
  "iat": 1738796400
}
```

**Key Claims:**
- `aud`: `api://cloudhealthoffice` (same for all services)
- `scp`: Space-separated list of granted scopes
- `roles`: App roles assigned to user

### Custom Claims (CHO-specific)
```json
{
  "tenant_id": "blueshield-ca",
  "extension_TenantId": "blueshield-ca",
  "organization_name": "Blue Shield of California",
  "payer_id": "BLUESHIELD-CA"
}
```

**TenantId Mapping:**
- Azure AD `tid` (organization's AD tenant) maps to CHO `tenant_id` (Cosmos DB partition key)
- Configured via claims transformation or user profile attributes
- Example: Azure AD tenant `12345-abcde` → CHO tenant `blueshield-ca`

---

## Frontend Integration

### Blazor Server (.NET 8)

**Program.cs:**
```csharp
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi(new[] {
        "api://cloudhealthoffice-authorization/Authorization.ReadWrite",
        "api://cloudhealthoffice-attachments/Attachments.Upload"
    })
    .AddInMemoryTokenCaches();

builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = options.DefaultPolicy;
});
```

**API Call with Token:**
```csharp
[Authorize]
public class AuthorizationsController : Controller
{
    private readonly ITokenAcquisition _tokenAcquisition;
    private readonly HttpClient _httpClient;

    public async Task<IActionResult> SubmitAuthorization(Authorization auth)
    {
        var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(new[] {
            "api://cloudhealthoffice-authorization/Authorization.ReadWrite"
        });

        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.PostAsJsonAsync(
            "https://authorization-service/api/Authorizations", auth);
        
        return Ok(await response.Content.ReadFromJsonAsync<Authorization>());
    }
}
```

### React/Angular (MSAL.js)

```typescript
import { PublicClientApplication } from "@azure/msal-browser";

const msalConfig = {
  auth: {
    clientId: "<YOUR_CLIENT_ID>",
    authority: "https://login.microsoftonline.com/common",
    redirectUri: window.location.origin
  }
};

const msalInstance = new PublicClientApplication(msalConfig);

// Acquire token
const tokenRequest = {
  scopes: ["api://cloudhealthoffice-authorization/Authorization.ReadWrite"]
};

const response = await msalInstance.acquireTokenSilent(tokenRequest);
const accessToken = response.accessToken;

// Call API
fetch("https://api.cloudhealthoffice.com/api/Authorizations", {
  method: "POST",
  headers: {
    "Authorization": `Bearer ${accessToken}`,
    "Content-Type": "application/json"
  },
  body: JSON.stringify(authorization)
});
```

---

## TenantMiddleware Flow

1. **Request arrives** with `Authorization: Bearer <JWT>`
2. **UseAuthentication()** validates JWT signature, issuer, audience, lifetime
3. **UseTenantMiddleware()** extracts `tenant_id` from validated claims:
   ```csharp
   var tenantId = context.User.FindFirst("tenant_id")?.Value
                 ?? context.User.FindFirst("extension_TenantId")?.Value
                 ?? context.User.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
   ```
4. **Stores in HttpContext** for repository access: `context.Items["TenantId"] = tenantId`
5. **Repository filters queries** by partition key: `/tenantId`

---

## Development vs Production

### Development (Local Testing)

**Disable authentication:**
```json
{
  "AzureAd": {
    "Enabled": false
  }
}
```

**Use header-based tenant:**
```bash
curl -H "X-Tenant-ID: blueshield-ca" \
     -H "Content-Type: application/json" \
     http://localhost:8082/api/Authorizations
```

### Production (Kubernetes)

**Always require JWT:**
- No `X-Tenant-ID` header fallback
- Health endpoints exempt: `/health`, `/ready`, `/live`
- All API calls must include valid bearer token

---

## Testing

### Get Test Token

```bash
# Using Azure CLI
az login
az account get-access-token \
  --resource api://cloudhealthoffice-authorization \
  --query accessToken -o tsv
```

### Test API Call

```bash
TOKEN=$(az account get-access-token --resource api://cloudhealthoffice-authorization --query accessToken -o tsv)

curl -H "Authorization: Bearer $TOKEN" \
     -H "Content-Type: application/json" \
     -d @test-auth-request.json \
     https://api.cloudhealthoffice.com/api/Authorizations
```

### Validate Token

**jwt.ms** - Paste token to decode claims  
**jwt.io** - Verify signature with public key

---

## Security Best Practices

1. **Use HTTPS only** - TLS 1.2+ required
2. **Short token lifetime** - 1 hour max (configurable in Azure AD)
3. **Validate signature** - Microsoft.Identity.Web handles this automatically
4. **Validate audience** - Each service validates its own `aud` claim
5. **Clock skew tolerance** - 5 minutes to handle time drift
6. **No anonymous endpoints** - Except `/health` for Kubernetes probes
7. **Role-based access** - Use `[Authorize(Roles = "PriorAuthManager")]` attributes
8. **Audit logging** - Log all authenticated requests with user/tenant context

---

## Troubleshooting

### "Unauthorized" (401)

- **Missing token**: Check `Authorization` header
- **Expired token**: Refresh token via MSAL
- **Invalid signature**: Verify ClientId and TenantId in appsettings.json
- **Wrong audience**: Token `aud` must match service's `Audience` config

### "Forbidden" (403)

- **Missing role**: User doesn't have required role in token
- **Check token claims**: Decode token at jwt.ms
- **Assign roles**: Azure AD → App Roles → Assign users

### "No TenantId found"

- **Missing claim**: `tenant_id` not in token
- **Configure claim mapping**: Azure AD → Token configuration
- **Check middleware logs**: Look for "TenantId extracted from JWT"

---

## Migration Plan

### Phase 1: Add Authentication (Current)
- ✅ Add Microsoft.Identity.Web packages
- ✅ Configure JWT validation in Program.cs
- ✅ Update TenantMiddleware to extract tenant_id from JWT
- ✅ Update appsettings.json with AzureAd section

### Phase 2: Azure AD Setup
- Create multi-tenant app registration
- Define API scopes and app roles
- Configure custom claims (tenant_id mapping)

### Phase 3: Frontend Integration
- Add MSAL.js or Microsoft.Identity.Web to portal
- Implement token acquisition
- Pass tokens to backend APIs

### Phase 4: Production Deployment
- Remove header-based tenant fallback
- Require authentication for all non-health endpoints
- Enable audit logging

---

## References

- [Microsoft Identity Platform](https://learn.microsoft.com/en-us/azure/active-directory/develop/)
- [Microsoft.Identity.Web Documentation](https://learn.microsoft.com/en-us/azure/active-directory/develop/microsoft-identity-web)
- [MSAL.js Documentation](https://learn.microsoft.com/en-us/azure/active-directory/develop/msal-js-initializing-client-applications)
- [Multi-tenant Applications](https://learn.microsoft.com/en-us/azure/active-directory/develop/howto-convert-app-to-be-multi-tenant)
