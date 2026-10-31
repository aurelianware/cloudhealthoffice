#!/usr/bin/env node
//
// Project the Inferno conformance kit results into a public-safe snapshot for
// the site's /evidence/inferno-conformance page.
//
//   node scripts/interop/publish-inferno-evidence.mjs \
//     [--run artifacts/interop/run.json] \
//     [--out src/site/insights/cms-0057-f/inferno-conformance-public-evidence.json] \
//     [--badges artifacts/interop/badges]
//
// --badges also writes one shields.io endpoint document per kit
// (inferno-<kit>.json), published to ci-evidence/badges/ for the README. A
// badge says what the kit reported and nothing more: there is no official
// Inferno badge, and none of these imply certification.
//
// A sibling of publish-public-evidence.mjs, kept as a separate document for one
// reason: the kits only run on the nightly schedule and on a manual dispatch,
// while the Da Vinci exchanges run on every push. One shared document would
// have every push overwrite the last nightly's verdicts with "not run". So the
// Inferno snapshot is written only from a run whose inferno job produced
// evidence, and the push-triggered publish never touches it.
//
// Inputs:
//
//   interop/scenarios.json + interop/versions.json   committed source of truth.
//       Which kit scenarios exist, which groups each runs, the kit pins, and the
//       tests recorded as known kit issues (infernoKnownKitIssues). Always present.
//
//   artifacts/interop/run.json                       the inferno job's outcome.
//       Optional. Its absence is published as `latestRun: null` and every kit as
//       NotPublished, never as an empty pass.
//
// Like its sibling, the projection is an ALLOW-LIST. A failing test is
// published as its test id and the first sentence Inferno reported for it. The
// validator messages and the CHO response bodies the harness appends to a
// finding stay in the CI artifact: they are diagnostic, they name internal
// container hosts, and a reader does not need them to see what failed.
//
// What this does NOT do: merge with, or roll up into, CMS-0057-F acceptance
// evidence or the Da Vinci interoperability snapshot. An Inferno pass is not
// certification and changes no CMS-0057-F scenario status.

import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const repoRoot = resolve(fileURLToPath(new URL('../..', import.meta.url)));
const repositoryUrl = 'https://github.com/aurelianware/cloudhealthoffice';

function arg(name, fallback) {
  const i = process.argv.indexOf(name);
  return i !== -1 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
}

const runPath = resolve(repoRoot, arg('--run', 'artifacts/interop/run.json'));
const outPath = resolve(
  repoRoot,
  arg('--out', 'src/site/insights/cms-0057-f/inferno-conformance-public-evidence.json')
);

const badgesDir = arg('--badges', null);

const readJson = (p) => JSON.parse(readFileSync(p, 'utf8'));

const scenarios = readJson(join(repoRoot, 'interop/scenarios.json'));
const versions = readJson(join(repoRoot, 'interop/versions.json'));
const run = existsSync(runPath) ? readJson(runPath) : null;

function currentCommit() {
  if (process.env.GITHUB_SHA) return process.env.GITHUB_SHA;
  try {
    return execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repoRoot }).toString().trim();
  } catch {
    return null;
  }
}

const sourceCommit = run?.choCommit || currentCommit();
const workflowRunUrl =
  process.env.GITHUB_RUN_ID && process.env.GITHUB_REPOSITORY
    ? `https://github.com/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}`
    : null;

// The CMS-0057-F API each kit speaks to, and why. Kept here rather than in the
// page so the JSON says on its own what a kit result is evidence about.
const REGULATORY_CONTEXT = {
  'US Core':
    'The ONC API standards at 45 CFR 170.215 that CMS-0057-F and CMS-9115-F require payer FHIR APIs to use; ' +
    'US Core is the base profile set the Patient Access, Provider Access and Payer-to-Payer APIs build on.',
  'CARIN BB':
    'The Patient Access API (claims and encounter data) under CMS-9115-F, expanded by CMS-0057-F. CARIN Blue ' +
    'Button is the implementation guide CMS recommends for that data.',
  'Plan-Net':
    'The Provider Directory API under CMS-9115-F, which must be publicly accessible without authentication. ' +
    'Da Vinci PDex Plan-Net is the implementation guide CMS recommends.',
  'Bulk Data':
    'The Provider Access and Payer-to-Payer APIs CMS-0057-F requires by January 1, 2027, which must support ' +
    'FHIR Bulk Data Access, with the calling system authorized by SMART Backend Services.',
  PDex:
    'The Payer-to-Payer API CMS-0057-F requires by January 1, 2027: a new payer finds a member at the prior payer ' +
    'with the HRex $member-match operation, under the member\'s consent. Da Vinci PDex is the implementation ' +
    'guide CMS recommends.'
};

