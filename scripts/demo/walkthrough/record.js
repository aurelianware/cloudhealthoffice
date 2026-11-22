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
// first, or those pages show "not found". The texas walkthrough (about 2
// minutes) follows docs/deploy/TEXAS-MEDICAID-DEMO.md and needs
// `demo.sh seed` (scripts/demo/texas) run the same day, as do
// cms0057-prior-auth (CRD answer -> DTR form -> PAS request -> decision clock
// -> metrics) and shadow-adjudication (report -> discrepancy CSV -> claim line
// -> fee schedule). cms0057-prior-auth also calls CRD directly, as an EHR
// would: set CRD_URL to fhir-service (http://localhost:5023 through
// `demo.sh connect`) and CHO_TOKEN to `python3 scripts/demo/gcp/cho_demo.py token`.

import { chromium } from 'playwright';
import { mkdir, rename, writeFile } from 'node:fs/promises';
import path from 'node:path';

const BASE_URL = (process.env.BASE_URL || 'https://demo.cloudhealthoffice.com').replace(/\/$/, '');
const WALKTHROUGH = process.env.WALKTHROUGH || 'prior-auth';
const OUT = path.resolve(process.env.OUT_DIR || 'out');
// The Texas-data walkthroughs carry longer captions than the original tours.
const PAUSE_MS = Number(process.env.PAUSE_MS || (['prior-auth', 'full-tour'].includes(WALKTHROUGH) ? 6500 : 9000));

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

// Texas Medicaid (scripts/demo/texas): the daily file drop, the claims it
// loaded, shadow adjudication, and the Texas prior-authorization clocks. A step
// may be a title card, may act on the page first, and may show several
// captions, each after its own action (a scroll to the part it describes).
const TX_MEMBER = 'TXM7100001';

async function titleCard(page, title, subtitle) {
  await page.setContent(`<!doctype html><html><body style="margin:0;height:100vh;display:flex;flex-direction:column;
    justify-content:center;align-items:center;background:#0f2742;color:#fff;font-family:system-ui,sans-serif;text-align:center">
    <div style="font-size:52px;font-weight:700;max-width:80%;line-height:1.2">${title}</div>
    <div style="font-size:24px;margin-top:28px;max-width:70%;line-height:1.5;color:#cfe0f2">${subtitle}</div>
    </body></html>`);
}

async function scrollTo(page, selector, block = 'start') {
  const target = page.locator(selector).first();
  await target.waitFor({ timeout: 15000 });
  await target.evaluate((el, where) => el.scrollIntoView({ block: where, behavior: 'smooth' }), block);
  await page.waitForTimeout(800);
}

async function searchClaimsForMember(page) {
  // The claims list is empty until searched: pick the member in the Member
  // autocomplete (placeholder "Search by name or ID"), then Search.
  const member = page.locator('input[placeholder="Search by name or ID"]').first();
  try {
    await member.waitFor({ timeout: 20000 });
    await member.click();
    await member.pressSequentially(TX_MEMBER, { delay: 80 });
    const option = page.locator('.mud-popover-open .mud-list-item, .mud-popover-open [role="option"]', { hasText: TX_MEMBER }).first();
    try {
      await option.waitFor({ timeout: 10000 });
      await option.click();
    } catch {
      // MudAutocomplete also takes the highlighted option from the keyboard.
      await member.press('ArrowDown');
      await member.press('Enter');
    }
    await page.waitForTimeout(800);
    await page.getByRole('button', { name: /^\s*Search\s*$/ }).first().click();
    // Each result row opens its claim from a button holding the claim number.
    await claimButtons(page).first().waitFor({ timeout: 20000 });
  } catch (err) {
    // Record what the page showed, so the run says which part did not match.
    const dump = await page.evaluate(() => {
      const control = document.querySelector('input[placeholder="Search by name or ID"]')?.closest('.mud-input-control');
      const popovers = [...document.querySelectorAll('.mud-popover')].map((p) => `${p.className}: ${p.innerText.slice(0, 300)}`);
      const alerts = [...document.querySelectorAll('.mud-alert')].map((a) => a.innerText.slice(0, 200));
      const rows = document.querySelectorAll('tbody tr').length;
      return { control: control ? control.outerHTML.slice(0, 1200) : 'no Member input', popovers, alerts, rows,
        inputs: [...document.querySelectorAll('input')].map((i) => i.placeholder || i.id).slice(0, 15) };
    }).catch(() => ({}));
    // To a file in the run's artifact, not the job log: the page can show member names.
    await writeFile(path.join(OUT, 'claims-search-diagnostics.json'), JSON.stringify(dump, null, 2)).catch(() => {});
    console.log('Claims search diagnostics saved to claims-search-diagnostics.json in the artifact.');
    throw err;
  }
}

