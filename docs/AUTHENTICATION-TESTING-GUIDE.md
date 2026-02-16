# Azure AD B2C Authentication Implementation - Testing Guide

> **⚠️ DEPRECATED - February 2026**  
> This guide documents the legacy Azure AD B2C implementation which has been replaced with **multi-tenant Microsoft Entra ID** authentication.  
> Azure AD B2C cannot be provisioned after May 1, 2025, and B2C is not the correct pattern for B2B SaaS platforms.  
> See `site/README.md` for current authentication setup instructions.

## Overview

This document provides testing instructions for the Azure AD B2C authentication implementation in Cloud Health Office.

## Implementation Summary

### Files Created
1. **site/login.html** - Sentinel-themed login/registration page
2. **site/js/auth.js** - Authentication helper library with core functions

### Files Modified
1. **site/staticwebapp.config.json** - Added Azure AD B2C authentication configuration
2. **site/index.html** - Added auth.js and dynamic navigation
3. **site/platform.html** - Added auth.js and dynamic navigation
4. **site/insights.html** - Added auth.js and dynamic navigation
5. **site/assessment.html** - Added auth.js and dynamic navigation
6. **site/release-notes.html** - Added auth.js and dynamic navigation
7. **site/js/markdown-converter.js** - Updated template for generated pages
8. **site/README.md** - Added comprehensive Azure AD B2C setup guide

## Key Features Implemented

### 1. Authentication Configuration
- Azure AD B2C identity provider configured in `staticwebapp.config.json`
- Protected routes: `/portal/*` and `/api/*` require authentication
- Login page accessible to anonymous users
- 401 errors redirect to Azure AD B2C login

### 2. Login Page (login.html)
- **Sentinel Theme**: Dark background with neon cyan/green accents
- **Branding**: Cloud Health Office logo and messaging
- **Functionality**:
  - "Sign In with Azure AD B2C" button
  - "Create Account" flow
  - Auto-redirect if already authenticated
  - Social proof elements highlighting platform benefits
- **Accessibility**: WCAG 2.1 Level AA compliant
  - Skip to main content link
  - Proper semantic HTML
  - Color contrast ratios exceed requirements

### 3. Authentication Helper Library (auth.js)

**Functions:**
- `loadUserProfile()` - Fetch user from `/.auth/me`
- `isAuthenticated()` - Check auth status
- `requireAuth(returnUrl)` - Redirect to login if not authenticated
- `callAuthenticatedAPI(url, options)` - Make authenticated API calls
- `logout(returnUrl)` - Log out current user
- `getUserDisplayName()` - Get user display name
- `updateNavigation(navSelector)` - Dynamically update navigation

**Security Features:**
- Proper error handling for API calls
- Auto-redirect on 401 responses
- No inline event handlers (CSP compliant)
- Uses addEventListener for event binding

### 4. Dynamic Navigation
All HTML pages now include dynamic navigation that shows:
- **When Not Authenticated**: "Sign In" button (cyan, bold)
- **When Authenticated**: 
  - "Portal (UserName)" link (green)
  - "Sign Out" link (gray)

## Testing Instructions

### Prerequisites
Before testing authentication, you need:
1. Azure AD B2C tenant created
2. App registration configured with redirect URIs
3. User flow `B2C_1_SignUpSignIn` created
4. Static Web App deployed to Azure
5. Application settings configured with client ID and secret

### Manual Testing Checklist

#### 1. Login Page Display
- [ ] Navigate to `/login.html`
- [ ] Verify Sentinel theme (dark background, cyan accents)
- [ ] Verify logo displays correctly
- [ ] Verify "Sign In with Azure AD B2C" button is visible
- [ ] Verify "Create Account" button is visible
- [ ] Verify social proof section displays platform benefits

#### 2. Registration Flow
- [ ] Click "Create Account"
- [ ] Redirects to Azure AD B2C registration page
- [ ] Complete registration form
- [ ] Verify email (if configured)
- [ ] Redirected back to `/portal/` after successful registration

#### 3. Login Flow
- [ ] Navigate to `/login.html`
- [ ] Click "Sign In with Azure AD B2C"
- [ ] Enter valid credentials
- [ ] Verify successful login
- [ ] Verify redirect to `/portal/`

#### 4. Authentication Status Check
- [ ] When logged in, navigate to `/.auth/me`
- [ ] Verify JSON response contains user profile
- [ ] Verify `clientPrincipal` object is present
- [ ] Verify `userId` and `userDetails` fields

#### 5. Protected Routes
- [ ] When **not** logged in, navigate to `/portal/`
- [ ] Verify redirect to Azure AD B2C login
- [ ] After login, verify redirect back to `/portal/`

