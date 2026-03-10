# CHO Enhancement Roadmap — Status Tracker

Date reviewed: 2026-03-09  
Basis: `CHO-ENHANCEMENT-ROADMAP` + current branch changes
Assumptions: Status reflects repository evidence on this branch plus maintainer confirmation.

## Related Docs
- [Execution Checklist](CHO-ENHANCEMENT-CHECKLIST.md)

## Legend
- ✅ Done
- 🟡 In Progress / Partial
- ⏳ Not Started

## Executive Summary
Most roadmap PR-sequence workstreams are implemented (PR1, PR3, PR4, PR5, PR6, PR7, PR8, PR9, PR10, PR12).  
Remaining priority is closing PR2 hardening details plus non-PR roadmap gaps (document storage abstraction, COB, encounter submission, risk adjustment/HCC).

## Workstream Status

### Workstream 1: Benefits & Accumulators Engine (Critical Path)
- ✅ Phase 1A Benefit Calculation Engine implemented
  - Core benefit calculation and adjudication integration are in place.
- ✅ Phase 1B Accumulator Engine implemented
  - Runtime accumulator tracking with update/reversal paths is in place.

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
  - New NCCI engine implemented with models, seed data, service logic, persistence (Cosmos/Mongo), and tests.

### Workstream 4: 835 ERA Generation
- ✅ Implemented
  - 835 generation service added and integrated into payment flows (including download/generation paths).

### Workstream 5: RFAI Service
- ✅ MVP implemented
  - New `rfai-service` with model, APIs, persistence, tenant middleware, and attachment-received lifecycle transition.
- 🟡 Beyond-MVP items still pending
  - SLA/reminder logic, deeper portal notification integration, richer outbound orchestration/correlation extensions.

### Workstream 6: 824 Application Advice Hardening (Phase 2)
- 🟡 Partial
  - OTI outcome mapping fix implemented (`TA/TR/TP` path addressed).
  - Still pending: additional `REF` enrichment + synthetic 824 examples + comprehensive rejection catalog coverage.

### Workstream 7: 277CA Claim Acknowledgment
- ✅ Implemented
  - 277CA generator service and claim-level endpoint/download path are in place.

### Workstream 8: Generic Document Storage Foundation
- ⏳ Not Started (as roadmap-defined abstraction)
  - No complete `IDocumentStore` abstraction + provider implementations set found in current change set.

## “What Doesn’t Exist Yet” Section — Current Status
- ✅ NCCI/MUE Edits Engine (now exists)
- ⏳ COB (Coordination of Benefits)
- ✅ Provider Contract / Network Management
- ✅ Payment Service + 835 (core roadmap objective implemented)
- ⏳ Encounter Submission
- ⏳ Risk Adjustment / HCC

## Recommended PR Sequence — Progress Snapshot
- ✅ PR1 RFAI MVP
- 🟡 PR2 824 readiness (partial)
- ✅ PR3 Benefit engine
- ✅ PR4 Accumulator engine
- ✅ PR5 Fee schedule/pricing
- ✅ PR6 Provider contract/network
- ✅ PR7 NCCI/MUE
- ✅ PR8 Adjudication wiring
- ✅ PR9 835 ERA
- ✅ PR10 277CA
- ⏳ PR11 Generic document storage
- ✅ PR12 Real 270/271

## Highest-Priority Remaining Work
1. Complete remaining Workstream 6 hardening items for 824.
2. Implement Workstream 8 document storage abstraction.
3. Address still-not-started areas: COB, encounter submission, risk adjustment/HCC.

## Notes on Modes (Augment vs Replace)
- 🟡 Partial
  - Some code structure supports dual-backend patterns and adapter-like behavior.
  - A formalized mode toggle consistently applied across all engines/workstreams is not yet complete.
