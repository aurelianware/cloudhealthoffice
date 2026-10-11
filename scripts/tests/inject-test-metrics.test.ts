/**
 * scripts/inject-test-metrics.js
 * ------------------------------
 * Runs the script against a throwaway copy of the files it rewrites, so the
 * regexes are checked end to end without touching the real repository.
 */

import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

const SCRIPT = path.join(__dirname, '..', 'inject-test-metrics.js');

const FEATURES_ROW =
  '| **Testing** | 13,359 automated tests across 51 test projects (count injected by CI) | ✅ Complete | [CONTRIBUTING.md](./CONTRIBUTING.md) |';

function write(root: string, relative: string, content: string): void {
  const full = path.join(root, relative);
  fs.mkdirSync(path.dirname(full), { recursive: true });
  fs.writeFileSync(full, content, 'utf8');
}

function makeRepo(projects: string[]): string {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'inject-metrics-'));
  write(root, 'docs/guides/FEATURES.md', `# Features\n\n${FEATURES_ROW}\n\nOther text across 7 test projects stays a count too.\n`);
  write(
    root,
    'test-metrics.json',
    JSON.stringify({ summary: { total_tests: 14001, coverage_pct: 61.5 } })
  );
  for (const project of projects) {
    write(root, project, '<Project Sdk="Microsoft.NET.Sdk" />');
  }
  return root;
}

function run(root: string): string {
  execFileSync(process.execPath, [SCRIPT, 'test-metrics.json'], { cwd: root, stdio: 'pipe' });
  return fs.readFileSync(path.join(root, 'docs/guides/FEATURES.md'), 'utf8');
}

describe('inject-test-metrics.js', () => {
  let root: string;

  afterEach(() => {
    if (root) fs.rmSync(root, { recursive: true, force: true });
  });

  it('updates the test count and the "across NN test projects" suffix', () => {
    root = makeRepo([
      'src/a/A.Tests.csproj',
      'src/b/nested/B.Tests.csproj',
      'tests/C.Tests/C.Tests.csproj',
    ]);

    const features = run(root);

    expect(features).toContain('14,001 automated tests across 3 test projects (count injected by CI)');
    expect(features).toContain('across 3 test projects stays a count too.');
    expect(features).not.toContain('across 51 test projects');
  });

  it('counts only *.Tests.csproj under src/ and tests/, like test-metrics.yml', () => {
    root = makeRepo([
      'src/a/A.Tests.csproj',
      'tests/E2E/CloudHealthOffice.E2E.csproj',
      'tests/Load/CloudHealthOffice.LoadTests.csproj',
      'tests/Fl/CloudHealthOffice.FlAhca.E2ETests.csproj',
      'tools/Other.Tests/Other.Tests.csproj',
    ]);

    expect(run(root)).toContain('across 1 test projects');
  });

  it('leaves the rest of the line unchanged', () => {
    root = makeRepo(['src/a/A.Tests.csproj', 'src/b/B.Tests.csproj']);

    const row = run(root).split('\n').find((line) => line.startsWith('| **Testing**'));

    expect(row).toBe(
      '| **Testing** | 14,001 automated tests across 2 test projects (count injected by CI) | ✅ Complete | [CONTRIBUTING.md](./CONTRIBUTING.md) |'
    );
  });

  it('changes nothing when there is no metrics file', () => {
    root = makeRepo(['src/a/A.Tests.csproj']);
    fs.rmSync(path.join(root, 'test-metrics.json'));

    expect(run(root)).toContain(FEATURES_ROW);
  });
});