const claimButtons = (page) => page.locator('td[data-label="Claim #"] button');

async function openFirstTexasClaim(page) {
  // A medical claim (TXC-), not a pharmacy one (TXR-): it has the lines the caption describes.
  const medical = claimButtons(page).filter({ hasText: /TXC-/ });
  await ((await medical.count()) ? medical.first() : claimButtons(page).first()).click();
  await page.waitForURL(/\/claims\/[^/?]+/, { timeout: 20000 });
  await page.waitForTimeout(2000);
}

async function openShadowReport(page) {
  // The latest run after a medical claim file; pharmacy runs have nothing to compare.
  const runs = page.locator('[data-testid="shadow-runs"] tbody tr');
  await runs.first().waitFor({ timeout: 15000 });
  // A cell holding a three-digit claim count marks a medical run (pharmacy runs compare a few dozen).
  const medical = runs.filter({ has: page.locator('td', { hasText: /^\s*[1-9]\d{2}\s*$/ }) })
    .locator('[data-testid="shadow-open-run"]');
  await ((await medical.count()) ? medical.first() : page.locator('[data-testid="shadow-open-run"]').first()).click();
  await scrollTo(page, '[data-testid="shadow-report"]');
}

async function openTexasPoststabilization(page) {
  // The list is empty until searched. seed-pa opens the poststabilization
  // request for the first synthetic member; the list links it by its number,
  // TXPA-<MMDDHHMM>-POSTSTAB. Earlier runs' requests are cancelled, so take the open one.
  const memberId = page.locator('.mud-input-control', { hasText: 'Member ID' }).locator('input').first();
  await memberId.waitFor({ timeout: 15000 });
  await memberId.fill(TX_MEMBER);
  await memberId.press('Tab'); // Blazor binds the field on change
  await page.getByRole('button', { name: /^\s*Search\s*$/ }).first().click();
  const link = page.locator('tr', { hasText: /TXPA-\d+-POSTSTAB/ }).filter({ hasNotText: /Cancel/ })
    .locator('[data-testid="authorization-link"]').last();
  await link.waitFor({ timeout: 20000 });
  await page.waitForTimeout(2500); // let the viewer see the result before opening it
  await link.click();
  await page.waitForLoadState('load');
}

// The shadow report says how many claims were skipped as "not found or not
// loaded from a core", so those steps do not treat "not found" as a failure.
const TEXAS = [
  { card: ['Texas Medicaid on Cloud Health Office',
    'A plan keeps its core system. CHO takes the core\'s nightly CPCDS file and adds CMS-0057-F APIs, a second opinion on every claim, and the Texas prior-authorization clocks. Every record shown is synthetic.'],
    caption: 'A two-minute tour of a synthetic Texas Medicaid plan.' },
  { path: '/edi-transactions', caption: 'The daily file drop: members and coverage load first, then medical and pharmacy claims and UM authorizations. Counts for each file, nothing refused.' },
  { path: `/members/${TX_MEMBER}/coverage`, caption: 'A STAR member loaded from the drop, with coverage mapped from the core\'s plan code.' },
  { path: '/claims', action: searchClaimsForMember, caption: 'This member\'s claims, as the core paid them. The same records serve Patient Access and Provider Access as CARIN Blue Button EOBs.' },
  { path: null, action: openFirstTexasClaim, caption: 'One claim, line by line, exactly as the core adjudicated it.' },
  { path: '/shadow-adjudication', action: openShadowReport, mayMentionNotFound: true,
    caption: 'Shadow adjudication: CHO re-adjudicates every loaded claim against the plan\'s own fee schedule and compares its answer with the core\'s.' },
  { path: null, mayMentionNotFound: true, captions: [
    { action: (page) => scrollTo(page, '[data-testid="shadow-by-procedure"]'),
      text: 'The finding: the core still pays therapeutic exercise (97110) at last year\'s rate, $23.45 a unit against $21.30. CHO shows the overpayment by code and by provider.' },
    { action: (page) => scrollTo(page, '[data-testid="shadow-gap-guide"]'),
      text: 'What CHO could not decide is listed with what to load next. A claims team works from the discrepancy CSV. The core is never changed.' },
  ] },
  { path: '/compliance/cms-0057f', caption: 'Prior authorization on the Texas clocks: Tex. Gov\'t Code ch. 540 alongside the federal timeframes. The earlier deadline wins.' },
  { path: '/authorizations', action: openTexasPoststabilization, caption: 'Poststabilization care for a hospitalized member: due one hour after receipt, under §540.0302(b)(3).' },
  { card: ['No core connector. No change to the core.',
    'The plan sends files it already produces. Cloud Health Office · cloudhealthoffice.com'],
    caption: 'Synthetic data only. Not a TMHP, HHSC or CMS certification.' },
];

