# Cloud Health Office Site - Build Process Documentation

## Overview

The Cloud Health Office marketing site uses a Markdown-to-HTML build process to generate static pages from source content. This document explains the build system, validation, and best practices.

## Build System

### Architecture

```
site/assets/cho-assessment.md  →  [build:site]  →  site/assessment.html
     (Source)                      (Converter)          (Output)
```

### Components

1. **Source Files**: Markdown content in `site/assets/`
2. **Build Script**: `site/js/markdown-converter.js` (uses `marked` library)
3. **Output**: Styled HTML pages in `site/`

## Running the Build

### Manual Build

```bash
# From repository root
npm run build:site
```

**Output:**
```
Cloud Health Office - Markdown to HTML Converter
=================================================

Found 1 Markdown file(s) to convert:

Converting: cho-assessment.md
  → assessment.html
  ✓ Success

=================================================
Conversion complete!
```

### What Gets Generated

- **Input:** `site/assets/cho-assessment.md` (13.5KB Markdown)
- **Output:** `site/assessment.html` (19KB styled HTML)

The generated HTML includes:
- Complete Sentinel-themed page template
- Navigation menu linking to all site pages
- Proper HTML5 semantic structure
- Accessibility features (skip-to-main, heading hierarchy)
- SEO meta tags

## Automated Build

### Pre-Commit Hook

A Git pre-commit hook automatically runs the build when you commit changes to Markdown files:

**Location:** `.husky/pre-commit`

**Triggers when:**
- Any file matching `site/assets/*.md` is staged for commit

**Actions:**
1. Runs `npm run build:site`
2. Stages the generated `assessment.html` automatically
3. Blocks commit if build fails

**Setup:**
```bash
# Install husky and set up hooks
npm install
npm run prepare
```

**Testing the hook:**
```bash
# Edit a Markdown file
echo "Test content" >> site/assets/cho-assessment.md

# Stage and commit
git add site/assets/cho-assessment.md
git commit -m "Test commit"

# Hook runs automatically:
# → Running pre-commit checks...
# → Detected changes to Markdown files in site/assets/
# → Running site build to regenerate HTML...
# → ✓ Regenerated assessment.html has been staged
# → ✓ Site build completed successfully
```

## CI/CD Validation

### GitHub Actions Workflow

**File:** `.github/workflows/pr-lint.yml`

**Step:** "Validate site build"

### What Gets Validated

1. **Build Success**
   ```bash
   npm run build:site
   # Must exit with code 0
   ```

2. **HTML Structure**
   - Valid DOCTYPE declaration
   - Proper `<html>`, `<head>`, `<body>` tags
   - Exactly one `<h1>` per page (accessibility)
   - Lists properly wrapped in `<ul>` or `<ol>`

3. **Source/Output Sync**
   - If `cho-assessment.md` changed → `assessment.html` must also change
   - Prevents committing stale HTML

4. **File Existence**
   - Generated files exist where expected
   - No broken references

### Example CI Output

```
✓ Site build validation
  Found Markdown files in site/assets
  Running npm run build:site
  ✓ Build completed successfully
  
  Validating generated HTML structure...
  ✓ Found valid DOCTYPE declaration
  ✓ Found exactly 1 <h1> tag (accessibility requirement met)
  ✓ All <li> elements properly nested in <ul>/<ol>
  ✓ Generated HTML has valid structure
  
✓ Site build validation complete
```

### Validation Failure Examples

**Example 1: Build fails**
```
✗ Site build validation
  Error: Site build failed
  → Check markdown-converter.js for syntax errors
  → Verify marked library is installed
```

**Example 2: Out of sync**
```
✗ Site build validation
  Error: assessment.html is out of sync with cho-assessment.md
  → Run 'npm run build:site' and commit the generated HTML
```

**Example 3: Invalid HTML structure**
```
✗ Site build validation
  Error: Found 3 <h1> tags (should have exactly 1 for accessibility)
  → Fix heading hierarchy in cho-assessment.md
```

## Best Practices

### When Editing Content

**✅ DO:**
```bash
# 1. Edit the source Markdown
vim site/assets/cho-assessment.md

# 2. Run the build
npm run build:site

# 3. Review the generated HTML
open site/assessment.html  # or use browser

# 4. Commit both files together
git add site/assets/cho-assessment.md site/assessment.html
git commit -m "Update platform assessment"
```

