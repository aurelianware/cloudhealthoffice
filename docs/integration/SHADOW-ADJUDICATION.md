# Shadow adjudication

Re-adjudicates claims the plan's core system (QNXT, Facets, HealthRules, ...)
already adjudicated, in CHO's own claim pipeline, and records where CHO's
answer differs from the core's: allowed, paid, member liability, and denial
codes (CARC), claim by claim and line by line.

Nothing a member, a provider or a FHIR API sees changes. The claim keeps the
core's answer, its EOB is unchanged, no accumulator moves and no event is sent.
The comparisons live in their own collections.

What it is for:

- **Payment-integrity findings.** Claims where CHO, applying the plan's
  contracts and benefits, would have paid differently, with the dollar
  difference and the reason.
- **A migration rehearsal with no risk.** How much of the plan's real claim
  volume CHO can already adjudicate to the same answer, and, for the rest,
  which missing input stops it.

It reads claims the [CPCDS file-drop importer](CPCDS-FILE-DROP-IMPORTER.md)
loaded (claims with an external source). Load claims there first.

## How a shadow run stays read-only

The run uses the same `ClaimAdjudicationOrchestrator` as a real adjudication,
on a detached copy of the claim with the core's answer removed. It executes
only the stages listed in `ClaimAdjudicationOrchestrator.ShadowSafeStages`:

| Stage | Why it is safe |
|---|---|
| Scrubbing, TimelyFiling, DuplicateClaim, ProviderIntegrity, NetworkCredentialing, InpatientGrouping, Pricing, CoordinationOfBenefits, NcciEdits | They read reference data, plan settings and claim history only. DuplicateClaim already excludes the claim's own chain. |
| BenefitCalculation | Calls the benefit engine in Prospective mode, which never writes accumulators. |
| PlanEdits | Calls the plan's own edit service, if the tenant configured one. |

Left out: **Persistence** (writes the claim's projection and the accumulator
outbox) and **AiExamination** (publishes a ClaimPended event and spends model
tokens). The run also drives no accumulator outbox and emits no adjudicated
event. The list is an allowlist: a stage added to the pipeline later is left out
of shadow runs until it is classified, and a unit test fails until it is.

## Endpoints (claims-service)

All require `claims:import`, the same permission as the CPCDS importer: the
results show the plan's payment differences.

`POST /api/v1/claims/shadow-adjudication/runs`

```json
{ "importBatchId": "8f0c...", "limit": 200 }
```

or `{ "claimIds": ["cpcds-...", "cpcds-..."] }` (the `choClaimId` values from
the CPCDS import report). Runs synchronously over at most
`MaxClaimsPerRun` claims and returns the run's totals:

| Field | Meaning |
|---|---|
| `claimsCompared` | Claims with a stored result. |
| `countsByKind` | How many of each kind (below). |
| `undecidedRate` | Share of the comparable claims CHO could not decide: the migration-readiness number. |
| `matchRate` | Share of the claims CHO decided that matched the core. |
| `netPaidVariance` | Sum of CHO paid minus core paid over decided claims. Positive: CHO would pay more. |
| `absolutePaidVariance` | Sum of the absolute paid differences. |
| `skipped` | Claims not found, or not loaded from a core. |
| `failed` | Claims whose run threw (logged). |

`GET /api/v1/claims/shadow-adjudication/runs` lists recent runs,
`GET .../runs/{runId}` returns one, and `GET .../runs/{runId}/results?kind=PaidVariance&limit=200`
returns per-claim comparisons, largest paid difference first.

## Kinds

One kind per claim, in this order of precedence:

| Kind | Meaning |
|---|---|
| `NotComparable` | The core's claim is not final (pended, voided). CHO is not run. |
| `ChoUndecided` | CHO pended or rejected the claim, or denied it only because the member is not loaded. `choPendCode` names the gap: `NOCONTRACT` (no contract or fee schedule rate), `PRICING`, `REJECT:<stage>`, `MEMBERNOTLOADED`, ... |
| `OutcomeMismatch` | One side paid, the other denied. |
| `DenialCodeMismatch` | Both denied, with different CARCs (or a line both denied, with different codes). |
| `AllowedVariance` | Allowed differs on the claim or a line: pricing. |
| `PaidVariance` | Allowed agrees, payment differs (or a line one side denied): benefits or accumulators. |
| `MemberLiabilityVariance` | Allowed and paid agree, the member's share differs. |
| `Match` | Same outcome, same amounts within `Tolerance`. |

Each result also carries the line comparisons (`lines[].differences`: Allowed,
Paid, MemberLiability, LineOutcome, DenialCode) and every CHO stage finding.

Line denial codes: the core's come from the CPCDS "Line payment denial code"
the importer stores; CHO's are the line's adjustments other than the member's
share (group PR) and the contractual write-down (CARC 45).

## What CHO needs to decide a plan's claims

Expect most claims to be `ChoUndecided` until these are loaded. The run tells
you which one is missing.

| Input | Loaded through |
|---|---|
| Members, coverage, plan id, group | 834 (`enrollment-import-service`), or the CPCDS member and coverage files |
| Provider network and credentialing | provider-service |
| Medicare physician fee schedule | [Medicare PFS importer](MEDICARE-PFS-IMPORTER.md), one schedule per locality (the PricingApi uploads do not reach adjudication) |
| Provider contracts and custom fee schedules | [Contract roster importer](CONTRACT-ROSTER-IMPORTER.md) and [fee schedule CSV importer](FEE-SCHEDULE-IMPORTER.md) |
| Benefit plan design | benefit-plan-service API / portal |
| Accumulators to date | Not yet: CHO starts the member at zero, so deductible and out-of-pocket differences show as `PaidVariance` / `MemberLiabilityVariance` (baseline seed planned) |
| Prior authorizations | authorization-service |

## Configuration

`Adjudication:Shadow` in claims-service:

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Runs are refused (409) unless true. |
| `Tenants` | `[]` | When set, only these tenants may run. |
| `MaxClaimsPerRun` | `200` | Most claims one synchronous run adjudicates. |
| `Tolerance` | `0.01` | Dollar difference treated as equal. |

A shadow run calls the same services a real adjudication does (benefit-plan,
pricing, provider, coverage, member, authorization), once per claim.

## Roadmap

1. **This change:** shadow mode in the pipeline, the comparer, the result
   store and the run endpoints.
2. Run report: top reasons by count and dollars (CARC, CHO stage, pend code,
   procedure, provider), CSV export of discrepant lines.
3. Provider contract and custom fee schedule loader: the contract roster and
   fee schedule CSV importers.
4. Accumulator baseline, seeded from a core accumulator extract or derived from
   the imported claims, held apart from live balances.
5. Automatic run after each CPCDS file-drop batch; re-run after a configuration
   change to show the gap closing.
6. Benefit plan template import and a portal report page.
