# Security Policy

CloudHealthOffice is a source-available healthcare platform that may process or
model workflows involving protected health information. Treat the repository,
fixtures, logs, screenshots, and benchmark artifacts accordingly.

## Reporting Vulnerabilities

Do not report security vulnerabilities through public GitHub issues,
discussions, pull requests, or comments.

Report suspected vulnerabilities privately by email:

```text
security@cloudhealthoffice.com
```

Include:

- Affected component or route.
- Steps to reproduce.
- Potential impact.
- Relevant logs or screenshots with all sensitive data removed.
- Whether the issue may involve PHI, credentials, or tenant data.

Please do not include real PHI or production credentials in the report body.

## Supported Scope

| Scope | Security handling |
| --- | --- |
| `main` branch | Actively reviewed for security fixes |
| Current pull requests | Reviewed before merge |
| Historical branches or old release notes | Not supported unless explicitly stated |
| Local forks and deployments | Maintainer guidance only |

CloudHealthOffice is not currently presented as a packaged production appliance.
Deployment operators are responsible for their own hosting environment,
security controls, business associate agreements, and regulatory posture.

## PHI And Sensitive Data Rules

Never commit or publicly share:

- Real patient, member, subscriber, employer, provider-contract, or claim data.
- Medical records, attachments, EOBs, remittance files, or eligibility payloads
  from a real environment.
- Tokens, passwords, certificates, private keys, connection strings, or cloud
  credentials.
- Screenshots that include real PHI, tenant names, account identifiers, or
  secrets.
- Logs containing bearer tokens, cookies, authorization headers, database
  connection strings, or identifiable healthcare data.

Synthetic test data is acceptable when it cannot be linked to a real person,
plan, employer, provider contract, tenant, or claim.

## Secure Development Expectations

- Use least-privilege credentials for local development.
- Keep secrets in local environment variables or secret stores, not source
  files.
- Redact or omit PHI from logs.
- Add tests for authorization, tenant isolation, and data-handling behavior
  where relevant.
- Prefer deterministic synthetic fixtures for benchmark and regression tests.
- Document any security-sensitive default or deployment prerequisite.

## Compliance Notes

HIPAA and CMS-0057-F documentation in this repository is implementation and
readiness guidance, not legal advice. Production deployment requires
organization-specific review, controls, contracts, monitoring, and incident
response procedures.