**❌ DON'T:**
```bash
# DON'T edit the generated HTML directly
vim site/assessment.html  # Changes will be overwritten!

# DON'T commit Markdown without rebuilding
git add site/assets/cho-assessment.md
git commit -m "Update"  # assessment.html now out of sync!

# DON'T skip the build
git commit -m "Update" --no-verify  # Bypasses pre-commit hook
```

### Maintaining Sync

**Golden Rule:** Source Markdown and generated HTML must always be committed together.

**Why?**
- Prevents confusion about which version is "current"
- Ensures deployed site matches repository state
- Makes rollbacks work correctly
- Keeps PR reviews focused on content changes

### Testing Changes

Before committing, verify your changes work:

```bash
# 1. Build the site
npm run build:site

# 2. Validate accessibility
npm run validate:site

# 3. Test locally
cd site
python3 -m http.server 8080
# Visit http://localhost:8080/assessment.html

# 4. Check HTML structure
grep -c "<h1>" site/assessment.html  # Should be exactly 1
grep -c "<ul>" site/assessment.html  # Should have list containers
```

## Troubleshooting

### Build Fails: "Cannot find module 'marked'"

**Problem:** Missing dependency

**Solution:**
```bash
npm install marked
```

### Pre-Commit Hook Doesn't Run

**Problem:** Husky not installed or hook not executable

**Solution:**
```bash
# Reinstall husky
npm install
npm run prepare

# Make hook executable (if needed)
chmod +x .husky/pre-commit
```

### CI Validation Fails: "assessment.html out of sync"

**Problem:** Committed Markdown changes without rebuilding HTML

**Solution:**
```bash
# Run the build
npm run build:site

# Stage the generated file
git add site/assessment.html

# Amend your commit or create a new one
git commit --amend --no-edit
# or
git commit -m "Rebuild assessment.html"
```

### HTML Has Multiple h1 Tags

**Problem:** Markdown converter created multiple h1 elements

**Solution:** The `marked` library properly handles heading hierarchy. Check your Markdown:
```markdown
# Main Title        ← Only ONE level-1 heading allowed
## Section 1        ← Use h2 for sections
### Subsection 1.1  ← Use h3 for subsections
```

### Generated HTML Looks Wrong

**Problem:** Template or styling issue

**Solution:**
1. Check `site/js/markdown-converter.js` template
2. Verify `site/css/sentinel.css` is included
3. Test with simple Markdown first
4. Compare with a known-good generated file

## Advanced Topics

### Customizing the Build

To modify the generated HTML template:

**File:** `site/js/markdown-converter.js`
**Function:** `generateHtmlPage(title, content, filename)`

**Example: Add a custom meta tag:**
```javascript
function generateHtmlPage(title, content, filename) {
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta name="custom-tag" content="your-value">  <!-- Add here -->
  ...
```

### Adding New Source Files

1. Create new Markdown file:
   ```bash
   touch site/assets/new-page.md
   ```

2. Add content with proper heading structure:
   ```markdown
   # Page Title (only one h1)
   
   ## Section 1
   Content here...
   
   ## Section 2
   More content...
   ```

3. Build:
   ```bash
   npm run build:site
   # Generates site/new-page.html
   ```

4. Add to navigation in all pages:
   ```html
   <nav>
     <ul>
       <li><a href="new-page.html">New Page</a></li>
     </ul>
   </nav>
   ```

### Build Script Options

Currently, the build script processes all `*.md` files in `site/assets/`.

To process a single file (for development):
```javascript
// Modify markdown-converter.js temporarily
const mdFiles = fs.readdirSync(assetsDir)
  .filter(f => f === 'cho-assessment.md');  // Process only this file
```

## Monitoring Build Quality

### Metrics to Track

1. **Build Time:** Should be <5 seconds
2. **HTML Size:** Generated files should be <50KB
3. **Validation Errors:** Should be 0
4. **Accessibility Score:** AAA level (see validate:site)

### Regular Audits

```bash
# Weekly: Run full validation
npm run build:site && npm run validate:site

# Monthly: Check HTML structure
find site -name "*.html" -exec grep -l "<li>" {} \; | \
  xargs -I {} sh -c 'echo "Checking: {}" && grep -c "<ul>" {} && grep -c "<li>" {}'

# Quarterly: Review build performance
time npm run build:site
```

## References

- **Marked Library:** https://marked.js.org/
- **Sentinel Branding:** `docs/BRANDING-GUIDELINES.md`
- **Accessibility Guidelines:** `site/js/validate-accessibility.js`
- **Deployment Guide:** `site/DEPLOYMENT.md`

---

**Version:** 1.0.0  
**Last Updated:** November 22, 2025  
**Maintainer:** Cloud Health Office Team
