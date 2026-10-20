# Repository Verification — October 2026

**Date:** 2026-10-20
**Scope:** `aurelianware/cloudhealthoffice` @ `main` (`a17c9f3`)
**Kind:** technical verification only. Commercial readiness is out of scope for
this repository.
**Relationship to earlier records:** re-verifies every finding in the
[September 2026 audit](investor-readiness-2026-09.md) and its October 11 status
section against current code. The [capability ledger](../docs/CAPABILITY_RECONCILIATION.md)
remains the authority for capability status; this document records what was
checked, how, and what it found on this date.

## Method

- Fresh clone of `main` at `a17c9f3`. Full Release build of
  `cloudhealthoffice-main.sln` with .NET SDK 8.0.426.
- Eleven core test suites executed locally (listed below).
- Each September finding re-checked by reading the code it names, not the
  documents that describe it.
- GitHub Actions history on `main` read through the API.
- Not checked: the Azure subscription, any running deployment, and the CMS-0057-F
  scenario scores in `docs/epa-gap-matrix.md` §11 (generator-owned).

## Build and test results

| Check | Result |
| --- | --- |
| Release build, whole solution | 0 warnings, 0 errors (warnings are errors) |
| claims-service tests | 1,476 passed |
| payment-service tests | 572 passed |
| premium-billing-service tests | 529 passed |
| appeals-service tests | 445 passed |
| BenefitEngine tests | 437 passed |
| FeeScheduleEngine tests | 304 passed |
| capitation-service tests | 82 passed |
| CobEngine tests | 69 passed |
| NcciEngine tests | 65 passed |
| AdjudicationController tests | 51 passed |
| PriorAuthRuleEngine tests | 40 passed |
| CI-reported totals | 14,986 tests in 66 test projects (`test-metrics.yml`); 76% line / 66% branch coverage on the October 20 coverage run |
| `main` CI on 2026-10-20 | Quality Gate, Security Scan, Test .NET Projects, Code Coverage, CMS-0057-F Acceptance Evidence, Da Vinci Interoperability and PHI Validation green. "Deploy demo to GKE" failed at the Strimzi Helm install (`context deadline exceeded`) while the demo cluster was scaled to zero; not a code defect |

Coverage by component on that run: the adjudication engines the September audit
flagged are now BenefitEngine 86%, FeeScheduleEngine 90%, NcciEngine 80% and
PriorAuthRuleEngine 86%. The lowest are CloudHealthOffice.ReferenceData 6%,
encounter-submission-service 23%, mcc-platform-validator 29%, DocumentStore 40%,
ffs-service 42%, Portal 47% and ProviderEnrollmentService 49%.

## September findings, re-verified

| Finding | Status on `a17c9f3` | Evidence |
| --- | --- | --- |
| 28 of 35 services had no authentication | Fixed | All 38 service hosts under `src/services` call the shared CHO authentication; endpoints without a stated permission take the service default or are denied (`DefaultPermissionConvention`) |
| Tenant fell back to a client header / `default-tenant` | Fixed | `TenantMiddleware` takes tenant from the validated token only: 401 without a tenant claim, 403 on a conflicting header; `RequireTenantId` no longer exists. Sampled repositories (claims, premium invoices) filter every query by tenant |
| Native Cosmos SDK in 31 projects | Fixed | No project references `Microsoft.Azure.Cosmos`; `MongoDB.Driver` 3.12.0 pinned centrally ([ADR 017](../docs/adr/017-mongodb-api-only-persistence.md)) |
| Per-project package pinning | Fixed | `Directory.Packages.props` with central management; no `Version=` on any `PackageReference` |
| `main` red; unreachable base-image registry | Fixed | Green CI on `main` (above) |
| Committed datastore credentials | Fixed | No literal passwords in manifests; the only literal is the throwaway Postgres service container in `test-dotnet.yml` |
| Overclaims in `assessment.html`; benchmark stated four ways; X12 diagram | Fixed | One canonical benchmark result; X12 inputs listed as 837, 834, 270/271 |
| Stale coverage and test figures | Fixed by this PR | Site no longer quotes coverage; remaining `docs/features` summaries carry a dated historical banner; Canonical Facts recomputed |
| Trust-boundary document | Fixed by this PR | `docs/diligence/SECURITY-ONE-PAGER.md` now describes token-derived tenancy, permission-gated endpoints, service identity and record-access audit |
| `NotImplementedException` | Reduced | 66 throws in production code (89 in September), almost all in QNXT/Facets/HealthEdge migration adapters (provider 22, claims 21, benefit plan 9, authorization 4) |
| `reference-data-service` on PostgreSQL | By design | Recorded in ADR 006 and ADR 017 |
| Dead duplicate manifests | Fixed by this PR | `infrastructure/k8s/coverage-service-deployment.yaml` and `member-service-deployment.yaml` removed; canonical manifests live under `src/services/*/k8s/` |
| Rotate the Postgres credential; confirm what Azure hosts | Owner action | Cannot be shown from the repository |
| Clean-checkout build transcript | Recorded here | Build and test results above |

## New findings

| Finding | Disposition |
| --- | --- |
| FHIR Patient and Coverage served only synthetic data: `MockFhirDataAdapter` was the sole `IFhirDataAdapter` and was registered unconditionally (CAP-22) | Addressed in PR #1420 (merged 2026-10-20): Patient and Coverage served from member-service and coverage-service when bound to `ChoNative` ([ADR 018](../docs/adr/018-fhir-patient-coverage-from-cho-services.md)). Encounter, Claim and the Payer-to-Payer member source remain mock |
| Database-per-tenant mode (`MongoDb:UseTenantScoping=true`) is refused by the claim-event outbox, accumulator outbox and authorization SLA watchdog | Documented in the security one-pager: isolation is shared-database with tenant filters |
| `docs/status/MODULE-STATUS.md` last updated May 2026, five domains only | Fixed by this PR (October snapshot of all domains) |
| `docs/POSITIONING.md` Canonical Facts from July 2026 (190K lines, 45 test projects) | Fixed by this PR; `scripts/verify-canonical-facts.sh` now recomputes them |
| `.github/copilot-instructions.md` directed assistants to write unsupported marketing claims and invented case studies | Replaced by this PR with evidence-first instructions |
| Prospecting and early strategy documents in the public repository | Removed by this PR (kept privately) |
| Portal Correspondence page calls claims-service routes that do not exist | Open; recorded in the module snapshot |
| `ffs-service` has no Dockerfile or deploy workflow | Open (CAP-20) |

## Open items carried forward

1. Rescore CMS-0057-F scenarios PAT-01, PROV-01 and P2P-01 after PR #1420 (done in PR #1423: the data-source row moves S → P; all three stay GAP on data ADR 018 left on the mock or on missing PA data).
2. Raise coverage on the lowest components listed above.
3. Owner actions: credential rotation where `reference-data-service` ran shared;
   a precise statement of what any cloud subscription hosts.
4. Attribute Patient Access disclosures to the SMART end user and client in the
   record access log (ADR 018 follow-up).
