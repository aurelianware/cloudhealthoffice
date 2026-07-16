# Contributing to CloudHealthOffice

CloudHealthOffice is a source-available healthcare payer platform licensed under
the Business Source License 1.1. Contributions are welcome, but the project is
not permissively licensed software. By contributing, you agree that your
contribution is provided under the same BSL 1.1 terms as the repository.

The highest-value contributions improve correctness, reproducibility,
operability, documentation, and developer experience.

## Before You Start

Read these first:

- [README.md](README.md) for the project overview and current evidence.
- [docs/README.md](docs/README.md) for the documentation map.
- [SECURITY.md](SECURITY.md) before sharing logs, screenshots, fixtures, or test data.
- [GOOD_FIRST_ISSUES.md](GOOD_FIRST_ISSUES.md) for contribution ideas.

Never include PHI, real patient data, real member data, real claim data,
production credentials, access tokens, or customer-specific configuration in an
issue, discussion, commit, pull request, screenshot, fixture, or benchmark
artifact.

## Good Contribution Areas

- Documentation accuracy and link health.
- Million Claim Challenge reproducibility notes, raw artifacts, and test
  coverage.
- Claims, benefits, eligibility, prior authorization, FHIR, and X12 bug fixes.
- Small portal usability improvements backed by deterministic tests.
- Test coverage for existing behavior.
- Deployment and local-development fixes.
- Security hardening that does not expose sensitive details publicly.

## Development Setup

Use the current quickstart:

```bash
git clone https://github.com/aurelianware/cloudhealthoffice.git
cd cloudhealthoffice

docker compose --profile core up -d
curl http://localhost:5001/health/live
```

Then continue with:

- [Quickstart](docs/guides/QUICKSTART.md)
- [Developer guide](docs/developer/README.md)
- [Testing guide](tests/README.md)

## Branches And Pull Requests

1. Create a focused branch from `main`.
2. Keep the change scoped to one problem.
3. Add or update tests for behavior changes.
4. Update docs when behavior, setup, commands, evidence, or limitations change.
5. Run the smallest relevant validation locally and report it in the PR.
6. Open a pull request using the PR template.

Prefer small, reviewable PRs. Large rewrites should be preceded by an issue or
architecture decision record.

## Code Review Expectations

Reviewers prioritize:

- Healthcare correctness and edge cases.
- Clear separation between implemented and planned behavior.
- Reproducible evidence for benchmark claims.
- No PHI or secrets in the change.
- Tests aligned with risk and blast radius.
- Minimal unrelated refactoring.
- Consistency with existing service and portal patterns.

## Documentation Expectations

Documentation should be factual, dated when tied to benchmark evidence, and
explicit about limitations. Do not use stale release-number claims or broad
production-readiness statements unless they are supported by current evidence.

Use "source-available" for this repository. Do not describe CloudHealthOffice as
permissive open source.

## License

CloudHealthOffice is licensed under BSL 1.1. See [LICENSE](LICENSE) for the
exact terms.
