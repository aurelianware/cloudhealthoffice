/**
 * Generates dist/sitemap.xml with lastmod values taken from git.
 *
 * Why this exists: sitemap.xml used to be hand-maintained, so its <lastmod>
 * dates drifted away from the content. A stale lastmod is worse than none —
 * it actively tells crawlers a changed page did not change.
 *
 * Design choices, deliberately conservative:
 *   - The curated <priority> and <changefreq> values in the committed
 *     src/site/sitemap.xml are preserved. This script does not invent SEO
 *     weightings.
 *   - URLs are discovered from the built output, so new pages are picked up,
 *     but anything matching EXCLUDED stays out (error pages, the authenticated
 *     portal, login).
 *   - <lastmod> is the last git commit date touching the page's source file.
 *     Falls back to the file mtime outside a git checkout (e.g. a CI export).
 */
import { readFileSync, writeFileSync, existsSync, statSync, readdirSync } from 'node:fs';
import { join, relative, dirname } from 'node:path';
import { execFileSync } from 'node:child_process';

/**
 * A shallow checkout (actions/checkout defaults to fetch-depth: 1) makes
 * `git log -1 -- <file>` resolve to the build commit for every file, so every
 * page would claim the same lastmod — the exact failure this script exists to
 * prevent, and invisible locally where history is complete.
 *
 * How strictly to treat that depends on what the build is for:
 *
 *   - A *deployed* build must not publish uniform dates, so the deploy
 *     workflows set SITEMAP_STRICT=1 and this becomes a hard failure.
 *   - A *validation* build (pr-lint) only checks that the site compiles. The
 *     sitemap it produces is thrown away, and forcing a full-history clone on
 *     every pull request would cost minutes for no benefit. There it warns and
 *     falls back to file mtimes.
 */
function checkHistoryDepth() {
  const strict = process.env.SITEMAP_STRICT === '1';
  let shallow = false;
  try {
    shallow = execFileSync('git', ['rev-parse', '--is-shallow-repository'], {
      cwd: SITE_ROOT, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'],
    }).trim() === 'true';
  } catch {
    return; // not a git checkout — lastModified() falls back to file mtimes
  }
  if (!shallow) return;

  const explanation =
    'This is a shallow git checkout, so per-page lastmod cannot be derived.\n' +
    'Every URL would receive the build date, which tells crawlers that nothing\n' +
    'changed in particular. Set `fetch-depth: 0` on actions/checkout, or run\n' +
    '`git fetch --unshallow`, before generating a sitemap that will be published.';

  if (strict) {
    console.error(`ERROR: ${explanation}`);
    process.exit(1);
  }
  console.warn(`WARNING: ${explanation}\nSITEMAP_STRICT is not set, so falling back to file mtimes.`);
}

const SITE_ROOT = process.cwd();
const DIST = join(SITE_ROOT, 'dist');
const ORIGIN = 'https://cloudhealthoffice.com';

checkHistoryDepth();

// Not marketing surface: error page, auth, and the authenticated portal.
const EXCLUDED = [/^\/404$/, /^\/login$/, /^\/portal(\/|$)/];

// Directories that hold assets rather than pages.
const SKIP_DIRS = new Set(['assets', 'css', 'js', 'graphics', 'fonts', 'img', 'images']);

const DEFAULT_PRIORITY = '0.6';
const DEFAULT_CHANGEFREQ = 'monthly';

function walk(dir, out = []) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name) || entry.name.startsWith('.')) continue;
      walk(join(dir, entry.name), out);
    } else if (entry.name.endsWith('.html')) {
      out.push(join(dir, entry.name));
    }
  }
  return out;
}

function toUrlPath(distFile) {
  const rel = relative(DIST, distFile).split('\\').join('/');
  if (rel === 'index.html') return '/';
  if (rel.endsWith('/index.html')) return '/' + rel.slice(0, -'/index.html'.length);
  return '/' + rel.slice(0, -'.html'.length);
}

