<!-- markdownlint-disable MD013 -->

# Repository Validation Baseline

Date: July 20, 2026 UTC / July 19, 2026 America/Phoenix

Baseline commit: `ce5d0f23`

Branch used for this audit: `codex/repository-validation-baseline`

Raw local logs: `/tmp/cho-repository-validation-20260720T055856Z`

## Purpose

This document records the current repository validation baseline before
additional product, demo, documentation, or deployment changes are made.

It is intentionally factual. Passing checks are recorded as passing. Failing
checks, skipped checks, warnings, and external prerequisites are recorded
without being silently fixed in this pass.

## Environment

| Tool | Version or value |
| --- | --- |
| OS | macOS 26.5, arm64 |
| .NET SDK | 8.0.128 |
| .NET runtime | 8.0.28 |
| Node.js | v26.4.0 |
| npm | 11.17.0 |
| Docker | 29.6.1 |
| Docker Compose | v5.3.0 |
| kubectl | v1.36.1 |
| kubectl bundled kustomize | v5.8.1 |
| Helm | installed locally |

Note: CI uses Node 20 in the primary JavaScript test workflow, while this
local baseline used Node 26.4.0.

## Evidence Surfaces Inspected

- [README.md](../../README.md)
- [docs/README.md](../README.md)
- [docs/roadmap/README.md](../roadmap/README.md)
- [docs/compliance/CMS-0057-F-READINESS-MATRIX.md](../compliance/CMS-0057-F-READINESS-MATRIX.md)
- [docs/million-claim-challenge/podcast/episode-008/benchmark-results.txt](../million-claim-challenge/podcast/episode-008/benchmark-results.txt)
- [SECURITY.md](../../SECURITY.md)
- [LICENSE](../../LICENSE)
- [LICENSE_SUMMARY.md](../../LICENSE_SUMMARY.md)
- [COMMERCIAL-LICENSING.md](../../COMMERCIAL-LICENSING.md)
- Root Docker Compose files
- Kubernetes and Helm manifests under `infrastructure/`, `k8s/`, and service
  `k8s/` folders
- GitHub Actions workflows under `.github/workflows/`
- Portal pages under `src/portal/CloudHealthOffice.Portal/Pages/`
- Test projects under `src/` and `tests/`

## Repository Shape

| Area | Observed value |
| --- | ---: |
| Tracked files | 3,098 |
| Tracked Markdown files | 370 |
| Non-E2E/non-load .NET test projects found | 46 |
| Deployment-related YAML/Bicep/Helm surfaces counted | 123 |
| Git pack size | 21.59 MiB |
| Working tree size after dependency restore/install | 10 GiB |

The working tree size includes local dependency folders and generated local
outputs created by validation commands.

## Validation Summary

| Check | Status | Notes |
| --- | --- | --- |
| .NET restore | Pass with warnings | `Stripe.net` 45.15.0 was not found; NuGet resolved 46.0.0 for premium billing projects. |
| .NET solution build | Pass with warnings | Build completed with 61 warnings and 0 errors. |
| .NET test suite | Pass | 46 TRX files. 4,719 executed tests passed, 0 failed. TRX counters reported total 4,734, executed 4,719. |
| npm install | Pass with warnings | `npm ci` installed 643 packages. npm reported 3 total vulnerabilities in all dependencies. |
| TypeScript build | Pass | `npm run build` completed. |
| Jest tests | Pass | 24 suites passed, 525 tests passed. Coverage: 88.36% statements, 76.44% branches, 93.37% functions, 88.71% lines. |
| npm lint | Fail locally | ESLint 9.39.5 crashed under Node 26 with an AJV/eslintrc `defaultMeta` error. |
| Root site build script | Remediated after baseline | The root `build:site` script now delegates to the current `src/site` package. |
| Root site accessibility script | Remediated after baseline | The root `validate:site` script now runs the current `src/site` accessibility validator. |
| Root validation script | Remediated after baseline | The root `validate` script now checks active TypeScript, Jest, and static-site surfaces instead of the legacy template generator path. |
| Current static site build path | Pass | Running `node build.mjs` from a copied `src/site` tree produced a deployable artifact. |
| Current site accessibility validator | Pass with reported issues | `node src/site/js/validate-accessibility.js` exited 0 and reported 36 potential accessibility issues. |
| actionlint | Pass | `.github/workflows/*.yml` passed local actionlint. |
| npm audit, all dependencies | Fail | 3 advisories: `@babel/core`, `brace-expansion`, and `js-yaml`. |
| npm audit, production only | Pass | `npm audit --omit=dev --audit-level=moderate` found 0 vulnerabilities. |
| audit-ci | Pass | `.audit-ci.json` passed; audit-ci noted an allowlisted advisory may no longer be needed. |
| .NET vulnerable packages | Findings present | High-severity transitive findings in three test projects via `System.Text.Json` 8.0.0 and one E2E project via `System.Security.Cryptography.Xml` 8.0.1. |
| Docker Compose config | Partial pass | `docker-compose.yml`, `--profile core`, and `docker-compose.development.yml` validated. `docker-compose.observability.yml` failed because `claims-service` has no image or build context in that compose project. |
| Kubernetes client dry-run | Partial pass | Built-in resources dry-ran. CRD-backed resources require KEDA, cert-manager, and Secrets Store CSI CRDs. |
| Helm lint | Pass with warnings | Chart lint passed, but chart metadata has a malformed field and `templates/` is missing. Dependencies are present. |
| Markdown lint | Fail | Broad markdownlint command produced 19,056 output lines, including line length, table, fence, heading, and generated-output noise. |
| Internal Markdown link scan | Fail | 370 tracked Markdown files scanned, 1,449 internal links checked, 511 missing local targets found. |
| Dedicated secret scanners | Skipped locally | `gitleaks`, `trivy`, and `trufflehog` were not installed in this environment. |
| Lightweight secret-pattern grep | Candidate inventory only | 2,939 matches for broad secret-related terms, mostly docs, examples, GitHub secret references, and security guidance. This is not a leak verdict. |

