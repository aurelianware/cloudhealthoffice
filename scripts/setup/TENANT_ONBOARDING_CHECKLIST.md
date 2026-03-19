# HTR — Tenant Onboarding Checklist

## 1. Get the Tenant's Azure AD Tenant ID

- [ ] Ask the tenant admin for their Azure AD **Tenant ID** (GUID).
  - Found at: **Azure Portal → Azure Active Directory → Overview → Tenant ID**

## 2. Run the MongoDB Seed Script

```bash
mongosh "<your-mongodb-connection-string>/CloudHealthOffice" scripts/setup/seed-tenant.js \
  --eval 'var tenantId="my-org", azureTenantId="AZURE-AD-GUID", orgName="My Org", adminEmail="admin@example.com"'
```

- [ ] Verify the document was inserted:
  ```bash
  mongosh "<connection-string>/CloudHealthOffice" --eval 'db.Tenants.findOne({tenantId:"my-org"})'
  ```

## 3. Azure AD — What You (CHO Admin) Need to Do

The portal app registration (`54f3419d-0d69-4b06-939a-c1a260596556`) is already configured as **multi-tenant** (`TenantId: "common"`), so:

- [x] **No redirect URI changes needed.** The existing `/signin-oidc` and `/signout-callback-oidc` paths work for all tenants.
- [x] **No API permission changes needed.** The portal's permissions (OpenID Connect `openid profile email` + downstream API scopes) are already declared on the app registration. They apply to all tenants that consent.
- [x] **No app registration modifications needed.** Multi-tenant apps accept tokens from any Azure AD tenant.

## 4. Azure AD — What the Tenant Admin Needs to Do

The tenant admin needs **Azure AD admin consent** the first time a user from their org logs in. There are two options:

### Option A: Admin Consent URL (Recommended — Proactive)

Send the tenant admin these URLs (replace `TENANT_ID` with their actual tenant ID).

**Portal consent:**

```
https://login.microsoftonline.com/TENANT_ID/adminconsent?client_id=54f3419d-0d69-4b06-939a-c1a260596556&redirect_uri=https://portal.cloudhealthoffice.com/signin-oidc
```

> **Note:** The `redirect_uri` parameter is required. Without it, Azure AD redirects to the app's default homepage after consent, which returns a 404.

The admin (who must be a **Global Admin**, **Cloud Application Admin**, or **Application Admin**) visits this URL:
1. Azure AD shows the permissions the CHO Portal requests
2. Admin clicks **Accept** to grant consent for all users in their org
3. Azure AD creates a **service principal** for the CHO Portal in their tenant

### Option B: First-Login Consent Prompt (Automatic)

If the admin doesn't pre-consent, Azure AD will prompt them to grant admin consent on first login to `portal.cloudhealthoffice.com`. They'll see an "Approval required" screen. This works, but:
- Only admin users see the consent prompt; regular users get an error (AADSTS65001)
- It's a better experience to use Option A before telling users to log in

### Option C: Use the Grant-AdminConsent.ps1 Script

You can also run the existing script from the admin's machine or with their credentials:

```powershell
.\scripts\setup\Grant-AdminConsent.ps1 `
  -TenantId "TENANT_ID" `
  -ApiClientId "cfada1ac-f251-48ea-9330-39212aa4c862" `
  -PortalClientId "54f3419d-0d69-4b06-939a-c1a260596556"
```

This creates the service principals and grants consent for both the API and Portal apps in the tenant.

## 5. Downstream API Consent

The portal calls the CHO Authorization Service API (`cfada1ac-f251-48ea-9330-39212aa4c862`) with these scopes:
- `Authorization.ReadWrite`
- `Attachments.ReadWrite`
- `Eligibility.Query`
- `Claims.Submit`

The tenant admin must also consent to the **API** app registration. The `Grant-AdminConsent.ps1` script (Option C) handles both. If using the URL method, send a second URL:

```
https://login.microsoftonline.com/TENANT_ID/adminconsent?client_id=cfada1ac-f251-48ea-9330-39212aa4c862
```

## 6. Verify End-to-End

- [ ] Tenant admin logs in at `portal.cloudhealthoffice.com`
- [ ] Index.razor extracts `tid` claim → matches `azureTenantId` in Tenants collection
- [ ] User is routed to `/dashboard` (not `/signup` or `/demo`)
- [ ] Downstream API calls succeed (no AADSTS650052 errors)

## Summary: Do I Need to Change the App Registration?

| Question | Answer |
|----------|--------|
| Add redirect URIs? | **No** — existing URIs work for all tenants |
| Add API permissions? | **No** — permissions are on the app reg, not per-tenant |
| Change TenantId from "common"? | **No** — "common" is correct for multi-tenant |
| Modify the portal code? | **No** — Index.razor already handles the lookup |
| Tenant admin needs to do something? | **Yes** — grant admin consent (Option A, B, or C above) |
