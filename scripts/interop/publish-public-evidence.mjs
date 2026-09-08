#!/usr/bin/env node
//
// Project the Da Vinci external interoperability evidence into a public-safe
// snapshot for the site.
//
//   node scripts/interop/publish-public-evidence.mjs \
//     [--run artifacts/interop/run.json] \
//     [--out src/site/insights/cms-0057-f/davinci-interop-public-evidence.json]
//
// Two inputs, with different standing:
//
//   interop/scenarios.json + interop/versions.json   committed source of truth.
//       Which scenarios exist, which are implemented, and exactly which external
//       implementation each is pinned against. Always present.
//
//   artifacts/interop/run.json                       a harness run's outcome.
//       Optional. Absent when this script runs outside CI, or before the
//       harness has executed on main. Its absence is published as
//       `latestRun: null` — never as an empty pass.
//
// The projection is an ALLOW-LIST, not a redaction pass. Only the fields named
// below reach the site, so a field added upstream to the private run document
// cannot become public by accident. (The harness already redacts credentials
// and sends only synthetic data; this is the second boundary, not the first.)
//
// What this deliberately does NOT do: merge with, reconcile against, or roll up
// into CMS-0057-F acceptance evidence. The vocabularies differ on purpose —
// Passed/Failed/Skipped/NotRun here, PASSABLE/PARTIAL/GAP there — and the two
// documents are published side by side, never added together.

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
  arg('--out', 'src/site/insights/cms-0057-f/davinci-interop-public-evidence.json')
);

const readJson = (p) => JSON.parse(readFileSync(p, 'utf8'));

const scenarios = readJson(join(repoRoot, 'interop/scenarios.json'));
const versions = readJson(join(repoRoot, 'interop/versions.json'));
const run = existsSync(runPath) ? readJson(runPath) : null;

// ── Source revision ────────────────────────────────────────────────────────
// The run document names the commit the harness actually executed at. Outside a
// run, fall back to the commit this snapshot was generated from — labelled as
// such on the site, so "the inventory as of" is never read as "tested at".
function currentCommit() {
  if (process.env.GITHUB_SHA) return process.env.GITHUB_SHA;
  try {
    return execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repoRoot })
      .toString()
      .trim();
  } catch {
    return null;
  }
}

const sourceCommit = run?.choCommit || currentCommit();

// ── Targets: the pinned external implementations ───────────────────────────
// `pin.reference` is the immutable coordinate (image digest, or tag plus
// commit). `sourceCommit` is what that artifact was built from. Both are
// published: a reader must be able to fetch exactly what was tested.
const executedTargets = new Set(
  (run?.targets || []).flatMap((t) => (t.results || []).map(() => t.key || t.name))
);

const targets = versions.targets.map((t) => ({
  name: t.name,
  key: t.key,
  role: t.role,
  protocols: t.protocols || [],
  upstreamRepository: t.upstreamRepository,
  license: t.license,
  pin: {
    kind: t.pin.kind,
    reference: t.pin.reference,
    sourceCommit: t.pin.sourceCommit || t.pin.commit || null
  },
  implementationGuides: t.implementationGuides || {},
  igVersionProvenance: t.igVersionProvenance || null,
  // "Executed" only where a scenario in the inventory is marked implemented
  // against this target. A pinned-but-undriven target says so.
  exercised: scenarios.scenarios.some((s) => s.externalTarget === t.key && s.implemented === true)
}));

// ── Scenario rows ──────────────────────────────────────────────────────────
// Status resolution, in order:
//   1. a result in the published run  -> that result's status
//   2. implemented, no published run  -> NotPublished
//   3. not implemented                -> NotImplemented
//
// `NotPublished` exists so a scenario that the harness does execute in CI is
// never displayed as though it had failed or been skipped, and never displayed
// as though it had passed either.
const runResultsById = new Map();
for (const target of run?.targets || []) {
  for (const result of target.results || []) {
    runResultsById.set(result.scenarioId, { result, target });
  }
}
for (const result of run?.notRunScenarios || []) {
  if (!runResultsById.has(result.scenarioId)) {
    runResultsById.set(result.scenarioId, { result, target: null });
  }
}

