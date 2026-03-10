# CHO Enhancement Roadmap — Status Tracker

Date reviewed: 2026-03-09 (updated 2026-03-09)
Basis: `CHO-ENHANCEMENT-ROADMAP` + current branch changes
Assumptions: Status reflects repository evidence on this branch plus maintainer confirmation.

## Related Docs
- [Execution Checklist](CHO-ENHANCEMENT-CHECKLIST.md)

## Legend
- ✅ Done
- 🟡 In Progress / Partial
- ⏳ Not Started

## Executive Summary
All 12 roadmap PRs are now complete. The codebase has a working benefit/accumulator engine,
fee schedule/pricing engine, NCCI/MUE edits, full EDI stack (835/277CA/824/270/271),
RFAI service, document storage abstraction, and real adjudication workflow.
Remaining work is entirely in the three greenfield workstreams: COB, Encounter Submission,
and Risk Adjustment/HCC.

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
- ⏳ COB (Coordination of Benefits)
- ✅ Provider Contract / Network Management
- ✅ Payment Service + 835 (core roadmap objective implemented)
- ⏳ Encounter Submission
- ⏳ Risk Adjustment / HCC

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
1. **COB** — Coordination of Benefits: payer order determination, primary/secondary adjudication,
   carry-over of patient responsibility, cross-payer amounts on EOB/835.
2. **Encounter Submission** — Outbound 837I to payers/HIEs for risk-bearing contracts.
3. **Risk Adjustment / HCC** — HCC coding, RAF score calculation, encounter-level diagnosis tagging.

## Notes on Modes (Augment vs Replace)
- 🟡 Partial
  - Some code structure supports dual-backend patterns and adapter-like behavior.
  - A formalized mode toggle consistently applied across all engines/workstreams is not yet complete.
