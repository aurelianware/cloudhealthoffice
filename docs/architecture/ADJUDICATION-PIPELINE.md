# Adjudication Pipeline

Claims are adjudicated in one place: the claims-service adjudication
pipeline (`ClaimAdjudicationOrchestrator`), run on submission. It owns the
outcome, the persisted `AdjudicationResult` and the accumulator commit.
benefit-plan-service supplies the pricing, benefit and provider-integrity
building blocks over HTTP; it does not adjudicate.

The detailed design (trigger transport, stage contract, idempotency,
persistence rules, denial codes) is in
[claim-adjudication-pipeline.md](claim-adjudication-pipeline.md). This page
is the overview.

## Table of contents

- [Overview](#overview)
- [Stages](#stages)
- [benefit-plan-service endpoints the pipeline calls](#benefit-plan-service-endpoints-the-pipeline-calls)
- [What-if pricing](#what-if-pricing)
- [Claim type routing and operating mode](#claim-type-routing-and-operating-mode)
- [AI Claims Examiner (pended claims)](#ai-claims-examiner-pended-claims)
- [History](#history)

---

## Overview

```
POST /api/v1/claims  (claims-service)
   │
   ├─ append-only ClaimVersionSubmitted event (system of record)
   └─ Service Bus → claim-version-events
                       │
                       ▼
        adjudication-orchestrator subscription
                       │
                       ▼
   ClaimAdjudicationOrchestrator — stages by Order, ascending;
   a terminal Deny / Reject short-circuits the rest
                       │
                       ▼
   PersistenceStage → ClaimVersionAdjudicated event (Service Bus)
```

## Stages

Order is fixed in code (`IClaimAdjudicationStage.Order`).

| Order | Stage | What it does | Calls |
|------:|-------|--------------|-------|
| 100 | `ScrubbingStage` | Pre-payment claim validation | in process |
| 120 | `DuplicateClaimStage` | Duplicate-claim detection against the member's prior claims | claims repository |
| 150 | `ProviderIntegrityStage` | OIG/LEIE/SAM.gov exclusion for billing and rendering NPI; exclusion → Deny, unresolved → Pend | benefit-plan-service `GET provider-integrity/{npi}` |
| 200 | `NetworkCredentialingStage` | Network membership and credentialing | provider membership and credentialing clients |
| 250 | `PricingStage` | Allowed amount per line; billed-charges fallback or unresolved rate → Pend | benefit-plan-service `POST resolve-rates` |
| 275 | `CoordinationOfBenefitsStage` | Payer order, secondary/tertiary COB | coverage-service |
| 300 | `BenefitCalculationStage` | Eligibility, prior authorization (authorization-service; Pend `PAUNAVAIL` when it cannot answer), cost-sharing waterfall, prepared accumulator write | benefit-plan-service `POST calculate-benefits` (Prospective), authorization-service |
| 400 | `NcciEditsStage` | NCCI pair / MUE edits, examiner edit dispositions | in process |
| 600 | `AiExaminationStage` | Publishes `claims.pended.v1` for pended claims | event publisher |
| 999 | `PersistenceStage` | Writes the adjudicated outcome | claims repository |

A claim that finally passes commits its prepared accumulator write
(`POST commit-accumulators`); a pended claim writes nothing until an
examiner approves it. Voids, replacements and Pay → Deny reprocessing
reverse accumulators through `POST reverse-claim`.

## benefit-plan-service endpoints the pipeline calls

All under `/api/v1/adjudication` (`AdjudicationController`):

| Endpoint | Caller | Side effects |
|----------|--------|--------------|
| `POST resolve-rates` | `PricingStage` | none |
| `POST calculate-benefits` | `BenefitCalculationStage` (Prospective) | none in Prospective mode; writes accumulators in Production mode |
| `GET provider-integrity/{npi}` | `ProviderIntegrityStage` | none |
| `POST commit-accumulators` | accumulator outbox (claims-service service principal only) | writes accumulators |
| `POST accumulator-commits/lookup` | post-rollout sweep (claims-service only) | none |
| `POST reverse-claim` | adjustment / void / reprocessing | reverses accumulators |

The same controller also exposes standalone `ncci-check`, `scrub-check`
and `validate-provider-enrollment` checks; the pipeline does not call them.

## What-if pricing

`POST /api/v1/adjudication/estimate` (`EstimateController`,
`PaymentEstimateService`) prices a hypothetical claim through the fee
schedule and benefit engines in Prospective mode. Nothing is written. It is
an estimate, not a prediction of the pipeline's decision: it does not run the
duplicate check, network credentialing, NCCI, COB or AI examination. See
[prospective-adjudication.md](prospective-adjudication.md).

## Claim type routing and operating mode

`IClaimTypeRouter` resolves a tenant's `OperatingModeConfiguration`
(`{claimType}-{lineOfBusiness}`, then `{claimType}`, then the
`benefitCalculation` default) to `ChoReplace`, `ChoAugment` or `LegacyOnly`.
Today only the payment estimate uses it, to label an estimate
`AuthoritativePayer` or `Simulation`. The claims-service pipeline runs in
Replace mode only (claim-adjudication-pipeline.md, D13): it does not route
Legacy-Only claims away or run Augment shadow comparisons. See
[OPERATING-MODE.md](OPERATING-MODE.md).

## AI Claims Examiner (pended claims)

When a stage pends a claim, the pipeline persists `Pended` + `PendDetails`
and `AiExaminationStage` publishes `claims.pended.v1`. `claims-examiner-service`
consumes it and writes an advisory recommendation
(`PUT /api/claims/{id}/ai-examination`); a human examiner decides in the work
queue. Advisories are never auto-applied. See
[CLAIMS-EXAMINER-SERVICE.md](CLAIMS-EXAMINER-SERVICE.md).

## History

- This page used to describe a 7-step Argo Workflow DAG whose step 6 posted
  to `POST /api/v1/adjudication/adjudicate`. That workflow
  (`claims-adjudication-workflow.yaml`) was removed in #1217.
- `POST /api/v1/adjudication/adjudicate`, a synchronous dry run that ran its
  own engine chain (scrub, NCCI, provider integrity, eligibility, rule-engine
  prior auth, terminology crosswalk, pricing, benefits, COB), was removed;
  its answers diverged from the pipeline's. See
  [adjudicate-endpoint-decision.md](adjudicate-endpoint-decision.md). What
  only it did — Legacy-Only / Augment routing of a claim, the plan
  terminology crosswalk before pricing, and in-process `PriorAuthRuleEngine`
  evaluation for a claim — no longer runs anywhere on the claim path.

## Related documentation

- [claim-adjudication-pipeline.md](claim-adjudication-pipeline.md) — pipeline design and decisions
- [FEE-SCHEDULE-ENGINE.md](../engines/FEE-SCHEDULE-ENGINE.md) — Rate resolution chain and pricing methods
- [ACCUMULATOR-ENGINE.md](../engines/ACCUMULATOR-ENGINE.md) — Redis accumulator design
- [OPERATING-MODE.md](OPERATING-MODE.md) — Augment/Replace pattern details
- [CLAIMS-EXAMINER-SERVICE.md](CLAIMS-EXAMINER-SERVICE.md) — AI advisory layer
