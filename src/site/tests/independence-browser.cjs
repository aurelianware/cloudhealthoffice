// Built-site browser checks; all external traffic blocked, Formspree mocked.
const { chromium } = require("playwright");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
let base = process.env.CHO_PREVIEW_URL;
const dist = path.resolve(__dirname, "../dist");
(async () => {
  // Serve the built artifact with clean-file routes when no preview was supplied.
  // This is a test fixture, not a production routing configuration.
  let server;
  if (!base) {
    server = http.createServer((request, response) => {
      const pathname = new URL(request.url, "http://localhost").pathname;
      const relative = decodeURIComponent(pathname).replace(/^\/+/, "");
      const target = path.resolve(dist, relative || "index.html");
      if (!target.startsWith(dist + path.sep)) { response.writeHead(403).end(); return; }
      const file = [target, target + ".html", path.join(target, "index.html")]
        .find(candidate => fs.existsSync(candidate) && fs.statSync(candidate).isFile());
      if (!file) { response.writeHead(404).end(); return; }
      const type = {".html":"text/html", ".css":"text/css", ".js":"text/javascript", ".svg":"image/svg+xml", ".png":"image/png"}[path.extname(file)] || "application/octet-stream";
      response.writeHead(200, {"Content-Type":type}); fs.createReadStream(file).pipe(response);
    });
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    base = "http://127.0.0.1:" + server.address().port;
  }
  const browser = await chromium.launch({
    headless: true,
    ...(process.env.CHO_CHROME_PATH
      ? { executablePath: process.env.CHO_CHROME_PATH }
      : {}),
  });
  const context = await browser.newContext();
  await context.route("**/*", (r) =>
    new URL(r.request().url()).origin === base ? r.continue() : r.abort(),
  );
  const p = await context.newPage();
  const errors = [];
  p.on("pageerror", (e) => errors.push(e.message));
  const output = path.resolve(
    __dirname,
    "../../../docs/marketing/independence/screenshots",
  );
  fs.mkdirSync(output, { recursive: true });
  for (const width of [1440, 390]) {
    await p.setViewportSize({ width, height: 1000 });
    for (const slug of [
      "index",
      "health-plan-independence",
      "economics-of-vendor-lock-in",
      "ai-ready-health-plan-architecture",
      "incremental-modernization",
      "founding-transformation-partners",
    ]) {
      assert.equal(
        (await p.goto(base + (slug === "index" ? "/" : "/" + slug))).status(),
        200,
      );
      await p.emulateMedia({ reducedMotion: "reduce" });
      assert.equal(await p.locator("h1").count(), 1);
      assert.equal(
        await p.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
        true,
        slug + " overflow",
      );
      assert.equal(
        await p.locator("link[rel=canonical]").getAttribute("href"),
        "https://cloudhealthoffice.com" + (slug === "index" ? "/" : "/" + slug),
      );
      if (slug === "economics-of-vendor-lock-in")
        await p.locator("#example").click();
      await p.screenshot({
        path: path.join(
          output,
          slug + "-" + (width === 1440 ? "desktop" : "mobile") + ".png",
        ),
        fullPage: true,
      });
    }
  }
  await p.goto(base + "/");
  assert.equal(await p.locator("#cho-asst-toggle").evaluate(el => getComputedStyle(el).position), "static");
  await p.keyboard.press("Tab");
  assert.equal(
    await p.evaluate(() => document.activeElement.className),
    "skip-to-main",
  );
  await p.locator("#mobileMenuToggle").click();
  assert.equal(
    await p.locator("#mobileMenuToggle").getAttribute("aria-expanded"),
    "true",
  );
  await p.keyboard.press("Escape");
  assert.equal(
    await p.locator("#mobileMenuToggle").getAttribute("aria-expanded"),
    "false",
  );
  assert.equal(
    await p.evaluate(() => document.activeElement.id),
    "mobileMenuToggle",
  );
  await p.goto(base + "/economics-of-vendor-lock-in");
  await p.evaluate(() => {
    window.captured = [];
    window.choTrack = (name, params) => window.captured.push({ name, params });
  });
  await p.locator("#economics-form button[type=submit]").click();
  assert.match(await p.locator("#calculator-results").innerText(), /unknown/);
  await p.locator("#example").click();
  assert.match(
    await p.locator("#calculator-results").innerText(),
    /-\$300,000.00/,
  );
  assert.match(
    await p.locator("#calculator-results").innerText(),
    /3.50 years/,
  );
  await p.locator("#alternativePmpm").fill("1.1234567");
  assert.match(
    await p.locator("#calculator-results").innerText(),
    /Calculate again/,
  );
  await p.locator("#economics-form button[type=submit]").click();
  assert.doesNotMatch(
    JSON.stringify(await p.evaluate(() => window.captured)),
    /1\.1234567|1200000|300000/,
  );
  await p.emulateMedia({media:"print"});
  assert.equal(await p.locator("#cho-asst-toggle").isVisible(), false);
  assert.equal(await p.locator("#calculator-results").isVisible(), true);
  await p.emulateMedia({media:"screen", reducedMotion:"reduce"});
  for (const kind of ["strategy", "partnership"]) {
    await p.goto(base + "/founding-transformation-partners");
    await p.evaluate(() => {
      window.captured = [];
      window.choTrack = (name, params) =>
        window.captured.push({ name, params });
    });
    let fail = true,
      count = 0;
    await p.route("https://formspree.io/f/*", async (r) => {
      count++;
      assert.equal(r.request().method(), "POST");
      await r.fulfill({
        status: fail ? 500 : 200,
        contentType: "application/json",
        body: fail ? "{}" : '{"ok":true}',
      });
    });
    await p.locator("#" + kind + "-firstName").fill("Synthetic Private Name");
    await p.locator("#" + kind + "-email").fill("synthetic@example.invalid");
    if (kind === "partnership") {
      await p.locator("#partnership-company").fill("Synthetic Private Company");
      await p.locator("#partnership-role").fill("Synthetic Private Role");
    }
    const f = p.locator("#" + kind + "Form");
    await f.locator("button").click();
    await f.locator("[data-lead-error]").waitFor({ state: "visible" });
    assert.equal(await f.locator("button").isEnabled(), true);
    assert.equal(
      await p.evaluate(
        () => window.captured.filter((x) => x.name === "form_submit").length,
      ),
      0,
    );
    fail = false;
    await f.locator("button").click();
    await p
      .locator('[data-for="' + kind + 'Form"]')
      .waitFor({ state: "visible" });
    assert.equal(count, 2);
    assert.equal(
      await p.evaluate(
        () => window.captured.filter((x) => x.name === "form_submit").length,
      ),
      1,
    );
    assert.equal(
      await p.evaluate(() => document.activeElement.dataset.for),
      kind + "Form",
    );
    assert.doesNotMatch(
      JSON.stringify(await p.evaluate(() => window.captured)),
      /synthetic@example|Synthetic Private/,
    );
    await p.unroute("https://formspree.io/f/*");
  }
  assert.deepEqual(errors, []);
  console.log(
    "PASS: 12 responsive screenshots, six routes/canonicals, no overflow, keyboard navigation, calculator privacy, two forms failure/retry/acceptance; zero production submissions.",
  );
  await browser.close();
  if (server) await new Promise(resolve => server.close(resolve));
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
