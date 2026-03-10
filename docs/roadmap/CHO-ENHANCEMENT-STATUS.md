# CHO Enhancement Roadmap — Status Tracker

Date reviewed: 2026-03-09 (updated 2026-03-09, sprint 2 complete)
Basis: `CHO-ENHANCEMENT-ROADMAP` + current branch changes
Assumptions: Status reflects repository evidence on this branch plus maintainer confirmation.

## Related Docs

- [Execution Checklist](CHO-ENHANCEMENT-CHECKLIST.md)

## Legend

- ✅ Done
- 🟡 In Progress / Partial
- ⏳ Not Started

## Executive Summary

All 12 roadmap PRs are complete and all three post-roadmap greenfield workstreams
(COB, Encounter Submission, Risk Adjustment/HCC) are now also complete.
The codebase has a working benefit/accumulator/COB engine, fee schedule/pricing engine,
NCCI/MUE edits, full EDI stack (835/277CA/824/270/271/837 encounter), RFAI service,
document storage abstraction, real adjudication workflow, encounter submission engine,
and HCC risk scoring pipeline. 120 new tests added across sprint 2.

## Workstream Status

### Workstream 1: Benefits & Accumulators Engine (Critical Path)
- ✅ Phase 1A Benefit Calculation Engine implemented
  - Core benefit calculation and adjudication integration are in place.
- ✅ Phase 1B Accumulator Engine implemented
  - Runtime accumulator tracking with update/reversal paths is in place.
  - ServiceTypeCode keying fixed so visit-count accumulators resolve correctly.
  - No Surprises Act: emergency OON services use in-network cost-sharing rules.

### Workstream 2: Claims Pricing / Fee Schedule Engine (Critical Path)
- ✅ Phase 2A Fee Schedule Management implemented
  - Rate resolution and fee schedule repository integration are in place.
- ✅ Phase 2B Provider Contract / Network Integration implemented
  - Provider contract lookup and network-status-aware rate resolution are in place.

### Workstream 3: Claim Adjudication Engine
- ✅ Core workflow enhancement implemented
  - Argo workflow is consolidated around adjudication with improved update/writeback behavior.
  - NCCI/MUE pre-payment scrub is integrated and 422 failure handling is implemented.
- ✅ NCCI / MUE edits component
  - New NCCI engine implemented with models, seed data, service logic, persistence (Cosmos/Mongo), and tests (27 tests).

### Workstream 4: 835 ERA Generation
- ✅ Implemented
  - 835 generation service added and integrated into payment flows (including download/generation paths).

### Workstream 5: RFAI Service
- ✅ MVP implemented
  - New `rfai-service` with model, APIs, persistence, tenant middleware, and attachment-received lifecycle transition.
- 🟡 Beyond-MVP items still pending
  - SLA/reminder logic, deeper portal notification integration, richer outbound orchestration/correlation extensions.

### Workstream 6: 824 Application Advice Hardening (Phase 2)
- ✅ Complete
  - OTI outcome mapping fix (`TA/TR/TP`).
  - REF*D9 corrected to carry claim number; REF*EJ added for RFAI/attachment control number.
  - `AcknowledgmentGeneratorService` extracted (pure EDI, no Cosmos dependency) — fully testable.
  - Rejection catalog: 8 standard codes → X12 TED01 error type codes; TED segment emitted on TR/TP.
  - Dynamic SE count — correct regardless of which optional segments are present.
  - 38 tests covering all paths (13 generator tests + 25 rejection catalog tests).

### Workstream 7: 277CA Claim Acknowledgment
- ✅ Implemented
  - 277CA generator service and claim-level endpoint/download path are in place.

### Workstream 8: Generic Document Storage Foundation
- ✅ Implemented
  - `IDocumentStore` abstraction with `UploadAsync`, `DownloadAsync`, `ExistsAsync`, `DeleteAsync`, `GetUri`.
  - `AzureBlobDocumentStore` — production implementation; auto-creates containers.
  - `InMemoryDocumentStore` — thread-safe test/dev implementation with `GetBytes`/`Count`/`Clear` helpers.
  - `attachment-service` wired to `IDocumentStore`; `BlobServiceClient` no longer used directly in controllers.
  - 13 tests covering all operations, container isolation, overwrite, and typed exception.

## "What Doesn't Exist Yet" Section — Current Status

- ✅ NCCI/MUE Edits Engine (now exists)
- ✅ COB (Coordination of Benefits) — `CloudHealthOffice.CobEngine`: payer order (birthday rule, MSP, active-employment), complementary + non-duplication models, BenefitEngine integration (OA-23 CAS), coverage-service `/cob` endpoint. 24 tests.
- ✅ Provider Contract / Network Management
- ✅ Payment Service + 835 (core roadmap objective implemented)
- ✅ Encounter Submission — `CloudHealthOffice.EncounterEngine`: X12 837P/837I transformer + ISA/GS batch builder, original/corrected/void, COB OI segments, MOA/MIA adjudication. 44 tests.
- ✅ Risk Adjustment / HCC — `CloudHealthOffice.RiskAdjustmentEngine`: CMS-HCC v28 + HHS-HCC crosswalk, hierarchy resolution, demographic factor table, batch risk scoring pipeline. 52 tests.

## Recommended PR Sequence — Progress Snapshot
- ✅ PR1 RFAI MVP
- ✅ PR2 824 readiness (complete)
- ✅ PR3 Benefit engine
- ✅ PR4 Accumulator engine
- ✅ PR5 Fee schedule/pricing
- ✅ PR6 Provider contract/network
- ✅ PR7 NCCI/MUE
- ✅ PR8 Adjudication wiring
- ✅ PR9 835 ERA
- ✅ PR10 277CA
- ✅ PR11 Generic document storage
- ✅ PR12 Real 270/271

## Highest-Priority Remaining Work

All sprint 2 priorities are complete. Potential sprint 3 candidates:

1. **Encounter service** (REST layer) — submission lifecycle tracking, batch dispatch, `GET /api/encounters/{id}/837` download, resubmission/correction flow wrapping `EncounterEngine`.
2. **Risk adjustment service** (REST layer) — measurement-year diagnosis collection, per-member score endpoint, plan-level RAF summary wrapping `RiskAdjustmentEngine`.
3. **FHIR R4 API layer** — expose member, coverage, encounter, and claim resources as FHIR-compliant endpoints.
4. **Portal real-time accumulator display** — surface `AccumulatorSnapshot` to the member portal for deductible/OOP tracking.

## Notes on Modes (Augment vs Replace)
- 🟡 Partial
  - Some code structure supports dual-backend patterns and adapter-like behavior.
  - A formalized mode toggle consistently applied across all engines/workstreams is not yet complete.
