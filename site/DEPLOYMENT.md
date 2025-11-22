# Cloud Health Office Website - Deployment Guide

This guide explains how to build, test, and deploy the Cloud Health Office static website.

## Prerequisites

- Node.js 12+ (for build script)
- Azure CLI (for manual deployment)
- Azure Static Web Apps resource created
- GitHub repository access

## Quick Start

### 1. Build the Site

Convert Markdown files to HTML:

```bash
cd /home/runner/work/cloudhealthoffice/cloudhealthoffice
npm run build:site
```

This generates `site/assessment.html` from `site/assets/cho-assessment.md`.

### 2. Test Locally

Serve the site on localhost:

```bash
# Option 1: Python
python3 -m http.server 8000 --directory site

# Option 2: Node.js http-server
npx http-server site -p 8000

# Option 3: PHP
php -S localhost:8000 -t site
```

Visit: `http://localhost:8000`

### 3. Deploy

#### Automatic Deployment (Recommended)

Push to `main` branch:

```bash
git add site/
git commit -m "Update website content"
git push origin main
```

GitHub Actions workflow (`.github/workflows/deploy-static-site.yml`) automatically:
1. Authenticates with Azure via OIDC
2. Retrieves Static Web App deployment token
3. Deploys site content
4. Configures custom domain
5. Verifies deployment

#### Manual Deployment

```bash
# Login to Azure
az login

# Set subscription
az account set --subscription "<subscription-id>"

# Deploy
az staticwebapp deploy \
  --name "<static-web-app-name>" \
  --resource-group "<resource-group-name>" \
  --source ./site
```

## Build Process

### Markdown to HTML Conversion

The build script (`site/js/markdown-converter.js`) performs the following:

1. **Reads Markdown files** from `site/assets/*.md`
2. **Converts to HTML** with basic Markdown syntax support:
   - Headings (h1-h6)
   - Paragraphs
   - Lists (ordered and unordered)
   - Links
   - Bold and italic
   - Code blocks and inline code
   - Blockquotes
   - Horizontal rules
3. **Wraps in Sentinel template** with:
   - Navigation menu
   - Hero section with logo
   - Footer with branding
   - Sentinel CSS styling
4. **Outputs HTML files** to `site/` directory

### Supported Markdown Syntax

```markdown
# Heading 1
## Heading 2
### Heading 3

**Bold text**
*Italic text*

[Link text](https://example.com)

- Unordered list item
- Another item

1. Ordered list item
2. Another item

`inline code`

```javascript
// Code block
const example = "code";
```

> Blockquote

---
Horizontal rule
```

### Adding New Pages

1. Create Markdown file in `site/assets/`:
   ```bash
   touch site/assets/new-page.md
   ```

2. Write content:
   ```markdown
   # New Page Title
   
   Your content here...
   ```

3. Build:
   ```bash
   npm run build:site
   ```

4. Add to navigation in all HTML files:
   ```html
   <nav>
     <ul>
       <li><a href="new-page.html">New Page</a></li>
     </ul>
   </nav>
   ```

5. Test locally, then commit and push.

## Deployment Workflow

### GitHub Actions: deploy-static-site.yml

**Trigger:**
- Push to `main` branch with changes in `site/**`
- Manual workflow dispatch

**Steps:**
1. **Validate secrets** (AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID)
2. **Azure login** via OIDC (no stored credentials)
3. **Get Static Web App details** (hostname, deployment token)
4. **Deploy content** using Azure/static-web-apps-deploy action
5. **Configure custom domain** (cloudhealthoffice.com)
6. **Generate DNS instructions** (artifact download)
7. **Verify deployment** (HTTP 200 check)

**Environment:** PROD

**Permissions:**
- `id-token: write` (OIDC authentication)
- `contents: read` (repository access)
- `pull-requests: write` (PR comments)

### Custom Domain Setup

The workflow automatically configures `cloudhealthoffice.com` as the custom domain.

**DNS Configuration Required:**

For root domain (@):
```
Type: ALIAS or ANAME
Host: @
Points to: <static-web-app>.azurestaticapps.net
TTL: 3600
```

For www subdomain:
```
Type: CNAME
Host: www
Points to: <static-web-app>.azurestaticapps.net
TTL: 3600
```

