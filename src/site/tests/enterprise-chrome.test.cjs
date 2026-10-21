// Guards the shared site chrome and the naming rule in MESSAGE_SHEET.md.
const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { execFileSync } = require("node:child_process");
const site = path.resolve(__dirname, "..");

function pages(dir = site) {
  const out = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (!["dist", "node_modules", "portal", "tests", "scripts"].includes(e.name)) out.push(...pages(full));
    } else if (e.name.endsWith(".html") && e.name !== "login.html") {
      out.push(full);
    }
  }
  return out;
}

test("every site page carries the one shared nav and footer", () => {
  execFileSync(process.execPath, [path.join(site, "scripts/sync-site-chrome.mjs"), "--check"], { cwd: site, stdio: "pipe" });
  const withNav = pages().filter((f) => fs.readFileSync(f, "utf8").includes('id="mainNav"'));
  assert(withNav.length > 60);
  for (const f of withNav) {
    const html = fs.readFileSync(f, "utf8");
    assert.equal((html.match(/class="site-nav"/g) || []).length, 1, f);
    assert.equal((html.match(/<footer class="cho-footer">/g) || []).length, 1, f);
    const sheets = [...html.matchAll(/<link rel="stylesheet" href="([^"]+)"/g)].map((m) => m[1]);
    assert.equal(sheets[sheets.length - 1], "/css/enterprise.css", f + " must load enterprise.css last");
  }
});

// Visible text for a content check (not a sanitizer): drop script/style bodies
// and tags, repeating until stable so nested or split markup cannot survive.
function visibleText(html) {
  let prev;
  let text = html;
  do {
    prev = text;
    text = text
      .replace(/<(script|style)\b[\s\S]*?<\/\1\s*>/gi, " ")
      .replace(/<[^<>]*>/g, " ");
  } while (text !== prev);
  return text.replace(/[<>]/g, " ");
}

test('visible copy does not use "independence" as a label (MESSAGE_SHEET naming rule)', () => {
  for (const f of pages()) {
    const html = fs.readFileSync(f, "utf8");
    const attrs = [...html.matchAll(/(?:content|alt|title|aria-label)="([^"]*)"/g)]
      .map((m) => m[1])
      .filter((v) => !v.startsWith("http"));
    const text = visibleText(html);
    for (const chunk of [text, ...attrs]) assert(!/independence/i.test(chunk), path.relative(site, f));
  }
});

test("homepage routes every audience to a known contact topic", () => {
  const home = fs.readFileSync(path.join(site, "index.html"), "utf8");
  const contact = fs.readFileSync(path.join(site, "contact.html"), "utf8");
  for (const key of ["cms0057-assessment", "vendor-integration", "channel-partner", "investor"]) {
    assert(home.includes("/contact?interest=" + key), key);
    assert(contact.includes('<option value="' + key + '">'), key);
  }
  assert(home.includes('id="interoperability"') && home.includes('id="work-with-us"'));
  assert(!/free CMS-0057-F/i.test(home), "commercial posture: do not headline free CMS-0057-F");
  assert(contact.includes('<option value="sandbox-access">'));
});

test("no page links to the retired sandbox host", () => {
  const RETIRED_HOST = "sandbox." + "cloudhealthoffice.com";
  for (const f of pages()) {
    const html = fs.readFileSync(f, "utf8");
    for (const m of html.matchAll(/\b(?:href|src|action)="([^"]+)"/g)) {
      let host = "";
      try {
        host = new URL(m[1], "https://cloudhealthoffice.com/").hostname;
      } catch {
        continue;
      }
      assert.notEqual(host, RETIRED_HOST, path.relative(site, f) + " links to " + m[1]);
    }
  }
});
