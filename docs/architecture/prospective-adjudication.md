# Prospective Adjudication (Claim Payment Estimate) API

Prospective adjudication lets a provider application (e.g. CloudDentalOffice)
submit a proposed set of services **before** a real claim exists and ask what
the expected payer payment and patient responsibility would be. It reuses the
same pricing and benefit engines that adjudicate real claims, but runs them in
a **read-only simulation mode** that never touches financial state.

> **Prospective adjudication is an estimate unless CloudHealthOffice is acting
> as the authoritative payer adjudication system. Final payment may change
> based on eligibility, accumulator changes, COB, other claims, authorization
> state, or payer processing rules.**

## Table of contents

- [Overview](#overview)
- [Data flow](#data-flow)
- [Read-only guarantee](#read-only-guarantee)
- [Authority model](#authority-model)
- [Confidence model](#confidence-model)
- [Explainability](#explainability)
- [Security & tenancy](#security--tenancy)
- [Example request](#example-request)
- [Example response](#example-response)
- [Limitations](#limitations)

---

## Overview

```
Provider application (CloudDentalOffice)
        │  POST /api/v1/adjudication/estimate
        ▼
EstimateController  ──►  PaymentEstimateService
                              │
        ┌─────────────────────┼──────────────────────────────┐
        ▼                     ▼                               ▼
 FeeScheduleEngine     BenefitCalculationEngine        Advisory checks
 (IRateResolution)     (Prospective execution mode)    · ProviderIntegrityGate
 allowed amounts       deductible → copay →            · PriorAuthRuleEngine
 + contractual adj     coinsurance → OOP max           · OperatingMode routing
                              │
                              ▼
                     line-level estimate + claim totals
```

The endpoint lives in `benefit-plan-service` alongside the existing
`AdjudicationController`, and deliberately reuses the **same** engines the real
adjudication pipeline uses (`docs/architecture/ADJUDICATION-PIPELINE.md`).
There is no second pricing or benefit engine.

`POST /api/v1/adjudication/estimate`

- **Request:** `PaymentEstimateRequest` (provider-facing wire contract, separate
  from the internal engine models).
- **Response:** `PaymentEstimateResponse` with claim-level totals, per-line
  adjudication, structured reasons, warnings, a confidence object, and a
  disclaimer.

## Data flow

1. **Fee-schedule pricing** — `IRateResolutionService.ResolveBatchAsync`
   resolves the allowed amount and contractual adjustment per line. This call
   is already side-effect free.
2. **Benefit calculation (prospective)** — `IBenefitCalculationEngine.CalculateAsync`
   runs the full cost-sharing waterfall with
   `BenefitResolutionRequest.ExecutionMode = Prospective`. The priced allowed
   amount is fed as the benefit line's billed amount, mirroring the production
   adjudication seam.
3. **Advisory checks** — provider integrity and prior-auth rules are consulted
   in a **non-blocking** way. Instead of denying the request, findings become
   warnings on the response. A downstream outage degrades to a warning plus a
   lower confidence signal rather than an error.
4. **Mapping** — priced + adjudicated lines are mapped onto the estimate lines,
   and claim-level totals are computed as the element-wise **sum of the line
   amounts** (so totals always equal the sum of lines).

## Read-only guarantee

Prospective adjudication MUST NOT persist a real claim, create payment records,
write claim history, consume/update deductibles, update benefit accumulators,
increment visit/frequency counters, trigger remittance, trigger downstream
workflows, or modify authorization state.

The mechanism is a single explicit execution context rather than scattered
boolean flags:

```csharp
public enum AdjudicationExecutionMode { Production, Prospective }
```

The benefit engine's cost-sharing waterfall is identical in both modes. The
only difference is at the end of the pipeline: in `Prospective` mode the engine
**skips the accumulator `ApplyUpdatesAsync` write** (both the per-line path and
the DRG/per-diem path). The in-memory accumulator working set is still advanced
so the returned snapshot reflects the *projected* post-claim balances — but that
projection is never written back.

Because pricing is already read-only and the advisory PA/integrity checks are
read-only lookups, skipping the accumulator write is sufficient to make the
whole estimate side-effect free. This is covered by explicit tests:

- `ProspectiveExecutionModeTests` (benefit engine): prospective runs never call
  `ApplyUpdatesAsync`; production runs still do; both compute identical
  cost-sharing; the DRG path is also read-only.
- `PaymentEstimateServiceTests`: the service always invokes the benefit engine
  with `ExecutionMode = Prospective`.

Real claim adjudication behavior (`AdjudicationController.Adjudicate` →
`CalculateWithModeAsync` → `CalculateAsync` with the default `Production` mode)
is unchanged.

## Authority model

The response carries an `authority` value so CHO can later support multiple
estimate sources:

| Value               | Meaning                                                                  |
|---------------------|--------------------------------------------------------------------------|
| `Simulation`        | Read-only projection from CHO's own engines. Not a payment guarantee.    |
| `PayerEstimate`     | Reserved for a future external-payer estimate connection.                |
| `AuthoritativePayer`| CHO is the authoritative adjudication engine for this claim type / LOB.  |

There is deliberately **no** "guaranteed payment" value — an estimate is never
a guarantee.

Authority is derived from the tenant's operating-mode configuration via the
same `IClaimTypeRouter` the real pipeline uses. When CHO both processes and is
authoritative (`Replace` mode) for the claim type / line of business, the
estimate reports `AuthoritativePayer`; otherwise it reports `Simulation`. If the
operating-mode lookup fails, it degrades safely to `Simulation`.

## Confidence model

The `confidence` object is **rule-based and deterministic** — it is derived
from the data actually available, not from any AI heuristic or invented
percentage.

```json
"confidence": {
  "level": "high",
  "reasons": ["Benefit plan resolved", "Provider fee schedule resolved", "Accumulator data available", "Provider integrity verified"],
  "missingData": []
}
```

Levels: `High`, `Medium`, `Low`, `InsufficientData`.

- `InsufficientData` — the benefit plan / coverage could not be resolved.
- `Low` — the provider is excluded, a line needs review (no benefit mapping),
  or several inputs are missing.
- `Medium` — a small number of inputs (e.g. a fee schedule) were missing.
- `High` — plan resolved, all lines priced from a real fee schedule, and no
  line needs review.

## Explainability

Every line carries structured, stable-coded `messages` so a provider-facing UI
can explain the result without parsing prose. Codes never contain stack traces
or internal implementation detail.

Representative line/claim codes:

| Code                       | Severity | Meaning                                             |
|----------------------------|----------|-----------------------------------------------------|
| `FEE_SCHEDULE_APPLIED`     | info     | Allowed amount came from a contracted/fee schedule. |
| `BILLED_CHARGES_USED`      | warning  | No fee schedule matched; billed charges used.       |
| `CONTRACTUAL_ADJUSTMENT`   | info     | Billed − allowed write-off.                         |
| `DEDUCTIBLE_APPLIED`       | info     | Amount applied to the deductible.                   |
| `COPAY_APPLIED`            | info     | Flat copay applied.                                 |
| `COINSURANCE_APPLIED`      | info     | Coinsurance percentage applied.                     |
| `OUT_OF_POCKET_MAX_APPLIED`| info     | Patient responsibility capped at OOP max.           |
| `PRIOR_AUTH_REQUIRED`      | warning  | Service typically requires prior authorization.     |
| `NON_COVERED_SERVICE`      | denial   | Service not covered under the plan (CARC 96).       |
| `FREQUENCY_LIMITATION`     | denial   | Visit/day/dollar limit exceeded (CARC 119).         |
| `NO_BENEFIT_MAPPING`       | denial   | Procedure has no benefit category (CARC 16/18).     |
| `PROVIDER_EXCLUDED`        | warning  | Provider on a federal exclusion list.               |

Line `status` is one of `payable`, `not_covered`, `denied`, `needs_review`.

## Security & tenancy

- Tenant context is taken from the authenticated request (JWT `tenant_id` claim
  or `X-Tenant-ID` header) via `TenantMiddleware`, exactly like the existing
  adjudication APIs. A tenant id in the request body **cannot** override it.
- No PHI is logged. Tracing uses `ChoActivitySource`, which hashes member/claim
  identifiers before tagging spans.
- Money is represented with `decimal` throughout.

## Example request

```json
POST /api/v1/adjudication/estimate
X-Tenant-ID: demo-health-plan

{
  "requestId": "estimate-123",
  "memberId": "member-123",
  "benefitPlanId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "providerNpi": "1234567890",
  "serviceDate": "2026-08-15",
  "claimType": "Dental",
  "lineOfBusiness": "Dental",
  "lines": [
    {
      "lineNumber": 1,
      "procedureCode": "D2392",
      "codeType": "CDT",
      "chargeAmount": 275.00,
      "units": 1,
      "toothNumber": "30",
      "toothSurface": "MO"
    }
  ]
}
```

The exact JSON shape is not required to be dental-specific — the same endpoint
supports professional (`837P`) and institutional (`837I`) claims. Dental line
detail (CDT code, tooth number, surface, quadrant) is preserved on the request
for future dental adjudication.

## Example response

```json
{
  "requestId": "estimate-123",
  "status": "estimated",
  "authority": "Simulation",
  "currency": "USD",
  "totals": {
    "billedAmount": 275.00,
    "allowedAmount": 210.00,
    "contractualAdjustment": 65.00,
    "payerResponsibility": 168.00,
    "patientResponsibility": 42.00,
    "deductibleAmount": 0.00,
    "copayAmount": 0.00,
    "coinsuranceAmount": 42.00
  },
  "lines": [
    {
      "lineNumber": 1,
      "procedureCode": "D2392",
      "billedAmount": 275.00,
      "allowedAmount": 210.00,
      "contractualAdjustment": 65.00,
      "payerResponsibility": 168.00,
      "patientResponsibility": 42.00,
      "deductibleAmount": 0.00,
      "copayAmount": 0.00,
      "coinsuranceAmount": 42.00,
      "status": "payable",
      "toothNumber": "30",
      "messages": [
        { "code": "FEE_SCHEDULE_APPLIED", "severity": "Info", "description": "Allowed amount from Commercial PPO (InNetwork)." },
        { "code": "CONTRACTUAL_ADJUSTMENT", "severity": "Info", "description": "Contractual adjustment of $65.00 between billed and allowed amounts." },
        { "code": "COINSURANCE_APPLIED", "severity": "Info", "description": "Coinsurance of $42.00 (20 %)." }
      ]
    }
  ],
  "warnings": [],
  "confidence": {
    "level": "High",
    "reasons": ["Benefit plan resolved", "Accumulator data available", "Provider fee schedule resolved", "Provider integrity verified"],
    "missingData": []
  },
  "disclaimer": "Estimate only. Final payment depends on eligibility, benefits, accumulators, coordination of benefits, other claims, authorization state, and claim state at adjudication time."
}
```

## Limitations

- **Eligibility** is not re-verified inside the estimate; the disclaimer covers
  eligibility drift between the estimate and real claim time. Callers that need
  a hard eligibility answer should call the eligibility service.
- **Terminology crosswalk** (plan-specific code overrides) is applied by the
  full adjudication pipeline but is not yet wired into the estimate; estimates
  price on the submitted procedure code. This is a candidate follow-up for
  plans where crosswalk materially changes the rate (e.g. TX Medicaid).
- **COB** is not modeled in the estimate; secondary/tertiary payment is a claim
  time concern.
- Advisory PA/integrity findings are **warnings**, not denials — an estimate
  never rejects the request.
