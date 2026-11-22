// Records a captioned walkthrough of the Cloud Health Office demo portal.
//
// Drives the portal with Playwright, puts a caption on screen for each step,
// and saves a WebM video plus a PNG per step under ./out. Run it from the
// "Record demo walkthrough" workflow, or locally against a port-forwarded
// portal:
//
//   npm install --no-save --no-package-lock playwright@1.49.1
//   npx playwright install chromium
//   BASE_URL=http://localhost:8080 WALKTHROUGH=prior-auth node record.js
//
// Behind Cloudflare Access, set CF_ACCESS_CLIENT_ID and CF_ACCESS_CLIENT_SECRET
// to an Access service token the demo application's policy allows. The demo
// portal signs the visitor in as its local demo user, so no password is needed.
//
// The prior-auth steps open the records `cho_demo.py seed-story` loads; run it
// first, or those pages show "not found". The texas walkthrough (about 3
// minutes) follows docs/deploy/TEXAS-MEDICAID-DEMO.md and needs
// `demo.sh seed` (scripts/demo/texas) run the same day.

import { chromium } from 'playwright';
import { mkdir, rename } from 'node:fs/promises';
import path from 'node:path';

const BASE_URL = (process.env.BASE_URL || 'https://demo.cloudhealthoffice.com').replace(/\/$/, '');
const WALKTHROUGH = process.env.WALKTHROUGH || 'prior-auth';
const OUT = path.resolve(process.env.OUT_DIR || 'out');
const PAUSE_MS = Number(process.env.PAUSE_MS || (WALKTHROUGH === 'texas' ? 9000 : 6500));

const PRIOR_AUTH = [
  { path: '/demo', caption: 'A synthetic Medicaid plan running on Cloud Health Office. Every record here is invented.' },
  { path: '/authorizations', caption: 'Prior authorization requests, each with its CMS-0057-F decision clock.' },
  { path: '/authorizations/story-pa-auto-approved/review', caption: 'An MRI on the auto-approve list: approved by a rule six minutes after receipt.' },
  { path: '/authorizations/story-pa-partial/services', caption: 'A partial approval. Each line carries its own decision and a coded reason.' },
  { path: '/authorizations/story-pa-denied/services', caption: 'A denial with the specific reason CMS-0057-F requires.' },
  { path: '/authorizations/story-pa-extended/review', caption: 'One lawful 14-day extension, with the member notice recorded.' },
  { path: '/authorizations/story-pa-expedited-open/review', caption: 'An expedited request running against its 72-hour deadline.' },
  { path: '/compliance/cms-0057f', caption: 'Timeliness across every request, and the metrics a plan must post each year.' },
];

const FULL_TOUR = [
  PRIOR_AUTH[0],
  { path: '/members', caption: 'Members, coverage and eligibility.' },
  { path: '/claims', caption: 'Claims through adjudication, with line-level detail.' },
  ...PRIOR_AUTH.slice(1),
  { path: '/mass-adjudication-runs', caption: 'Batch adjudication runs: claim counts, throughput and dead letters.' },
];

// Texas Medicaid (scripts/demo/texas): the daily file drop, Patient Access from
// it, shadow adjudication, and the Texas prior-authorization clocks. A step may
// show several captions in turn, and may act on the page (open a report,
// follow a link) before its captions.
const TX_MEMBER = 'TXM7100001';

async function openShadowReport(page) {
  // The run after the medical claim file (183 claims); pharmacy runs have nothing to compare.
  const runs = page.locator('[data-testid="shadow-runs"] tbody tr');
  await runs.first().waitFor({ timeout: 15000 });
  const medical = runs.filter({ hasText: '183' }).locator('[data-testid="shadow-open-run"]');
  await ((await medical.count()) ? medical.first() : page.locator('[data-testid="shadow-open-run"]').first()).click();
  const report = page.locator('[data-testid="shadow-report"]');
  await report.waitFor({ timeout: 20000 });
  await report.scrollIntoViewIfNeeded();
}

async function scrollDown(page) {
  await page.mouse.wheel(0, 700);
}

async function openTexasPoststabilization(page) {
  // seed-pa names each request txpa-<scenario>-<MMDDHHMM>; open today's poststabilization one.
  const link = page.locator('a[href*="/authorizations/txpa-poststab-"]').first();
  await link.waitFor({ timeout: 15000 });
  await link.click();
  await page.waitForLoadState('load');
}