// ── Shadow adjudication deep dive (Texas demo data) ─────────────────────────
// One finding followed from the run report to the discrepancy CSV, to a claim
// line as the core paid it, to the rate on the plan's fee schedule. Needs the
// Texas demo's `seed` (its medical claim file is shadowed as it loads).
const SHADOW_CODE = '97110';
const finding = {}; // the claim the video follows, read from the CSV while recording

// RFC 4180 rows: quoted fields may hold commas, quotes and line breaks.
function parseCsv(text) {
  const rows = [];
  let row = [];
  let field = '';
  let quoted = false;
  for (let i = 0; i < text.length; i += 1) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') { field += '"'; i += 1; } else if (c === '"') quoted = false; else field += c;
    } else if (c === '"') quoted = true;
    else if (c === ',') { row.push(field); field = ''; } else if (c === '\n' || c === '\r') {
      if (c === '\r' && text[i + 1] === '\n') i += 1;
      row.push(field); field = '';
      if (row.some((f) => f !== '')) rows.push(row);
      row = [];
    } else field += c;
  }
  row.push(field);
  if (row.some((f) => f !== '')) rows.push(row);
  return rows;
}

const escapeHtml = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

async function showDiscrepancyCsv(page) {
  // The file the report's "Discrepancies CSV" button downloads, fetched with the
  // same signed-in session and shown as a table (a download has nothing to film).
  const href = await page.locator('[data-testid="shadow-csv"]').getAttribute('href');
  if (!href) throw new Error('the report has no Discrepancies CSV link');
  const res = await page.request.get(new URL(href, page.url()).toString());
  if (!res.ok()) throw new Error(`discrepancies.csv answered HTTP ${res.status()}`);
  const rows = parseCsv(await res.text());
  const header = rows.shift() || [];
  const col = (name) => header.indexOf(name);
  const missing = ['ClaimNumber', 'LineNumber', 'ProcedureCode', 'CoreAllowed', 'ChoAllowed', 'PaidVariance', 'Differences']
    .filter((name) => col(name) < 0);
  // Column names only: the rows are claim data and stay out of the job log.
  if (missing.length) throw new Error(`discrepancies.csv has no ${missing.join(', ')} column; it has ${header.join(', ')}`);
  const lines = rows.filter((r) => r[col('ProcedureCode')] === SHADOW_CODE);
  if (!lines.length) throw new Error(`discrepancies.csv has ${rows.length} rows and none for ${SHADOW_CODE}`);
  finding.claim = lines[0][col('ClaimNumber')];
  const money = (v) => (v === '' ? '' : `${Number(v) < 0 ? '−' : ''}$${Math.abs(Number(v)).toFixed(2)}`);
  const share = lines.length === rows.length ? `all of them ${SHADOW_CODE}` : `${lines.length} of them ${SHADOW_CODE}`;
  const body = lines.slice(0, 8).map((r, i) => `<tr${i === 0 ? ' class="pick"' : ''}>
      <td class="mono">${escapeHtml(r[col('ClaimNumber')])}</td><td>${escapeHtml(r[col('LineNumber')])}</td>
      <td class="mono">${escapeHtml(r[col('ProcedureCode')])}</td><td class="num">${money(r[col('CoreAllowed')])}</td>
      <td class="num">${money(r[col('ChoAllowed')])}</td><td class="num">${money(r[col('PaidVariance')])}</td>
      <td>${escapeHtml(r[col('Differences')].replace(/;/g, ', '))}</td></tr>`).join('');
  await page.setContent(`<!doctype html><html><body style="margin:0;background:#f4f6f9;font-family:system-ui,sans-serif;color:#1f2933">
    <div style="max-width:1180px;margin:56px auto;background:#fff;border:1px solid #d5dde6;border-radius:10px;padding:28px 32px">
      <div style="font:600 15px system-ui;color:#0e7490">discrepancies.csv · downloaded from the shadow run report</div>
      <div style="font:700 30px system-ui;margin:6px 0 4px">${rows.length} differing claim lines, ${share}</div>
      <div style="font:400 17px system-ui;color:#52606d;margin-bottom:18px">One row per line where CHO's answer differs from the core's. The file also carries the source claim id, provider, outcomes and CHO's findings. First ${Math.min(8, lines.length)} ${SHADOW_CODE} rows:</div>
      <style>table{border-collapse:collapse;width:100%;font-size:18px}th,td{padding:9px 12px;border-bottom:1px solid #e4e9ef;text-align:left}
        th{font-size:15px;color:#52606d;font-weight:600}.num{text-align:right}.mono{font-family:ui-monospace,monospace}
        tr.pick td{background:#fff7e0;font-weight:600}</style>
      <table><thead><tr><th>Claim</th><th>Line</th><th>Code</th><th class="num">Core allowed</th><th class="num">CHO allowed</th>
        <th class="num">Paid difference</th><th>What differs</th></tr></thead><tbody>${body}</tbody></table>
    </div></body></html>`);
}