#### 6. Dynamic Navigation
Test on all pages (index, platform, insights, assessment, release-notes):
- [ ] When **not** logged in, verify "Sign In" link appears in navigation
- [ ] Click "Sign In" and verify redirect to login page
- [ ] When **logged in**, verify navigation shows:
  - [ ] "Portal (UserName)" link
  - [ ] "Sign Out" link
  - [ ] No "Sign In" link

#### 7. Logout Flow
- [ ] When logged in, click "Sign Out" in navigation
- [ ] Verify redirect to home page
- [ ] Navigate to `/.auth/me` and verify empty response
- [ ] Verify navigation now shows "Sign In" link

#### 8. Session Persistence
- [ ] Log in successfully
- [ ] Close browser
- [ ] Reopen browser and navigate to site
- [ ] Verify still logged in (session persists)

#### 9. Auto-Redirect on Login Page
- [ ] Log in successfully
- [ ] Navigate to `/login.html`
- [ ] Verify auto-redirect to `/portal/` with message

### Automated Validation

Run these commands to validate the implementation:

```bash
# Validate JSON syntax
jq -e . site/staticwebapp.config.json

# Validate JavaScript syntax
node -c site/js/auth.js

# Build site
npm run build:site

# Validate accessibility
npm run validate:site

# Security scan
# (CodeQL analysis run automatically in GitHub Actions)
```

### Expected Results

#### Valid Configuration
- ✅ staticwebapp.config.json is valid JSON
- ✅ auth.js has no syntax errors
- ✅ Site builds successfully
- ✅ All pages pass accessibility checks
- ✅ No security vulnerabilities detected

#### Authentication Flow
- ✅ Unauthenticated users can access public pages
- ✅ Protected routes redirect to login
- ✅ After login, users can access protected routes
- ✅ Logout successfully clears session
- ✅ Navigation updates based on auth status

## Troubleshooting

### Issue: Login button doesn't work
**Solution:** Verify Azure AD B2C tenant is configured and redirect URIs match exactly.

### Issue: 401 errors even after login
**Solution:** Check that client ID and secret are correctly configured in Static Web App settings.

### Issue: Navigation doesn't update
**Solution:** 
1. Check browser console for JavaScript errors
2. Verify `auth.js` is loaded (check Network tab)
3. Verify `updateNavigation()` is called on `DOMContentLoaded`

### Issue: Can't access `/.auth/me`
**Solution:** This endpoint only works when deployed to Azure Static Web Apps. Local development requires the SWA CLI or deployment to Azure.

## Security Considerations

### Implemented Security Measures
1. **HTTPS Only**: All authentication endpoints require HTTPS
2. **No Credentials in Code**: Client secrets stored in Azure configuration
3. **CSP Compliance**: No inline event handlers
4. **Proper Error Handling**: API calls handle 401 responses
5. **Session Management**: Azure-managed tokens with configurable expiration

### Security Scan Results
- **CodeQL Analysis**: 0 vulnerabilities found
- **Dependency Audit**: No critical vulnerabilities in authentication code
- **Code Review**: All feedback addressed

## Deployment Checklist

Before deploying to production:

- [ ] Azure AD B2C tenant configured
- [ ] User flows created (B2C_1_SignUpSignIn)
- [ ] App registration with correct redirect URIs
- [ ] GitHub secrets configured: `AZURE_AD_B2C_CLIENT_ID`, `AZURE_AD_B2C_CLIENT_SECRET`
- [ ] Static Web App application settings configured
- [ ] Update `staticwebapp.config.json` placeholders with actual tenant values
- [ ] Test in staging environment first
- [ ] Verify all protected routes work correctly
- [ ] Test logout flow
- [ ] Verify session persistence

## Known Limitations

1. **Local Testing**: Full authentication flow requires deployment to Azure Static Web Apps or use of SWA CLI
2. **Placeholder Values**: The `staticwebapp.config.json` contains placeholder values that must be replaced before deployment
3. **Portal Pages**: The `/portal/*` routes don't exist yet - this implementation provides the authentication foundation for future portal pages

## Next Steps

After this authentication foundation is deployed:

1. Create portal pages under `/portal/` directory
2. Implement API endpoints under `/api/` for authenticated operations
3. Add role-based access control (RBAC) if needed
4. Implement password reset flow (B2C_1_PasswordReset)
5. Implement profile editing flow (B2C_1_ProfileEdit)
6. Add social login providers (Google, Microsoft, etc.)

## References

- [Azure Static Web Apps Authentication](https://learn.microsoft.com/azure/static-web-apps/authentication-authorization)
- [Azure AD B2C Documentation](https://learn.microsoft.com/azure/active-directory-b2c/)
- [Static Web Apps CLI](https://github.com/Azure/static-web-apps-cli)
- [site/README.md](../site/README.md) - Detailed setup instructions
