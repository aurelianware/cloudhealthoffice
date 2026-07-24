import { cpSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const siteRoot = process.cwd();
const scriptDir = fileURLToPath(new URL('.', import.meta.url)).replace(/[\\/]$/, '');
const defaultGoogleAnalyticsId = 'G-H1HCD5EYPN';
const defaultFormspreeFoundingPartnerEndpoint = 'https://formspree.io/f/xgojygon';

if (siteRoot !== scriptDir) {
  console.error(
    `Error: build.mjs must be run from the src/site directory.\n` +
    `  Expected: ${scriptDir}\n` +
    `  Current:  ${siteRoot}`
  );
  process.exit(1);
}

const outputDir = join(siteRoot, 'dist');
const rawGoogleAnalyticsId = process.env.GOOGLE_ANALYTICS_ID;
const googleAnalyticsId = rawGoogleAnalyticsId === undefined
  ? defaultGoogleAnalyticsId
  : rawGoogleAnalyticsId.trim();
const rawFormspreeFoundingPartnerEndpoint = process.env.FORMSPREE_FOUNDING_PARTNER_ENDPOINT;
const formspreeFoundingPartnerEndpoint = (
  rawFormspreeFoundingPartnerEndpoint === undefined
    ? defaultFormspreeFoundingPartnerEndpoint
    : rawFormspreeFoundingPartnerEndpoint.trim()
) || defaultFormspreeFoundingPartnerEndpoint;

if (googleAnalyticsId && !/^G-[A-Z0-9]+$/i.test(googleAnalyticsId)) {
  console.error(
    `Error: GOOGLE_ANALYTICS_ID "${googleAnalyticsId}" is not a valid GA4 measurement ID (expected format: G-XXXXXXXXXX)`
  );
  process.exit(1);
}

if (
  formspreeFoundingPartnerEndpoint &&
  !/^https:\/\/formspree\.io\/f\/[a-z0-9]+$/i.test(formspreeFoundingPartnerEndpoint)
) {
  console.error(
    `Error: FORMSPREE_FOUNDING_PARTNER_ENDPOINT must use the format https://formspree.io/f/{form_id}`
  );
  process.exit(1);
}

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
      '</script>',
      '<!-- Conversion / engagement event tracking -->',
      '<script defer src="/js/analytics-events.js"></script>'
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
      .replace(plausibleInlinePattern, '\n')
      .replaceAll(
        '__FORMSPREE_FOUNDING_PARTNER_ENDPOINT__',
        formspreeFoundingPartnerEndpoint
      )
      .replaceAll(
        defaultFormspreeFoundingPartnerEndpoint,
        formspreeFoundingPartnerEndpoint
      );

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
  console.warn('Google Analytics is disabled; generated site artifact will not include analytics');
}

console.log('Injected Formspree endpoint for the founding partner form');

console.log(`Prepared GitHub Pages artifact in ${outputDir}`);