async function openFindingClaimLine(page) {
  if (!finding.claim) throw new Error('no claim was read from discrepancies.csv');
  await page.goto(`${BASE_URL}/claims/${encodeURIComponent(finding.claim)}/lines`, { waitUntil: 'load' });
  const line = page.locator('[data-testid="claim-tab-lines"] .mud-expand-panel', { hasText: SHADOW_CODE }).first();
  await line.waitFor({ timeout: 20000 });
  await line.locator('.mud-expand-panel-header').first().click();
  await page.waitForTimeout(1200);
  await scrollTo(page, '[data-testid="claim-tab-lines"]');
}

async function openPlanFeeSchedule(page) {
  await page.locator('.mud-tab', { hasText: /your schedules/i }).first().click();
  const row = page.locator('[data-testid="tenant-row"]', { hasText: /Medicaid STAR \(synthetic\)/ }).first();
  await row.waitFor({ timeout: 20000 });
  await page.waitForTimeout(1500);
  await row.click();
  const search = page.locator('input[data-testid="code-search"], [data-testid="code-search"] input').first();
  await search.waitFor({ timeout: 15000 });
  await page.waitForTimeout(2000); // the whole schedule first, then the one code
  await search.pressSequentially(SHADOW_CODE, { delay: 120 });
  await search.press('Enter');
  await page.locator('[data-testid="schedule-lines"] tbody tr', { hasText: SHADOW_CODE }).first().waitFor({ timeout: 15000 });
}

const SHADOW = [
  { card: ['One shadow-adjudication finding, traced to the rate',
    'From the run report to the discrepancy file, to the claim line the core paid, to the plan\'s own fee schedule. Every record shown is synthetic.'],
    caption: 'Shadow adjudication on a synthetic Texas Medicaid plan: one finding, followed to its source.' },
  { path: '/shadow-adjudication', action: openShadowReport, mayMentionNotFound: true,
    caption: 'The core\'s claim file came in last night\'s drop. CHO re-adjudicated every claim against the plan\'s fee schedule and compared the answers.' },
  { path: null, mayMentionNotFound: true, captions: [
    { action: (page) => scrollTo(page, '[data-testid="shadow-by-procedure"]', 'center'),
      text: `Every pricing difference is on one code, therapeutic exercise (${SHADOW_CODE}), billed by one therapy provider.` },
    { action: (page) => scrollTo(page, '[data-testid="shadow-results"]'),
      text: 'Claim by claim: what the core paid, what CHO would have paid, and why they differ.' },
  ] },
  { path: null, action: showDiscrepancyCsv,
    caption: 'The discrepancy CSV a claims team works from: one row per differing line. Follow the highlighted claim.' },
  { path: null, action: openFindingClaimLine,
    caption: `That claim as the core paid it: 2 units of ${SHADOW_CODE}, allowed $46.90. That is $23.45 a unit, last year's rate.` },
  { path: '/fee-schedules', action: openPlanFeeSchedule, captionFirst: true,
    caption: `The plan's 2026 fee schedule: $21.30 a unit. CHO allows $42.60 for 2 units. The core is $4.30 over on this line.` },
  { card: ['The core is never changed.',
    'CHO shows the difference, the claims behind it and the rate that explains it. The plan decides what to correct. Cloud Health Office · cloudhealthoffice.com'],
    caption: 'Synthetic data only. Not a TMHP, HHSC or CMS certification.' },
];

