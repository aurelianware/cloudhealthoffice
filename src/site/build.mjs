import { cpSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const siteRoot = process.cwd();
const scriptDir = fileURLToPath(new URL('.', import.meta.url)).replace(/[\\/]$/, '');
const defaultGoogleAnalyticsId = 'G-H1HCD5EYPN';

if (siteRoot !== scriptDir) {
  console.error(
    `Error: build.mjs must be run from the src/site directory.\n` +
    `  Expected: ${scriptDir}\n` +
    `  Current:  ${siteRoot}`
  );
  process.exit(1);
}

const outputDir = join(siteRoot, 'dist');
const googleAnalyticsId = process.env.GOOGLE_ANALYTICS_ID?.trim() || defaultGoogleAnalyticsId;
const ignoredEntries = new Set([
  '.dockerignore',
  '.gitignore',
  'BUILD-PROCESS.md',
  'DEPLOYMENT.md',
  'Dockerfile',
  'IMPLEMENTATION-SUMMARY.md',
  'README.md',
  'build.mjs',
  'dist',
  'nginx.conf',
  'node_modules',
  'package-lock.json',
  'package.json'
]);

const shouldInclude = (sourcePath) =>
  !sourcePath.endsWith('.md') &&
  !/[\\/]dist([\\/]|$)/.test(sourcePath) &&
  !/[\\/]node_modules([\\/]|$)/.test(sourcePath);

const plausibleCommentPattern = /\s*<!-- Privacy-friendly analytics by Plausible -->\s*/gi;
const plausibleLoaderPattern = /\s*<script[^>]*src="https:\/\/plausible\.io\/js\/pa-JQNNrBf52mV2BxHPtkLAv\.js"><\/script>\s*/gi;
const plausibleInlinePattern = /\s*<script>window\.plausible[\s\S]*?<\/script>\s*/gi;
const analyticsInjection = googleAnalyticsId
  ? [
      '<!-- Google Analytics -->',
      `<script async src="https://www.googletagmanager.com/gtag/js?id=${googleAnalyticsId}"></script>`,
      '<script>',
      '  window.dataLayer = window.dataLayer || [];',
      '  function gtag(){dataLayer.push(arguments);}',
      "  gtag('js', new Date());",
      `  gtag('config', '${googleAnalyticsId}');`,
      '</script>'
    ].join('\n')
  : '';

function processHtmlFiles(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const fullPath = join(directory, entry.name);

    if (entry.isDirectory()) {
      processHtmlFiles(fullPath);
      continue;
    }

    if (!entry.isFile() || !entry.name.endsWith('.html')) {
      continue;
    }

    let html = readFileSync(fullPath, 'utf8')
      .replace(plausibleCommentPattern, '\n')
      .replace(plausibleLoaderPattern, '\n')
      .replace(plausibleInlinePattern, '\n');

    if (analyticsInjection) {
      html = html.replace('</head>', `  ${analyticsInjection}\n</head>`);
    }

    writeFileSync(fullPath, html);
  }
}

rmSync(outputDir, { recursive: true, force: true });
mkdirSync(outputDir, { recursive: true });

for (const entry of readdirSync(siteRoot, { withFileTypes: true })) {
  if (ignoredEntries.has(entry.name) || entry.name.endsWith('.md')) {
    continue;
  }

  cpSync(join(siteRoot, entry.name), join(outputDir, entry.name), {
    recursive: true,
    filter: shouldInclude
  });
}

processHtmlFiles(outputDir);

writeFileSync(join(outputDir, '.nojekyll'), '');

if (googleAnalyticsId) {
  console.log(`Injected Google Analytics ID ${googleAnalyticsId} into site HTML`);
} else {
  console.warn('GOOGLE_ANALYTICS_ID is not set; generated site artifact will not include analytics');
}

console.log(`Prepared GitHub Pages artifact in ${outputDir}`);