// Groups the kit declares without an id are named by the runnable id Inferno
// gives them, which means nothing to a reader. Publish what they contain.
const GROUP_LABELS = {
  'c4bb_v200-Group02':
    'C4BB FHIR API Tests (CapabilityStatement, Patient, ExplanationOfBenefit, Coverage, Organization, Practitioner, RelatedPerson)'
};

// ── Inferno test identity ──────────────────────────────────────────────────
// Inferno ids are suite-group-test. A finding names a test, a group or the
// suite; only tests are counted, because a group's row fails whenever any test
// under it does and would double count.
function parseFinding(summary) {
  const match = /^Inferno reported '([^']+)' for (\S+?):?\s+(?:a known kit issue|([\s\S]*))$/.exec(summary || '');
  if (!match) return null;
  const fullId = match[2].replace(/,$/, '');
  const segments = fullId.split('-');
  const test = segments[segments.length - 1];
  const isTest = test.endsWith('_test') || segments.length >= 3;
  const headline = (match[3] || '').split(' | ')[0].trim();
  return { result: match[1], test, isTest, message: clip(internalHosts(headline), 280) };
}

// The stack runs on a private compose network, so a URL Inferno quotes names a
// container (http://cho-fhir-service:8080/...). The host means nothing to a
// reader; the path is the useful part.
function internalHosts(text) {
  return String(text || '').replace(/https?:\/\/[a-z0-9_-]+(?::\d+)?(?=\/|\s|$)/gi, '{base}');
}

function clip(text, length) {
  const flat = String(text || '').replace(/\s+/g, ' ').trim();
  return flat.length <= length ? flat : `${flat.slice(0, length)}…`;
}

const resultsById = new Map();
for (const target of run?.targets || []) {
  for (const result of target.results || []) {
    resultsById.set(result.scenarioId, { result, target });
  }
}

const targetsByKey = new Map(versions.targets.map((t) => [t.key, t]));

const kitScenarios = scenarios.scenarios.filter(
  (s) => s.implemented === true && s.infernoSuite && s.choRole === 'Server'
);

const kits = kitScenarios.map((s) => {
  const pin = targetsByKey.get(s.externalTarget) || {};
  const hit = resultsById.get(s.id);
  const findings = hit?.result?.findings || [];
  const knownIssues = s.infernoKnownKitIssues || [];

  const failingTests = [];
  const notRunTests = [];
  const seen = new Set();
  for (const f of findings) {
    if (f.code !== 'inferno.result.failed' && f.code !== 'inferno.result.notRun') continue;
    const parsed = parseFinding(f.summary);
    if (!parsed || !parsed.isTest || seen.has(parsed.test)) continue;
    seen.add(parsed.test);
    (f.code === 'inferno.result.failed' ? failingTests : notRunTests).push({
      test: parsed.test,
      result: parsed.result,
      message: parsed.message
    });
  }

  const knownKitIssueFailures = new Set(
    findings
      .filter((f) => f.code === 'inferno.result.knownKitIssue')
      .map((f) => parseFinding(f.summary)?.test)
      .filter(Boolean)
  );

  // Test-level counts are taken from the harness when it records them. Without
  // them only what did not pass is known, so no total and no pass count is
  // invented.
  const counts = hit?.result?.infernoTests
    ? {
        passed: hit.result.infernoTests.passed ?? 0,
        failed: hit.result.infernoTests.failed ?? 0,
        skipped: hit.result.infernoTests.skipped ?? 0,
        omitted: hit.result.infernoTests.omitted ?? 0,
        knownKitIssue: hit.result.infernoTests.knownKitIssue ?? 0,
        total: hit.result.infernoTests.total ?? 0
      }
    : null;

  return {
    scenarioId: s.id,
    title: s.title,
    protocol: s.protocol,
    regulatoryContext: REGULATORY_CONTEXT[s.protocol] || null,
    kit: {
      name: pin.name || s.externalTarget,
      key: s.externalTarget,
      upstreamRepository: pin.upstreamRepository || null,
      tag: pin.pin?.tag || null,
      commit: pin.pin?.commit || pin.pin?.sourceCommit || null,
      reference: pin.pin?.reference || null,
      implementationGuides: pin.implementationGuides || {}
    },
    suite: s.infernoSuite,
    // An empty list means the whole suite was run.
    groups: (s.infernoGroups || []).map((g) => GROUP_LABELS[g] || g),
    // NotPublished: no kit run has been published. NotRun: a run was, and it
    // carried no result for this kit.
    status: hit ? hit.result.status : run ? 'NotRun' : 'NotPublished',
    statusReason: hit?.result?.statusReason || null,
    testedAtUtc: hit?.result?.endedAtUtc || null,
    tests: counts,
    failingTests,
    notRunTests,
    knownKitIssues: knownIssues.map((k) => ({
      test: k.test,
      reason: k.reason,
      failedThisRun: knownKitIssueFailures.has(k.test)
    }))
  };
});

