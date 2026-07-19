# Adjudication Pipeline

The claims adjudication pipeline is CHO's end-to-end claim processing system. It orchestrates pre-adjudication validation, multi-engine adjudication, and post-adjudication routing through an Argo Workflow DAG backed by a unified C# endpoint in the benefit-plan-service.

## Table of contents

- [Overview](#overview)
- [Argo Workflow DAG](#argo-workflow-dag)
- [AdjudicationController pipeline](#adjudicationcontroller-pipeline)
- [Engine inventory](#engine-inventory)
- [Claim type routing](#claim-type-routing)
- [Operating mode (Augment / Replace)](#operating-mode-augment--replace)
- [AI Claims Examiner (pended claims)](#ai-claims-examiner-pended-claims)
- [Request / response contracts](#request--response-contracts)
- [Observability](#observability)
- [Configuration reference](#configuration-reference)

---

## Overview

A claim enters the system via Kafka (`claims-adjudication` topic) and is processed through a 7-step Argo Workflow DAG. Steps 1–5 run pre-adjudication checks in parallel where possible. Step 6 calls the `AdjudicationController.Adjudicate()` endpoint, which orchestrates all pricing and cost-sharing engines in a single HTTP round-trip. Step 7 writes the result back to claims-service.

The controller runs the following engines in sequence via DI:

1. **ClaimsScrubEngine** — Pre-payment validation and claim routing
2. **NcciEngine** — NCCI Column 1/Column 2 pair edits and MUE maximum units
3. **ProviderVerificationEngine** — OIG/LEIE/SAM.gov exclusion screening
4. **PriorAuthRuleEngine** — Prior authorization requirement evaluation
5. **TerminologyService** — Plan-specific procedure code crosswalk
6. **FeeScheduleEngine** — Allowed amount resolution (6 pricing methods)
7. **BenefitCalculationEngine** — Full cost-sharing waterfall (deductible → copay → coinsurance → OOP max)
8. **CobEngine** — Coordination of benefits (complementary + non-duplication models)
9. **ProviderEnrollmentService** — State Medicaid enrollment verification gate

---

## Argo Workflow DAG

```
837 Claim Ingested (Kafka → Argo Event)
        │
        ▼
┌─── Argo Workflow DAG ─────────────────────────────────────────────┐
│                                                                    │
│  ┌────────────┐                                                    │
│  │ 1. get-claim│                                                   │
│  └──────┬─────┘                                                    │
│         │                                                          │
│  ┌──────┴──────────────┬───────────────────────┐                   │
│  ▼                     ▼                       ▼                   │
│ 2. verify-coverage   3. validate-provider   4. validate-codes      │
│  │                     │                       │                   │
│  └─────────────────────┤           5. check-prior-auth ◄───────┘   │
│                        │                       │                   │
│                        └───────┬───────────────┘                   │
│                                ▼                                   │
│                    6. adjudicate (single POST)                     │
│                                │                                   │
│                                ▼                                   │
│                    7. update-claim                                  │
│                     ┌──────────┴──────────┐                        │
│                     ▼                     ▼                        │
│              Approved/Denied          Pended (NCCI)                │
│                     │              Kafka: claims.pended.v1         │
└─────────────────────┼──────────────────────┼──────────────────────┘
                      │                      │
                      ▼                      ▼
               claims-service        claims-examiner-service
                                     (AI advisory → human review)
```

**Step details:**

| Step | Service called | Parallel with | Short-circuits on |
|------|---------------|---------------|-------------------|
| 1. get-claim | claims-service | — | — |
| 2. verify-coverage | coverage-service | 3, 4 | No active coverage → CARC 26 |
| 3. validate-provider | provider-service | 2, 4 | Not credentialed → CARC 185 |
| 4. validate-codes | reference-data-service | 2, 3 | Invalid codes → CARC 11 |
| 5. check-prior-auth | authorization-service | — (depends on 2, 4) | Auth required but missing, expired, inactive, or not valid for the procedure when authorization-service has evidence → CARC 197 |
| 6. adjudicate | benefit-plan-service | — (depends on 2, 3, 5) | See engine pipeline below |
| 7. update-claim | claims-service | — | — |

---

## AdjudicationController pipeline

`POST /api/v1/adjudication/adjudicate`

The controller receives the assembled claim data from the workflow and runs all engines sequentially. Each step can short-circuit the pipeline.

```
Request arrives
    │
    ▼
Routing Decision (tenant config → ClaimTypeRouter)
    │  CHO Replace / CHO Augment / Legacy Only
    │
    ▼ (Legacy Only returns immediately)
Step 0a: ClaimsScrubEngine
    │  422 → scrub failure
    ▼
Step 0b: NcciEngine
    │  422 → NCCI edit failure → pend for AI examiner
    ▼
Step 0c: ProviderVerificationEngine (OIG/LEIE/SAM.gov)
    │  422 → provider excluded from federal programs
    ▼
Step 0d: PriorAuthRuleEngine
    │  422 → prior auth required but not provided
    ▼
Step 0e: TerminologyService (code crosswalk)
    │  (enrichment only, never blocks)
    ▼
Step 1: FeeScheduleEngine
    │  Resolves allowed amounts per line
    ▼
Step 2: BenefitCalculationEngine (with OperatingMode)
    │  Cost-sharing waterfall + accumulator persistence
    │  In Augment mode: captures discrepancies vs. legacy
    ▼
Step 2b: COB (if secondary/tertiary payer)
    │
    ▼
Step 3: Merge → AdjudicationResponse
```

---

## Engine inventory

### Wired into adjudication pipeline

| Engine | Interface | Step | Failure behavior |
|--------|-----------|------|-----------------|
| ClaimsScrubEngine | `IClaimRoutingService` | 0a | 422 with validation errors |
| NcciEngine | `INcciEditService` | 0b | 422 with edit failures → pend |
| ProviderVerificationEngine | `IProviderIntegrityGate` | 0c | 422 with exclusion details |
| PriorAuthRuleEngine | `IPriorAuthRuleEngine` | 0d | 422 with PA requirement details |
| TerminologyService | `ITerminologyCrosswalkClient` | 0e | Passthrough on failure |
| FeeScheduleEngine | `IRateResolutionService` | 1 | UCR fallback (billed charges) |
| BenefitCalculationEngine | `IBenefitCalculationEngine` | 2 | Denial with CARC code |
| CobEngine | (inline in BenefitEngine) | 2b | — |
| ProviderEnrollmentService | `IEnrollmentDecisionGate` | gate | 422 with CARC 185 |

### Available but not in adjudication path

| Engine | Status | Path |
|--------|--------|------|
| RiskAdjustmentEngine | Implemented; validation pending | Post-adjudication (encounter submission) |
| EncounterEngine | Implemented; validation pending | Post-adjudication (encounter submission) |

---

## Claim type routing

The `IClaimTypeRouter` determines how a claim is processed based on its type (Professional/Institutional) and line of business, using the tenant's `OperatingModeConfiguration`.

**Routing resolution hierarchy:**

1. Compound key: `{claimType}-{lineOfBusiness}` (e.g., `professional-medicaid`)
2. Claim type key: `{claimType}` (e.g., `professional`)
3. Engine-level default: `benefitCalculation` mode

**Routing outcomes:**

| Route | Behavior |
|-------|----------|
| `ChoReplace` | CHO adjudicates, result is authoritative |
| `ChoAugment` | CHO adjudicates in shadow alongside QNXT, QNXT is authoritative |
| `LegacyOnly` | Return immediately, claim routed to QNXT |

**Example tenant configuration:**

```json
{
  "tenantId": "txmco01",
  "engines": {
    "professional-medicaid": "replace",
    "institutional-medicaid": "augment",
    "professional-commercial": "legacy",
    "benefitCalculation": "replace"
  }
}
```

---

## Operating mode (Augment / Replace)

The `OperatingMode` pattern enables progressive QNXT replacement per engine, per claim type, per tenant.

- **Replace mode**: CHO's result is authoritative. Legacy system not consulted.
- **Augment mode**: CHO runs in shadow. Both results computed and compared. Legacy remains authoritative. Discrepancies logged and surfaced in the response.

The `BenefitCalculationEngine.CalculateWithModeAsync()` method wraps results in `AugmentResult<T>`, which includes the CHO result, optional legacy result, discrepancies, and mode context.

See [OPERATING-MODE.md](OPERATING-MODE.md) for full details.

---

## AI Claims Examiner (pended claims)

When the NcciEngine returns edit failures (HTTP 422), the update-claim step routes the claim to the pend queue (`PUT /api/claims/{id}/pend`), which publishes a `claims.pended.v1` Kafka event. The `claims-examiner-service` consumes this event and produces an AI advisory recommendation.

**v1 scope:**

- Only processes pend code `NCCI` (modifier-addressable bundling edits)
- Calls Claude with forced tool use (`recommend_disposition`)
- Dispositions: Approve, Deny, RequestInfo, EscalateToHuman
- Never auto-applies — writes advisory to `PUT /api/claims/{id}/ai-examination`
- Human examiner reviews in the work queue

See [CLAIMS-EXAMINER-SERVICE.md](CLAIMS-EXAMINER-SERVICE.md) for full details.

---

## Request / response contracts

### AdjudicationRequest

| Field | Type | Description |
|-------|------|-------------|
| `claimId` | string | Unique claim identifier |
| `memberId` | string | Member ID from coverage verification |
| `subscriberId` | string | Subscriber ID |
| `benefitPlanId` | Guid | Benefit plan from coverage verification |
| `serviceDate` | DateOnly | Service date |
| `providerNpi` | string | Billing provider NPI |
| `networkTier` | NetworkTier | InNetwork / OutOfNetwork |
| `lineOfBusiness` | int? | 1=Commercial, 2=Medicare, 3=Medicaid |
| `claimType` | string | "Professional" (837P), "Institutional" (837I) |
| `stateCode` | string? | Jurisdiction (e.g., "TX") |
| `providerTaxonomy` | string? | Provider taxonomy for PA rule evaluation |
| `priorAuthorizationNumber` | string? | Auth number if on file |
| `lines` | List | Claim lines with procedure codes, amounts, modifiers |
| `cob` | AdjudicationCobInfo? | COB context for secondary/tertiary claims |

### AdjudicationResponse

| Field | Type | Description |
|-------|------|-------------|
| `claimId` | string | Claim identifier |
| `success` | bool | Whether the claim was approved |
| `denialReasonCode` | string? | CARC code on denial |
| `operatingMode` | string? | "Replace", "Augment", or "LegacyOnly" |
| `routingKey` | string? | Routing decision key that was matched |
| `isAuthoritative` | bool | Whether CHO's result is official |
| `discrepancies` | List | CHO vs. legacy differences (Augment mode) |
| `providerIntegrityScore` | int? | Provider integrity score (0-100) |
| `totals` | AdjudicationTotals | Billed, allowed, deductible, copay, coinsurance, plan payment |
| `lines` | List | Per-line adjudication detail |
| `accumulators` | List? | Updated accumulator state |

---

## Observability

Every step emits an OpenTelemetry span with structured tags:

| Tag | Description |
|-----|-------------|
| `cho.claim_type` | "Professional" or "Institutional" |
| `cho.operating_mode` | "Replace" or "Augment" |
| `cho.routing.route` | "ChoReplace", "ChoAugment", "LegacyOnly" |
| `cho.routing.key` | Matched routing key |
| `cho.outcome` | "approved", "denied", "scrub_failure", "ncci_failure", "provider_excluded", "pa_denied", "legacy_routed" |
| `cho.integrity.score` | Provider integrity score |
| `cho.integrity.excluded` | Whether provider is on exclusion list |
| `cho.pa.outcome` | Prior auth rule decision |
| `cho.benefit.authoritative` | Whether CHO result is authoritative |
| `cho.benefit.discrepancy_count` | Number of CHO vs. legacy discrepancies |

The `ChoMetrics.AdjudicationOutcome` counter tracks outcomes by claim type and operating mode.

---

## Configuration reference

### benefit-plan-service appsettings.json

```json
{
  "Services": {
    "ClaimsServiceUrl": "http://claims-service:8080",
    "TenantServiceUrl": "http://tenant-service:8080",
    "ProviderVerificationServiceUrl": "http://provider-verification-service:8080",
    "TerminologyServiceUrl": "http://terminology-service:8080"
  },
  "Redis": {
    "ConnectionString": "redis:6379"
  },
  "PriorAuthRuleEngine": {
    "RuleSetCacheTtlMinutes": 15,
    "GoldCardLookbackDays": 180,
    "PendOnRuleError": true
  },
  "ProviderEnrollmentService": {
    "TenantConfigCacheTtlSeconds": 300,
    "EnabledStateCodes": ["TX", "FL", "CA", "NY"]
  }
}
```

### Tenant operating mode configuration

Managed via `PUT /api/tenants/{tenantId}/operating-mode` on tenant-service.

```json
{
  "engines": {
    "professional-medicaid": "replace",
    "institutional-medicaid": "augment",
    "professional-commercial": "legacy",
    "benefitCalculation": "replace",
    "rateResolution": "replace",
    "ncciEdits": "replace",
    "claimsScrubbing": "replace",
    "priorAuthRules": "replace",
    "providerVerification": "replace",
    "terminologyCrosswalk": "replace"
  }
}
```

---

## Related documentation

- [FEE-SCHEDULE-ENGINE.md](../engines/FEE-SCHEDULE-ENGINE.md) — Rate resolution chain and pricing methods
- [ACCUMULATOR-ENGINE.md](../engines/ACCUMULATOR-ENGINE.md) — Redis accumulator design
- [OPERATING-MODE.md](OPERATING-MODE.md) — Augment/Replace pattern details
- [CLAIMS-EXAMINER-SERVICE.md](CLAIMS-EXAMINER-SERVICE.md) — AI advisory layer