## Exact Commands

### Environment Capture

```bash
VALIDATION_DIR="/tmp/cho-repository-validation-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$VALIDATION_DIR"
printf '%s\n' "$VALIDATION_DIR" > /tmp/cho-validation-dir
{
  date -u
  git rev-parse --short HEAD
  git branch --show-current
  dotnet --info
  node --version
  npm --version
  docker --version
  docker compose version
} 2>&1 | tee "$VALIDATION_DIR/environment.txt"
```

### .NET Restore, Build, And Tests

```bash
dotnet restore cloudhealthoffice-main.sln
dotnet build cloudhealthoffice-main.sln --no-restore
find src tests -name '*.Tests.csproj' \
  ! -name '*E2ETests.csproj' \
  ! -name '*LoadTests.csproj' | sort > "$VALIDATION_DIR/dotnet-test-projects.txt"

while read -r PROJECT; do
  SAFE_NAME=$(printf '%s' "$PROJECT" | tr '/.' '__')
  dotnet test "$PROJECT" \
    --no-restore \
    --logger "trx;LogFileName=${SAFE_NAME}.trx" \
    --results-directory "$VALIDATION_DIR/dotnet-test-results/$SAFE_NAME" \
    --verbosity minimal
done < "$VALIDATION_DIR/dotnet-test-projects.txt"
```

### JavaScript Build, Tests, And Validation

```bash
npm ci
npm run build
npm test -- --coverage --passWithNoTests
npm run lint
npm run build:site
npm run validate:site
npm run validate
```

The current static site path was also validated directly because the root npm
site scripts point at stale paths:

```bash
npm --prefix src/site run build
node src/site/js/validate-accessibility.js
```

### Security And Dependency Checks

```bash
npm audit --audit-level=moderate
npm audit --omit=dev --audit-level=moderate
npx audit-ci --config .audit-ci.json
dotnet list "$PWD/cloudhealthoffice-main.sln" package --vulnerable --include-transitive
```

Scanner availability was checked with:

```bash
for tool in gitleaks trivy trufflehog yamllint markdown-link-check kubectl helm kustomize; do
  command -v "$tool" || true
done
```

A lightweight candidate-pattern scan was also run:

```bash
git grep -n -I -E \
  '(password|secret|api[_-]?key|connectionstring|BEGIN (RSA |OPENSSH |EC |DSA )?PRIVATE KEY)' \
  -- . ':!node_modules' ':!dist' ':!src/site/dist'
```

### Workflow, Compose, Kubernetes, And Helm

```bash
actionlint .github/workflows/*.yml

docker compose -f docker-compose.yml config --quiet
docker compose --profile core -f docker-compose.yml config --quiet
docker compose -f docker-compose.development.yml config --quiet
docker compose --profile core --profile finance \
  -f docker-compose.yml -f docker-compose.observability.yml config --quiet

kubectl version --client
kubectl apply --dry-run=client --validate=false -f infrastructure/k8s --recursive

helm lint infrastructure/helm/cloudhealthoffice
helm dependency list infrastructure/helm/cloudhealthoffice
```

### Markdown And Internal Links

