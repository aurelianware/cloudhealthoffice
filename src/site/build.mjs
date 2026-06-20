import { cpSync, mkdirSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const siteRoot = process.cwd();
const scriptDir = fileURLToPath(new URL('.', import.meta.url)).replace(/[\\/]$/, '');

if (siteRoot !== scriptDir) {
  console.error(
    `Error: build.mjs must be run from the src/site directory.\n` +
    `  Expected: ${scriptDir}\n` +
    `  Current:  ${siteRoot}`
  );
  process.exit(1);
}

const outputDir = join(siteRoot, 'dist');
const ignoredEntries = new Set([
  '.dockerignore',
  '.gitignore',
  'BUILD-PROCESS.md',
  'DEPLOYMENT.md',
  'Dockerfile',
  'IMPLEMENTATION-SUMMARY.md',
  'README.md',
  'build.mjs',
  'dist',
  'nginx.conf',
  'node_modules',
  'package-lock.json',
  'package.json'
]);

const shouldInclude = (sourcePath) =>
  !sourcePath.endsWith('.md') &&
  !/[\\/]dist([\\/]|$)/.test(sourcePath) &&
  !/[\\/]node_modules([\\/]|$)/.test(sourcePath);

rmSync(outputDir, { recursive: true, force: true });
mkdirSync(outputDir, { recursive: true });

for (const entry of readdirSync(siteRoot, { withFileTypes: true })) {
  if (ignoredEntries.has(entry.name) || entry.name.endsWith('.md')) {
    continue;
  }

  cpSync(join(siteRoot, entry.name), join(outputDir, entry.name), {
    recursive: true,
    filter: shouldInclude
  });
}

writeFileSync(join(outputDir, '.nojekyll'), '');

console.log(`Prepared GitHub Pages artifact in ${outputDir}`);