// Prefer the checked-in source over the build artifact so git history applies.
function sourceFor(urlPath) {
  const base = urlPath === '/' ? 'index' : urlPath.replace(/^\//, '');
  for (const c of [`${base}.html`, `${base}/index.html`]) {
    const p = join(SITE_ROOT, c);
    if (existsSync(p)) return p;
  }
  return null;
}

function lastModified(sourcePath, distPath) {
  const target = sourcePath ?? distPath;
  try {
    const out = execFileSync('git', ['log', '-1', '--format=%cs', '--', target], {
      cwd: SITE_ROOT, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'],
    }).trim();
    if (out) return out;
  } catch {
    // not a git checkout, or the file is untracked — fall through
  }
  return statSync(target).mtime.toISOString().slice(0, 10);
}

// Preserve curated priority/changefreq from the committed sitemap.
function existingMetadata() {
  const p = join(SITE_ROOT, 'sitemap.xml');
  const meta = new Map();
  if (!existsSync(p)) return meta;
  const xml = readFileSync(p, 'utf8');
  for (const block of xml.split('<url>').slice(1)) {
    const loc = block.match(/<loc>([^<]+)<\/loc>/)?.[1];
    if (!loc) continue;
    meta.set(loc.replace(ORIGIN, '') || '/', {
      priority: block.match(/<priority>([^<]+)<\/priority>/)?.[1],
      changefreq: block.match(/<changefreq>([^<]+)<\/changefreq>/)?.[1],
    });
  }
  return meta;
}

const curated = existingMetadata();
const entries = [];

// Both `docs.html` and `docs/index.html` ship, and both map to /docs. Emitting
// the same <loc> twice is invalid and splits crawl signals, so collisions are
// resolved in favour of the directory index, which is the canonical form the
// flat file redirects to.
const byUrlPath = new Map();
for (const file of walk(DIST)) {
  const urlPath = toUrlPath(file);
  const existing = byUrlPath.get(urlPath);
  if (existing) {
    const preferred = file.endsWith('/index.html') ? file : existing;
    byUrlPath.set(urlPath, preferred);
    console.log(`  collision on ${urlPath}: using ${relative(DIST, preferred)}`);
    continue;
  }
  byUrlPath.set(urlPath, file);
}

for (const [urlPath, file] of byUrlPath) {
  if (EXCLUDED.some((re) => re.test(urlPath))) continue;
  const src = sourceFor(urlPath);
  const meta = curated.get(urlPath) ?? {};
  entries.push({
    loc: urlPath === '/' ? `${ORIGIN}/` : `${ORIGIN}${urlPath}`,
    lastmod: lastModified(src, file),
    changefreq: meta.changefreq ?? DEFAULT_CHANGEFREQ,
    priority: meta.priority ?? DEFAULT_PRIORITY,
    isNew: !curated.has(urlPath),
  });
}

entries.sort((a, b) => (Number(b.priority) - Number(a.priority)) || a.loc.localeCompare(b.loc));

const xml = [
  '<?xml version="1.0" encoding="UTF-8"?>',
  '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">',
  ...entries.map((e) => [
    '  <url>',
    `    <loc>${e.loc}</loc>`,
    `    <lastmod>${e.lastmod}</lastmod>`,
    `    <changefreq>${e.changefreq}</changefreq>`,
    `    <priority>${e.priority}</priority>`,
    '  </url>',
  ].join('\n')),
  '</urlset>',
  '',
].join('\n');

writeFileSync(join(DIST, 'sitemap.xml'), xml);

const added = entries.filter((e) => e.isNew).map((e) => e.loc.replace(ORIGIN, ''));
const dropped = [...curated.keys()].filter(
  (u) => !entries.some((e) => (e.loc.replace(ORIGIN, '') || '/') === u) && !EXCLUDED.some((re) => re.test(u)),
);

console.log(`Generated sitemap.xml with ${entries.length} URLs (lastmod from git).`);
if (added.length) console.log(`  newly included: ${added.join(', ')}`);

// Silently dropping URLs is how a sitemap de-indexes pages by accident, which
// is exactly what happened the first time this script ran with 'evidence' in
// SKIP_DIRS. Dropping a URL must be a deliberate act, so fail loudly instead.
if (dropped.length) {
  console.error(`\nERROR: ${dropped.length} URL(s) are in the committed sitemap but were not generated:`);
  for (const u of dropped) console.error(`  ${u}`);
  console.error(
    '\nIf these pages were intentionally removed, delete them from src/site/sitemap.xml\n' +
    'and add redirects. If not, this is a bug in page discovery — check SKIP_DIRS.\n',
  );
  process.exit(1);
}
