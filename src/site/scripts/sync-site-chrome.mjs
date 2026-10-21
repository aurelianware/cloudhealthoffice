// Applies the one shared primary navigation and footer to every site page.
//
//   node scripts/sync-site-chrome.mjs           rewrite pages in place
//   node scripts/sync-site-chrome.mjs --check   exit 1 if any page has drifted
//
// A page takes part when it carries the primary nav (id="mainNav"). The portal
// and the legacy login surface are application screens and keep their own
// chrome. Edit NAV_LINKS / FOOTER below, then run this script; never hand-edit
// a page's nav or footer.
import { readdirSync, readFileSync, writeFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const siteRoot = fileURLToPath(new URL('..', import.meta.url));
const SKIP_DIRS = new Set(['dist', 'node_modules', 'portal', 'tests', 'scripts']);
const SKIP_FILES = new Set(['login.html']);

export const HEAD_ASSETS = [
  '<link rel="preconnect" href="https://fonts.googleapis.com">',
  '<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>',
  '<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@500&amp;family=IBM+Plex+Sans:wght@400;500;600;700&amp;display=swap">',
  '<link rel="stylesheet" href="/css/enterprise.css">'
];

const NAV_GROUPS = [
  ['Solutions', [
    ['/solutions-payers', 'For health plans'],
    ['/cms-0057f-compliance', 'CMS-0057-F compliance'],
    ['/claims-repricing', 'Claims repricing'],
    ['/incremental-modernization', 'Incremental modernization']
  ]],
  ['Platform', [
    ['/platform', 'Platform overview'],
    ['/health-plan-independence', 'Why sovereignty'],
    ['/ai-ready-health-plan-architecture', 'AI-ready architecture'],
    ['/deploy', 'Deployment'],
    ['/pricing', 'Pricing'],
    ['/trust', 'Trust &amp; security']
  ]],
  ['/#interoperability', 'Interoperability'],
  ['Developers', [
    ['/developers', 'Developer hub'],
    ['/docs', 'Documentation'],
    ['/docs/quickstart', 'Quickstart'],
    ['/docs/api', 'API reference'],
    ['/api-explorer', 'API explorer'],
    ['https://github.com/aurelianware/cloudhealthoffice', 'Source on GitHub']
  ]],
  ['/evidence', 'Evidence'],
  ['Company', [
    ['/founder', 'Founder'],
    ['/#work-with-us', 'Partners &amp; investors'],
    ['/services', 'Professional services'],
    ['/insights', 'Insights'],
    ['/contact', 'Contact']
  ]]
];

const CTA = ['/contact?interest=cms0057-assessment', 'Book a readiness review'];

function link([href, label], current) {
  const cur = href === current ? ' aria-current="page"' : '';
  return `<a href="${href}"${cur}>${label}</a>`;
}

export function renderNav(current) {
  const items = NAV_GROUPS.map((entry) => {
    if (Array.isArray(entry[1])) {
      const [summary, links] = entry;
      return `<li>\n<details>\n<summary>${summary}</summary>\n<div>\n${links.map((l) => link(l, current)).join('\n')}\n</div>\n</details>\n</li>`;
    }
    return `<li>${link(entry, current)}</li>`;
  });
  items.push(`<li><a class="nav-cta" data-ga-event="readiness_review_cta_click" href="${CTA[0]}">${CTA[1]}</a></li>`);
  return [
    '<nav class="site-nav" aria-label="Primary">',
    '<div class="site-nav-inner">',
    '<a class="site-brand" href="/"><img src="/graphics/logo-cloudhealthoffice-sentinel-primary.svg" alt="" width="32" height="32"><span>Cloud Health Office</span></a>',
    '<button id="mobileMenuToggle" type="button" aria-label="Toggle navigation menu" aria-controls="mainNav" aria-expanded="false">☰</button>',
    '<ul id="mainNav">',
    items.join('\n'),
    '</ul>',
    '</div>',
    '</nav>'
  ].join('\n');
}

export const FOOTER = `<footer class="cho-footer">
<div class="cho-footer-inner">
<div class="cho-footer-brand">
<strong>Cloud Health Office</strong>
<p>Sovereign, interoperable health plan administration. A product of Aurelianware, Inc.</p>
</div>
<div class="cho-footer-col">
<p class="cho-footer-heading">Platform</p>
<a href="/platform">Overview</a>
<a href="/#interoperability">Interoperability</a>
<a href="/deploy">Deployment</a>
<a href="/pricing">Pricing</a>
<a href="/trust">Trust &amp; security</a>
</div>
<div class="cho-footer-col">
<p class="cho-footer-heading">Developers</p>
<a href="/developers">Developer hub</a>
<a href="/docs">Documentation</a>
<a href="/docs/api">API reference</a>
<a href="/evidence">Evidence</a>
<a href="/release-notes">Release notes</a>
</div>
<div class="cho-footer-col">
<p class="cho-footer-heading">Work with us</p>
<a href="/contact?interest=cms0057-assessment">Health plans</a>
<a href="/contact?interest=vendor-integration">Technology vendors</a>
<a href="/contact?interest=channel-partner">Channel partners</a>
<a href="/contact?interest=investor">Investors</a>
<a href="/services">Professional services</a>
</div>
<div class="cho-footer-col">
<p class="cho-footer-heading">Company</p>
<a href="/founder">Founder</a>
<a href="/insights">Insights</a>
<a href="/contact">Contact</a>
<a href="/legal/privacy-policy">Privacy</a>
<a href="https://github.com/aurelianware/cloudhealthoffice">Public source</a>
</div>
</div>
<p class="cho-footer-legal">© 2026 Aurelianware, Inc. Source-available under BSL 1.1. Production use requires a commercial license.</p>
</footer>`;

export function routeOf(file) {
  const rel = relative(siteRoot, file).split(sep).join('/');
  if (rel === 'index.html') return '/';
  return '/' + rel.replace(/\/index\.html$/, '').replace(/\.html$/, '');
}

function currentFor(route) {
  if (route === '/docs' || route.startsWith('/docs/')) return route === '/docs/quickstart' || route === '/docs/api' ? route : '/docs';
  if (route.startsWith('/insights/')) return '/insights';
  return route;
}

export function applyChrome(html, route) {
  const navAt = html.indexOf('id="mainNav"');
  if (navAt === -1) return html;
  const start = html.lastIndexOf('<nav', navAt);
  const end = html.indexOf('</nav>', navAt);
  if (start === -1 || end === -1) throw new Error(`${route}: primary nav not delimited`);
  html = html.slice(0, start) + renderNav(currentFor(route)) + html.slice(end + '</nav>'.length);

  const fStart = html.lastIndexOf('<footer');
  const fEnd = html.indexOf('</footer>', fStart);
  if (fStart !== -1 && fEnd !== -1) {
    html = html.slice(0, fStart) + FOOTER + html.slice(fEnd + '</footer>'.length);
  } else {
    html = html.replace(/<\/body>/i, FOOTER + '\n</body>');
  }

  for (const asset of HEAD_ASSETS) {
    if (!html.includes(asset)) html = html.replace(/<\/head>/i, asset + '\n</head>');
  }
  if (!html.includes('/js/mobile-nav.js')) {
    html = html.replace(/<\/head>/i, '<script defer src="/js/mobile-nav.js"></script>\n</head>');
  }
  // enterprise.css must load after every other stylesheet.
  const css = '<link rel="stylesheet" href="/css/enterprise.css">';
  html = html.replace(css + '\n', '');
  html = html.replace(/<\/head>/i, css + '\n</head>');
  return html;
}

export function sitePages(dir = siteRoot) {
  const out = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (!SKIP_DIRS.has(entry.name)) out.push(...sitePages(full));
    } else if (entry.name.endsWith('.html') && !SKIP_FILES.has(entry.name)) {
      if (readFileSync(full, 'utf8').includes('id="mainNav"')) out.push(full);
    }
  }
  return out;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const check = process.argv.includes('--check');
  const drifted = [];
  for (const file of sitePages()) {
    const html = readFileSync(file, 'utf8');
    const next = applyChrome(html, routeOf(file));
    if (next !== html) {
      drifted.push(relative(siteRoot, file));
      if (!check) writeFileSync(file, next);
    }
  }
  if (check && drifted.length) {
    console.error('Site chrome drifted on:\n  ' + drifted.join('\n  ') + '\nRun: node scripts/sync-site-chrome.mjs');
    process.exit(1);
  }
  console.log((check ? 'checked' : 'updated') + ` ${drifted.length} page(s)`);
}
