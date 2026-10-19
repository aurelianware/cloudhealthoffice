// Read-only hosted verification. Never submits financial/contact values or deploys.
const fs = require("node:fs");
const path = require("node:path");
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const { execFileSync } = require("node:child_process");
const origin = process.env.CHO_HOSTED_PREVIEW;
if (!origin || !/^https:\/\//.test(origin))
  throw new Error("Set CHO_HOSTED_PREVIEW to the existing HTTPS preview URL.");
const head = execFileSync("git", ["rev-parse", "HEAD"], {
  cwd: __dirname,
  encoding: "utf8",
}).trim();
const result = {
  head,
  preview: origin,
  checkedAt: new Date().toISOString(),
  checks: [],
  verified: false,
};
async function get(route) {
  const url = new URL(route, origin);
  const res = await fetch(url, {
    redirect: "manual",
    signal: AbortSignal.timeout(20000),
  });
  const body = await res.text();
  const entry = {
    route,
    status: res.status,
    location: res.headers.get("location"),
    noindex: res.headers.get("x-robots-tag"),
    bodySha256: crypto.createHash("sha256").update(body).digest("hex"),
  };
  result.checks.push(entry);
  return { res, body, entry };
}
(async () => {
  for (const slug of [
    "",
    "health-plan-independence",
    "economics-of-vendor-lock-in",
    "ai-ready-health-plan-architecture",
    "incremental-modernization",
    "founding-transformation-partners",
  ]) {
    const clean = "/" + slug,
      { res, body } = await get(clean);
    assert.equal(res.status, 200, clean + " inaccessible");
    assert(
      body.includes('href="https://cloudhealthoffice.com' + clean + '"'),
      clean + " canonical",
    );
    for (const variant of slug
      ? ["/" + slug + ".html", "/" + slug + "/"]
      : ["/index.html"]) {
      const { res, entry } = await get(variant);
      assert(
        [301, 302, 307, 308].includes(res.status),
        variant + " missing canonical redirect",
      );
      assert.equal(
        new URL(entry.location, new URL(variant, origin)).pathname,
        clean,
        variant + " wrong target",
      );
      const final = await fetch(new URL(variant, origin), {
        signal: AbortSignal.timeout(20000),
      });
      assert.equal(final.status, 200);
      assert.equal(
        new URL(final.url).pathname,
        clean,
        variant + " loop/wrong final",
      );
    }
  }
  const docs = await get("/docs/");
  assert.equal(docs.res.status, 301);
  assert.equal(new URL(docs.entry.location, origin).pathname, "/docs");
  const js = await get("/js/pmpm-calculator.js");
  assert.equal(js.res.status, 200);
  const local = fs.readFileSync(
    path.resolve(__dirname, "../dist/js/pmpm-calculator.js"),
  );
  assert.equal(
    js.entry.bodySha256,
    crypto.createHash("sha256").update(local).digest("hex"),
    "preview calculator does not match this built head",
  );
  result.verified = true;
})()
  .catch((error) => {
    result.accessLimitation = error.message;
    process.exitCode = 1;
  })
  .finally(() => {
    const output =
      process.env.CHO_HOSTED_EVIDENCE || "/tmp/cho-hosted-evidence.json";
    fs.writeFileSync(output, JSON.stringify(result, null, 2) + "\n");
    console.log(JSON.stringify(result, null, 2));
  });
