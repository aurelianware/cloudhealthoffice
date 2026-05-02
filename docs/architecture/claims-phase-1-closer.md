# Claims Phase 1 — Closer

**Status:** ✅ Complete (May 2026)
**Scope:** Capabilities 5.1a through 5.12b — full claim lifecycle
**PR sequence:** #725, #728, #729, #731 (#732 follow-up), #733, #734, #736, #737, #738, #739, #740, #741, #742, #743

This document closes Claims Phase 1. It is a **navigation hub**, not a
substitute for the per-capability architecture docs at
[`docs/architecture/claim-*.md`](.) — those remain the source of truth
for implementation detail, decision ratification, and per-capability
Phase 2 deferrals. This closer narrates the lifecycle as a single
operational journey, indexes the cross-cutting patterns, and points
readers outward.

For the broader Cloud Health Office (CHO) product context — four
product lines: Public Tools, Transactional Services, Managed Data
Services, Platform Engagement — see
[`docs/POSITIONING.md`](../POSITIONING.md). Claims is part of
**Platform Engagement Layer 1**. Subsequent uses of "CHO" in this
document refer to Cloud Health Office.

For the complete Phase 2 work backlog harvested from per-capability
deferrals, see
[`docs/roadmap/claims-phase-2-backlog.md`](../roadmap/claims-phase-2-backlog.md).

For CMS-0057-F readiness posture, see
[`docs/compliance/claims-cms-0057-f-readiness.md`](../compliance/claims-cms-0057-f-readiness.md).

For the canonical V1 API surface, see
[`docs/api/claims-v1-surface.md`](../api/claims-v1-surface.md).

For the 5.1b operator runbook (Cosmos partition migration to
`/tenantId`), see
[`docs/migrations/claims-cosmos-partition-migration.md`](../migrations/claims-cosmos-partition-migration.md).

---

## Capability matrix