```bash
npx markdownlint-cli "**/*.md" \
  --ignore node_modules \
  --ignore dist \
  --ignore src/site/dist
```

Internal links were checked with a local script over `git ls-files '*.md'`.
The script ignored external URLs, `mailto:`, `tel:`, and same-file anchors.

## Skipped Or Limited Checks

| Check | Reason |
| --- | --- |
| `CloudHealthOffice.FlAhca.E2ETests` | E2E tests were excluded from the local non-credential test sweep. |
| `CloudHealthOffice.LoadTests` | Load tests were excluded from the local unit/integration validation sweep. |
| Full Docker Compose `core` startup | The main and core profiles were configuration-validated. The full service stack was not started in this audit pass to avoid mutating a long-lived local MongoDB/Redis environment. |
| Dedicated leak scan with gitleaks/trivy/trufflehog | Tools were not installed locally. GitHub Actions still defines security-scan coverage for those tools. |
| External credential workflows | Cloud, clearinghouse, and live integration checks that require unavailable credentials were not run locally. |
| Browser screenshots | No UI behavior was changed in this baseline PR. |

## Key Findings

### Healthy Baseline Areas

- The main .NET solution restores, builds, and runs the non-E2E/non-load test
  suite successfully.
- The primary TypeScript build and Jest test suite pass.
- Current Million Claim Challenge evidence is unusually strong for a local
  benchmark packet: raw outputs, run-summary JSON, p10/p12/p16 100K runs, false
  pend checks, payment-comparison gates, unsupported scenario labels, and local
  Docker Desktop scope are all documented.
- CMS-0057-F readiness documentation is careful to avoid certification claims
  and frames the repo as technical readiness evidence, not legal advice.
- BSL 1.1 licensing is explicit. Non-production evaluation is free, while
  production use requires a commercial license.
- `SECURITY.md` clearly prohibits PHI, real claim/member/provider data, secrets,
  and sensitive screenshots in public artifacts.
- GitHub workflow syntax passes local actionlint.

### Areas Needing Follow-Up

- The legacy generator templates under `scripts/templates` are not treated as the
  canonical repository validation path. If they become product-active again,
  they need a dedicated owner and validator.
- ESLint crashes locally under Node 26. This should be reproduced under CI's
  Node 20 before deciding whether the issue is local-toolchain-only.
- Dev dependency audit has three advisories. Production npm audit is clean.
- .NET vulnerable-package scan reports high-severity transitive packages in
  test projects and one E2E project.
- Markdown hygiene is not currently enforceable with the broad markdownlint
  command. The command also catches generated or copied package documentation,
  so the lint scope needs a deliberate include/exclude policy.
- Internal documentation links need a dedicated cleanup pass. The first baseline
  scan found 511 missing local targets.
- `docker-compose.observability.yml` is not standalone-valid.
- Kubernetes manifests depend on CRDs not installed in the local client-only
  validation environment.
- Helm chart lint passes but reports chart metadata and missing-template
  warnings.
- Dedicated local leak scanning was not reproducible because the expected tools
  were not installed.

## Proposed PR Sequence

1. `docs/repository-validation-baseline`: Record this baseline and do not
   change product behavior.
2. `chore/validation-script-alignment`: Align root npm scripts with the current
   `src/site` layout and active TypeScript/Jest validation surface.
3. `chore/dependency-security-baseline`: Resolve or document npm and .NET
   vulnerable-package findings without changing runtime behavior.
4. `docs/internal-link-hygiene`: Fix or archive broken internal Markdown links
   and add a repeatable scoped link check.
5. `chore/markdown-lint-scope`: Define enforceable Markdown lint scope and
   ignore generated/package artifacts.
6. `docs/platform-maturity-map`: Add a product maturity map with evidence,
   limitations, and pilot requirements.
7. `docs/minimal-deployment-profile`: Make the smallest evaluator/pilot
   deployment profile explicit.
8. `feat/evaluator-demo-path`: Add a deterministic synthetic-data demo path with
   reset, seed, validation, and troubleshooting.
9. `docs/security-review-baseline`: Convert scanner/tooling gaps and candidate
   secret-pattern findings into a clearer security review baseline.

## Current Readiness Statement

The repository has a strong engineering core: large .NET and Jest test suites
pass, the source-available license is explicit, the security policy is careful,
and the latest documented benchmark evidence is detailed and scoped.

The next diligence risk is not lack of code. It is reproducibility and
navigation: broken internal documentation links, non-reproducible local security
tooling, and unclear minimal demo/deployment paths make the product harder to
evaluate than the underlying evidence merits.
