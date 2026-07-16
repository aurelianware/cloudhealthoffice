# Good First Issues

Good first contributions should be small, testable, and low-risk. The best
starting points are documentation, reproducibility, tests, and local developer
experience.

## Documentation

- Fix broken links or stale paths.
- Add missing docs to [docs/README.md](docs/README.md).
- Clarify setup steps in [docs/guides/QUICKSTART.md](docs/guides/QUICKSTART.md).
- Improve benchmark caveats with dated, sourced evidence.
- Add Mermaid diagrams for an already documented service flow.

## Tests

- Add focused unit tests around claims, benefits, terminology, or validator
  helpers.
- Add regression tests for a known bug fixed in a recent PR.
- Improve portal service tests for query-string or DTO behavior.

## Developer Experience

- Improve local error messages.
- Make a script fail fast with clearer instructions.
- Document a common troubleshooting path.
- Reduce noisy logs in local development without hiding important failures.

## Healthcare Domain

- Add plain-English explanations to [docs/domain/README.md](docs/domain/README.md).
- Map claim, benefit, eligibility, or authorization terms to the corresponding
  service or model.
- Improve examples using synthetic data only.

## Before Opening A PR

- Keep the change focused.
- Avoid unrelated formatting churn.
- Do not include PHI, secrets, or production data.
- Link the issue or explain the problem in the PR body.