// The shadow report says how many claims were skipped as "not found or not
// loaded from a core", so those steps do not treat "not found" as a failure.
const TEXAS = [
  { path: '/edi-transactions', captions: [
    'A Texas Medicaid plan keeps its core system. Cloud Health Office takes the core\'s nightly CPCDS extract. Everything here is synthetic.',
    'The daily file drop: members and coverage load first, then medical and pharmacy claims and UM authorizations. Counts, nothing refused.',
  ] },
  { path: `/members/${TX_MEMBER}/coverage`, caption: 'A STAR member loaded from the drop, with coverage mapped from the core\'s plan code.' },
  { path: `/members/${TX_MEMBER}/claims`, caption: 'Her claims, as the core paid them. The same records serve Patient Access and Provider Access as CARIN Blue Button EOBs.' },
  { path: '/apis', caption: 'CMS-0057-F Patient Access, Provider Access and Prior Authorization APIs, served from the file drop. No core connector.' },
  { path: '/shadow-adjudication', action: openShadowReport, mayMentionNotFound: true, captions: [
    'Shadow adjudication: CHO re-adjudicates every loaded claim and compares its answer with the core\'s.',
    'The finding: the core still pays therapeutic exercise (97110) at last year\'s rate, $23.45 a unit against $21.30.',
  ] },
  { path: null, action: scrollDown, mayMentionNotFound: true, caption: 'Claims CHO could not decide are listed with what to load next. A claims team works from the discrepancy CSV. The core is never changed.' },
  { path: '/compliance/cms-0057f', caption: 'Prior authorization on the Texas clocks: Tex. Gov\'t Code ch. 540 alongside the federal timeframes. The earlier deadline wins.' },
  { path: '/authorizations', action: openTexasPoststabilization, caption: 'Poststabilization care for a hospitalized member: due one hour after receipt, under §540.0302(b)(3).' },
  { path: '/compliance/cms-0057f', caption: 'The plan sends files it already produces. No change to its core. Cloud Health Office: cloudhealthoffice.com' },
];

const WALKTHROUGHS = { 'prior-auth': PRIOR_AUTH, 'full-tour': FULL_TOUR, texas: TEXAS };

async function showCaption(page, text, index, total) {
  await page.evaluate(({ text, index, total }) => {
    let box = document.getElementById('cho-walkthrough-caption');
    if (!box) {
      box = document.createElement('div');
      box.id = 'cho-walkthrough-caption';
      Object.assign(box.style, {
        position: 'fixed', left: '50%', bottom: '28px', transform: 'translateX(-50%)', zIndex: '2147483647',
        maxWidth: '78%', padding: '14px 22px', borderRadius: '10px', background: 'rgba(17, 24, 39, 0.88)',
        color: '#fff', font: '500 20px/1.4 system-ui, sans-serif', boxShadow: '0 6px 24px rgba(0,0,0,0.25)',
        pointerEvents: 'none',
      });
      document.body.appendChild(box);
    }
    box.textContent = `${index}/${total}  ${text}`;
  }, { text, index, total });
}

async function main() {
  const steps = WALKTHROUGHS[WALKTHROUGH];
  if (!steps) throw new Error(`Unknown WALKTHROUGH "${WALKTHROUGH}". Use one of: ${Object.keys(WALKTHROUGHS).join(', ')}`);
  await mkdir(OUT, { recursive: true });

  const headers = {};
  if (process.env.CF_ACCESS_CLIENT_ID && process.env.CF_ACCESS_CLIENT_SECRET) {
    headers['CF-Access-Client-Id'] = process.env.CF_ACCESS_CLIENT_ID;
    headers['CF-Access-Client-Secret'] = process.env.CF_ACCESS_CLIENT_SECRET;
  }

  const browser = await chromium.launch();
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    recordVideo: { dir: OUT, size: { width: 1440, height: 900 } },
    extraHTTPHeaders: headers,
  });
  const page = await context.newPage();
  const failures = [];

  // The demo portal's local sign-in sets the session cookie and redirects.
  await page.goto(`${BASE_URL}/local-demo/sign-in?redirectUri=${encodeURIComponent('/demo')}`, { waitUntil: 'load' });

  const total = steps.reduce((n, step) => n + (step.captions ? step.captions.length : 1), 0);
  let shown = 0;
  for (const [i, step] of steps.entries()) {
    const label = step.path || steps.slice(0, i).reverse().find((s) => s.path)?.path || '/';
    let response = null;
    if (step.path) {
      response = await page.goto(`${BASE_URL}${step.path}`, { waitUntil: 'load' });
      // Blazor renders after the circuit connects; give it a moment to fill the page.
      await page.waitForTimeout(2500);
    }
    if (step.action) {
      try {
        await step.action(page);
        await page.waitForTimeout(1500);
      } catch (err) {
        failures.push(`${label}: ${err.message.split('\n')[0]}`);
      }
    }
    const body = (await page.textContent('body')) || '';
    if ((step.path && (!response || response.status() >= 400))
        || (step.mayMentionNotFound ? /could not be loaded|error occurred|unable to connect|failed to load/i
          : /not found|could not be loaded|error occurred|unable to connect|failed to load/i).test(body)) {
      failures.push(`${label} (HTTP ${response ? response.status() : 'n/a'})`);
    }
    for (const caption of step.captions || [step.caption]) {
      shown += 1;
      await showCaption(page, caption, shown, total);
      await page.screenshot({ path: path.join(OUT, `${String(shown).padStart(2, '0')}${label.replace(/[^a-z0-9]+/gi, '-')}.png`) });
      await page.waitForTimeout(PAUSE_MS);
    }
  }

  const video = page.video();
  await context.close();
  await browser.close();
  if (video) await rename(await video.path(), path.join(OUT, `${WALKTHROUGH}.webm`));

  console.log(`Recorded ${total} captions over ${steps.length} steps to ${OUT}`);
  if (failures.length) {
    console.error(`These steps failed to render cleanly; check the screenshots:\n  ${failures.join('\n  ')}`);
    process.exitCode = 1;
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