| ID | PR | Status | Summary | Per-capability doc |
|----|-----|--------|---------|---------------------|
| **5.1a** | [#725](https://github.com/aurelianware/cloudhealthoffice/pull/725) | ✅ | Claim identity + version-chain foundation. `Claim.Id` is per-version (renamed from prior `_id` reuse); appends to a Mongo `claim_version_events` stream as the system-of-record version chain; introduces `IClaimVersionEventPublisher`. | [claim-versioning.md](./claim-versioning.md) |
| **5.1b** | [#743](https://github.com/aurelianware/cloudhealthoffice/pull/743) | ✅ | Cosmos partition-key migration to `/tenantId` for the `Claims` container. Pattern parity with Provider/BP/AiExaminationAudit migrations. Operator-driven runbook ships in `docs/migrations/`. | [claim-versioning.md](./claim-versioning.md) (5.1b section) |
| **5.2** | [#728](https://github.com/aurelianware/cloudhealthoffice/pull/728) | ✅ | Adapter pattern foundation. `IClaimAdapter` + `AdapterClaim` insulate engine surfaces (NCCI, COB, AI examiner, scrub) from the persisted `Claim` shape. Fourth instance of the cross-service adapter pattern. | [claim-adapter-pattern.md](./claim-adapter-pattern.md) |
| **5.3** | [#729](https://github.com/aurelianware/cloudhealthoffice/pull/729) | ✅ | Canonical V1 submission API. `POST /api/v1/claims` on `ClaimsV1Controller` ships as the canonical surface (accepts `AdapterClaim`); the versionless legacy `POST /api/claims` on `ClaimsController` is preserved as `[Obsolete]` with RFC 8594 `Deprecation` / `Link` headers and routes through the same `IClaimSubmissionService` for continuous audit chain. Routes 277CA generation through `IClaimAcknowledgmentService`. | [claim-submission-api.md](./claim-submission-api.md) |
| **5.4** | [#734](https://github.com/aurelianware/cloudhealthoffice/pull/734) | ✅ | Pre-adjudication scrubbing stage (`ClaimsScrubEngine` C# port). Decommissions standalone `claims-scrubbing-service` — scrubbing is now in-process at the pipeline boundary. | [claim-scrubbing-pipeline.md](./claim-scrubbing-pipeline.md) |
| **5.5** | [#731](https://github.com/aurelianware/cloudhealthoffice/pull/731) (+ [#732](https://github.com/aurelianware/cloudhealthoffice/pull/732)) | ✅ | Adjudication pipeline foundation. Order-driven orchestrator (`IClaimAdjudicationOrchestrator`); `IClaimAdjudicationStage` contract; ships `BenefitCalculationStage` (Order=300) and the terminal `PersistenceStage` (Order=999) which projects `AdjudicationResult` + `PendDetails`. | [claim-adjudication-pipeline.md](./claim-adjudication-pipeline.md) |
| **5.6** | [#733](https://github.com/aurelianware/cloudhealthoffice/pull/733) | ✅ | Network & credentialing enforcement stage. First production `EnforcementOutcome` projection; cached resolution-client pattern for provider lookup. | [claim-adjudication-pipeline.md](./claim-adjudication-pipeline.md) (NetworkStage section) |
| **5.7** | [#736](https://github.com/aurelianware/cloudhealthoffice/pull/736) | ✅ | NCCI / MUE edits enforcement. Projection-metadata bypass extension. First production consumer of engine-suggested CARC/RARC codes that 5.10 surfaces in 835. | [claim-ncci-pipeline.md](./claim-ncci-pipeline.md) |
| **5.8** | [#737](https://github.com/aurelianware/cloudhealthoffice/pull/737) | ✅ | Coordination of Benefits stage with Phase 2 hook stub. Detection-only posture: CHO-secondary scenarios pend with stable reason `cob-secondary-not-supported-phase-1`. CHO-primary calculation completes fully via `CobEngine`. | [claim-cob-pipeline.md](./claim-cob-pipeline.md) |
| **5.9** | [#738](https://github.com/aurelianware/cloudhealthoffice/pull/738) | ✅ | AI-Backed Examination pipeline stage. Two enforcement modes (Off, SoftValidation). NCCI ModifierOverridePresent scope. Per-tenant `AiExaminationAudit` repository. | [claim-ai-examination.md](./claim-ai-examination.md) |
| **5.10** | [#740](https://github.com/aurelianware/cloudhealthoffice/pull/740) | ✅ | Operator-initiated batched 835 remittance + cross-service finalize. `PaymentRun` aggregate; `BatchEraGeneratorService`; `IClaimFinalizationService` via typed HttpClient (`FinalizeAsync` idempotent dual-emit). | [claim-remittance-generation.md](./claim-remittance-generation.md) |
| **5.11** | [#739](https://github.com/aurelianware/cloudhealthoffice/pull/739) | ✅ | FHIR `ExplanationOfBenefit` projection. Read-only authenticated surface. `ExplanationOfBenefitProjector` (sixth FHIR projector instance). Coverage reference, AI supportingInfo, CARC/RARC adjudication. | [claim-fhir-projection.md](./claim-fhir-projection.md) |
| **5.12a** | [#741](https://github.com/aurelianware/cloudhealthoffice/pull/741) | ✅ | Adjustment Workflow chain + re-adjudication. `ClaimAdjustment` aggregate; predecessor chain (depth=1 enforced); `AwaitingReadjudication → PendingReversal` lifecycle; pipeline-stage DI replacement. | [claim-adjustment-workflow.md](./claim-adjustment-workflow.md) |
| **5.12b** | [#742](https://github.com/aurelianware/cloudhealthoffice/pull/742) | ✅ | `ReversalRun` batched 835 reversal + lifecycle wiring. Negative 835 envelope (CLP02=22 reversal pattern); `VoidAsync` idempotent dual-emit; pattern parity with PaymentRun. | [claim-reversal-run.md](./claim-reversal-run.md) |

---

## End-to-end lifecycle — operational narrative

A single claim's journey through Phase 1 spans four operational
boundaries: **submit**, **adjudicate**, **pay**, and (where needed)
**adjust** or **reverse**. Each boundary is owned by one or more
capabilities; the closer narrates the hand-offs.

### 1. Submit (capabilities 5.1a, 5.1b, 5.2, 5.3)

A claim arrives via `POST /api/v1/claims`. Submission:

1. Persists a `Claim` document with versioning fields populated
   (5.1a) — `OriginalClaimId`, `ClaimVersionId`, `VersionNumber`,
   `ClaimVersionState`, `EventId`. The version chain is maintained
   in the Mongo `claim_version_events` append-only stream.
2. On Cosmos-backed deployments, persists into the `ClaimsV2`
   container partitioned by `/tenantId` (5.1b). The migration runbook
   covers the cutover from the legacy `/memberId`-partitioned
   container.
3. Emits a 277CA acknowledgment via `IClaimAcknowledgmentService`
   (pull-shaped per Decision in 5.3 — event-driven 277CA emission is
   Phase 2).
4. Adapter contracts (`IClaimAdapter`, `AdapterClaim`) make the
   submitted claim consumable by the adjudication engines without
   leaking the persisted `Claim` shape (5.2).

The submission API surface — including the legacy `ClaimsV1Controller`
paths preserved with `[Obsolete]` — is enumerated in
[`docs/api/claims-v1-surface.md`](../api/claims-v1-surface.md).

### 2. Adjudicate (capabilities 5.4, 5.5, 5.6, 5.7, 5.8, 5.9)

Submission triggers the 7-stage adjudication pipeline. Stages
execute in `IClaimAdjudicationStage.Order` ascending; each either
passes the context forward or pends/denies the claim with a stable
machine reason. The orderings below are the live values in code.

| Order | Stage | Capability | Outcome on failure |
|-------|-------|-----------|---------------------|
| 100 | `ScrubbingStage` | 5.4 | `Pend` with `ScrubbingResult` reason codes |
| 200 | `NetworkCredentialingStage` | 5.6 | `Pend` with `EnforcementOutcome` |
| 300 | `BenefitCalculationStage` | 5.5 | Calculates allowed/copay/coinsurance via `IBenefitCalculationEngine`; failures pend with calculation-error reasons |
| 400 | `NcciEditsStage` | 5.7 | `Pend` with `PendDetails.EditFailures` (suggested CARC/RARC populated) |
| 500 | `CoordinationOfBenefitsStage` | 5.8 | `Pend` with `cob-secondary-not-supported-phase-1` (CHO-secondary) or proceed (CHO-primary) |
| 600 | `AiExaminationStage` | 5.9 | Soft `Pend` with `AiExamination` advisory (Off/SoftValidation modes only) |
| 999 | `PersistenceStage` | 5.5 | Terminal write — projects `AdjudicationResult`, `PendDetails`, `EnforcementOutcome`, `AiExamination` onto the `Claim` document |

The orchestrator emits `ClaimVersionAdjudicated` at the terminal stage
boundary. CHO-primary claims that pass all stages reach `Adjudicated`
state with `AdjudicationResult.Disposition` of `Approved` or
`PartiallyPaid`. CHO-secondary claims emit the Phase 2 detection signal
via Pend; the work-queue surface routes them to manual COB review.

### 3. Pay (capability 5.10)

Operators initiate payment via `PaymentRunsController.Execute`. A
`PaymentRun` aggregate:

1. Selects `Adjudicated` claims (Approved / PartiallyPaid) that match
   the run's tenant + trading-partner + payment-date filters.
2. Calls `IClaimFinalizationService.FinalizeAsync(claimId)` per claim
   via the typed `ClaimsServiceHttpClient` (cross-service HTTP-only
   contract, idempotent dual-emit — first call performs the
   transition + emits Kafka `claims.finalized.v1`; subsequent calls
   are no-ops).
3. Generates a single batched 835 envelope via
   `BatchEraGeneratorService` and persists an `EraEnvelopeRecord`
   with the EDI string, CLP/CAS/PLB segments, and per-claim line
   detail (CARC/RARC sourced from 5.7's `SuggestedCarc`/`SuggestedRarc`
   when populated, or `CarcRarcMappingService` defaults otherwise).
4. Aggregates per-claim warnings (cross-service finalize failures,
   trading-partner resolution misses) into `PaymentRun.Warnings`
   without failing the run.

Generated EDI is retrievable via `GET /api/v1/era-envelopes/{id}/edi`
(no transmission to trading partner in Phase 1 — sFTP/Availity is
Phase 2).

### 4. Adjust or reverse (capabilities 5.12a, 5.12b)

When a claim needs correction post-adjudication, an operator initiates
an adjustment via `POST /api/v1/claims/{predecessorClaimId}/adjustments`.
The flow:

1. **Adjustment chain (5.12a)** — `ClaimAdjustment` aggregate persists
   the predecessor reference, transitions the claim to
   `AwaitingReadjudication`, and re-runs the 7-stage pipeline.
   Successful re-adjudication emits
   `ClaimVersionAdjudicated` (the new version becomes the chain head).
   Depth is capped at 1 in Phase 1 (Mongo unique index on
   `(TenantId, ClaimVersionId)`).
2. **Reversal run (5.12b)** — When re-adjudication produces a different
   payment outcome than the predecessor (e.g., overpayment recovery
   needed), the adjustment lifecycle marks the predecessor
   `PendingReversal`. Operators initiate a `ReversalRun` via
   `ReversalRunsController.Execute`. Each pending-reversal predecessor
   gets a negative 835 envelope (CLP02=22 reversal CLP) generated by
   `BatchEraGeneratorService` in reversal mode; `VoidAsync` is called
   on each predecessor (idempotent dual-emit, mirrors 5.10's
   `FinalizeAsync` shape).

The re-adjudicated chain head and the voided predecessor are
distinct claim versions — the version chain (5.1a) carries both;
FHIR `ExplanationOfBenefit` reads (5.11) reflect the latest version.

The accumulator-service consumer of `claims.finalized.v1` is **not**
extended to consume `ClaimVersionReversed` events in Phase 1 — drift
in member-year accumulators on reversal is accepted via the BP
engine's reconciliation path (5.12a Decision D16). See
[Phase 2 backlog](../roadmap/claims-phase-2-backlog.md) item
**Accumulator reversal consumer**.

### 5. FHIR projection (capability 5.11)

Independent of the lifecycle states above, `GET /fhir/ExplanationOfBenefit/{id}`
projects the latest `Claim` version into a FHIR R4 EOB resource.
Authenticated, tenant-scoped, read-only. Includes Coverage reference
(forward-compat — coverage-service has no FHIR Coverage projection
yet), AI examination supportingInfo (Decision 5: advisory disposition
+ ConfidenceScore + ModelId + PromptVersion only — no Rationale or
PolicyCitations until a redaction/review gate ships), CARC/RARC
adjudication entries.

`_history`, `_lastUpdated`, `_include`, and unauthenticated CMS-0057-F
public access are Phase 2 — see
[Phase 2 backlog](../roadmap/claims-phase-2-backlog.md) and
[CMS-0057-F readiness](../compliance/claims-cms-0057-f-readiness.md).

---

## Architectural patterns — index

Phase 1 ships against 14 cross-cutting patterns. Each pattern's
listing identifies the capability instances; per-capability docs
carry the implementation detail.

### 1. Versioned entities + append-only event chain

Pattern parity with Provider and BenefitPlan. `Claim` carries
versioning fields; `claim_version_events` stream is the
system-of-record version chain. **Instances:** all 14 capabilities
read or transition the chain. **Doc:** [claim-versioning.md](./claim-versioning.md).

### 2. Adapter pattern (4 instances total in CHO)

Insulates engine surfaces from persisted entity shapes.
**Claims instance:** `IClaimAdapter` / `AdapterClaim` (5.2). Other
instances live in Provider, BenefitPlan, AiExaminationAudit
domains. **Doc:** [claim-adapter-pattern.md](./claim-adapter-pattern.md).

### 3. Projection-metadata bypass (5 instances total)

For documents where a single field projection is required without
loading the full document. **Claims instances:** 5.1a (versioning
projection), 5.7 (NCCI projection bypass extension). **Doc:**
[claim-versioning.md](./claim-versioning.md), [claim-ncci-pipeline.md](./claim-ncci-pipeline.md).

### 4. Hosted index initializer pattern (5 instances total)

Background services that create Mongo indexes once at startup so
scoped repository resolution stays side-effect free. **Claims
instances:** `ClaimVersionEventIndexInitializer` (5.1a),
`ClaimAdjustmentIndexInitializer` (5.12a),
`AiExaminationAuditIndexInitializer` (5.9). **Doc:** referenced
across per-capability docs.

### 5. FHIR projector pattern (6 instances total)

Read-only resource projection from internal aggregates to FHIR R4.
**Claims instance:** `ExplanationOfBenefitProjector` (5.11). Other
instances: Practitioner/PractitionerRole/Organization (Provider),
InsurancePlan/Endpoint (BenefitPlan). **Doc:**
[claim-fhir-projection.md](./claim-fhir-projection.md).

### 6. HTTP-only cross-service contracts with typed HttpClient

Cross-service calls go through typed `HttpClient` wrappers, never
shared DLLs or Service Bus message contracts for synchronous
request/response. **Claims instances:** `ClaimsServiceHttpClient` in
payment-service (5.10, 5.12b — finalize + void); cached
resolution-client pattern instances for coverage / member /
provider lookups. See [Phase 2 backlog](../roadmap/claims-phase-2-backlog.md)
item **Typed `ClaimsServiceClient` extraction**.

### 7. Cached resolution-client pattern (5 pairs total)

In-memory TTL cache wrapping an HTTP client for lookup-style cross-
service calls. **Claims instances:** member, coverage, provider,
trading-partner resolution. **Doc:** referenced across
[claim-adjudication-pipeline.md](./claim-adjudication-pipeline.md) and
[claim-cob-pipeline.md](./claim-cob-pipeline.md).

### 8. Service Bus messaging via `IMessageBus`

Internal eventing where consumers can run alongside producers in
the same process or split out. **Claims usage:** lightweight; most
claim eventing flows through Kafka (pattern 9). **Doc:** referenced
in [claim-versioning.md](./claim-versioning.md).

### 9. Kafka messaging via `IClaimEventPublisher`

Cross-service event broadcast for lifecycle transitions. **Topics
in Phase 1:** `claims.adjudicated.v1`, `claims.finalized.v1`,
`claims.versions.reversed.v1`. The broader-stream
`claims.versions.v1` topic is Phase 2 (waiting for a real consumer).
**Doc:** [claim-versioning.md](./claim-versioning.md).

### 10. Pipeline-stage DI replacement (6 stages)

Each adjudication stage is registered as
`IClaimAdjudicationStage` and discovered by the orchestrator via
DI ordering. Replacement / reordering happens at registration time,
not by editing orchestrator code. **Doc:**
[claim-adjudication-pipeline.md](./claim-adjudication-pipeline.md).

### 11. Engine class library wiring (4 engines)

Domain logic engines are referenced as class libraries from the
service that needs them, not as separate microservices.
**Claims-touched engines:** `ClaimsScrubEngine` (5.4),
`NcciEngine` (5.7), `CobEngine` (5.8),
`BenefitCalculationEngine` (5.5+). **Doc:** referenced across
per-capability docs.

### 12. X12 outbound builder pattern (3 instances)

Pure functions that build EDI X12 strings from input aggregates.
**Claims instances:** 277CA acknowledgment (5.3), 835 batched
payment (5.10), 835 batched reversal (5.12b). **Doc:**
[claim-remittance-generation.md](./claim-remittance-generation.md),
[claim-reversal-run.md](./claim-reversal-run.md).

### 13. Operator-initiated batch workflow (2 instances)

Long-running batched operations with explicit operator initiation,
status tracking aggregate, idempotent execute endpoint, cancel
semantics, per-item warnings. **Claims instances:** `PaymentRun`
(5.10), `ReversalRun` (5.12b). **Doc:**
[claim-remittance-generation.md](./claim-remittance-generation.md),
[claim-reversal-run.md](./claim-reversal-run.md).

### 14. Idempotent state-transition endpoint with dual-emit

State transitions persist + emit Kafka in a single dual-emit
sequence; second call is a no-op. **Claims instances:**
`FinalizeAsync` (5.10), `VoidAsync` (5.12b). **Doc:**
[claim-remittance-generation.md](./claim-remittance-generation.md),
[claim-reversal-run.md](./claim-reversal-run.md).

---

## Phase 1 → Phase 2 boundary

The Phase 1 → Phase 2 boundary is drawn at the natural
"functionality complete for the operational lifecycle" line.
Phase 2 picks up at the **completeness, integration depth, and
public-access** edges. Categorized highlights below; the canonical
list lives in
[`docs/roadmap/claims-phase-2-backlog.md`](../roadmap/claims-phase-2-backlog.md).

- **Inbound EDI ingest** — 837P/837I parsing, 277 chaining for 835
  transmission status. Phase 1 is outbound-only for the trading-
  partner surface and accepts canonical JSON for inbound.
- **FHIR completeness** — `_history`, `_lastUpdated` and other
  search parameters, full Patient Access API surface, AI rationale
  exposure with redaction gate.
- **CMS-0057-F unauthenticated public access** — Phase 1 is
  authenticated-only; January 2027 mandate timeline tracked in the
  compliance readiness doc.
- **COB Phase 2 priorEob calculation** — CHO-secondary persisted
  calculation, 835 CAS OA/23 codes, FHIR EOB secondary-payer
  fields, coverage-service contract fixes.
- **AI examiner enhancements** — `Required` enforcement mode (gated
  on Kafka availability signal), broader scope beyond NCCI
  ModifierOverridePresent.
- **accumulator-service consumer extension** for
  `ClaimVersionReversed` events.
- **Operational** — auto-reverse mode, programmatic adjustment
  triggers, multi-envelope-per-file 835, per-claim 835 mode,
  adjustment chain depth > 1.
- **Trading-partner transmission** — sFTP / Availity envelope
  delivery, 277 ack chaining, BPR banking-field surface on
  TradingPartner.
- **NCCI seed-data quarterly import workflow.**
- **Old `Claims` Cosmos container deletion** (30-day retention from
  5.1b cutover; follow-up Bicep PR).
- **Payments container `/memberId` divergence** — same-shape future
  migration when payment-service operational pressure justifies.

---

## Diligence-readiness checklist

For external diligence consumers (investors, acquirers, regulators,
prospective enterprise customers), Phase 1 close represents the
following posture:

| Dimension | Posture |
|-----------|---------|
| **Architecture** | 14 cross-cutting patterns documented. Per-capability decisions ratified. Versioning + adapter + projection patterns enable safe evolution. |
| **Functional completeness** | Full claim lifecycle operational end-to-end: submit → adjudicate → pay → adjust → reverse. CHO-primary scenarios fully calculated; CHO-secondary detected and pended for Phase 2. |
| **Compliance** | CMS-0057-F authenticated readiness for the FHIR EOB read surface. Provider directory (machine-readable) shipped via Provider Phase 1. Patient Access unauthenticated access and `_history` are Phase 2. January 2027 mandate timeline. See [readiness doc](../compliance/claims-cms-0057-f-readiness.md). |
| **Security** | Tenant-scoped via `TenantMiddleware`. Authentication required on all API surfaces. AI examination Rationale and PolicyCitations gated behind redaction/review (Phase 2). |
| **Scalability** | Cosmos `/tenantId` partitioning shipped (5.1b). Mongo append-only event chains. Operator-initiated batch workflows (PaymentRun, ReversalRun) avoid synchronous fan-out. EDI envelopes within 16MB Mongo doc limit at Phase 1 batch sizes. |
| **Observability** | OpenTelemetry counter coverage; per-stage adjudication telemetry; `cho_secondary_detected` / `cho_tertiary_detected` for Phase 2 sizing; per-run warnings array on PaymentRun / ReversalRun. |
| **Operational maturity** | One operator runbook shipped (Cosmos partition migration, 5.1b). PaymentRun / ReversalRun lifecycle UIs in portal. Idempotent dual-emit pattern on every state-transition endpoint. |
| **Test posture** | Per-capability integration + unit suites green. WebApplicationFactory smoke tests on the V1 surface. Engine-side tests on NcciEngine, CobEngine, BenefitCalculationEngine, EncounterEngine. |
| **Recovery posture** | Per-capability recovery sections name failure modes + revert paths. Cosmos migration retains the legacy container 30 days. Reversal pattern provides operator-initiated rollback for paid claims. |

---

## What this closer does **not** do

- **Does not introduce new architectural decisions.** Per-capability
  decisions ratified in their per-capability docs are the source of
  truth. This closer narrates and indexes; it doesn't argue.
- **Does not modify per-capability docs** (except for incidental
  cross-link additions via this closer). The 12 per-capability
  architecture docs remain unchanged.
- **Does not modify behavior.** No code change in the lifecycle path.
  No Bicep, Service Bus, Kafka, or Cosmos partition strategy
  changes. No DI lifetime changes.
- **Does not modify the broader CHO commercial-readiness roadmap**
  (`docs/roadmap/CHO-Roadmap-Readme.md`). That roadmap's "Phase 1"
  refers to commercial-readiness milestones (Hardening & Credibility,
  Weeks 1–4) — a different taxonomy than the Claims-domain
  Phase 1 / Phase 2 split documented here.

---

## Future closures

The closer pattern established by this document is reusable for any
service-level or domain-level Phase 1 / Phase 2 closure. Sections to
mirror:

1. **Capability matrix** with PR cross-references
2. **End-to-end operational narrative** — the lifecycle as journey
3. **Architectural patterns index** — instances per pattern
4. **Phase boundary** — explicit named deferrals pointing to a
   roadmap registry
5. **Diligence-readiness checklist** — multi-dimensional posture

`docs/status/MODULE-STATUS.md` tracks which services have closed
which phases.
