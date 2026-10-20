# Instructions for AI coding assistants

Cloud Health Office (CHO) is a source-available (BSL 1.1), Kubernetes-first payer
administration platform: .NET 8 services, MongoDB wire protocol for document
storage, FHIR R4 projections owned by each domain service, and X12 as a first-class
interface. Read `README.md`, `docs/POSITIONING.md` and the ADRs in `docs/adr/` before
proposing structural changes.

## Claims and copy

- Evidence first. Any capability, number or status you write must match the
  canonical capability ledger (`docs/CAPABILITY_RECONCILIATION.md`) and the
  Canonical Facts in `docs/POSITIONING.md`. If the evidence is not there, do not
  write the claim.
- Never invent customers, case studies, testimonials, outcomes, savings figures,
  certifications (SOC 2, HITRUST) or production deployments. CHO has no
  production reference customer unless the ledger says otherwise.
- Keep demo and live distinct. Synthetic data, mock adapters and local benchmark
  runs are labelled as such (`docs/diligence/ADAPTER-STATUS.md`).
- Name the product "Cloud Health Office" in formal copy; "CHO" is fine in code
  and technical docs.

## Code

- Tenant comes only from the validated token (`TenantMiddleware`); every
  repository query filters by tenant. Never add a header or body fallback.
- Every endpoint states its permission (`[RequirePermission]`) or is denied by
  default. Reads of member records use `[AuditRecordAccess]`.
- No PHI in logs: log route templates and ids that are not member-identifying.
- New behaviour ships with tests, including real-MongoDB tests (EphemeralMongo)
  for repository queries. Do not skip, disable or weaken tests to get green.
- Package versions are managed centrally in `Directory.Packages.props`.
- Never commit credentials; local defaults must be clearly development-only.
