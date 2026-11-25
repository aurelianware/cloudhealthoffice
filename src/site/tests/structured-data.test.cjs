/**
 * Guards the JSON-LD on every source page against the "Invalid structured data
 * items" faults a Semrush Site Audit raised on 2026-11-25 (37 pages):
 *
 *   - `breadcrumb` is a WebPage property. On an Article or TechArticle it is
 *     invalid, so the BreadcrumbList has to sit beside the article in @graph.
 *   - Any SoftwareApplication (even one nested under `about`) is treated as a
 *     software rich result, which needs offers plus aggregateRating or review.
 *     We have no real ratings, so the product is described as a CreativeWork
 *     with additionalType SoftwareApplication, and other pages point at it by
 *     @id. Never add made-up ratings to make the warning go away.
 *   - ProfessionalService is a LocalBusiness and needs a street address, and
 *     serviceType, provider and audience are not LocalBusiness fields. A
 *     consulting offer is a Service.
 *
 * Reads the committed source pages, so it needs no build.
 */
const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const site = path.resolve(__dirname, "..");
const SKIP = new Set(["node_modules", "dist", ".git"]);
const LD = /<script type="application\/ld\+json">([\s\S]*?)<\/script>/g;
const PAGE_TYPES = new Set(["WebPage", "CollectionPage", "AboutPage", "FAQPage", "ContactPage", "ItemPage", "ProfilePage", "QAPage", "SearchResultsPage"]);
const RICH_SOFTWARE = new Set(["SoftwareApplication", "WebApplication", "MobileApplication", "VideoGame"]);
const LOCAL_BUSINESS = new Set(["LocalBusiness", "ProfessionalService"]);

function htmlFiles(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    if (SKIP.has(e.name)) return [];
    const p = path.join(dir, e.name);
    return e.isDirectory() ? htmlFiles(p) : e.name.endsWith(".html") ? [p] : [];
  });
}

const types = (n) => [].concat(n["@type"] ?? []);

function* nodes(value) {
  if (Array.isArray(value)) for (const v of value) yield* nodes(v);
  else if (value && typeof value === "object") {
    yield value;
    for (const v of Object.values(value)) yield* nodes(v);
  }
}

const blocks = htmlFiles(site).flatMap((file) =>
  [...fs.readFileSync(file, "utf8").matchAll(LD)].map((m) => ({ file: path.relative(site, file), raw: m[1] }))
);

test("source pages carry JSON-LD to check", () => {
  assert.ok(blocks.length > 50, `found only ${blocks.length} JSON-LD blocks`);
});

test("every JSON-LD block parses", () => {
  for (const { file, raw } of blocks) assert.doesNotThrow(() => JSON.parse(raw), file);
});

test("breadcrumb is only set on WebPage types, never on articles", () => {
  const bad = [];
  for (const { file, raw } of blocks) {
    for (const n of nodes(JSON.parse(raw))) {
      if ("breadcrumb" in n && !types(n).some((t) => PAGE_TYPES.has(t))) bad.push(`${file} (${types(n).join("/")})`);
    }
  }
  assert.deepEqual(bad, []);
});

test("no software rich-result type without real ratings or reviews", () => {
  const bad = [];
  for (const { file, raw } of blocks) {
    for (const n of nodes(JSON.parse(raw))) {
      if (types(n).some((t) => RICH_SOFTWARE.has(t)) && !(n.offers && (n.aggregateRating || n.review))) bad.push(file);
    }
  }
  assert.deepEqual(bad, []);
});

test("no LocalBusiness types (we publish no street address)", () => {
  const bad = [];
  for (const { file, raw } of blocks) {
    for (const n of nodes(JSON.parse(raw))) if (types(n).some((t) => LOCAL_BUSINESS.has(t))) bad.push(file);
  }
  assert.deepEqual(bad, []);
});