**DNS Instructions Artifact:**
Download from GitHub Actions workflow run:
- Go to Actions → deploy-static-site workflow
- Select latest run
- Download `dns-configuration-instructions` artifact

**SSL/TLS Certificate:**
Azure automatically provisions certificates after DNS verification (5-10 minutes).

## Testing

### HTML Validation

```bash
npm install -g html-validator-cli
html-validator site/*.html
```

### Link Checking

```bash
npm install -g broken-link-checker
blc http://localhost:8000 -ro
```

### Accessibility Testing

```bash
npm install -g pa11y
pa11y http://localhost:8000/index.html
```

### Performance Testing

```bash
npm install -g lighthouse
lighthouse http://localhost:8000 --view
```

### Manual Testing Checklist

- [ ] Homepage loads correctly
- [ ] Navigation menu works on all pages
- [ ] Platform page displays capabilities
- [ ] Insights page shows Magic Quadrant visualizations
- [ ] Assessment page renders from Markdown
- [ ] All images have alt text
- [ ] Links open in correct target (internal vs external)
- [ ] Mobile responsive design works
- [ ] Keyboard navigation functional
- [ ] Screen reader compatible
- [ ] Fast loading time (<3 seconds)

## Troubleshooting

### Build Script Fails

**Error:** `Cannot find module`
```bash
# Verify Node.js installation
node --version

# Should be 12+ 
# If not, install/upgrade Node.js
```

**Error:** `ENOENT: no such file or directory`
```bash
# Verify you're in repository root
pwd
# Should be: /home/runner/work/cloudhealthoffice/cloudhealthoffice

# Verify assets directory exists
ls -la site/assets/
```

### Deployment Fails

**Error:** `Static Web App not found`
```bash
# Verify resource exists
az staticwebapp list --resource-group <rg-name>

# If not, create via infrastructure deployment first
cd infra
az deployment group create -g <rg-name> -f main.bicep
```

**Error:** `Authentication failed`
```bash
# Verify OIDC secrets are configured in GitHub
# Settings → Secrets and variables → Actions
# Required: AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID
```

**Error:** `Deployment token invalid`
```bash
# Regenerate token
az staticwebapp secrets list \
  --name <swa-name> \
  --resource-group <rg-name> \
  --query "properties.apiKey" -o tsv
```

### Styling Issues

**CSS not loading:**
- Verify `css/sentinel.css` exists
- Check browser console for 404 errors
- Clear browser cache (Ctrl+Shift+R)
- Verify file path in HTML: `<link rel="stylesheet" href="css/sentinel.css">`

**Colors wrong:**
- Ensure Sentinel theme is applied
- Check CSS variables in `:root`
- Verify `--absolute-black: #000000`
- Inspect element in browser dev tools

### Navigation Broken

**Links don't work:**
- Verify file paths are relative (no leading `/`)
- Check `staticwebapp.config.json` navigation fallback
- Test locally with `http-server` (not just `file://`)

### Images Missing

**Graphics not displaying:**
- Verify SVG files exist: `ls site/graphics/`
- Check image paths in HTML
- Verify alt text is present
- Test in browser dev tools Network tab

### Accessibility Issues

**Failed WCAG audit:**
- Run pa11y for specific errors
- Check color contrast ratios (minimum 4.5:1)
- Verify heading hierarchy (no skipped levels)
- Ensure all images have alt text
- Test keyboard navigation (Tab key)
- Test with screen reader (NVDA/JAWS/VoiceOver)

## Rollback

### Revert to Previous Version

```bash
# Find commit hash of working version
git log --oneline site/

# Revert changes
git revert <commit-hash>
git push origin main

# Or checkout specific files
git checkout <commit-hash> -- site/
git commit -m "Rollback site to previous version"
git push origin main
```

### Manual Rollback in Azure

```bash
# List previous deployments
az staticwebapp show \
  --name <swa-name> \
  --resource-group <rg-name>

# Azure Static Web Apps maintains deployment history
# Rollback via Azure Portal:
# 1. Go to Static Web Apps resource
# 2. Select "Environments"
# 3. Choose previous deployment
# 4. Click "Activate"
```