const latestRun = run
  ? {
      generatedAtUtc: run.generatedAtUtc,
      environment: run.environment,
      choCommit: run.choCommit || null,
      choCommitUrl: run.choCommit ? `${repositoryUrl}/commit/${run.choCommit}` : null,
      workflowRunUrl,
      dataClassification: run.dataClassification || 'synthetic',
      summary: {
        passed: kits.filter((k) => k.status === 'Passed').length,
        failed: kits.filter((k) => k.status === 'Failed').length,
        skipped: kits.filter((k) => k.status === 'Skipped').length,
        notRun: kits.filter((k) => k.status === 'NotRun' || k.status === 'NotPublished').length,
        total: kits.length
      }
    }
  : null;

const snapshot = {
  schemaVersion: 1,
  evidenceKind: 'inferno-conformance',
  generatedAtUtc: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'),
  sourceCommit,
  sourceCommitShort: sourceCommit ? sourceCommit.slice(0, 12) : null,
  sourceCommitUrl: sourceCommit ? `${repositoryUrl}/commit/${sourceCommit}` : null,
  dataClassification: 'synthetic',
  relationshipToCmsAcceptance:
    'Independent of CMS-0057-F acceptance evidence and of the Da Vinci interoperability snapshot. A kit result ' +
    'never changes a CMS-0057-F scenario status, and no result here is CMS, ONC or HL7 certification.',
  kits,
  latestRun,
  disclaimers: [
    'An Inferno test kit encodes its authors’ reading of an implementation guide at one release. A pass ' +
      'means the API met the requirements that kit version tests, in the groups that were run, at the named ' +
      'source revision. It is not certification by CMS, ONC, HL7 or the Inferno project.',
    'Every run uses synthetic data against a Cloud Health Office stack started for the run. No production ' +
      'member data is involved.',
    'A known kit issue is a test that cannot judge any server as the kit is written. It is listed with its ' +
      'reason, its failure is still recorded, and it is not counted against the result.'
  ]
};

mkdirSync(dirname(outPath), { recursive: true });
writeFileSync(outPath, `${JSON.stringify(snapshot, null, 2)}\n`);

const BADGE_LABELS = {
  'US Core': 'Inferno US Core',
  'CARIN BB': 'Inferno CARIN BB',
  'Plan-Net': 'Inferno Plan-Net',
  'Bulk Data': 'Inferno Bulk Data',
  PDex: 'Inferno PDex'
};

function badgeFor(kit) {
  const t = kit.tests;
  if (kit.status === 'Passed') {
    // Excused tests stay visible on the badge, so a pass never reads as more
    // than the kit actually judged.
    const excused = t?.knownKitIssue ? ` · ${t.knownKitIssue} kit issues excused` : '';
    return { message: t ? `passed ${t.passed}/${t.passed + t.failed}${excused}` : 'passed', color: 'brightgreen' };
  }
  if (kit.status === 'Failed') {
    return {
      message: t ? `${t.failed} failing of ${t.passed + t.failed}` : 'failed',
      color: 'red'
    };
  }
  return { message: 'no result yet', color: 'lightgrey' };
}

if (badgesDir) {
  const dir = resolve(repoRoot, badgesDir);
  mkdirSync(dir, { recursive: true });
  for (const kit of kits) {
    const { message, color } = badgeFor(kit);
    const name = `inferno-${kit.kit.key.replace(/^inferno-/, '')}.json`;
    writeFileSync(
      join(dir, name),
      `${JSON.stringify({ schemaVersion: 1, label: BADGE_LABELS[kit.protocol] || `Inferno ${kit.protocol}`, message, color })}\n`
    );
  }
}

console.log(
  `Wrote ${outPath}\n` +
    kits
      .map((k) => `  ${k.scenarioId}: ${k.status} (${k.failingTests.length} failing tests published)`)
      .join('\n') +
    `\n  latestRun: ${latestRun ? latestRun.generatedAtUtc : 'none published'}`
);

if (run && resultsById.size === 0) {
  console.warn('Warning: the run document carried no kit results.');
}
