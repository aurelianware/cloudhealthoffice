# CHO Enhancement Roadmap — Execution Checklist

Date: 2026-03-09

Assumptions: Status is based on current branch implementation evidence and maintainer confirmation.

## Related Docs
- [Status Tracker](CHO-ENHANCEMENT-STATUS.md)

## Completed
- [x] PR1 — RFAI Service MVP
- [x] PR3 — Benefit Calculation Engine
- [x] PR4 — Accumulator Engine
- [x] PR5 — Fee Schedule / Pricing Engine
- [x] PR6 — Provider Contract / Network Model
- [x] PR7 — NCCI / MUE Edits Engine
- [x] PR8 — Adjudication wiring enhancement
- [x] PR9 — 835 ERA Generation (core implementation)
- [x] PR10 — 277CA Claim Acknowledgment
- [x] PR12 — Eligibility Service real 270/271

## In Progress / Partial
- [ ] PR2 — 824 Readiness hardening (remaining: extra REF enrichment, rejection catalog completeness, synthetic 824 examples)

## Not Started
- [ ] PR11 — Generic Document Storage abstraction (`IDocumentStore` + providers)

## Additional High-Priority Gaps (from roadmap “Doesn’t Exist Yet”)
- [ ] COB (Coordination of Benefits)
- [ ] Encounter Submission
- [ ] Risk Adjustment / HCC

## Immediate Next Sprint (recommended)
- [ ] Close remaining 824 hardening tasks

## Mode Toggle Maturity (Augment vs Replace)
- [ ] Define and standardize mode-toggle interfaces across all core engines
- [ ] Ensure each workstream has explicit `Augment` and `Replace` implementation path
