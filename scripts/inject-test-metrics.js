#!/usr/bin/env node

/**
 * inject-test-metrics.js
 *
 * Reads test-metrics.json (produced by the test-metrics workflow) and injects
 * the real test count and coverage values into static HTML files and markdown
 * before deployment.
 *
 * Usage:
 *   node scripts/inject-test-metrics.js [path-to-metrics-json]
 *
 * If no metrics file is found, the script exits gracefully and leaves files
 * unchanged (the existing static values remain as-is until the next
 * test-metrics workflow run produces an artifact).
 *
 * This script is called by:
 *   - deploy-static-site.yml (before site deployment)
 *   - The site Dockerfile build stage
 */

const fs = require('fs');
const path = require('path');

const METRICS_PATH = process.argv[2] || 'test-metrics.json';

// ── Files to update ──────────────────────────────────────────────────
const TARGETS = [
  { file: 'src/site/index.html', type: 'html' },
  { file: 'src/site/cms-0057f-compliance.html', type: 'html' },
  { file: 'README.md', type: 'markdown' },
  { file: 'docs/guides/FEATURES.md', type: 'markdown' },
];

// ── Load metrics ─────────────────────────────────────────────────────
let metrics;
if (fs.existsSync(METRICS_PATH)) {
  metrics = JSON.parse(fs.readFileSync(METRICS_PATH, 'utf8'));
  const totalCount = metrics.summary.total_tests;
  const coverageResult = metrics.summary.coverage_pct;
  console.log('Loaded metrics from ' + METRICS_PATH);
  console.log('  Total count: ' + totalCount);
  console.log('  Coverage result: ' + coverageResult + '%');
} else {
  console.log('No metrics file found at ' + METRICS_PATH + ', using fallback count...');
  metrics = null;
}

const totalTests = metrics ? metrics.summary.total_tests : null;
const coveragePct = metrics ? metrics.summary.coverage_pct : null;

if (!totalTests) {
  console.log('No test count available, skipping injection.');
  process.exit(0);
}

const formattedCount = totalTests.toLocaleString('en-US');
const coverageStr = coveragePct ? `${coveragePct}%` : null;

console.log('\nInjecting: ' + formattedCount + ' tests, ' + (coverageStr || 'N/A') + ' coverage\n');

let filesUpdated = 0;

for (const target of TARGETS) {
  const filePath = path.resolve(target.file);
  if (!fs.existsSync(filePath)) {
    console.log('  SKIP: ' + target.file + ' (not found)');
    continue;
  }

  let content = fs.readFileSync(filePath, 'utf8');
  const originalContent = content;

  if (target.type === 'html') {
    // Replace patterns like "1,018 Tests" or "531 Tests" with actual count
    content = content.replace(
      /(\d{1,3}(?:,\d{3})*)\s+Tests?\b/g,
      `${formattedCount} Tests`
    );

    // Replace patterns like "1,018 comprehensive tests"
    content = content.replace(
      /(\d{1,3}(?:,\d{3})*)\s+comprehensive\s+tests/g,
      `${formattedCount} comprehensive tests`
    );

    // Replace coverage percentages like "85.93% coverage"
    if (coverageStr) {
      content = content.replace(
        /\d+\.\d+%\s+coverage/g,
        `${coverageStr} coverage`
      );
    }

    // Replace stat-number elements containing just a number (for the stats section)
    // Pattern: <span class="stat-number">1,018</span> followed by Tests label
    content = content.replace(
      /(<span class="stat-number">)(\d{1,3}(?:,\d{3})*)(<\/span>\s*<span class="stat-label">Tests)/g,
      `$1${formattedCount}$3`
    );

    // Replace stat-num elements (cms-0057f page)
    content = content.replace(
      /(<div class="stat-num[^"]*">)(\d{1,3}(?:,\d{3})*)(<\/div>\s*<div class="stat-label">Tests)/g,
      `$1${formattedCount}$3`
    );

    // Replace badge content like <span class="badge green">531 Tests</span>
    content = content.replace(
      /(<span class="badge[^"]*">)(\d{1,3}(?:,\d{3})*)\s+Tests(<\/span>)/g,
      `$1${formattedCount} Tests$3`
    );
  }

  if (target.type === 'markdown') {
    // Replace badge URLs: tests-973%20passing → tests-{count}%20passing
    content = content.replace(
      /tests-\d+%20passing/g,
      `tests-${totalTests}%20passing`
    );

    // Replace coverage badge: coverage-85.93%25 → coverage-{pct}%25
    if (coverageStr) {
      content = content.replace(
        /coverage-\d+\.\d+%25/g,
        `coverage-${coveragePct}%25`
      );
    }

    // Replace table row: |Automated Tests|973|
    content = content.replace(
      /(\|Automated Tests\s*\|)\d+/g,
      `$1${totalTests}`
    );

    // Replace "973 tests" or "1,018 automated tests" in prose
    content = content.replace(
      /(\d{1,3}(?:,\d{3})*)\s+automated\s+tests/g,
      `${formattedCount} automated tests`
    );

    // Replace coverage in parenthetical: (85.93% coverage)
    if (coverageStr) {
      content = content.replace(
        /\(\d+\.\d+%\s+coverage\)/g,
        `(${coverageStr} coverage)`
      );
    }
  }

  if (content !== originalContent) {
    fs.writeFileSync(filePath, content, 'utf8');
    console.log('  UPDATED: ' + target.file);
    filesUpdated++;
  } else {
    console.log('  NO CHANGE: ' + target.file);
  }
}

console.log('\nDone. ' + filesUpdated + ' file(s) updated.');
