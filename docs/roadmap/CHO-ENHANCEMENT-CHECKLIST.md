# CHO Enhancement Roadmap — Execution Checklist

Date: 2026-03-09 (updated 2026-03-09, sprint 2 complete)

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

## Additional High-Priority Gaps (from roadmap "Doesn't Exist Yet") — Sprint 2

- [x] COB (Coordination of Benefits) — `CloudHealthOffice.CobEngine`: payer order (birthday rule, MSP, active-employment), complementary + non-duplication adjudication models, BenefitEngine integration (ApplyCob + OA-23 CAS), AdjudicationController wired, coverage-service `/cob` endpoint added. 24 tests.
- [x] Encounter Submission — `CloudHealthOffice.EncounterEngine`: X12 837P/837I transformer (original/corrected/void, COB OI segments, MOA/MIA, SV1/SV2, per-line AMT breakdown), ISA/GS batch builder. 44 tests.
- [x] Risk Adjustment / HCC — `CloudHealthOffice.RiskAdjustmentEngine`: CMS-HCC v28 + HHS-HCC crosswalk, hierarchy resolution (8 rules across DM/CHF/Cancer/COPD/CKD), demographic factor table (24 age/sex cells), full pipeline orchestrator with batch scoring. 52 tests.

## Mode Toggle Maturity (Augment vs Replace)
- [ ] Define and standardize mode-toggle interfaces across all core engines
- [ ] Ensure each workstream has explicit `Augment` and `Replace` implementation path

## Potential Next Sprint

- [ ] Encounter service (REST layer + submission lifecycle tracking) — wraps EncounterEngine
- [ ] Risk adjustment service (REST layer + measurement year data collection) — wraps RiskAdjustmentEngine
- [ ] FHIR R4 API layer — expose member/coverage/encounter/claim resources
- [ ] Portal real-time accumulator display — surface AccumulatorSnapshot to member portal