// ── CMS-0057-F prior authorization, end to end (Texas demo data) ────────────
// A provider office documents an MRI with the plan's DTR form in the provider
// portal, sends it as a PAS request, and the request is followed to its
// decision clock and the CMS-0057-F metrics. Needs the Texas demo's `seed`
// (member TXM7100001 and the provider NPIs). Each run files a new request.
const PA = {
  questionnaire: 'https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri|1.0.0',
  code: 'http://www.ama-assn.org/go/cpt|70553',
  npi: '1000010052', // North Texas Imaging Center (synthetic), from the Texas seed
  practice: 'North Texas Imaging Center (synthetic)',
  diagnoses: 'R51.9, R20.2',
};
const submitted = {}; // the authorization number PAS returned
const plannedDate = new Date(Date.now() + 7 * 864e5);

// CRD is called the way an EHR calls it at order signing: fhir-service's
// cds-services endpoint, with the member's active Coverage as prefetch. The
// recorder needs fhir-service (CRD_URL) and a CHO token (CHO_TOKEN); the
// workflow port-forwards one and mints the other.
const CRD_URL = (process.env.CRD_URL || '').replace(/\/$/, '');
const CHO_TOKEN = process.env.CHO_TOKEN || '';
const TENANT = process.env.TENANT_ID || 'demo';
const CRD_INFO = 'http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information';

