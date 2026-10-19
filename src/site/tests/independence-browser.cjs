// Built-site browser checks; all external traffic blocked, Formspree mocked.
const { chromium } = require("playwright");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { execFileSync } = require("node:child_process");
const crypto = require("node:crypto");
const { calculate } = require("../js/pmpm-calculator.js");
const destinations = require("./legacy-destinations.cjs");
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
      if (!target.startsWith(dist + path.sep)) {
        response.writeHead(403).end();
        return;
      }
      const file = [
        target,
        target + ".html",
        path.join(target, "index.html"),
      ].find(
        (candidate) =>
          fs.existsSync(candidate) && fs.statSync(candidate).isFile(),
      );
      if (!file) {
        response.writeHead(404).end();
        return;
      }
      const type =
        {
          ".html": "text/html",
          ".css": "text/css",
          ".js": "text/javascript",
          ".svg": "image/svg+xml",
          ".png": "image/png",
        }[path.extname(file)] || "application/octet-stream";
      response.writeHead(200, { "Content-Type": type });
      fs.createReadStream(file).pipe(response);
    });
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    base = "http://127.0.0.1:" + server.address().port;
  }
  const browser = await chromium.launch({
    headless: true,
    ...(process.env.CHO_CHROME_PATH
      ? { executablePath: process.env.CHO_CHROME_PATH }
      : {}),
  });
  const head = execFileSync("git", ["rev-parse", "HEAD"], {
    cwd: __dirname,
    encoding: "utf8",
  }).trim();
  const output =
    process.env.CHO_EVIDENCE_DIR ||
    path.resolve(__dirname, "../../../docs/marketing/independence/screenshots");
  fs.mkdirSync(output, { recursive: true });
  const checks = [];
  const contexts = [];
  async function instrument(options = {}) {
    const context = await browser.newContext(options);
    contexts.push(context);
    const requests = [];
    context.on("request", (r) =>
      requests.push({
        url: r.url(),
        method: r.method(),
        body: r.postData(),
        headers: r.headers(),
      }),
    );
    await context.route("**/*", (r) =>
      new URL(r.request().url()).origin === base ? r.continue() : r.abort(),
    );
    return { context, requests };
  }
  const { context, requests } = await instrument();
  const p = await context.newPage(),
    errors = [];
  p.on("pageerror", (e) => errors.push(e.message));
  // Inspect the actual first-party event script and build-injected gtag queue.
  // Never replace choTrack. Block remote GA loading/receipt, and inspect all requests.
  async function events(page) {
    return page.evaluate(() =>
      Array.from(window.dataLayer || [])
        .filter((x) => x[0] === "event")
        .map((x) => ({ name: x[1], params: x[2] })),
    );
  }
  function noSentinels(value, sentinels, label) {
    const text = JSON.stringify(value);
    for (const sentinel of sentinels)
      assert(!text.includes(String(sentinel)), label + " leaked " + sentinel);
  }
  async function keyboardAndFragments(page) {
    await page.goto(base + "/");
    await page.keyboard.press("Tab");
    assert.equal(
      await page.evaluate(() => document.activeElement.className),
      "skip-to-main",
    );
    await page.keyboard.press("Enter");
    assert.equal(
      await page.evaluate(() => document.activeElement.id),
      "main-content",
    );
    if (await page.locator("#mobileMenuToggle").isVisible()) {
      await page.locator("#mobileMenuToggle").focus();
      await page.keyboard.press("Enter");
      assert.equal(
        await page.locator("#mobileMenuToggle").getAttribute("aria-expanded"),
        "true",
      );
      await page.keyboard.press("Escape");
      assert.equal(
        await page.locator("#mobileMenuToggle").getAttribute("aria-expanded"),
        "false",
      );
      assert.equal(
        await page.evaluate(() => document.activeElement.id),
        "mobileMenuToggle",
      );
      await page.locator("#mobileMenuToggle").click();
    }
    await page.locator("summary").first().focus();
    await page.keyboard.press("Enter");
    assert(
      await page
        .locator("details")
        .first()
        .evaluate((el) => el.open),
    );
    for (const [id, d] of Object.entries(destinations)) {
      await page.goto(base + "/#" + id);
      const target = page.locator('[id="' + id + '"]');
      assert(await target.isVisible());
      assert(
        (await target.innerText()).trim().length > 0,
        id + " empty destination",
      );
      const section = target.locator("xpath=ancestor-or-self::section[1]");
      assert.equal(await section.getAttribute("id"), d.section);
      assert(
        (await section.innerText()).includes(d.text),
        id + " wrong subject",
      );
      assert.equal(
        (await section.locator('a[href="' + d.href + '"]').count()) > 0,
        true,
      );
      const box = await target.boundingBox();
      assert(box.y >= -1 && box.y < 1000, id + " not scrolled into view");
    }
  }
  for (const width of [1440, 390]) {
    await p.setViewportSize({ width, height: 1000 });
    await p.emulateMedia({ reducedMotion: "reduce" });
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
      assert.equal(await p.locator("h1").count(), 1);
      assert.equal(
        await p.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
        true,
        slug + " overflow at " + width,
      );
      assert.equal(
        await p.locator('link[rel="canonical"]').getAttribute("href"),
        "https://cloudhealthoffice.com" + (slug === "index" ? "/" : "/" + slug),
      );
      assert.equal(
        await p.locator('meta[property="og:url"]').getAttribute("content"),
        await p.locator('link[rel="canonical"]').getAttribute("href"),
      );
      if (slug === "economics-of-vendor-lock-in") {
        await p.locator("#example").click();
        assert.match(
          await p.locator("#calculator-results").innerText(),
          /-\$300,000.00/,
        );
      }
      await p.screenshot({
        path: path.join(
          output,
          slug + "-" + (width === 1440 ? "desktop" : "mobile") + ".png",
        ),
        fullPage: true,
      });
      // Every executive page has a usable, focusable skip destination.
      await p.goto(base + (slug === "index" ? "/" : "/" + slug));
      await p.keyboard.press("Tab");
      assert.equal(await p.evaluate(() => document.activeElement.className), "skip-to-main");
      const outline = await p.locator(".skip-to-main").evaluate(el => getComputedStyle(el).outlineStyle);
      assert.notEqual(outline, "none");
      await p.keyboard.press("Enter");
      assert.equal(await p.evaluate(() => document.activeElement.id), "main-content");
    }
    await keyboardAndFragments(p);
  }
  checks.push(
    "six pages at 1440/390; metadata, no overflow, keyboard skip/menu/disclosure and 13 meaningful fragment destinations",
  );

  // JavaScript absent, blocked, and initialization failure: both ordinary UI
  // attempts and defense-in-depth forced enabling must have no request surface.
  for (const mode of ["disabled", "blocked", "initialization-failure"]) {
    const fixture = await instrument({
      javaScriptEnabled: mode !== "disabled",
    });
    if (mode === "blocked")
      await fixture.context.route("**/js/pmpm-calculator.js", (r) => r.abort());
    if (mode === "initialization-failure")
      await fixture.context.addInitScript(() => {
        Intl.NumberFormat = function () {
          throw new Error("Synthetic initialization failure");
        };
      });
    const page = await fixture.context.newPage();
    await page.goto(base + "/economics-of-vendor-lock-in");
    assert(
      await page.locator("#calculator-controls").evaluate((el) => el.disabled),
    );
    assert(await page.locator("#currentPmpm").isDisabled());
    assert.match(
      await page.locator("#calculator-status").innerText(),
      /unavailable/i,
    );
    assert(await page.locator("#calculate").isDisabled());
    assert.equal(await page.locator("#economics-calculator form").count(), 0);
    assert(
      await page
        .locator("#economics-calculator input")
        .evaluateAll((fields) =>
          fields.every((field) => field.form === null && !field.name),
        ),
    );
    const before = fixture.requests.length,
      url = page.url();
    // Disabled button click by coordinates and Enter cannot activate a request.
    await page.locator("#calculate").scrollIntoViewIfNeeded();
    const box = await page.locator("#calculate").boundingBox();
    await page.mouse.click(box.x + 10, box.y + 10);
    await page.keyboard.press("Enter");
    // DOM changes simulate a restored/stale enabled control after script failure.
    // They are test setup only; no init script/handler is installed by this step.
    await page
      .locator("#calculator-controls")
      .evaluate((el) => (el.disabled = false));
    await page.locator("#currentPmpm").fill("93.71");
    await page.locator("#calculate").click();
    await page.locator("#currentPmpm").press("Enter");
    await page.waitForTimeout(100);
    assert.equal(page.url(), url);
    assert.equal(fixture.requests.length, before, mode + " attempted request");
    noSentinels(fixture.requests, ["93.71"], mode);
    await fixture.context.close();
  }
  checks.push(
    "no-JS, blocked-script and formatter-init-failure: disabled unavailable state; button/Enter emit no native request even if controls are forcibly re-enabled",
  );

  await p.goto(base + "/economics-of-vendor-lock-in");
  assert.match(await p.locator("#calculator-status").innerText(), /ready/i);
  assert(await p.locator("#calculate").isEnabled());
  await p.locator("#calculate").click();
  assert.match(await p.locator("#calculator-results").innerText(), /unknown/);
  await p.locator("#example").click();
  const scenario = {
    members: 135791,
    memberMonths: 1629492,
    horizon: 7,
    currentPmpm: 23.87,
    alternativePmpm: 17.43,
    currentFixed: 987654.32,
    alternativeFixed: 876543.21,
    currentInfrastructure: 765432.1,
    alternativeInfrastructure: 654321.09,
    currentSupport: 543210.98,
    alternativeSupport: 432109.87,
    currentStaffing: 321098.76,
    alternativeStaffing: 210987.65,
    currentIntegration: 109876.54,
    alternativeIntegration: 198765.43,
    transition: 456789.12,
    exit: 345678.23,
    parallel: 234567.34,
    licenseKnown: true,
    supportKnown: true,
  };
  const secrets = Object.entries(scenario)
    .filter(
      ([key]) => !["licenseKnown", "supportKnown", "horizon"].includes(key),
    )
    .map(([, v]) => String(v));
  const url = p.url(),
    beforeRequests = requests.length;
  for (const [key, value] of Object.entries(scenario))
    if (!["licenseKnown", "supportKnown", "memberMonths"].includes(key))
      await p.locator("#" + key).fill(String(value));
  assert.match(
    await p.locator("#calculator-results").innerText(),
    /Calculate again/,
  );
  async function assertResults(data) {
    const expected = calculate(data),
      currency = new Intl.NumberFormat("en-US", {
        style: "currency",
        currency: "USD",
        maximumFractionDigits: 2,
      });
    const results = p.locator("#calculator-results > dl").first();
    for (const [i, key] of [
      "currentAnnual",
      "alternativeAnnual",
      "currentHorizon",
      "alternativeHorizon",
      "difference",
      "effectivePmpm",
    ].entries())
      assert.equal(
        await results.locator("dd").nth(i).innerText(),
        currency.format(expected[key]),
      );
    assert.match(
      await p.locator("#calculator-results").innerText(),
      /Scenario assumptions/,
    );
    assert.equal(await p.locator("#calculator-error").isVisible(), false);
  }
  const beforeEvents = (await events(p)).filter(
    (x) => x.name === "calculator_calculated",
  ).length;
  await p.locator("#calculate").click();
  await assertResults(scenario);
  assert.equal(
    (await events(p)).filter((x) => x.name === "calculator_calculated").length,
    beforeEvents + 1,
  );
  scenario.alternativePmpm = 16.41;
  secrets.push("16.41");
  await p.locator("#alternativePmpm").fill("16.41");
  await p.locator("#alternativePmpm").press("Enter");
  await assertResults(scenario);
  assert.equal(
    (await events(p)).filter((x) => x.name === "calculator_calculated").length,
    beforeEvents + 2,
  );
  assert.equal(
    (await events(p)).filter((x) => x.name === "calculator_engagement").length,
    1,
  );
  assert.equal(p.url(), url);
  assert.equal(
    requests.length,
    beforeRequests,
    "calculator unexpectedly requested a resource",
  );
  noSentinels(await events(p), secrets, "real analytics event queue");
  noSentinels(requests, secrets, "request URL/body/headers");
  noSentinels(
    await p.evaluate(() => ({
      local: { ...localStorage },
      session: { ...sessionStorage },
      cookie: document.cookie,
    })),
    secrets,
    "storage",
  );
  await p.emulateMedia({ media: "print" });
  assert.equal(await p.locator("#cho-asst-toggle").isVisible(), false);
  assert.equal(await p.locator("#print-results").isVisible(), false);
  assert(await p.locator("#calculator-results").isVisible());
  await assertResults(scenario);
  await p.screenshot({
    path: path.join(output, "calculator-print.png"),
    fullPage: true,
  });
  await p.emulateMedia({ media: "screen", reducedMotion: "reduce" });
  checks.push(
    "valid unique sentinels; successful button and Enter recalculation with six exact numeric results; actual analytics/dataLayer, request URL/body/headers and storage inspected; printable computed results",
  );

  for (const kind of ["strategy", "partnership"]) {
    await p.goto(base + "/founding-transformation-partners");
    let fail = true,
      count = 0;
    await p.route("https://formspree.io/f/*", async (r) => {
      count++;
      assert.equal(r.request().method(), "POST");
      await r.fulfill({
        status: fail ? 500 : 200,
        contentType: "application/json",
        body: fail
          ? '{"errors":[{"message":"Synthetic Private Server Error"}]}'
          : '{"ok":true}',
      });
    });
    await p.locator("#" + kind + "-firstName").fill("Synthetic Private Name");
    await p.locator("#" + kind + "-email").fill("synthetic@example.invalid");
    if (kind === "partnership") {
      await p.locator("#partnership-company").fill("Synthetic Private Company");
      await p.locator("#partnership-role").fill("Synthetic Private Role");
    }
    const form = p.locator("#" + kind + "Form");
    await form.locator("button").click();
    await form.locator("[data-lead-error]").waitFor({ state: "visible" });
    assert(await form.locator("button").isEnabled());
    assert.equal(
      (await events(p)).filter((x) => x.name === "form_submit").length,
      0,
    );
    assert.equal(
      (await events(p)).filter((x) => x.name === "form_error").length,
      1,
    );
    fail = false;
    await form.locator("button").click();
    await p
      .locator('[data-for="' + kind + 'Form"]')
      .waitFor({ state: "visible" });
    assert.equal(count, 2);
    assert.equal(
      (await events(p)).filter((x) => x.name === "form_submit").length,
      1,
    );
    assert.equal(
      await p.evaluate(() => document.activeElement.dataset.for),
      kind + "Form",
    );
    noSentinels(
      await events(p),
      ["synthetic@example", "Synthetic Private"],
      "form analytics queue",
    );
    await p.unroute("https://formspree.io/f/*");
  }
  checks.push(
    "both Formspree pathways: mocked failure/retry/accepted response; actual form_error/form_submit queue and focus checked; zero real inquiries",
  );
  assert.deepEqual(errors, []);
  const hashes = Object.fromEntries(
    fs
      .readdirSync(output)
      .filter((f) => f.endsWith(".png"))
      .sort()
      .map((file) => [
        file,
        crypto
          .createHash("sha256")
          .update(fs.readFileSync(path.join(output, file)))
          .digest("hex"),
      ]),
  );
  const evidence = {
    head,
    baseline: execFileSync(
      "git",
      ["rev-parse", process.env.CHO_BASELINE_REF || "origin/main"],
      { cwd: __dirname, encoding: "utf8" },
    ).trim(),
    worktreeStatus: execFileSync("git", ["status", "--porcelain"], {
      cwd: __dirname,
      encoding: "utf8",
    }).trim(),
    generatedAt: new Date().toISOString(),
    checks,
    screenshots: hashes,
    externalAnalytics:
      "GTM/GA network blocked; actual build-injected gtag queue and unmodified first-party instrumentation inspected",
    productionInquiries: 0,
  };
  fs.writeFileSync(
    path.join(output, "evidence.json"),
    JSON.stringify(evidence, null, 2) + "\n",
  );
  console.log(JSON.stringify(evidence, null, 2));
  for (const ctx of contexts) await ctx.close();
  await browser.close();
  if (server) await new Promise((resolve) => server.close(resolve));
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
