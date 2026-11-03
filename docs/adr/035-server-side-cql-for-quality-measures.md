# ADR 035: Run CQL Server-Side for Quality Measures Only

## Status

Proposed. Date: 2026-11-01.

Narrows, and does not replace, owner decision Q-6
([epa-gap-matrix.md, section 7.1](../epa-gap-matrix.md)). Q-6 stays in force
for Documentation Templates and Rules (DTR). This ADR adds a separate decision
for quality measurement (HEDIS and Star Ratings).

The owner approved a digital-first quality module (CQL on FHIR rather than a
traditional SQL measure engine) on 2026-11-01, and chose Medicaid in Texas
and California as the first line of business on 2026-11-03. The spike
reported on 2026-11-03 (below) and recommends the Firely CQL SDK. The engine
choice is proposed until the owner approves it.

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

## Spike result (2026-11-03)

Harness and full numbers: [tools/cql-engine-spike](../../tools/cql-engine-spike/README.md).
The measure was the public CMS125 Breast Cancer Screening eCQM (QI-Core 2025
content, 66 test cases), because NCQA's Digital HEDIS content needs a license.

| | Java clinical-reasoning 4.14.0 | Firely CQL SDK 2.18.0 |
| --- | --- | --- |
| Correct on the 66 test cases | 66 / 66 | 66 / 66 |
| 10k members, 4 threads | 2.56 ms/member | 0.24 ms/member |
| 1M members, 4 threads | about 43 min (extrapolated) | 105 s measured, 1.8 GB peak |
| License | Apache-2.0 | BSD-3-Clause |
| Output | `MeasureReport` from `$evaluate-measure` | Definition results; CHO builds the `MeasureReport` |

What the spike also found:

- **Value sets.** The published content carries a partial VSAC expansion.
  Java evaluates with it silently; Firely refuses it. CHO must refuse partial
  expansions in its loader whichever engine runs.
- **Measurement Period precision.** Java gives a `ZonedDateTime` period second
  precision, which fails two boundary test cases. The period has to be passed
  at millisecond precision.
- **Content compatibility.** Firely rejects the published ELM as is
  (namespaced includes, and QICoreCommon needs explicit `DateTime` casts) and
  ran on the patched copy the Firely repository maintains. NCQA content would
  need the same check.
- **Profiles.** Neither engine needs `meta.profile` on the data. CHO's
  projections can feed the engine without claiming QI-Core profiles.
- **Claims versus EHR content.** CMS eCQMs read EHR codes (the CMS125
  numerator is a LOINC Mammography `Observation`). A claims-only member with
  a CPT mammogram never meets it. CHO needs NCQA's HEDIS digital content,
  which models claims, so the NCQA license (Step 0) is on the critical path.
- **FHIR SDK version.** Firely CQL SDK 2.18.0 needs Hl7.Fhir.R4 6.x; CHO's
  services are on 5.10.2.

### Recommendation

Use the **Firely CQL SDK**, in a **new quality worker service** that owns its
own Hl7.Fhir 6.x dependency, so no existing service has to move off 5.10.2 to
adopt it. Reasons: it was about 10 to 20 times faster here, it is .NET like the
rest of CHO, NCQA co-maintains it and tests it against HEDIS content, and it
fails closed on partial value sets. CHO pays for that with code it must write:
`MeasureReport` assembly, supplemental data and stratification, and a cache of
compiled assemblies so the ~9 s ELM compile happens once per measure version.

Keep the Java engine as a CI cross-check: run both on each measure's test
deck and fail the build when they disagree. That gives an independent second
answer for auditors at no runtime cost.

## Consequences

- A new runtime component: with the recommendation above, a new .NET
  quality worker service using the Firely CQL SDK. Per the
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
