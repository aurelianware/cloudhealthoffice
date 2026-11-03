# CQL engine spike (ADR 035)

Reproduces the engine comparison recorded in
[ADR 035](../../docs/adr/035-server-side-cql-for-quality-measures.md). This is
a throwaway harness, not product code: no CHO service references it, it is not
in the solution, and it does not inherit the repository's build settings.

It runs the public CMS125 Breast Cancer Screening eCQM (QI-Core 2025 content
from `cqframework/dqm-content-qicore-2025`, 66 test cases with expected
`MeasureReport`s) through:

| Folder | Engine | How it is called |
| --- | --- | --- |
| `java/` | HL7/CQF clinical-reasoning 4.14.0 (Apache-2.0) | `R4MultiMeasureService.evaluate`, which returns a `MeasureReport` |
| `dotnet/` | Firely CQL SDK 2.18.0 (BSD-3-Clause) | ELM compiled to .NET assemblies; the four population definitions invoked per member |

NCQA's Digital HEDIS content is licensed, so a public CMS measure stands in for
it. No PHI is used; the data is the measure's own synthetic test patients.

## Run

```bash
./run.sh tests              # both engines against the 66 test cases
./run.sh scale 1516 4       # ~100k members (66 x 1516), 4 threads
```

Needs git, Java 21 + Maven, the .NET 8 SDK, python3, and network access to
GitHub, Maven Central and nuget.org. Content is fetched into `work/` at pinned
commits.

## Results (2026-11-03, 4 vCPU / 15 GB sandbox)

| | Java clinical-reasoning | Firely CQL SDK |
| --- | --- | --- |
| Test cases | 66 / 66 | 66 / 66 |
| 10k members, 1 thread | 5.05 ms/member | 0.31 ms/member |
| 10k members, 4 threads | 2.56 ms/member | 0.24 ms/member |
| 100k members, 4 threads | not run (est. ~4 min) | 0.12 ms/member (12 s) |
| 1M members, 4 threads | not run (est. ~43 min) | 0.105 ms/member (105 s), peak working set 1.8 GB |
| Start-up | none beyond JVM | ~9 s to compile ELM to assemblies (cacheable) |

Scale runs clone each test patient with a new id, so every member is small
(1 to 6 resources). Real members carry more data, so treat these as relative
numbers, not capacity planning.

The two harnesses do not do identical work. Java builds a full
`MeasureReport` (populations plus supplemental data elements) per batch of 66
members held in an in-memory repository; Firely only invokes the four
population definitions per member. That difference flatters Firely, but not by
the ~20x measured here. Per-member Java evaluation (one repository per member)
was slower still, about 126 ms/member, because each call rebuilds its
evaluation context.

## Things the spike found

- **Measurement Period precision (Java).** Passing `periodStart`/`periodEnd`
  as `ZonedDateTime` gives the period second precision, and two test cases
  (procedures at 23:59:59.000 on 31 December) then fail. Passing the period as
  a `Parameters` "Measurement Period" at millisecond precision fixes it.
- **Partial value set expansions.** The published bundle carries a partial
  VSAC expansion (AdvancedIllness, 1000 of 1997 codes). Java uses it without a
  word; Firely refuses to load it. CHO's value set loader must check
  `expansion.total` and refuse partial expansions.
- **Content compatibility (Firely).** Firely's code generator rejects the
  published ELM: it does not resolve namespaced includes, and QICoreCommon
  needs explicit `as DateTime` casts. The harness uses the patched ELM that the
  Firely repository maintains for this content.
- **Profile tags are not needed.** With every `meta.profile` removed from the
  test data, both engines still pass 66/66, so CHO's projections do not need
  to claim QI-Core profiles for retrieves to work.
- **eCQMs are EHR measures.** CMS125's numerator reads a LOINC Mammography
  `Observation`; a claims-only member with a CPT 77067 mammogram never meets
  it. HEDIS needs NCQA's digital content, which models claims, not the CMS
  eCQM.
- **FHIR SDK version.** Firely CQL SDK 2.18.0 pulls Hl7.Fhir.R4 6.8.0; CHO's
  services are on 5.10.2.
