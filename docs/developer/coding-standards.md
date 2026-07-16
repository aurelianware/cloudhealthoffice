# Coding Standards

CloudHealthOffice favors small, explicit changes that preserve domain
correctness and operational visibility.

## General

- Follow existing patterns in the service or engine you are editing.
- Keep behavior changes focused.
- Prefer typed models and structured parsing over ad hoc string handling.
- Add comments only where they clarify non-obvious domain or timing behavior.
- Keep planned behavior clearly labeled as planned.

## Healthcare Correctness

- Treat paid, denied, pended, failed, unsupported, and mismatched outcomes as
  distinct states.
- Do not count unsupported behavior as success.
- Keep synthetic fixtures deterministic.
- Preserve date relationships in eligibility and newborn scenarios.
- Keep amount-level scoring separate from disposition-level scoring.

## Tests

- Add tests at the lowest level that proves the behavior.
- Use deterministic fixtures.
- Avoid network, live cluster, or real tenant dependencies in unit tests.
- Broaden validation when touching shared contracts or cross-service behavior.

## Security And Privacy

- Do not log PHI or secrets.
- Do not commit real healthcare data.
- Keep tenant isolation and authorization checks explicit.
- Redact tokens, cookies, connection strings, and identifiers in examples.
