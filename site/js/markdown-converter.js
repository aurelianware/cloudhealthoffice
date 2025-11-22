/**
 * Simple Markdown to HTML converter for Cloud Health Office site
 * Converts .md files in /site/assets to styled HTML pages
 * 
 * Usage: node site/js/markdown-converter.js
 */

const fs = require('fs');
const path = require('path');

/**
 * Basic Markdown to HTML conversion
 * Supports: headings, paragraphs, lists, links, bold, italic, code blocks, blockquotes, horizontal rules
 */
function markdownToHtml(markdown) {
  let html = markdown;
  
  // Convert code blocks first (triple backticks)
  html = html.replace(/```(\w*)\n([\s\S]*?)```/g, (match, lang, code) => {
    return `<pre><code class="language-${lang}">${escapeHtml(code.trim())}</code></pre>`;
  });
  
  // Convert inline code
  html = html.replace(/`([^`]+)`/g, '<code>$1</code>');
  
  // Convert headings (h1 should only appear once, so convert subsequent ones to h2)
  let h1Count = 0;
  html = html.replace(/^### (.*$)/gim, '<h3>$1</h3>');
  html = html.replace(/^## (.*$)/gim, '<h2>$1</h2>');
  html = html.replace(/^# (.*$)/gim, (match, content) => {
    h1Count++;
    // Only the first h1 stays as h1, rest become h2 for accessibility
    return h1Count === 1 ? `<h1>${content}</h1>` : `<h2>${content}</h2>`;
  });
  
  // Convert bold
  html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
  
  // Convert italic
  html = html.replace(/\*([^*]+)\*/g, '<em>$1</em>');
  
  // Convert links
  html = html.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2">$1</a>');
  
  // Convert unordered lists
  html = html.replace(/^\* (.+)$/gim, '<li>$1</li>');
  html = html.replace(/(<li>.*<\/li>)/s, '<ul>$1</ul>');
  
  // Convert ordered lists
  html = html.replace(/^\d+\. (.+)$/gim, '<li>$1</li>');
  
  // Convert blockquotes
  html = html.replace(/^> (.+)$/gim, '<blockquote>$1</blockquote>');
  
  // Convert horizontal rules
  html = html.replace(/^---$/gim, '<hr>');
  
  // Convert paragraphs (lines that don't start with HTML tags)
  html = html.split('\n').map(line => {
    line = line.trim();
    if (line === '' || line.match(/^<[^>]+>/)) {
      return line;
    }
    return `<p>${line}</p>`;
  }).join('\n');
  
  // Clean up extra p tags around block elements
  html = html.replace(/<p><(h[1-6]|ul|ol|li|blockquote|pre|hr|div)>/gi, '<$1>');
  html = html.replace(/<\/(h[1-6]|ul|ol|li|blockquote|pre|hr|div)><\/p>/gi, '</$1>');
  
  return html;
}

/**
 * Escape HTML special characters
 */
function escapeHtml(text) {
  const map = {
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#039;'
  };
  return text.replace(/[&<>"']/g, m => map[m]);
}

/**
 * Generate HTML page with Sentinel styling
 */
function generateHtmlPage(title, content, filename) {
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta name="description" content="${title} - Cloud Health Office platform assessment and technical analysis">
  <meta name="keywords" content="healthcare EDI, HIPAA compliance, Azure Logic Apps, payer integration, platform assessment">
  <title>${title} - Cloud Health Office</title>
  <link rel="stylesheet" href="css/sentinel.css">
</head>
<body>
  <a href="#main-content" class="skip-to-main">Skip to main content</a>

  <nav>
    <ul>
      <li><a href="index.html">Home</a></li>
      <li><a href="platform.html">Platform</a></li>
      <li><a href="insights.html">Insights</a></li>
      <li><a href="assessment.html">Assessment</a></li>
      <li><a href="https://github.com/aurelianware/cloudhealthoffice" target="_blank" rel="noopener noreferrer">GitHub</a></li>
    </ul>
  </nav>

  <main id="main-content">
    <div class="hero circuit-veins">
      <div class="hero-content">
        <img src="https://github.com/aurelianware/cloudhealthoffice/raw/main/docs/images/logo-cloudhealthoffice-sentinel-primary.svg" 
             alt="Cloud Health Office Sentinel Logo"
             style="max-width: 500px; height: auto; margin: 20px 0;">
      </div>
    </div>

    <div class="container-narrow">
      <article class="assessment-section">
        ${content}
      </article>

      <section class="cta-section">
        <h2>Ready to Transform Your Payer Operations?</h2>
        <p>The sequence is immutable. Legacy EDI integration is now optional.</p>
        <div style="margin-top: 30px;">
          <a href="platform.html" class="button">Platform Overview</a>
          <a href="https://calendly.com/mark-cloudhealthoffice" class="button button-green" target="_blank" rel="noopener noreferrer">Schedule Demo</a>
          <a href="https://github.com/aurelianware/cloudhealthoffice" class="button button-secondary" target="_blank" rel="noopener noreferrer">Clone Repository</a>
        </div>
      </section>
    </div>
  </main>

  <footer>
    <p>
      <strong>Cloud Health Office v1.0.0 — The Sentinel</strong><br>
      Capabilities beyond question. Systems that do not fail.
    </p>
    <p>
      Apache 2.0 • Not affiliated with Availity, LLC • © 2025 Aurelianware<br>
      <a href="https://github.com/aurelianware/cloudhealthoffice">GitHub</a> • 
      <a href="mailto:mark@aurelianware.com">Contact</a>
    </p>
  </footer>
</body>
</html>`;
}

