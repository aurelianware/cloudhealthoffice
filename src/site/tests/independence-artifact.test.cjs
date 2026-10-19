const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { execFileSync } = require("node:child_process");
const site = path.resolve(__dirname, ".."),
  repo = path.resolve(site, "../.."),
  dist = path.join(site, "dist");
const slugs = [
  "index",
  "health-plan-independence",
  "economics-of-vendor-lock-in",
  "ai-ready-health-plan-architecture",
  "incremental-modernization",
  "founding-transformation-partners",
];
const read = (f) => fs.readFileSync(path.join(dist, f), "utf8");
test("built executive routes have unique metadata, one h1, shared scripts once and resolvable local links", () => {
  const titles = new Set(),
    descriptions = new Set();
  for (const slug of slugs) {
    const html = read(slug + ".html");
    assert.equal((html.match(/<h1\b/g) || []).length, 1);
    const title = html.match(/<title>(.*?)<\/title>/)[1];
    assert(!titles.has(title));
    titles.add(title);
    const description = html.match(/name="description" content="([^"]*)/)[1];
    assert(!descriptions.has(description));
    descriptions.add(description);
    assert.equal((html.match(/rel="canonical"/g) || []).length, 1);
    assert(
      html.includes(
        'href="https://cloudhealthoffice.com' +
          (slug === "index" ? "/" : "/" + slug) +
          '"',
      ),
    );
    for (const script of [
      "analytics-events.js",
      "lead-capture.js",
      "assistant.js",
    ])
      assert.equal(html.split('src="/js/' + script + '"').length - 1, 1);
    for (const m of html.matchAll(
      /<script type="application\/ld\+json">([\s\S]*?)<\/script>/g,
    ))
      assert.doesNotThrow(() => JSON.parse(m[1]));
    for (const m of html.matchAll(/(?:href|src)="(\/[^"]*|#[^"]+)"/g)) {
      const [url, hash] = m[1].split("#");
      const target = url ? url.split("?")[0] : "/" + slug + ".html";
      const candidates = [
        target,
        target + ".html",
        target.replace(/\/$/, "") + "/index.html",
      ].map((x) => path.join(dist, x));
      const file = candidates.find(
        (x) => fs.existsSync(x) && fs.statSync(x).isFile(),
      );
      assert(file, slug + " unresolved " + m[1]);
      if (hash && file.endsWith(".html"))
        assert(
          fs.readFileSync(file, "utf8").includes('id="' + hash + '"'),
          slug + " missing fragment " + m[1],
        );
    }
  }
});
test("all baseline HTML destinations and homepage fragments remain; redirects unchanged", () => {
  const baseline = execFileSync(
    "git",
    ["ls-tree", "-r", "--name-only", "origin/main", "src/site"],
    { cwd: repo, encoding: "utf8" },
  )
    .split("\n")
    .filter((x) => x.endsWith(".html"));
  for (const file of baseline)
    assert(fs.existsSync(path.join(repo, file)), file + " removed");
  const old = execFileSync("git", ["show", "origin/main:src/site/index.html"], {
    cwd: repo,
    encoding: "utf8",
  });
  const home = read("index.html");
  for (const m of old.matchAll(/\bid="([^"]+)"/g))
    assert(
      home.includes('id="' + m[1] + '"'),
      "homepage fragment removed " + m[1],
    );
  assert.equal(
    read("_redirects"),
    execFileSync("git", ["show", "origin/main:src/site/_redirects"], {
      cwd: repo,
      encoding: "utf8",
    }),
  );
});
test("sitemap adds five routes once and preserves baseline URLs/exclusions", () => {
  const xml = read("sitemap.xml"),
    urls = [...xml.matchAll(/<loc>(.*?)<\/loc>/g)].map((m) => m[1]);
  assert.equal(new Set(urls).size, urls.length);
  const baseline = execFileSync(
    "git",
    ["show", "origin/main:src/site/sitemap.xml"],
    { cwd: repo, encoding: "utf8" },
  );
  for (const m of baseline.matchAll(/<loc>(.*?)<\/loc>/g))
    assert(urls.includes(m[1]), "dropped " + m[1]);
  for (const slug of slugs.slice(1))
    assert.equal(
      urls.filter((x) => x === "https://cloudhealthoffice.com/" + slug).length,
      1,
    );
  assert(
    !urls.some((x) =>
      /\/(portal(?:\/|$)|login$|404$|cho-[a-z0-9-]+-marketing$)/.test(x),
    ),
  );
  assert(!fs.existsSync(path.join(dist, "tests")));
});
test("executive text and focus colors meet contrast requirements on their actual surfaces", () => {
  function lum(hex) {
    const c = hex
      .match(/\w\w/g)
      .map((x) => parseInt(x, 16) / 255)
      .map((x) => (x <= 0.04045 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4));
    return c[0] * 0.2126 + c[1] * 0.7152 + c[2] * 0.0722;
  }
  function ratio(a, b) {
    const x = lum(a),
      y = lum(b);
    return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
  }
  for (const bg of ["0b1218", "131f28"])
    for (const fg of ["f0f5f5", "b7c8ce", "77e4d0", "ffb7ae"])
      assert(ratio(fg, bg) >= 4.5, fg + " on " + bg);
  assert(ratio("0b1218", "77e4d0") >= 4.5);
  assert(ratio("a8f5df", "131f28") >= 3);
});
