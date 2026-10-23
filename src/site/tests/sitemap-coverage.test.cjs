/**
 * Guards the one sitemap invariant nothing else checks: that each page's own
 * <link rel="canonical"> agrees with the URL the generator emits for it.
 *
 * What already holds this ground, and why these tests do not repeat it:
 *   - scripts/generate-sitemap.mjs discovers pages from the built output, so a
 *     new page is picked up without anyone editing a list, and it exits non-zero
 *     if a URL in the committed sitemap stops being generated.
 *   - independence-artifact.test.cjs asserts the executive routes appear once
 *     and that the excluded paths stay out.
 *
 * The gap is disagreement rather than absence. A page can be in the sitemap
 * under /foo while telling crawlers it is really /bar, which splits the crawl
 * signal between two URLs and is invisible to a test that only counts entries.
 * That is a silent SEO fault, so it gets a test with a name that says so.
 *
 * These read dist/, so they need a real build first (`npm run build`, which
 * runs the generator). src/site/sitemap.xml is NOT the published file: it is
 * the curated priority/changefreq source the generator reads, and it is
 * deliberately partial — pages absent from it are emitted with defaults.
 */
const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const site = path.resolve(__dirname, "..");
const dist = path.join(site, "dist");

const CANONICAL = /<link[^>]+rel="canonical"[^>]+href="([^"]+)"/i;
const SKIP_DIRS = new Set(["assets", "css", "js", "graphics", "fonts", "img", "images"]);
// Mirrors EXCLUDED in scripts/generate-sitemap.mjs.
const EXCLUDED = [/^\/404$/, /^\/login$/, /^\/portal(\/|$)/, /^\/cho-[a-z0-9-]+-marketing$/];

function builtPages(dir = dist, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name) || entry.name.startsWith(".")) continue;
      builtPages(path.join(dir, entry.name), out);
    } else if (entry.name.endsWith(".html")) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

const sitemapPath = path.join(dist, "sitemap.xml");
const hasBuild = fs.existsSync(sitemapPath);
const locs = hasBuild
  ? [...fs.readFileSync(sitemapPath, "utf8").matchAll(/<loc>(.*?)<\/loc>/g)].map((m) => m[1])
  : [];

test("the build produced a sitemap", () => {
  assert(
    hasBuild,
    "dist/sitemap.xml is missing — run `npm run build`, not `node build.mjs`, " +
      "since the sitemap is written by scripts/generate-sitemap.mjs",
  );
});

test("every page in the generated sitemap declares that same URL as its canonical", () => {
  if (!hasBuild) return;
  const listed = new Set(locs);
  const disagree = [];
  for (const file of builtPages()) {
    const html = fs.readFileSync(file, "utf8");
    const m = html.match(CANONICAL);
    if (!m) continue;
    const canonical = m[1];
    const rel = "/" + path.relative(dist, file).split(path.sep).join("/");
    const urlPath = rel.replace(/\/index\.html$/, "/").replace(/\.html$/, "");
    if (EXCLUDED.some((re) => re.test(urlPath))) continue;
    // Only pages the sitemap actually offers are in scope: a page it omits on
    // purpose is the generator's business, not this test's.
    const mine = locs.filter((u) => {
      const p = u.replace("https://cloudhealthoffice.com", "") || "/";
      return p === urlPath || p === urlPath + "/" || p + "/" === urlPath;
    });
    if (!mine.length) continue;
    if (!listed.has(canonical))
      disagree.push(`${path.relative(dist, file)}: sitemap offers ${mine[0]}, page canonicalizes to ${canonical}`);
  }
  assert.deepEqual(disagree, [], "canonical/sitemap disagreement:\n  " + disagree.join("\n  "));
});

test("every indexable built page declares a canonical URL at all", () => {
  if (!hasBuild) return;
  const missing = [];
  for (const file of builtPages()) {
    const rel = "/" + path.relative(dist, file).split(path.sep).join("/");
    const urlPath = rel.replace(/\/index\.html$/, "/").replace(/\.html$/, "");
    if (EXCLUDED.some((re) => re.test(urlPath))) continue;
    if (!CANONICAL.test(fs.readFileSync(file, "utf8"))) missing.push(path.relative(dist, file));
  }
  assert.deepEqual(
    missing,
    [],
    "pages with no <link rel=\"canonical\">, so nothing states which URL they are:\n  " +
      missing.join("\n  "),
  );
});
