/**
 * Cloud Health Office - Accessibility Validation Script
 * Validates color contrast ratios and accessibility features
 * 
 * Usage: node site/js/validate-accessibility.js
 */

const fs = require('fs');
const path = require('path');

/**
 * Calculate relative luminance for a color
 * Following WCAG 2.1 guidelines
 */
function getLuminance(r, g, b) {
  const [rs, gs, bs] = [r, g, b].map(c => {
    c = c / 255;
    return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * rs + 0.7152 * gs + 0.0722 * bs;
}

/**
 * Calculate contrast ratio between two colors
 */
function getContrastRatio(color1, color2) {
  const lum1 = getLuminance(...color1);
  const lum2 = getLuminance(...color2);
  const lighter = Math.max(lum1, lum2);
  const darker = Math.min(lum1, lum2);
  return (lighter + 0.05) / (darker + 0.05);
}

/**
 * Convert hex color to RGB
 */
function hexToRgb(hex) {
  const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
  return result ? [
    parseInt(result[1], 16),
    parseInt(result[2], 16),
    parseInt(result[3], 16)
  ] : null;
}

/**
 * Check WCAG compliance levels
 */
function checkWCAGCompliance(ratio, fontSize = 18, isBold = false) {
  const isLargeText = fontSize >= 18 || (fontSize >= 14 && isBold);
  const levelAA = isLargeText ? 3 : 4.5;
  const levelAAA = isLargeText ? 4.5 : 7;
  
  return {
    ratio: ratio.toFixed(2),
    passAA: ratio >= levelAA,
    passAAA: ratio >= levelAAA,
    level: ratio >= levelAAA ? 'AAA' : ratio >= levelAA ? 'AA' : 'Fail'
  };
}

console.log('Cloud Health Office - Accessibility Validation');
console.log('==============================================');
console.log('');

// Define Sentinel color palette
const colors = {
  'absolute-black': '#000000',
  'dark-gray': '#0a0a0a',
  'medium-gray': '#808080',
  'light-gray': '#b0b0b0',
  'neon-cyan': '#00ffff',
  'neon-green': '#00ff88'
};

console.log('Color Contrast Ratios (WCAG 2.1):');
console.log('----------------------------------');
console.log('');

// Test combinations used in the site
const tests = [
  { fg: 'light-gray', bg: 'absolute-black', use: 'Body text', size: 18 },
  { fg: 'neon-cyan', bg: 'absolute-black', use: 'Headings', size: 24, bold: true },
  { fg: 'neon-green', bg: 'absolute-black', use: 'Accents', size: 18 },
  { fg: 'medium-gray', bg: 'absolute-black', use: 'Secondary text', size: 16 },
  { fg: 'absolute-black', bg: 'neon-cyan', use: 'Button text', size: 18, bold: true },
  { fg: 'absolute-black', bg: 'neon-green', use: 'Button text (green)', size: 18, bold: true },
  { fg: 'light-gray', bg: 'dark-gray', use: 'Card text', size: 18 },
  { fg: 'neon-cyan', bg: 'dark-gray', use: 'Card headings', size: 20, bold: true }
];

let allPass = true;

tests.forEach(test => {
  const fgRgb = hexToRgb(colors[test.fg]);
  const bgRgb = hexToRgb(colors[test.bg]);
  const ratio = getContrastRatio(fgRgb, bgRgb);
  const compliance = checkWCAGCompliance(ratio, test.size, test.bold);
  
  const status = compliance.passAA ? '✓' : '✗';
  const levelStr = compliance.passAAA ? 'AAA ⭐' : compliance.passAA ? 'AA ✓' : 'FAIL ✗';
  
  console.log(`${status} ${test.use}`);
  console.log(`  ${test.fg} (${colors[test.fg]}) on ${test.bg} (${colors[test.bg]})`);
  console.log(`  Contrast: ${compliance.ratio}:1 | Level: ${levelStr}`);
  console.log('');
  
  if (!compliance.passAA) {
    allPass = false;
  }
});

console.log('----------------------------------');
console.log('');

if (allPass) {
  console.log('✅ All color combinations pass WCAG 2.1 Level AA');
} else {
  console.log('❌ Some color combinations fail WCAG 2.1 Level AA');
}

console.log('');

// Check HTML files for common accessibility issues
console.log('HTML Accessibility Checks:');
console.log('----------------------------------');
console.log('');

const siteDir = path.join(__dirname, '..');
const htmlFiles = fs.readdirSync(siteDir).filter(f => f.endsWith('.html'));

let totalIssues = 0;

htmlFiles.forEach(file => {
  const filePath = path.join(siteDir, file);
  const content = fs.readFileSync(filePath, 'utf8');
  const issues = [];
  
  // Check for lang attribute
  if (!content.includes('html lang=')) {
    issues.push('Missing lang attribute on <html>');
  }
  
  // Check for viewport meta tag
  if (!content.includes('viewport')) {
    issues.push('Missing viewport meta tag');
  }
  
  // Check for main landmark
  if (!content.includes('<main') && !content.includes('role="main"')) {
    issues.push('Missing <main> landmark');
  }
  
  // Check for skip link
  if (!content.includes('skip-to-main') && !content.includes('Skip to')) {
    issues.push('Missing skip-to-main link');
  }
  
  // Check for alt text on images
  const imgMatches = content.match(/<img[^>]*>/g) || [];
  imgMatches.forEach(img => {
    if (!img.includes('alt=')) {
      issues.push('Image missing alt attribute: ' + img.substring(0, 50) + '...');
    }
  });
  
  // Check for proper heading structure
  const h1Count = (content.match(/<h1[^>]*>/g) || []).length;
  if (h1Count === 0) {
    issues.push('Missing <h1> heading');
  } else if (h1Count > 1) {
    issues.push(`Multiple <h1> headings found (${h1Count})`);
  }
  
  // Check for form labels
  const inputMatches = content.match(/<input[^>]*>/g) || [];
  inputMatches.forEach(input => {
    if (!input.includes('aria-label') && !input.includes('id=')) {
      issues.push('Input may be missing label: ' + input.substring(0, 50) + '...');
    }
  });
  
  if (issues.length > 0) {
    console.log(`📄 ${file}:`);
    issues.forEach(issue => {
      console.log(`  ⚠️  ${issue}`);
      totalIssues++;
    });
    console.log('');
  } else {
    console.log(`✅ ${file}: No accessibility issues found`);
  }
});

console.log('----------------------------------');
console.log('');

if (totalIssues === 0) {
  console.log('✅ All HTML files pass basic accessibility checks');
} else {
  console.log(`⚠️  Found ${totalIssues} potential accessibility issue(s)`);
  console.log('   Review and fix issues above');
}

console.log('');
console.log('==============================================');
console.log('Validation complete!');
console.log('');
console.log('Note: This is a basic validation. For comprehensive');
console.log('accessibility testing, use tools like:');
console.log('  - pa11y (automated testing)');
console.log('  - axe DevTools (browser extension)');
console.log('  - WAVE (web accessibility evaluation tool)');
console.log('  - Screen readers (NVDA, JAWS, VoiceOver)');
