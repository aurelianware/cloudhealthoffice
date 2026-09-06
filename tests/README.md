# Tests

This folder contains test projects and supporting test assets.

## Test Projects
- [CloudHealthOffice.Edi.Tests](CloudHealthOffice.Edi.Tests/README.md) — X12 EDI parser/generator regression tests (835, 277CA, 270, 271)
- [CloudHealthOffice.NcciEngine.Tests](CloudHealthOffice.NcciEngine.Tests)
- [CloudHealthOffice.BenefitEngine.Tests](CloudHealthOffice.BenefitEngine.Tests)

## Acceptance And Interoperability Suites

Two suites answer deliberately different questions, and their results are never
merged into one score:

- [Cms0057Acceptance.Tests](Cms0057Acceptance.Tests) — does CHO implement the
  behavior its own CMS-0057-F acceptance specification requires? Reports
  `PASSABLE` / `PARTIAL` / `GAP`.
- [DaVinciInterop.Tests](DaVinciInterop.Tests) — can CHO exchange
  standards-conformant requests and responses with an *independent* HL7 Da Vinci
  implementation? Reports `Passed` / `Failed` / `Skipped` / `NotRun`. External
  scenarios are opt-in and start pinned third-party containers; see
  [docs/interop/davinci.md](../docs/interop/davinci.md). Executing today:
  `BR-PAS-SUBMIT-001` (PAS `$submit`), `BR-CRD-001` (CRD CDS Hooks) and
  `BR-DTR-001` (DTR `$questionnaire-package`, chained from the payer's own CRD
  determination).

## Supporting Artifacts
- [E2E-TEST-RESULTS.md](E2E-TEST-RESULTS.md)
- [fixtures](fixtures)
- [integration](integration)
- [unit](unit)
