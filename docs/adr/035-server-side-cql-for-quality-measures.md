# ADR 035: Run CQL Server-Side for Quality Measures Only

## Status

Proposed. Date: 2026-11-01.

Narrows, and does not replace, owner decision Q-6
([epa-gap-matrix.md, section 7.1](../epa-gap-matrix.md)). Q-6 stays in force
for Documentation Templates and Rules (DTR). This ADR adds a separate decision
for quality measurement (HEDIS and Star Ratings).

The owner approved a digital-first quality module (CQL on FHIR rather than a
traditional SQL measure engine) on 2026-11-01. The engine choice below is
open until the spike reports.

## Context

**What Q-6 decided (2026-10-10).** The prior authorization work (gaps G-55 and
G-56) asked whether CHO should build a DTR SMART app and the CQL engine it
needs. The answer was no. CHO serves conformant questionnaire packages
(Questionnaire, CQL Library, ValueSets) from `$questionnaire-package`, and the
provider's EHR or an external DTR app executes the CQL. CHO authors CQL as
content and only compiles it in CI (`.github/workflows/dtr-cql.yml`) to prove
the committed ELM matches the CQL.

That was right for DTR because DTR CQL reads the patient's chart, and the
chart is on the provider side. Running it at the payer would mean pulling the
chart to the payer first.

**Why quality measures are different.** A HEDIS measure runs over the plan's
whole enrolled population, using data the plan holds: claims, encounters,
enrollment spans, pharmacy, labs and supplemental clinical data. In CHO those
live in CHO's own services. Nobody outside CHO can run the measure, so a
digital quality module needs a CQL engine inside CHO.

**Where HEDIS is going.** NCQA plans to phase out the hybrid method by
MY 2029, new measures are ECDS-first, and NCQA's Digital HEDIS content is
written in CQL on FHIR. NCQA offers a validation for engines that run that
content. Building a traditional SQL engine would put CHO against the 73
vendors already on NCQA's MY 2026 Measure Certification list. The gap
assessment is in the project's strategy folder
(`cho-hedis-engine-2026-11.html`).

## Decision

1. CHO executes CQL server-side **for quality measures only**. DTR keeps Q-6:
   CHO still does not run DTR CQL and still ships no DTR app.
2. Execution is **batch over a population**, not per request. A run takes a
   measure package, a measurement year and a population (tenant, product line)
   and writes `MeasureReport` resources (individual and summary) plus
   member-level gap rows.
3. The engine reads CHO data through a **QI-Core view** built from the FHIR R4
   projections CHO already produces ([ADR 008](008-fhir-r4-projections.md),
   [ADR 018](018-fhir-patient-coverage-from-cho-services.md),
   [ADR 024](024-fhir-encounter-claim-from-cho-services.md)). The engine does
   not query service databases directly.
4. Every run is **pinned and reproducible**: measure package version, value
   set and Medication List Directory version, engine version and input
   snapshot are recorded with the result, so an auditor can re-run a past
   measurement year and get the same answer.
5. PHI stays inside the plan's CHO deployment. No measure data is sent to an
   external engine or service.
6. The engine is chosen by a **time-boxed spike** (below), not in this ADR.

## Engine spike

One to two weeks. Run one NCQA Digital HEDIS sample measure (from the NCQA
Digital HEDIS Evaluation Toolkit, if the license allows, otherwise a public
CMS eCQM with similar logic) against CHO demo data on two candidates:

| Candidate | Shape | What to learn |
| --- | --- | --- |
| HL7 / CQF clinical-reasoning engine (Java) | Sidecar service called by a CHO worker | Most mature CQL-on-FHIR engine; cost of running a JVM service beside .NET |
| Firely CQL SDK (.NET) | In-process library in a CHO worker | Fits the stack; maturity and coverage of CQL 1.5 and QI-Core |

Compare: correct results against the sample's expected output, time and
memory for 100k and 1M synthetic members, packaging and licensing of the
engine, and how cleanly each records the provenance needed for decision 4.
The spike writes its result as an update to this ADR.

## Consequences

- A new runtime component (a JVM sidecar or a new .NET dependency). Per the
  owner's ground rules this is a major dependency, which is why it needs this
  ADR and the owner's approval.
- A new batch workload. Population runs need a worker, a schedule and
  capacity planning; they must not share request paths with the FHIR APIs.
- New data obligations that come before useful results: pharmacy claims,
  lab results, enrollment benefit flags and Medicare (MMR) flags, an NCQA
  provider-type crosswalk, and LOINC, RxNorm, NDC and CVX in the terminology
  service.
- NCQA licensing is needed before CHO ships NCQA measure content or value
  sets in a product. Fees and the digital validation path are still to be
  confirmed with NCQA.
- The CI CQL toolchain from Q-6 is reused for quality CQL content.
- Plans that already run a managed FHIR store ([ADR 023](023-managed-fhir-store-projection.md))
  could later run measures there instead. That is a possible follow-up, not
  part of this decision.

## References

- Owner decision Q-6: [epa-gap-matrix.md, section 7](../epa-gap-matrix.md)
- DTR CQL compile check: `.github/workflows/dtr-cql.yml`, `scripts/dtr/compile-cql.sh`
- NCQA, Digital HEDIS: https://www.ncqa.org/programs/data-and-information-technology/digital-content-services/
- NCQA, hybrid retirement timeline: https://www.ncqa.org/resources/ncqas-proposed-timeline-for-retiring-and-replacing-hedis-hybrid-measures/
- NCQA, MY 2026 Measure Certification vendor list: https://wpcdn.ncqa.org/www-prod/wp-content/uploads/MY2026_MeasureCertification_VendorList.pdf
- HL7 Da Vinci DEQM (gaps in care): https://hl7.org/fhir/us/davinci-deqm/
