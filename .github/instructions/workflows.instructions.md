# GitHub Actions Workflows Instructions

**Applies to**: `.github/workflows/*.yml`

## Overview

This directory contains GitHub Actions workflow files for CI/CD automation of the Cloud Health Office solution.

## Workflow Files

### Deployment Workflows
- **deploy-gcp-gke.yml**: the GKE demo (synthetic data only; manual trigger)
- **deploy-azure-aks.yml**: AKS deployment (paused while Azure is not in use)
- **deploy-dev.yml.disabled / deploy-uat.yml.disabled**: retired; kept for reference only

### Validation Workflows
- **pr-lint.yml**: Comprehensive pull request validation (syntax, security, compliance, and site checks)
- **sanity.yml**: Basic health checks
- **debug-oidc.yml**: OIDC authentication debugging

### Blocking quality gates
- **security-scan.yml**: Trivy fails on a fixable High/Critical finding; the NuGet scan fails on a High/Critical vulnerable package (whole solution, transitive included); npm audit gates through `.audit-ci.json`.
- **test-dotnet.yml**: overall .NET line coverage below 75% fails the Coverage Summary job, as does a passing test run that produced no coverage.
- **dependency-review.yml**: a PR that adds a High/Critical-vulnerable package or a GPL/AGPL-licensed one fails.

### main takes reviewed PRs only
No workflow commits to main. Generated output (test metrics, badges, published interoperability and acceptance evidence) goes to the `ci-evidence` branch through `scripts/ci/publish-evidence.sh`; the site deploys overlay the evidence from there and README badges read it. A push made with `GITHUB_TOKEN` starts no workflows, so publishers redeploy the site with `gh workflow run`.

### Pin third-party actions that run with secrets or write access by commit SHA
`aquasecurity/trivy-action` tags were force-pushed to malicious commits in March 2026 (CVE-2026-33634); it and `actions/dependency-review-action` are pinned by SHA with the version in a comment, which Dependabot keeps current.

## Best Practices

### Authentication
- **Always use OIDC** (OpenID Connect) for Azure authentication
- Never store Azure credentials as secrets
- Use environment-specific secrets: `AZURE_CLIENT_ID_{ENV}`, `AZURE_TENANT_ID_{ENV}`, `AZURE_SUBSCRIPTION_ID_{ENV}`

### Workflow Structure
- Use matrix strategy for multi-environment deployments
- Set appropriate timeouts (30+ minutes for deployments)
- Include validation steps before deployment
- Add health checks after deployment

### YAML Conventions
- Line length restrictions are disabled (long parameter lines are acceptable)
- For boolean fields, use only `true` and `false` (do not use `on`, `off`, `yes`, or `no`).  
  - Note: `on` and `off` are reserved for workflow trigger events and should not be used as boolean values.
- Always include descriptive `name` fields for steps
- Use `reviewdog/action-actionlint` for workflow validation

### Deployment Process
Each deployment workflow should:
1. Validate inputs and checkout code
2. Authenticate with Azure using OIDC
3. Run Bicep validation and ARM What-If analysis
4. Deploy infrastructure via Bicep
5. Run health checks and report status

### Common Pitfalls
- **Don't cancel deployments**: Set adequate timeouts (30+ minutes)
- **Bicep warnings**: "use-parent-property" warnings for Service Bus topics are expected and safe
- **Parallel deployments**: Avoid deploying to same environment concurrently

### Lint Command
```bash
# Run actionlint locally
actionlint -color .github/workflows/*.yml

# Run yamllint
yamllint .github/workflows/
```

## Modifying Workflows

### Adding a New Environment
1. Create environment-specific secrets in GitHub
2. Add matrix entry or new workflow file
3. Update resource naming pattern
4. Test in non-production first

### Changing Deployment Steps
1. Always preserve validation steps
2. Test Bicep changes locally first
3. Keep health checks at the end
4. Update timeout if adding long-running steps

### Testing Workflow Changes
- Use workflow_dispatch for manual testing
- Test in DEV environment first
- Review ARM What-If output before production
- Monitor Application Insights during deployment
