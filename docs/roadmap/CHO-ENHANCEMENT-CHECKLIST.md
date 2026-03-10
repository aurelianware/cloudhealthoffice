# CHO Enhancement Roadmap — Execution Checklist

Date: 2026-03-09 (updated 2026-03-09)

Assumptions: Status is based on current branch implementation evidence and maintainer confirmation.

## Related Docs
- [Status Tracker](CHO-ENHANCEMENT-STATUS.md)

## Completed
- [x] PR1 — RFAI Service MVP
- [x] PR2 — 824 Readiness hardening (OTI fix, REF*D9/REF*EJ enrichment, TED rejection catalog, dynamic SE count, 38 tests)
- [x] PR3 — Benefit Calculation Engine
- [x] PR4 — Accumulator Engine (incl. ServiceTypeCode keying fix, No Surprises Act emergency OON fix)
- [x] PR5 — Fee Schedule / Pricing Engine
- [x] PR6 — Provider Contract / Network Model
- [x] PR7 — NCCI / MUE Edits Engine
- [x] PR8 — Adjudication wiring enhancement
- [x] PR9 — 835 ERA Generation (core implementation)
- [x] PR10 — 277CA Claim Acknowledgment
- [x] PR11 — Generic Document Storage abstraction (`IDocumentStore` + AzureBlob + InMemory providers, 13 tests)
- [x] PR12 — Eligibility Service real 270/271

## In Progress / Partial
_(none — all PRs complete)_

## Additional High-Priority Gaps (from roadmap "Doesn't Exist Yet")
- [ ] COB (Coordination of Benefits)
- [ ] Encounter Submission
- [ ] Risk Adjustment / HCC

## Immediate Next Sprint (recommended)
- [ ] COB — payer order, carry-over amounts, cross-payer adjudication logic

## Mode Toggle Maturity (Augment vs Replace)
- [ ] Define and standardize mode-toggle interfaces across all core engines
- [ ] Ensure each workstream has explicit `Augment` and `Replace` implementation path
