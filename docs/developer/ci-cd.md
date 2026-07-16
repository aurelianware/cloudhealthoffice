# CI/CD

CloudHealthOffice uses GitHub Actions for build, test, scan, and deployment
checks. Workflows are path-sensitive where possible, but contributors should
still run targeted validation locally before opening a PR.

## Pull Request Expectations

- Build or test the changed project.
- Update docs when public behavior, setup, evidence, or deployment changes.
- Report commands in the PR template.
- Fix failures in the changed area before merging.

## Common Check Types

- .NET build and tests.
- Portal tests.
- Validator tests.
- Site/docs checks.
- Security and dependency scans.
- Deployment or Kubernetes validation where relevant.

## Failing Checks

When a check fails:

1. Read the failing job log.
2. Identify whether it is a real failure, flaky failure, or stale/misconfigured
   workflow.
3. Fix in scope when the failure is related to the PR.
4. Document out-of-scope failures in the PR.

Do not hide failing benchmark or validation behavior by weakening the check.