/**
 * Convert all Markdown files in assets directory to HTML
 */
function convertMarkdownFiles() {
  const assetsDir = path.join(__dirname, '..', 'assets');
  const siteDir = path.join(__dirname, '..');
  
  console.log('Cloud Health Office - Markdown to HTML Converter');
  console.log('=================================================');
  console.log('');
  
  if (!fs.existsSync(assetsDir)) {
    console.error(`Error: Assets directory not found: ${assetsDir}`);
    process.exit(1);
  }
  
  const files = fs.readdirSync(assetsDir).filter(f => f.endsWith('.md'));
  
  if (files.length === 0) {
    console.log('No Markdown files found in assets directory.');
    return;
  }
  
  console.log(`Found ${files.length} Markdown file(s) to convert:\n`);
  
  files.forEach(file => {
    const mdPath = path.join(assetsDir, file);
    const basename = path.basename(file, '.md');
    const htmlFilename = basename === 'cho-assessment' ? 'assessment.html' : `${basename}.html`;
    const htmlPath = path.join(siteDir, htmlFilename);
    
    console.log(`Converting: ${file}`);
    console.log(`  → ${htmlFilename}`);
    
    try {
      const markdown = fs.readFileSync(mdPath, 'utf8');
      const htmlContent = markdownToHtml(markdown);
      
      // Extract title from first h1 or use filename
      const titleMatch = markdown.match(/^#\s+(.+)$/m);
      const title = titleMatch ? titleMatch[1] : basename.replace(/-/g, ' ');
      
      const fullHtml = generateHtmlPage(title, htmlContent, htmlFilename);
      fs.writeFileSync(htmlPath, fullHtml, 'utf8');
      
      console.log(`  ✓ Success\n`);
    } catch (error) {
      console.error(`  ✗ Error: ${error.message}\n`);
    }
  });
  
  console.log('=================================================');
  console.log('Conversion complete!');
  console.log('');
  console.log('Generated HTML files are ready for deployment.');
  console.log('Run `npm run build` to rebuild if needed.');
}

// Run conversion
if (require.main === module) {
  convertMarkdownFiles();
}

module.exports = { markdownToHtml, generateHtmlPage, convertMarkdownFiles };
