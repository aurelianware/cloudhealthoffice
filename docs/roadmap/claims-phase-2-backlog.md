# Claims Phase 2 — Backlog Registry

**Status:** Open / living document
**Source:** Harvested from per-capability "Phase 2" / "deferred"
markers across the 12 architecture docs at
[`docs/architecture/claim-*.md`](../architecture/), plus
cross-capability deferrals named in
[`docs/architecture/claims-phase-1-closer.md`](../architecture/claims-phase-1-closer.md).

This document is the canonical source of truth for Cloud Health
Office (CHO) Claims-domain Phase 2 work. Items are added as new
Phase 1 work surfaces deferrals and as Phase 2 work itself surfaces
follow-ups. Sequencing is **indicative, not committed** — actual
sequencing depends on pilot priorities, regulatory deadlines, and
dependency resolution. Subsequent uses of "CHO" in this document
refer to Cloud Health Office.

For Phase 1 status, see
[`docs/architecture/claims-phase-1-closer.md`](../architecture/claims-phase-1-closer.md).

---

## Item categories

1. [Inbound EDI ingest & resubmission](#1-inbound-edi-ingest--resubmission)
2. [FHIR completeness](#2-fhir-completeness)
3. [CMS-0057-F public access](#3-cms-0057-f-public-access)
4. [COB priorEob calculation](#4-cob-prioreob-calculation)
5. [AI examiner enhancements](#5-ai-examiner-enhancements)
6. [Cross-service event-stream depth](#6-cross-service-event-stream-depth)
7. [Operational](#7-operational)
8. [Trading-partner transmission](#8-trading-partner-transmission)
9. [Reference-data workflows](#9-reference-data-workflows)
10. [Infrastructure & follow-ups](#10-infrastructure--follow-ups)

---

## 1. Inbound EDI ingest & resubmission

### 1.1 — 837P / 837I inbound parser

**Source:** 5.3 ([claim-submission-api.md](../architecture/claim-submission-api.md)),
5.10 ([claim-remittance-generation.md](../architecture/claim-remittance-generation.md)),
5.12b ([claim-reversal-run.md](../architecture/claim-reversal-run.md))

**Rationale:** Phase 1 accepts canonical JSON via `POST /api/v1/claims`.
Real-world payer integrations require X12 837P / 837I ingest.

**Scope estimate:** Large. Reuse `EncounterEngine`'s 837 transformer
(already ships in CHO; bidirectional). New service or in-process
parser at the claims-service boundary; correlate to canonical
`Claim` shape via the adapter pattern (5.2).

**Dependencies:** None (engine already exists).

**Sequencing:** Early Phase 2 — gates the first real EDI trading-
partner integration.

### 1.2 — Resubmission / correction workflow distinct from adjustment

**Source:** 5.10 ([claim-remittance-generation.md](../architecture/claim-remittance-generation.md))

**Rationale:** Phase 1 collapses correction into the adjustment
workflow (5.12a). Some payer trading-partner contracts require a
distinct resubmission flow with its own X12 codes (CLP02 = 7
Replacement vs CLP02 = 22 Reversal vs new claim).

**Scope estimate:** Medium. New `IResubmissionService` distinct from
`ClaimAdjustmentService`; CLP02=7 path through `BatchEraGeneratorService`.

**Dependencies:** 1.1 (837 inbound parser) for incoming corrected
claims.

**Sequencing:** Mid Phase 2.

### 1.3 — Provider-initiated 837 corrected claims

**Source:** Cross-capability (named in
[claims-phase-1-closer.md](../architecture/claims-phase-1-closer.md))

**Rationale:** Provider-initiated corrected-claim flow (frequency 7
in 837 BHT segment) is distinct from CHO-internal adjustment
workflow.

**Scope estimate:** Medium. Builds on 1.1 + 1.2.

**Sequencing:** After 1.1, 1.2.

---

## 2. FHIR completeness

### 2.1 — `_history` operation (per-version reads)

**Source:** 5.11 ([claim-fhir-projection.md](../architecture/claim-fhir-projection.md))

**Rationale:** Phase 1 EOB reads return the current claim version
only. The adjustment chain (5.12a) makes per-version FHIR history
meaningful — readers can see the original adjudication, the
adjustment that voided it, and the re-adjudicated successor.

**Scope estimate:** Small-Medium. `ExplanationOfBenefitProjector`
extension; repository `GetVersionHistoryAsync(originalClaimId)`;
`GET /fhir/ExplanationOfBenefit/{id}/_history` route.

**Dependencies:** 5.12a (already shipped).

**Sequencing:** Mid Phase 2.

### 2.2 — Search parameter expansion

**Source:** 5.11 ([claim-fhir-projection.md](../architecture/claim-fhir-projection.md))

**Rationale:** Phase 1 supports a minimal search-parameter set.
Phase 2 adds `_lastUpdated`, `created`, `provider`, `status`, `type`,
`identifier`, `_include`, `_revinclude`. Each requires a repository
search-seam expansion.

**Scope estimate:** Medium. Repository extensions per parameter;
search-parameter validation; `_include` / `_revinclude` reference
resolution.

**Sequencing:** Mid Phase 2; gates patient-access full surface.

### 2.3 — AI examination Rationale + PolicyCitations exposure

**Source:** 5.11 ([claim-fhir-projection.md](../architecture/claim-fhir-projection.md), Decision 5)

**Rationale:** Phase 1 surfaces only advisory disposition,
ConfidenceScore, ModelId, PromptVersion in EOB `supportingInfo`.
Rationale and PolicyCitations require a redaction / review gate
before patient-facing exposure (PHI risk; potential adversarial
content from prompt manipulation).

**Scope estimate:** Medium-Large. Redaction service; per-tenant
review-gate workflow; tenant config to enable; possibly a
PolicyCitation registry.

**Dependencies:** None gating; risk-adjusted before exposure.

**Sequencing:** Late Phase 2 — depends on customer-facing review
process maturing.

### 2.4 — `Coverage/{id}` resolved payload

**Source:** 5.11 ([claim-fhir-projection.md](../architecture/claim-fhir-projection.md), Decision 15)

**Rationale:** Phase 1 emits a structural Coverage reference; the
referenced resource doesn't resolve because coverage-service has no
FHIR Coverage projection yet. Forward-compat reference.

**Scope estimate:** Cross-service. New `CoverageProjector` in
coverage-service.

**Dependencies:** Coverage-service domain Phase 2 (out of claims
scope).

**Sequencing:** Cross-service coordination; not gated on claims.

### 2.5 — FHIR ExplanationOfBenefit COB extensions (secondary-payer fields)

**Source:** 5.8 ([claim-cob-pipeline.md](../architecture/claim-cob-pipeline.md))

**Rationale:** Phase 1 EOB projects CHO-primary only. Phase 2 priorEob
work surfaces secondary-payer fields on the FHIR resource.

**Scope estimate:** Small (gated on 4.x items below).

**Dependencies:** 4.1, 4.2.

---

## 3. CMS-0057-F public access

### 3.1 — Unauthenticated patient-access FHIR surface

**Source:** 5.11 ([claim-fhir-projection.md](../architecture/claim-fhir-projection.md))

**Rationale:** Phase 1 EOB read is authenticated, tenant-scoped
(mirrors BP 5.8 / Provider 5.7-5.9 posture). CMS-0057-F mandates
patient-facing unauthenticated access by **January 2027**.

**Scope estimate:** Large. SMART-on-FHIR or equivalent
patient-authentication surface; consent / OAuth2 grant flow; rate
limiting; audit logging for unauthenticated reads;
de-tenantization (or per-patient tenant resolution) at the
controller boundary.

**Dependencies:** Cross-service — Provider, BenefitPlan, Coverage,
Member also require unauthenticated surfaces for the Patient Access
API set.

**Sequencing:** **Compliance-deadline-driven**. Should land before
January 2027.

### 3.2 — Patient Access API surface beyond EOB

**Source:** 5.11 ([claim-fhir-projection.md](../architecture/claim-fhir-projection.md)),
named in [claims-phase-1-closer.md](../architecture/claims-phase-1-closer.md)

**Rationale:** CMS-0057-F Patient Access API requires Coverage,
member-demographic resources beyond claims. Provider directory and
formulary requirements are partly satisfied by Provider Phase 1
(Practitioner, PractitionerRole, Organization) and BP Phase 1
(InsurancePlan, Endpoint).

**Scope estimate:** Cross-service. Member, Coverage projections
needed.

**Dependencies:** Out of claims scope (Coverage / Member / Provider
domain Phase 2).

**Sequencing:** Coordination tracked in CMS-0057-F readiness doc.

---

## 4. COB priorEob calculation

### 4.1 — `ICobCalculationService` activation

**Source:** 5.8 ([claim-cob-pipeline.md](../architecture/claim-cob-pipeline.md), Decision 17)

**Rationale:** Phase 1 ships the `ICobCalculationService` interface
registered for forward-compat but not exercised by stage logic. Phase 2
priorEob work activates it — calculates CHO-secondary payer payment
given a primary EOB.

**Scope estimate:** Large. Activation requires structured priorEob
ingest, secondary-pricing rules, persistence on
`AdjudicationResult` (CobReduction, SecondaryPlanPayment,
PrimaryPayerPayment fields not yet on the model).

**Dependencies:** 4.2 (priorEob ingest), 4.3 (model extension).

**Sequencing:** Heart of COB Phase 2 work.

### 4.2 — priorEob ingest path

**Source:** 5.8 ([claim-cob-pipeline.md](../architecture/claim-cob-pipeline.md))

**Rationale:** Source of primary-payer EOB structured data. Could be
via CMS Patient Access cross-payer API, partner trading-partner
ingest, or operator upload.

**Scope estimate:** Medium-Large. Multi-source.

**Dependencies:** Cross-payer API specs (industry-wide).

### 4.3 — `AdjudicationResult` COB-secondary fields

**Source:** 5.8 ([claim-cob-pipeline.md](../architecture/claim-cob-pipeline.md))

**Rationale:** Phase 2 needs `CobReduction`, `SecondaryPlanPayment`,
`PrimaryPayerPayment` fields persisted on `AdjudicationResult`.
Schema-additive change.

**Scope estimate:** Small. Model + projection extension.

**Sequencing:** Gates 4.1.

### 4.4 — CobOutcome PersistenceStage projection

**Source:** 5.8 ([claim-cob-pipeline.md](../architecture/claim-cob-pipeline.md))

**Rationale:** Phase 1 keeps `CobOutcome` on the orchestration context
only (α posture, consistent with 5.4 ScrubbingResult). Phase 2 may
project it for audit + work-queue workflows.

**Scope estimate:** Small. PersistenceStage extension.

### 4.5 — 835 remittance COB CAS segments (OA / 23)

**Source:** 5.8 ([claim-cob-pipeline.md](../architecture/claim-cob-pipeline.md))

**Rationale:** OA/23 reduction codes flow through `BatchEraGeneratorService`
in Phase 2 alongside CHO-secondary calculation.

**Scope estimate:** Small. EDI builder extension.

**Dependencies:** 4.1.

### 4.6 — coverage-service `CobEntry.PayerId` semantic fix

**Source:** 5.8 ([claim-cob-pipeline.md](../architecture/claim-cob-pipeline.md), Decision D9.1)

**Rationale:** Phase 1 carries the lying field-name through unchanged
for telemetry continuity. Phase 2 priorEob work introduces a payer
registration and fixes the upstream field's source.

**Scope estimate:** Cross-service. coverage-service contract change.

### 4.7 — coverage-service empty-list wire semantic

**Source:** 5.8 ([claim-cob-pipeline.md](../architecture/claim-cob-pipeline.md), Decision D9.2)

**Rationale:** Phase 1 stage normalizes 404 → empty list inline; Phase 2
may move the semantic to the wire (200 + empty body).

**Scope estimate:** Small cross-service.

### 4.8 — Multi-payer beyond two-coverage scenarios

**Source:** Cross-capability (named in
[claims-phase-1-closer.md](../architecture/claims-phase-1-closer.md))

**Rationale:** Tertiary, quaternary scenarios are detected in Phase 1
(`cho_tertiary_detected` counter) but not calculated.

**Scope estimate:** Medium. Builds on 4.1.

### 4.9 — Reverse-direction CHO-as-primary payer registration

**Source:** 5.8 ([claim-cob-pipeline.md](../architecture/claim-cob-pipeline.md), Decision D9.3)

**Rationale:** Phase 1 shim populates literal `"CHO"` for CHO's
coverage. Real payer registration once platform onboards multiple
LOB payers.

**Scope estimate:** Cross-service.

---

## 5. AI examiner enhancements

### 5.1 — `Required` enforcement mode

**Source:** 5.9 ([claim-ai-examination.md](../architecture/claim-ai-examination.md), Gap H.3)

**Rationale:** Phase 1 ships Off + SoftValidation. `Required` mode
forks Pend-vs-Pass on AI advisory — but requires a Kafka availability
signal in publisher contract to avoid hard-blocking when AI service
is down.

**Scope estimate:** Medium. Publisher contract extension; per-tenant
config; circuit-breaker pattern.

**Dependencies:** Kafka consumer-side health-signal pattern (cross-
infrastructure).

### 5.2 — Broader scope beyond NCCI ModifierOverridePresent

**Source:** 5.9 ([claim-ai-examination.md](../architecture/claim-ai-examination.md))

**Rationale:** Phase 1 invokes AI examiner only when NCCI stage
detects modifier-override. Broader triggers (high-dollar claims,
unusual code combos, high-pend-rate providers) are Phase 2.

**Scope estimate:** Medium. Trigger config + new `IAiExaminationTrigger`
abstraction.

### 5.3 — Augment-mode comparison

**Source:** 5.5 ([claim-adjudication-pipeline.md](../architecture/claim-adjudication-pipeline.md))

**Rationale:** Phase 1 calls `IBenefitCalculationEngine.CalculateAsync`
without operating-mode variant. Augment-mode (compare against legacy
adjudication result) ships when a real legacy result is available.

**Scope estimate:** Small-Medium. Engine surface extension.

**Dependencies:** Real legacy adjudication source.

---

## 6. Cross-service event-stream depth

### 6.1 — accumulator-service `ClaimVersionReversed` consumer

**Source:** 5.12a ([claim-adjustment-workflow.md](../architecture/claim-adjustment-workflow.md), Decision D16)

**Rationale:** Phase 1 accepts member-year accumulator drift on
reversal; reconciles via BP engine path. Phase 2 extends
`accumulator-service.ClaimFinalizedConsumer` with EventId-keyed
dedup + signed deltas + a `Reverse` branch on `ApplyClaimFinalizedAsync`
(~150 LOC) so the snapshot stream becomes reversal-native.

**Scope estimate:** Small (~150 LOC named) — but cross-service.

**Sequencing:** Not gated on 5.12a or 5.12b — explicitly tracked as a
deferred follow-up.

### 6.2 — `claims.versions.v1` broader-stream Kafka topic

**Source:** 5.1a ([claim-versioning.md](../architecture/claim-versioning.md))

**Rationale:** Phase 1 emits `claims.adjudicated.v1`,
`claims.finalized.v1`, `claims.versions.reversed.v1`. A broader
`claims.versions.v1` stream covers all version transitions but isn't
emitted until a consumer materializes.

**Scope estimate:** Small. Refactor `ClaimEventPublisher` Kafka
emission path; new dual-emit.

**Dependencies:** Concrete consumer requirement.

### 6.3 — Event-driven 277CA emission

**Source:** 5.3 ([claim-submission-api.md](../architecture/claim-submission-api.md))

**Rationale:** Phase 1 277CA generation is pull-shaped via
`ClaimAcknowledgmentService`. Event-driven emission ships when a real
trading-partner consumer requires push semantics.

**Scope estimate:** Small.

### 6.4 — Phase 2 sweep job for orphaned adjustment lifecycle states

**Source:** 5.12b ([claim-reversal-run.md](../architecture/claim-reversal-run.md))

**Rationale:** Edge-case recovery for two failure modes named in 5.12b:
(a) adjustment-lifecycle callback throws after void persists, leaving
adjustment in source state; (b) orchestrator-callback throws inside
`EmitAdjudicatedEventAsync`, leaving adjustment in `AwaitingReadjudication`.
Sweep job re-drives based on terminal events in Mongo.

**Scope estimate:** Small.

---

## 7. Operational

### 7.1 — Programmatic / event-driven adjustment triggers

**Source:** 5.12a ([claim-adjustment-workflow.md](../architecture/claim-adjustment-workflow.md), Decision 1)

**Rationale:** Phase 1 adjustment is operator-initiated only.
Programmatic triggers (e.g., daily sweep for claims with stale
provider-contract changes) are Phase 2.

**Scope estimate:** Medium.

### 7.2 — Auto-reverse mode

**Source:** 5.12b ([claim-reversal-run.md](../architecture/claim-reversal-run.md))

**Rationale:** Phase 1 reversal is operator-initiated only.
Policy-driven auto-reverse (when adjustment lands in `PendingReversal`
and meets configured criteria) is Phase 2.

**Scope estimate:** Medium.

### 7.3 — Adjustment chain depth > 1

**Source:** 5.12a ([claim-adjustment-workflow.md](../architecture/claim-adjustment-workflow.md), Decision 11),
5.12b ([claim-reversal-run.md](../architecture/claim-reversal-run.md))

**Rationale:** Phase 1 caps depth at 1 via the
`(TenantId, ClaimVersionId)` Mongo unique index. Phase 2 widens the
key with a generation field.

**Scope estimate:** Small-Medium. Index migration (online); model
extension; policy decisions on max depth.

### 7.4 — Per-adjustment retry granularity in ReversalRun

**Source:** 5.12b ([claim-reversal-run.md](../architecture/claim-reversal-run.md))

**Rationale:** Phase 1 re-runs the whole batch on partial failure.
Phase 2 may add per-adjustment retry IDs.

**Scope estimate:** Small-Medium.

### 7.5 — Multi-envelope-per-file 835 mode

**Source:** 5.10, 5.12b ([claim-remittance-generation.md](../architecture/claim-remittance-generation.md), [claim-reversal-run.md](../architecture/claim-reversal-run.md))

**Rationale:** Phase 1 ships single-envelope-per-file. Multi-envelope
packing is needed for some trading partners' file-size preferences.

**Scope estimate:** Small. EDI builder extension.

### 7.6 — Per-claim 835 mode

**Source:** 5.10 ([claim-remittance-generation.md](../architecture/claim-remittance-generation.md)),
named in [claims-phase-1-closer.md](../architecture/claims-phase-1-closer.md)

**Rationale:** Phase 1 retains the per-payment `EraGeneratorService`
for backward-compat / audit / replay; primary path is batched.
Per-claim mode as a primary path is Phase 2.

**Scope estimate:** Small.

### 7.7 — Custom CARC/RARC overrides per tenant

**Source:** Cross-capability (named in
[claims-phase-1-closer.md](../architecture/claims-phase-1-closer.md))

**Rationale:** Phase 1 uses `CarcRarcMappingService` global mappings.
Tenant-specific override registries are Phase 2.

**Scope estimate:** Small-Medium. Registry + lookup precedence.

### 7.8 — Real-time payment status via 277 chaining

**Source:** 5.10 ([claim-remittance-generation.md](../architecture/claim-remittance-generation.md))

**Rationale:** Phase 1 generates 835 envelopes; status retrieval is
via REST surface. Phase 2 may add 277 transmission-status chaining.

**Scope estimate:** Small.

---

## 8. Trading-partner transmission

### 8.1 — sFTP / Availity transmission of generated 835 envelopes

**Source:** 5.10, 5.12b ([claim-remittance-generation.md](../architecture/claim-remittance-generation.md), [claim-reversal-run.md](../architecture/claim-reversal-run.md))

**Rationale:** Phase 1 generates EDI strings + persists
`EraEnvelopeRecord`; transmission is a separate Phase 2 capability.

**Scope estimate:** Medium. New transmission service or extend
trading-partner-service; sFTP and Availity API patterns; retry +
ack handling.

### 8.2 — 277 ack chaining for 835 transmission

**Source:** 5.10 ([claim-remittance-generation.md](../architecture/claim-remittance-generation.md))

**Rationale:** Trading-partner 277 acks for transmitted 835s feed
back to update transmission status.

**Scope estimate:** Medium.

**Dependencies:** 8.1.

### 8.3 — Trading-partner BPR banking-field surface

**Source:** 5.10 ([claim-remittance-generation.md](../architecture/claim-remittance-generation.md))

**Rationale:** Phase 1 BPR banking fields are env-scoped deployment
config; Phase 2 surfaces them on `TradingPartner` aggregate.

**Scope estimate:** Small. Model + UI.

### 8.4 — Reversal-run 835 line-level CAS segments

**Source:** 5.12b ([claim-reversal-run.md](../architecture/claim-reversal-run.md))

**Rationale:** Phase 1 reversal 835 produces header-level CAS for
recoupment. Line-level CAS for partial reversals is Phase 2.

**Scope estimate:** Small.

---

## 9. Reference-data workflows

### 9.1 — NCCI seed-data quarterly import workflow

**Source:** 5.7 ([claim-ncci-pipeline.md](../architecture/claim-ncci-pipeline.md))

**Rationale:** Phase 1 doesn't auto-load NCCI seed data at startup
(`SeedNcciDataAsync` is operator-controlled). Phase 2 ships the
quarterly import workflow.

**Scope estimate:** Medium. Scheduled import job; CMS NCCI feed
connector; per-quarter version diff.

### 9.2 — Per-rule-set claim-type handling

**Source:** 5.7 ([claim-ncci-pipeline.md](../architecture/claim-ncci-pipeline.md))

**Rationale:** Phase 1 NCCI engine has limited per-rule-set claim-
type handling.

**Scope estimate:** Small-Medium. Engine-internal.

### 9.3 — State-Medicaid EDI ingest via NCCI engine

**Source:** 5.7 ([claim-ncci-pipeline.md](../architecture/claim-ncci-pipeline.md))

**Rationale:** Adapter mapper from state-Medicaid EDI directly into
the engine.

**Scope estimate:** Medium-Large. New adapter; state-Medicaid X12
variants.

---

## 10. Infrastructure & follow-ups

### 10.1 — Old `Claims` Cosmos container final deletion

**Source:** 5.1b runbook ([claims-cosmos-partition-migration.md](../migrations/claims-cosmos-partition-migration.md))

**Rationale:** 30-day retention window from cutover. Follow-up Bicep
PR removes the legacy `/memberId`-partitioned container.

**Scope estimate:** Trivial. Bicep PR.

**Sequencing:** ~30 days post 5.1b cutover.

### 10.2 — `payments` container `/memberId` divergence

**Source:** Cross-capability (named in
[claims-phase-1-closer.md](../architecture/claims-phase-1-closer.md))

**Rationale:** payment-service's `payments` Cosmos container retains
`/memberId` partition. Same-shape future migration when payment-
service operational pressure justifies.

**Scope estimate:** Mirror of 5.1b.

**Sequencing:** Demand-driven.

### 10.3 — Typed `ClaimsServiceClient` extraction in payment-service

**Source:** 5.12b ([claim-reversal-run.md](../architecture/claim-reversal-run.md))

**Rationale:** Phase 1 has inline `HttpClient("ClaimsService")` calls
in payment-service for finalize + void. Phase 2 cross-PR refactor
pulls them into a typed client wrapping both surfaces.

**Scope estimate:** Small. Typed-client extraction.

### 10.4 — payment-service migration to `AddChoInfrastructure` Swagger

**Source:** Audit during 5.13 closer
([claims-phase-1-closer.md](../architecture/claims-phase-1-closer.md))

**Rationale:** payment-service uses direct `AddSwaggerGen` registration
(pattern drift from claims-service which uses shared
`AddChoInfrastructure`). Functional, but pattern parity reduces drift.

**Scope estimate:** Trivial. ~10 line change in `Program.cs`.

### 10.5 — XML-comment-driven Swagger surface enrichment

**Source:** Audit during 5.13 closer

**Rationale:** XML doc comments are present on controllers but
`<GenerateDocumentationFile>true</GenerateDocumentationFile>` is not
enabled in csproj files. Enabling produces richer Swagger UI.
Trade-off: shared `AddChoInfrastructure.AddSwaggerGen` would need
`IncludeXmlComments` extension, touching all ~24 services using the
shared helper.

**Scope estimate:** Small (per-service) or Medium (shared-infra path).

### 10.6 — EraEnvelope blob storage when sizing requires

**Source:** 5.10 ([claim-remittance-generation.md](../architecture/claim-remittance-generation.md))

**Rationale:** Phase 1 stores envelope EDI strings in Mongo; well
under 16MB limit at expected batch sizes. Phase 2 sizing review may
move to blob storage with retention lifecycle policies.

**Scope estimate:** Medium. Blob abstraction (already exists via
`IDocumentStore`); migration path.

### 10.7 — `ClaimEventPublisher` Kafka emission refactor

**Source:** 5.1a ([claim-versioning.md](../architecture/claim-versioning.md))

**Rationale:** Phase 1 preserves the existing emission path; the
dual-emit refactor and `claims.versions.v1` topic ship together
when a consumer materializes (see 6.2).

**Scope estimate:** Small.

---

## Sequencing summary

**Compliance-deadline-driven (Jan 2027):**
- 3.1 — CMS-0057-F unauthenticated patient access

**Customer-onboarding-driven:**
- 1.1 — 837 inbound parser
- 8.1 — sFTP / Availity transmission

**Operator-experience-driven (post-pilot):**
- 7.1, 7.2, 7.3 — programmatic adjustment, auto-reverse, deeper chains
- 5.1, 5.2 — AI examiner Required mode + broader scope

**Cross-service follow-ups (parallel tracks):**
- 6.1 — accumulator reversal consumer
- 4.6, 4.7, 4.9 — coverage-service contract fixes
- 2.4 — coverage-service FHIR Coverage projection

**Quick wins:**
- 10.1 — old Cosmos container deletion (~30 day window)
- 10.4 — payment-service Swagger pattern parity
- 6.4 — sweep job for orphaned adjustment states
- 8.3 — TradingPartner BPR fields

---

## Item count

- Section 1: 3 items
- Section 2: 5 items
- Section 3: 2 items
- Section 4: 9 items
- Section 5: 3 items
- Section 6: 4 items
- Section 7: 8 items
- Section 8: 4 items
- Section 9: 3 items
- Section 10: 7 items

**Total: 48 items**

---

## Updating this document

- Add new items as Phase 2 work itself surfaces follow-ups.
- Move items to a "Closed" section (or remove them if redundant) as
  Phase 2 PRs land.
- Periodic re-review: every 4-6 PRs, verify sequencing against
  shifted priorities.
- Cross-link new per-capability docs as Phase 2 work matures.
