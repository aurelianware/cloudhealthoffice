# Cloud Health Office - Static Website

**Last Updated:** February 11, 2026 - v4.0.0 Release

This directory contains the Cloud Health Office marketing website with platform assessment, insights, and documentation.

## Structure

```
site/
├── index.html              # Homepage with hero and Calendly integration
├── login.html              # Login/registration page with multi-tenant Entra ID
├── platform.html           # Platform overview and capabilities
├── insights.html           # Market insights with Magic Quadrant visualization
├── assessment.html         # Generated from assets/cho-assessment.md
├── css/
│   └── sentinel.css        # Sentinel theme styling (Absolute black, neon cyan/green)
├── js/
│   ├── auth.js             # Authentication helper library
│   ├── markdown-converter.js  # Build script to convert .md to .html
│   └── validate-accessibility.js
├── graphics/
│   ├── MQ_Objective_GartnerBlue.svg
│   └── MQ_Objective_Minimalist.svg
├── assets/
│   └── cho-assessment.md   # Source Markdown for assessment page
├── staticwebapp.config.json  # Azure Static Web Apps configuration
└── README.md               # This file
```

## Authentication Setup - Multi-Tenant Microsoft Entra ID

### Overview
Cloud Health Office uses **multi-tenant Microsoft Entra ID (Azure AD)** authentication for organizational sign-ups. Healthcare organizations sign in with their existing corporate Microsoft accounts.

### Prerequisites
- Azure subscription
- Azure CLI or Azure Portal access

### Step 1: Create Multi-Tenant App Registration