const scenarioRows = scenarios.scenarios.map((s) => {
  const hit = runResultsById.get(s.id);
  const status = hit ? hit.result.status : s.implemented ? 'NotPublished' : 'NotImplemented';
  return {
    id: s.id,
    title: s.title,
    protocol: s.protocol,
    choRole: s.choRole,
    externalTarget: s.externalTarget,
    implemented: s.implemented === true,
    linkedFromScenario: s.linkedFromScenario || null,
    description: s.description,
    status,
    // Only present once a run has been published: what the exchange chained to.
    linkedArtifact: hit?.result?.linkedArtifact || null,
    testedVersion: hit?.target?.version || null
  };
});

// ── Latest published run ───────────────────────────────────────────────────
// Findings are published with their coded identity and summary. They carry no
// PHI by construction (the harness sends synthetic data only) and the observed
// values are canonical URLs and version strings — the substance a reader needs
// to check a mismatch for themselves.
const latestRun = run
  ? {
      generatedAtUtc: run.generatedAtUtc,
      environment: run.environment,
      choCommit: run.choCommit || null,
      choCommitUrl: run.choCommit ? `${repositoryUrl}/commit/${run.choCommit}` : null,
      dataClassification: run.dataClassification || 'synthetic',
      summary: {
        passed: run.summary?.passed ?? 0,
        failed: run.summary?.failed ?? 0,
        skipped: run.summary?.skipped ?? 0,
        notRun: run.summary?.notRun ?? 0,
        total: run.summary?.total ?? 0
      },
      findings: (run.findings || []).map((f) => ({
        code: f.code,
        severity: f.severity,
        summary: f.summary,
        choObserved: f.choObserved ?? null,
        externalObserved: f.externalObserved ?? null
      }))
    }
  : null;

const snapshot = {
  schemaVersion: 1,
  // Never "cms0057". A reader (or a script) can tell the two documents apart
  // from the first field.
  evidenceKind: 'davinci-interoperability',
  generatedAtUtc: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'),
  sourceCommit,
  sourceCommitShort: sourceCommit ? sourceCommit.slice(0, 12) : null,
  sourceCommitUrl: sourceCommit ? `${repositoryUrl}/commit/${sourceCommit}` : null,
  inventoryReviewedUtc: versions.lastReviewedUtc || null,
  dataClassification: 'synthetic',
  relationshipToCmsAcceptance:
    run?.relationshipToCmsAcceptance ||
    'Independent of CMS-0057-F acceptance evidence. External interoperability results never change a ' +
      'CMS-0057-F scenario status, and CMS acceptance statuses never imply an interoperability result.',
  targets,
  scenarios: scenarioRows,
  latestRun,
  disclaimers: [
    'External interoperability evidence shows that Cloud Health Office exchanged standards-conformant ' +
      'requests and responses with an independently developed implementation it does not own. It is not ' +
      'CMS certification, ONC certification, or an HL7 endorsement.',
    'Every exchange uses synthetic data against a pinned reference implementation. A pin represents the ' +
      'version tested, not every implementation of the same specification.',
    'Protocol interoperability is not payer rule parity: two payers running different rule content can ' +
      'interoperate perfectly and still reach different coverage decisions.'
  ]
};

mkdirSync(dirname(outPath), { recursive: true });
writeFileSync(outPath, `${JSON.stringify(snapshot, null, 2)}\n`);

const executed = scenarioRows.filter((s) => s.implemented).length;
console.log(
  `Wrote ${outPath}\n` +
    `  scenarios: ${scenarioRows.length} (${executed} implemented)\n` +
    `  targets:   ${targets.length} (${targets.filter((t) => t.exercised).length} exercised)\n` +
    `  latestRun: ${latestRun ? `${latestRun.summary.passed} passed / ${latestRun.summary.failed} failed` : 'none published'}`
);

if (executedTargets.size === 0 && run) {
  console.warn('Warning: the run document carried no target results.');
}
