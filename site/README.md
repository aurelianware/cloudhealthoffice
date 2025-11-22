# Cloud Health Office - Static Website

This directory contains the Cloud Health Office marketing website with platform assessment, insights, and documentation.

## Structure

```
site/
├── index.html              # Homepage with hero and Calendly integration
├── platform.html           # Platform overview and capabilities
├── insights.html           # Market insights with Magic Quadrant visualization
├── assessment.html         # Generated from assets/cho-assessment.md
├── css/
│   └── sentinel.css        # Sentinel theme styling (Absolute black, neon cyan/green)
├── js/
│   └── markdown-converter.js  # Build script to convert .md to .html
├── graphics/
│   ├── MQ_Objective_GartnerBlue.svg
│   └── MQ_Objective_Minimalist.svg
├── assets/
│   └── cho-assessment.md   # Source Markdown for assessment page
├── staticwebapp.config.json  # Azure Static Web Apps configuration
└── README.md               # This file
```

## Building the Site

The site uses a simple Node.js script to convert Markdown files in `/assets` to styled HTML pages.

### Build Command

```bash
npm run build:site
```

This will:
1. Read all `.md` files from `site/assets/`
2. Convert Markdown to HTML
3. Wrap content in Sentinel-themed template
4. Output HTML files to `site/` directory

### Example: cho-assessment.md → assessment.html

The assessment page is generated from `assets/cho-assessment.md`:

```bash
cd /home/runner/work/cloudhealthoffice/cloudhealthoffice
npm run build:site
```

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

Apache 2.0 • © 2025 Aurelianware