## Monitoring

### Check Deployment Status

```bash
# Get Static Web App details
az staticwebapp show \
  --name <swa-name> \
  --resource-group <rg-name> \
  --query "{name:name, defaultHostname:defaultHostname, customDomains:customDomains}" \
  -o table
```

### View Logs

**GitHub Actions:**
- Go to: https://github.com/aurelianware/cloudhealthoffice/actions
- Select workflow run
- Click job to see logs

**Azure Portal:**
- Go to Static Web Apps resource
- Click "Logs" in left menu
- View deployment and runtime logs

### Custom Domain Status

```bash
# Check custom domain configuration
az staticwebapp hostname list \
  --name <swa-name> \
  --resource-group <rg-name> \
  -o table
```

### Verify SSL Certificate

```bash
# Check SSL certificate
curl -vI https://cloudhealthoffice.com 2>&1 | grep -E "subject|issuer|expire"

# Or use online tool
# https://www.ssllabs.com/ssltest/
```

## Performance Optimization

### Minimize File Sizes

```bash
# Minify CSS (optional)
npm install -g csso-cli
csso site/css/sentinel.css -o site/css/sentinel.min.css

# Optimize SVG files
npm install -g svgo
svgo site/graphics/*.svg
```

### Enable Caching

Already configured in `staticwebapp.config.json`:
- HTML files: 1 hour cache
- CSS/JS: 1 year cache (immutable)
- Graphics: 1 year cache (immutable)

### Reduce HTTP Requests

- CSS is inline in HTML or single file
- Minimal JavaScript dependencies
- SVG graphics (small file size)
- No external font files (using system fonts)

## Security

### HTTP Security Headers

Configured in `staticwebapp.config.json`:
- `X-Frame-Options: DENY` (clickjacking protection)
- `X-Content-Type-Options: nosniff` (MIME sniffing protection)
- `X-XSS-Protection: 1; mode=block` (XSS protection)
- `Referrer-Policy: strict-origin-when-cross-origin` (privacy)
- `Permissions-Policy` (feature restrictions)

### Content Security Policy

Consider adding CSP header:
```json
{
  "globalHeaders": {
    "Content-Security-Policy": "default-src 'self'; script-src 'self' https://assets.calendly.com; style-src 'self' 'unsafe-inline'; img-src 'self' https://github.com data:; frame-src https://calendly.com;"
  }
}
```

### HTTPS Enforcement

Azure Static Web Apps automatically:
- Redirects HTTP to HTTPS
- Provisions SSL/TLS certificates
- Enforces TLS 1.2+
- Provides Azure-managed certificates

## Maintenance

### Regular Updates

1. **Content updates:** Edit Markdown files, rebuild, deploy
2. **Styling updates:** Modify `sentinel.css`, test, deploy
3. **New pages:** Create Markdown, build, update nav, deploy
4. **Image updates:** Replace graphics, optimize, deploy

### Monthly Checks

- [ ] Verify SSL certificate validity (auto-renews)
- [ ] Test all links (broken-link-checker)
- [ ] Run accessibility audit (pa11y)
- [ ] Check Google Search Console for errors
- [ ] Review Azure Static Web Apps metrics
- [ ] Update content as needed (stats, features, etc.)

### Quarterly Reviews

- [ ] Performance testing (Lighthouse)
- [ ] Security header validation
- [ ] Browser compatibility testing
- [ ] Mobile device testing
- [ ] SEO audit
- [ ] Analytics review (if tracking enabled)

## Support

### Documentation

- [Azure Static Web Apps Docs](https://learn.microsoft.com/azure/static-web-apps/)
- [Markdown Guide](https://www.markdownguide.org/)
- [WCAG 2.1 Guidelines](https://www.w3.org/WAI/WCAG21/quickref/)
- [Sentinel Branding Guidelines](../docs/BRANDING-GUIDELINES.md)

### Contact

- **GitHub Issues:** https://github.com/aurelianware/cloudhealthoffice/issues
- **Email:** mark@aurelianware.com

---

**Cloud Health Office v1.0.0 — The Sentinel**  
*The deployment is inevitable. The sequence is immutable.*

Apache 2.0 • © 2025 Aurelianware