1. Go to [Azure Portal](https://portal.azure.com) → **Microsoft Entra ID** → **App registrations**
2. Click **New registration**
3. Configure:
   - **Name**: `Cloud Health Office`
   - **Supported account types**: **Accounts in any organizational directory (Any Microsoft Entra ID tenant - Multitenant)**
   - **Redirect URI**: 
     - Platform: **Web**
     - URI: `https://<your-static-web-app>.azurestaticapps.net/.auth/login/aad/callback`
4. Click **Register**

### Step 2: Configure App Registration

1. **Copy Application (client) ID** - You'll need this for `AZURE_AD_CLIENT_ID`

2. **Create Client Secret**:
   - Go to **Certificates & secrets** → **New client secret**
   - Description: `StaticWebApp`
   - Expiration: Choose appropriate duration
   - Click **Add**
   - **Copy the secret value** - You'll need this for `AZURE_AD_CLIENT_SECRET`

3. **Configure API Permissions** (optional, for future API access):
   - Go to **API permissions** → **Add a permission**
   - Add **Microsoft Graph** → **Delegated permissions**:
     - `User.Read` (basic profile)
     - `openid`, `profile`, `email`
   - Click **Grant admin consent** if you're admin

4. **Configure Token Configuration**:
   - Go to **Token configuration** → **Add optional claim**
   - Token type: **ID**
   - Add claims: `email`, `preferred_username`, `upn`

### Step 3: Configure Static Web App Secrets

Add these secrets to your **GitHub repository** or **Azure Static Web App configuration**:

**GitHub Secrets** (for deployment):
1. Go to your repo → **Settings** → **Secrets and variables** → **Actions**
2. Add:
   - `AZURE_AD_CLIENT_ID`: Application (client) ID from Step 2.1
   - `AZURE_AD_CLIENT_SECRET`: Secret value from Step 2.2

**Azure Static Web App Configuration**:
1. Go to Azure Portal → Your Static Web App → **Configuration**
2. Add application settings:
   - `AZURE_AD_CLIENT_ID`: Application (client) ID
   - `AZURE_AD_CLIENT_SECRET`: Secret value

### Step 4: Test Authentication

1. Deploy the site (or test locally with Static Web Apps CLI)
2. Navigate to `/login.html`
3. Click "Sign in with your organization account"
4. Sign in with any organizational Microsoft account
5. First-time users may see admin consent prompt
6. Verify redirect to portal after successful authentication

### Organizational Sign-Up Flow

1. **User from Organization A** clicks "Sign In"
2. Redirected to Microsoft login page
3. Signs in with their work account (e.g., `user@hospital.org`)
4. If first user from this organization, may need admin consent
5. After consent, user is authenticated and redirected to portal
6. User profile available at `/.auth/me`

### Admin Consent

For organizations to use Cloud Health Office, an admin may need to grant consent:

**Option 1: User-triggered consent**
- First user from organization sees consent prompt
- Can request admin approval

**Option 2: Admin consent URL**
Share this URL with organization admins:
```
https://login.microsoftonline.com/organizations/v2.0/adminconsent?client_id=<YOUR_CLIENT_ID>&redirect_uri=<YOUR_REDIRECT_URI>
```

Replace `<YOUR_CLIENT_ID>` with your Application (client) ID and `<YOUR_REDIRECT_URI>` with your callback URL.

### Testing Authentication

#### Local Testing with Azure Static Web Apps CLI

Install the CLI globally:

```bash
npm install -g @azure/static-web-apps-cli
```

Start the local development server:

```bash
# From the repository root of Cloud Health Office
swa start site --app-location site
```

**Note:** Local authentication testing with multi-tenant Entra ID requires additional configuration. For full authentication testing, deploy to Azure Static Web Apps.

#### Test User Flow

1. **Sign-In Test:**
   - Navigate to `/login.html`
   - Click "Sign in with your organization account"
   - Sign in with organizational Microsoft account (e.g., `user@contoso.com`)
   - If first user from organization, may see admin consent prompt
   - Verify redirect to `/portal/`

2. **Authentication Status:**
   - Navigate to `/.auth/me`
   - Should return user profile JSON when authenticated
   - Should return empty when not authenticated

3. **Protected Routes:**
   - Navigate to `/portal/` when not authenticated
   - Should redirect to login page
   - After login, should access portal successfully

4. **Logout Test:**
   - Click "Sign Out" in navigation
   - Should redirect to home page
   - `/.auth/me` should return empty

### Authentication Helper Functions

The `js/auth.js` library provides helper functions:

```javascript
// Check if user is authenticated
const authenticated = await isAuthenticated();

// Get user profile
const user = await loadUserProfile();

// Redirect to login if not authenticated
await requireAuth();

// Make authenticated API call
const response = await callAuthenticatedAPI('/api/claims');

// Logout
logout('/');

// Get user display name
const name = await getUserDisplayName();

// Update navigation with auth links
await updateNavigation('#mainNav');
```

### Troubleshooting

#### Common Authentication Issues

**Issue: Redirect loop after login**
- Verify redirect URIs in app registration match exactly (including protocol and trailing slash)
- Check that client ID and secret are correctly configured

**Issue: "Invalid client secret" error**
- Verify client secret hasn't expired
- Check that secret is correctly configured in Static Web App settings
- Ensure no extra whitespace in secret value

**Issue: User redirected to wrong page after login**
- Check `post_login_redirect_uri` parameter in login links
- Verify route configuration in `staticwebapp.config.json`

**Issue: `/.auth/me` returns empty even when logged in**
- Clear browser cookies and try again
- Check browser console for errors
- Verify Static Web App has correct authentication configuration

**Issue: Local testing doesn't work**
- Multi-tenant Entra ID authentication requires deployment to Azure Static Web Apps
- Use local emulation with mock auth or deploy to staging environment

#### Debug Mode

Enable verbose logging in browser console:

```javascript
// In browser console
localStorage.setItem('debug', 'auth:*');
location.reload();
```

### Security Considerations

1. **HTTPS Only:** All authentication endpoints require HTTPS
2. **Secrets Management:** Never commit client secrets to source control
3. **Token Expiration:** Tokens expire after configured period (default: 8 hours)
4. **CORS Configuration:** Ensure proper CORS settings for API endpoints
5. **Content Security Policy:** Review CSP headers for authentication flows

### Environment Variables

Required environment variables for authentication:

```bash
# Multi-tenant Microsoft Entra ID Configuration
AZURE_AD_CLIENT_ID=<application-client-id>
AZURE_AD_CLIENT_SECRET=<client-secret-value>

# Optional: Custom domain configuration
AZURE_STATIC_WEB_APP_URL=https://cloudhealthoffice.com
```

## Building the Site

The site uses a simple Node.js script to convert Markdown files in `/assets` to styled HTML pages.

### Build Command

```bash
npm run build:site
```

This will:
1. Read all `.md` files from `site/assets/`
2. Convert Markdown to HTML using the `marked` library
3. Wrap content in Sentinel-themed template
4. Output HTML files to `site/` directory

### Example: cho-assessment.md → assessment.html

The assessment page is generated from `assets/cho-assessment.md`:

```bash
cd /home/runner/work/cloudhealthoffice/cloudhealthoffice
npm run build:site
```

### ⚠️ Important: Keep Source and Generated Files in Sync

**Before committing changes to Markdown files, you must run the build:**

```bash
# Edit the Markdown source
vim site/assets/cho-assessment.md

# Build to regenerate HTML
npm run build:site

# Commit both source and generated files together
git add site/assets/cho-assessment.md site/assessment.html
git commit -m "Update platform assessment content"
```

### Automated Build with Pre-Commit Hook

A pre-commit hook automatically runs `npm run build:site` when you commit changes to `site/assets/*.md` files. This ensures:

1. ✅ Generated HTML is always up-to-date with source Markdown
2. ✅ No risk of committing stale HTML
3. ✅ Consistent build process across all contributors

To install the pre-commit hook:

```bash
npm install
npm run prepare  # Installs husky hooks
```

### CI Validation

The GitHub Actions workflow (`pr-lint.yml`) validates:

1. **Site builds successfully** - Runs `npm run build:site`
2. **HTML structure is valid** - Checks for DOCTYPE, proper heading hierarchy
3. **Source and generated files match** - Ensures no uncommitted changes
4. **Accessibility compliance** - Validates heading structure (single h1, proper list nesting)

If validation fails, the PR will be blocked until issues are fixed.

## Deployment

The site is automatically deployed to Azure Static Web Apps when changes are pushed to the `main` branch.

### GitHub Actions Workflow

`.github/workflows/deploy-static-site.yml` handles deployment:
- Triggers on push to `main` branch (paths: `site/**`)
- Authenticates with Azure via OIDC (no secrets in code)
- Deploys to Static Web App using deployment token
- Configures custom domain `cloudhealthoffice.com`

### Manual Deployment

```bash
# Get deployment token
az staticwebapp secrets list \
  --name <swa-name> \
  --resource-group <rg-name> \
  --query "properties.apiKey" -o tsv

# Deploy using Azure CLI
az staticwebapp deploy \
  --name <swa-name> \
  --resource-group <rg-name> \
  --source ./site
```

## Styling Guidelines

All pages follow the **Sentinel** brand identity:

### Colors
- **Background:** Absolute black (`#000000`)
- **Primary:** Neon cyan (`#00ffff`)
- **Accent:** Neon green (`#00ff88`)
- **Text:** Light gray (`#b0b0b0`)

### Typography
- **Font:** Segoe UI
- **Headings:** Bold weight, neon cyan with glow effect
- **Body:** Regular weight, light gray

### Effects
- **Circuit Veins:** Grid pattern background with cyan transparency
- **Glow:** Text and box shadows for neon aesthetic
- **Hover:** Transform and enhanced glow on interactive elements

See `css/sentinel.css` for complete styling reference.

## Accessibility

The site follows WCAG 2.1 Level AA standards:

- ✅ Semantic HTML5 structure
- ✅ Skip-to-main-content link for keyboard navigation
- ✅ Alt text for all images and graphics
- ✅ Proper heading hierarchy (h1 → h6)
- ✅ Focus indicators for keyboard navigation
- ✅ Sufficient color contrast ratios
- ✅ Responsive design for mobile devices
- ✅ Reduced motion support (`prefers-reduced-motion`)

### Testing Accessibility

```bash
# Use browser dev tools
# Chrome: Lighthouse audit
# Firefox: Accessibility inspector
# Safari: Audit tab

# Or use automated tools:
npm install -g pa11y
pa11y http://localhost:3000/index.html
```

## SEO Optimization

All pages include:

- ✅ Meta descriptions
- ✅ Structured heading hierarchy
- ✅ Alt tags for images
- ✅ Open Graph tags (Facebook)
- ✅ Twitter Card tags
- ✅ Semantic HTML5 elements
- ✅ Mobile-friendly viewport
- ✅ Fast loading (minimal dependencies)

## Adding New Pages

### 1. Create Markdown Source

Add a new `.md` file to `site/assets/`:

```markdown
# New Page Title

Your content here...
```

### 2. Build HTML

Run the converter:

```bash
npm run build:site
```

This generates `new-page.html` in the `site/` directory.

### 3. Add Navigation

Update the `<nav>` section in all HTML files:

```html
<nav>
  <ul>
    <li><a href="index.html">Home</a></li>
    <li><a href="platform.html">Platform</a></li>
    <li><a href="insights.html">Insights</a></li>
    <li><a href="assessment.html">Assessment</a></li>
    <li><a href="new-page.html">New Page</a></li>
    <li><a href="https://github.com/aurelianware/cloudhealthoffice">GitHub</a></li>
  </ul>
</nav>
```

### 4. Test Locally

Serve the site locally:

```bash
# Using Python
python3 -m http.server 8000 --directory site

# Using Node.js http-server
npx http-server site -p 8000

# Using PHP
php -S localhost:8000 -t site
```

Visit `http://localhost:8000`

### 5. Deploy

Push to `main` branch:

```bash
git add site/
git commit -m "Add new page"
git push origin main
```

GitHub Actions will automatically deploy to Azure Static Web Apps.

## Custom Domain Configuration

The site is configured for `cloudhealthoffice.com` custom domain.

### DNS Records Required

**For Root Domain:**
```
Type: ALIAS or ANAME
Host: @
Points to: <static-web-app-hostname>.azurestaticapps.net
TTL: 3600
```

**For www Subdomain:**
```
Type: CNAME
Host: www
Points to: <static-web-app-hostname>.azurestaticapps.net
TTL: 3600
```

Azure automatically provisions SSL/TLS certificates after DNS verification (typically 5-10 minutes).

## Testing

### Validate HTML

```bash
# Install validator
npm install -g html-validator-cli

# Validate all HTML files
html-validator site/*.html
```

### Check Links

```bash
# Install broken-link-checker
npm install -g broken-link-checker

# Check all links
blc http://localhost:8000 -ro
```

### Performance Testing

```bash
# Using Lighthouse CLI
npm install -g lighthouse
lighthouse http://localhost:8000 --view
```

## Branding Compliance

All content must follow the Sentinel brand guidelines:

- [ ] Uses Sentinel logo (docs/images/logo-cloudhealthoffice-sentinel-primary.svg)
- [ ] Backgrounds are absolute black (#000000)
- [ ] Headings use Segoe UI Bold
- [ ] Color scheme: Cyan (#00ffff) and Green (#00ff88)
- [ ] Tone is authoritative and inevitable
- [ ] "Cloud Health Office" is never abbreviated in formal contexts
- [ ] No legacy branding (PrivaseeAI, PrivacyAI) present
- [ ] Neon glow effects applied to interactive elements

See [docs/BRANDING-GUIDELINES.md](../docs/BRANDING-GUIDELINES.md) for complete brand identity guide.

## Troubleshooting

### Build Fails

```bash
# Check Node.js version (requires Node 12+)
node --version

# Reinstall dependencies if needed
npm install

# Run build with verbose output
node site/js/markdown-converter.js
```

### Deployment Fails

```bash
# Check GitHub Actions logs
# Go to: https://github.com/aurelianware/cloudhealthoffice/actions

# Verify secrets are configured
# Settings → Secrets and variables → Actions

# Test deployment locally
az staticwebapp deploy --name <swa-name> --resource-group <rg-name> --source ./site
```

### Styling Issues

- Verify `css/sentinel.css` is linked in HTML
- Clear browser cache (Ctrl+Shift+R)
- Check browser console for CSS loading errors
- Validate CSS: `npx stylelint site/css/*.css`

### Accessibility Issues

- Run Lighthouse audit in Chrome DevTools
- Use pa11y for automated testing
- Test with screen reader (NVDA, JAWS, VoiceOver)
- Verify keyboard navigation works

## Resources

- **Azure Static Web Apps Docs:** https://learn.microsoft.com/azure/static-web-apps/
- **Markdown Guide:** https://www.markdownguide.org/
- **WCAG Guidelines:** https://www.w3.org/WAI/WCAG21/quickref/
- **Sentinel Branding:** ../docs/BRANDING-GUIDELINES.md

## Contact

For questions or issues:
- **GitHub Issues:** https://github.com/aurelianware/cloudhealthoffice/issues
- **Email:** mark@aurelianware.com

---

**Cloud Health Office v1.0.0 — The Sentinel**  
*Just emerged from the void*

BSL 1.1 • © 2026 Aurelianware, Inc