async function crdCall(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: { Authorization: `Bearer ${CHO_TOKEN}`, 'X-Tenant-ID': TENANT, Accept: 'application/json',
      ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  // Status only: the bodies are member data and stay out of the job log.
  if (!res.ok) throw new Error(`${method} ${new URL(url).pathname} answered HTTP ${res.status}`);
  return res.json();
}

async function showCrdAnswer(page) {
  if (!CRD_URL || !CHO_TOKEN) throw new Error('CRD_URL and CHO_TOKEN are needed to call CRD (see the workflow)');
  const coverage = await crdCall('GET', `${CRD_URL}/fhir/r4/Coverage?patient=${TX_MEMBER}&status=active`);
  const planned = plannedDate.toISOString().slice(0, 10);
  const [system, code] = PA.code.split('|');
  const answer = await crdCall('POST', `${CRD_URL}/cds-services/cho-order-sign`, {
    hookInstance: crypto.randomUUID(), hook: 'order-sign',
    context: { userId: `Practitioner/${PA.npi}`, patientId: TX_MEMBER, draftOrders: { resourceType: 'Bundle', type: 'collection',
      entry: [{ resource: { resourceType: 'ServiceRequest', id: 'mri-order', status: 'draft', intent: 'order',
        code: { coding: [{ system, code, display: 'MRI brain without and with contrast' }] },
        subject: { reference: `Patient/${TX_MEMBER}` }, occurrenceDateTime: planned } }] } },
    prefetch: { coverage },
  });
  const card = (answer.cards || [])[0];
  if (!card) throw new Error('CRD answered with no card');
  const info = (answer.systemActions || []).flatMap((a) => a.resource?.extension || []).find((e) => e.url === CRD_INFO);
  const part = (name) => info?.extension?.find((e) => e.url === name);
  const form = part('questionnaire')?.valueCanonical || '';
  const rows = [
    ['Covered', part('covered')?.valueCode],
    ['Documentation needed', part('doc-needed')?.valueCode],
    ['Prior authorization', part('pa-needed')?.valueCode === 'indeterminate' ? 'not decided at order time' : part('pa-needed')?.valueCode],
    ['Form to complete (DTR)', form.replace(/^https?:\/\/[^/]+\/fhir\//, '')],
    ['Coverage assertion', part('coverage-assertion-id')?.valueString],
  ].filter(([, v]) => v);
  const color = card.indicator === 'critical' ? '#b42318' : card.indicator === 'warning' ? '#b54708' : '#0e7490';
  await page.setContent(`<!doctype html><html><body style="margin:0;background:#eef1f5;font-family:system-ui,sans-serif;color:#1f2933">
    <div style="max-width:1120px;margin:44px auto">
      <div style="font:600 15px system-ui;color:#52606d;letter-spacing:.04em;text-transform:uppercase">EHR · order signing · CDS Hooks order-sign</div>
      <div style="background:#fff;border:1px solid #d5dde6;border-radius:10px;padding:22px 28px;margin-top:10px">
        <div style="font:700 26px system-ui">MRI brain without and with contrast</div>
        <div style="font:400 18px system-ui;color:#52606d;margin-top:6px">CPT ${escapeHtml(code)} · planned ${escapeHtml(planned)} · member ${TX_MEMBER} · ordered by ${escapeHtml(PA.practice)}</div>
      </div>
      <div style="background:#fff;border:1px solid #d5dde6;border-left:8px solid ${color};border-radius:10px;padding:22px 28px;margin-top:18px">
        <div style="font:600 14px system-ui;color:#52606d">CARD FROM THE PLAN · Da Vinci CRD · Cloud Health Office</div>
        <div style="font:700 24px system-ui;color:${color};margin-top:6px">${escapeHtml(card.summary || '')}</div>
        <div style="font:400 18px system-ui;line-height:1.5;margin-top:8px">${escapeHtml(card.detail || '')}</div>
      </div>
      <div style="background:#fff;border:1px solid #d5dde6;border-radius:10px;padding:18px 28px;margin-top:18px">
        <div style="font:600 14px system-ui;color:#52606d;margin-bottom:6px">COVERAGE INFORMATION RETURNED WITH THE ORDER</div>
        <table style="border-collapse:collapse;width:100%;font-size:18px">${rows.map(([k, v]) => `<tr>
          <td style="padding:7px 0;color:#52606d;width:270px">${k}</td><td style="padding:7px 0;font-family:ui-monospace,monospace">${escapeHtml(v)}</td></tr>`).join('')}</table>
      </div>
    </div></body></html>`);
}

const dtrField = (page, linkId) => page.locator(`[data-testid="dtr-item-${linkId}"]`).first();
const inputIn = (scope) => scope.locator('input, textarea').first();
const byLabel = (page, label) => page.locator('.mud-input-control', { hasText: label }).locator('input, textarea').first();

async function typeSlowly(locator, text) {
  await locator.click();
  await locator.pressSequentially(text, { delay: 35 });
  await locator.press('Tab'); // Blazor binds on change
}

async function choose(page, scope, option) {
  await scope.locator('.mud-select').first().click();
  await page.locator('.mud-popover-open .mud-list-item', { hasText: option }).first().click();
  await page.waitForTimeout(500);
}

async function dtrFindMember(page) {
  const member = page.locator('input[data-testid="dtr-member-id"], [data-testid="dtr-member-id"] input').first();
  await member.waitFor({ timeout: 20000 });
  await typeSlowly(member, TX_MEMBER);
  await page.locator('[data-testid="dtr-find-member"]').click();
  try {
    await page.locator('[data-testid="dtr-form"]').first().waitFor({ timeout: 20000 });
  } catch (err) {
    // Which part of the page answered instead (member not found, no coverage,
    // no form, a service error); the messages hold no member data.
    const seen = await page.locator('[data-testid="dtr-member-not-found"], [data-testid="dtr-no-coverage"], [data-testid="dtr-error"], .mud-alert')
      .allInnerTexts().catch(() => []);
    throw new Error(`the MRI form did not open; the page shows: ${seen.join(' | ').slice(0, 400) || 'nothing'}`);
  }
  await page.waitForTimeout(800);
}

async function dtrAnswer(page) {
  await scrollTo(page, '[data-testid="dtr-form"]');
  await typeSlowly(inputIn(dtrField(page, '1')), 'Recurrent headaches with new focal numbness. Rule out a demyelinating lesion.');
  await choose(page, dtrField(page, '2'), 'Head');
  await choose(page, dtrField(page, '3'), 'Yes');
  if (await dtrField(page, '3a').count()) {
    await typeSlowly(inputIn(dtrField(page, '3a')), 'Six weeks of NSAIDs and physical therapy, without relief.');
  }
  await typeSlowly(inputIn(dtrField(page, '4')), PA.npi);
}

async function dtrSave(page) {
  await page.locator('[data-testid="dtr-save"]').click();
  try {
    await page.locator('[data-testid="dtr-pa-section"]').waitFor({ timeout: 20000 });
  } catch (err) {
    const seen = await page.locator('[data-testid="dtr-form-problem"], [data-testid="dtr-error"]').allInnerTexts().catch(() => []);
    throw new Error(`the answers were not saved: ${seen.join(' | ').slice(0, 400) || 'no message'}`);
  }
  await scrollTo(page, '[data-testid="dtr-pa-section"]');
  const npi = page.locator('input[data-testid="dtr-npi"], [data-testid="dtr-npi"] input').first();
  await typeSlowly(npi, PA.npi);
  await typeSlowly(byLabel(page, 'Provider or practice name'), PA.practice);
  await page.locator('.mud-checkbox', { hasText: 'organization' }).first().click();
  const planned = plannedDate.toLocaleDateString('en-US', { month: '2-digit', day: '2-digit', year: 'numeric' });
  await typeSlowly(byLabel(page, 'Planned service date'), planned);
  await page.keyboard.press('Escape'); // close the date picker
  await typeSlowly(byLabel(page, 'Diagnosis codes'), PA.diagnoses);
}

async function dtrSubmit(page) {
  await page.locator('[data-testid="dtr-submit"]').click();
  const answer = page.locator('[data-testid="dtr-submitted"], [data-testid="dtr-submit-problem"], [data-testid="dtr-error"]').first();
  await answer.waitFor({ timeout: 30000 });
  if (!(await page.locator('[data-testid="dtr-submitted"]').count())) {
    const seen = await page.locator('[data-testid="dtr-submit-problem"], [data-testid="dtr-error"]').allInnerTexts().catch(() => []);
    throw new Error(`the PAS request was not accepted: ${seen.join(' | ').slice(0, 400)}`);
  }
  submitted.number = ((await page.locator('[data-testid="dtr-submitted"] strong').first().textContent()) || '').trim();
  // The answer is the last thing on the page: make room to lift it above the caption.
  await page.evaluate(() => { document.body.style.paddingBottom = '260px'; });
  await scrollTo(page, '[data-testid="dtr-submitted"]', 'center');
}

async function openSubmittedAuthorization(page) {
  if (!submitted.number) throw new Error('no authorization number came back from the PAS request');
  const authId = page.locator('.mud-input-control', { hasText: 'Auth ID' }).locator('input').first();
  await authId.waitFor({ timeout: 15000 });
  await authId.fill(submitted.number);
  await authId.press('Tab');
  await page.getByRole('button', { name: /^\s*Search\s*$/ }).first().click();
  const link = page.locator('[data-testid="authorization-link"]').first();
  await link.waitFor({ timeout: 20000 });
  await page.waitForTimeout(2000);
  await link.click();
  await page.waitForURL(/\/authorizations\/[^/?]+/, { timeout: 20000 });
  const review = page.url().replace(/\/(overview|services|review|documents|claims|access-log)$/, '') + '/review';
  await page.goto(review, { waitUntil: 'load' });
  await page.locator('[data-testid="decision-timeframe"]').waitFor({ timeout: 20000 });
  submitted.record = review.replace(/\/review$/, '');
  await scrollTo(page, '[data-testid="decision-timeframe"]');
}

async function openSubmittedDocuments(page) {
  if (!submitted.record) throw new Error('the authorization record was not opened');
  await page.goto(`${submitted.record}/documents`, { waitUntil: 'load' });
  await page.locator('[data-testid="authorization-dtr-forms"]').waitFor({ timeout: 20000 });
}

const dtrUrl = `/dtr/questionnaire?questionnaire=${encodeURIComponent(PA.questionnaire)}&code=${encodeURIComponent(PA.code)}`;

const CMS0057_PA = [
  { card: ['CMS-0057-F prior authorization, end to end',
    'The EHR asks what an MRI needs (Da Vinci CRD), the office documents it (DTR) and sends it (PAS), and the plan\'s decision clock and public metrics follow. Every record shown is synthetic.'],
    caption: 'Prior authorization on a synthetic Texas Medicaid plan, from the provider\'s order to the published metrics.' },
  { path: null, action: showCrdAnswer,
    caption: 'The doctor signs an MRI order and the EHR asks the plan\'s CRD service what it needs. The answer: documentation, on the plan\'s MRI form.' },
  { path: dtrUrl, action: dtrFindMember, captionFirst: true,
    caption: 'The office opens that form (Da Vinci DTR) in the plan\'s provider portal and finds the member. EHRs get the same form through the DTR API.' },
  { path: null, action: dtrAnswer, captionFirst: true,
    caption: 'The office answers the plan\'s questions. Answers are typed, never pulled from a chart, and saved as a FHIR QuestionnaireResponse.' },
  { path: null, action: dtrSave, captionFirst: true,
    caption: 'Then the request itself: the ordering provider, the MRI, the planned date and the diagnoses.' },
  { path: null, action: dtrSubmit, captionFirst: true,
    caption: 'Sent as a Da Vinci PAS request. The plan answers at once: received, documentation attached, pended for clinical review.' },
  { path: '/authorizations', action: openSubmittedAuthorization,
    caption: 'The decision clock starts at receipt. For a Texas Medicaid member the Texas clock (3 business days) and the federal one (7 days) both run, and the earlier deadline applies.' },
  { path: null, action: openSubmittedDocuments,
    caption: 'The DTR answers travel with the request, so the reviewer sees exactly what the office sent.' },
  { path: '/compliance/cms-0057f', captions: [
    { action: (page) => scrollTo(page, '[data-testid="clocks"]'),
      text: 'Every open request on its clock, with the ones at risk and overdue flagged before the deadline passes.' },
    { action: async (page) => {
      await page.locator('.mud-tab', { hasText: /public metrics/i }).first().click();
      await scrollTo(page, '[data-testid="metrics"]', 'center');
    },
      text: 'The prior authorization metrics CMS-0057-F requires plans to post publicly each year, from the same records.' },
  ] },
  { card: ['Request, documentation, decision clock, public metrics.',
    'One record from the provider\'s form to the CMS-0057-F report. Cloud Health Office · cloudhealthoffice.com'],
    caption: 'Synthetic data only. Not a TMHP, HHSC or CMS certification.' },
];

const WALKTHROUGHS = {
  'prior-auth': PRIOR_AUTH, 'full-tour': FULL_TOUR, texas: TEXAS,
  'cms0057-prior-auth': CMS0057_PA, 'shadow-adjudication': SHADOW,
};

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

  const captionsOf = (step) => (step.captions || [step.caption]).map((c) => (typeof c === 'string' ? { text: c } : c));
  const total = steps.reduce((n, step) => n + captionsOf(step).length, 0);
  let shown = 0;
  const attempt = async (label, action) => {
    try {
      await action(page);
      await page.waitForTimeout(1500);
    } catch (err) {
      failures.push(`${label}: ${err.message.split('\n')[0]}`);
    }
  };
  for (const [i, step] of steps.entries()) {
    const label = step.card ? 'title' : step.path || steps.slice(0, i).reverse().find((s) => s.path)?.path || '/';
    let response = null;
    if (step.card) {
      await titleCard(page, ...step.card);
    } else if (step.path) {
      response = await page.goto(`${BASE_URL}${step.path}`, { waitUntil: 'load' });
      // Blazor renders after the circuit connects; give it a moment to fill the page.
      await page.waitForTimeout(2500);
    }
    // A captionFirst step puts its caption up before it acts, so typing and
    // clicking are filmed under the caption that describes them.
    if (step.captionFirst) await showCaption(page, captionsOf(step)[0].text, shown + 1, total);
    if (step.action) await attempt(label, step.action);
    if (!step.card) {
      const body = (await page.textContent('body')) || '';
      if ((step.path && (!response || response.status() >= 400))
          || (step.mayMentionNotFound ? /could not be loaded|error occurred|unable to connect|failed to load/i
            : /not found|could not be loaded|error occurred|unable to connect|failed to load/i).test(body)) {
        failures.push(`${label} (HTTP ${response ? response.status() : 'n/a'})`);
      }
    }
    for (const caption of captionsOf(step)) {
      if (caption.action) await attempt(label, caption.action);
      shown += 1;
      await showCaption(page, caption.text, shown, total);
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
