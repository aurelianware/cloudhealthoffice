# CPCDS export

A plan that runs CHO as its system of record can hand its members, coverage
and adjudicated claims to another tool, a vendor or the state in CPCDS, the
CARIN Common Payer Consumer Data Set. The files use the CPCDS element names
CHO's own CPCDS import reads, so an export loads into another CHO tenant, or
back into this one, without a column map. Any tool that reads the CARIN sample
layout reads them the same way.

The export rides on the [claims warehouse extract](CLAIMS-WAREHOUSE-EXTRACT.md).
It uses the same schedule, data-use agreement, `/outbound` folder, run record
and manifest. Turning it on adds three files to each run.

## Turning it on

```
PUT /api/claims/extracts/claims
{ "enabled": true, "dataUseAgreement": "DUA-2026-014", "includeCpcds": true }
```

The caller needs `extracts:manage`. A run started while it is on writes:

```
/outbound/extracts/claims/{runId}/
  claims.csv, claim-lines.csv   the warehouse files, as before
  CPCDS_Members.csv             the whole membership as of the run
  CPCDS_Coverages.csv           every coverage record as of the run
  CPCDS_Claims.csv              adjudicated claims changed in the run's window
  manifest.json                 written last; "cpcds": true, rows, SHA-256 and columns for every file
```

## What each file holds

**CPCDS_Members.csv** is a full snapshot on every run. Draft members are left
out. Columns: Member id, First name, Last name, Middle name, Date of birth,
Date of death, Gender code (male, female, unknown), Home address line, city,
state and zip code, Subscriber id, Relationship to subscriber.

Relationship comes from the 834 INS02 code: 18 is self, 01 spouse, 19 child,
anything else other. A subscriber with no code is self.

**CPCDS_Coverages.csv** is a full snapshot on every run. Columns: Coverage id,
Member id, Subscriber id, Coverage type, Coverage status, Start date, End
date, Group id, Group name, Plan identifier, Plan name, Relationship to
subscriber.

Coverage type comes from the 834 insurance line: DEN is dental, VIS vision,
PDG pharmacy, anything else medical. Coverage status is `active` for every
record, as FHIR Coverage uses it; an ended coverage has an End date.

**CPCDS_Claims.csv** has one row per claim line, with the claim header repeated
on each row. The rows cover:

- The current version of each claim whose version changed in the run's window.
  The first run, or a run with `full=true`, covers every claim.
- Only adjudicated claims: paid, partially paid, denied and voided. A claim
  still in adjudication has nothing to report yet and appears in a later run.
- Professional, institutional (inpatient or outpatient facility), oral and
  pharmacy claims, with every element CHO reads on import. See
  [CPCDS-SPEC-COVERAGE.md](CPCDS-SPEC-COVERAGE.md).

A voided claim has processing status `cancelled`. A claim with more diagnoses
than lines carries the extra diagnoses on more rows with the first line's
number. ICD-10-PCS procedures are on rows without a line number.

## How to load it

- Replace your member and coverage copies with each run's files. They are
  complete, so there are no deletes to work out.
- Upsert claims by `Claim unique identifier`. A claim that changes appears
  again in a later run with its new content. Replace all of its rows.
- Wait for `manifest.json`, and check each file's SHA-256 against it.

## Access

The run reads members from member-service and coverage from coverage-service
with claims-service's own service identity for the tenant, a page of 100 at a
time. Deployment sets `Services:MemberService` and `Services:CoverageService`.
If either service cannot be read, the run fails and the next run tries again,
since the window does not move on a failed run.

The files carry PHI for every member. They are covered by the same controls as
the warehouse extract: a named data-use agreement, a run record that is never
deleted, and a list-and-download-only `/outbound` folder.
