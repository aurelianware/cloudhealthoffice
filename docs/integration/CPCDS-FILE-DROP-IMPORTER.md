# CPCDS file-drop importer

Loads claims a plan's core admin system already adjudicated (QNXT, Facets,
HealthRules, or anything that can write the CARIN Common Payer Consumer Data
Set) into CHO, so the CMS-0057-F Patient Access and Provider Access APIs serve
them as CARIN Blue Button ExplanationOfBenefits. CHO does not adjudicate these
claims. It stores the core's answer.

This is the first of the hydration options in the
[core adapter implementer guide](CORE-ADAPTER-IMPLEMENTER-GUIDE.md) (tier 1,
"Foothold"). It closes known gap 1 there for claims, members and coverage.
Practitioners and organizations follow (see [Roadmap](#roadmap)).

## Why CPCDS

Every core can produce a flat claim extract, and CPCDS is the data dictionary
the CARIN BB IG was built from, so its claim elements map one-to-one onto the
C4BB EOB. Onyx asks plans for CPCDS files for the same reason. The importer
maps CPCDS rows onto CHO's own claim record rather than straight to FHIR, so an
imported claim goes through the same `ExplanationOfBenefitProjector` and
`CarinNonFinancialEobProjector` as a claim CHO adjudicated: one shaping, one
set of conformance tests, one Inferno run.

## Endpoint

`POST /api/v1/claims/import/cpcds` (claims-service), multipart form.

| Field | Required | Meaning |
|---|---|---|
| `file` | yes | The claim file. Comma, pipe or tab delimited (detected from the header row), header row first, UTF-8. |
| `sourceSystem` | no | The core that adjudicated the claims, e.g. `qnxt`. Recorded on each claim, and passed to the member crosswalk. |
| `extractedAt` | no | When the core produced the extract (ISO 8601). Data freshness is measured from this. |
| `lineOfBusiness` | no | `Commercial` (default), `Medicare`, `Medicaid`, `Exchange`, `TRICARE` or `VA`. CPCDS has no element for it. |
| `columnMap` | no | JSON object, CPCDS column name to the file's header, for columns the file names differently: `{"Claim unique identifier":"CLM_ID","Member id":"MBR_KEY"}`. |
| `dryRun` | no | `true` parses, maps and reports without writing anything. |

Permission: `claims:import`. No claims role holds it, because these claims go
straight to members' apps with no CHO adjudication in between. TenantAdmin has
it through `*:*`; a file-drop job gets it as a service identity.

The response is the import report (below). It is `422` only when the file
could not be read at all (empty, or a required column missing); a file with
some rejected claims is `200`, and the report says which and why.

## File layout

One row per claim line, with the claim header repeated on each row, as CPCDS
and the CARIN sample data lay it out. Diagnosis and procedure codes can sit on
any row of the claim; a row may repeat a line number to carry another
diagnosis. Headers match ignoring case, spaces and punctuation, so
`CLAIM_UNIQUE_IDENTIFIER` and `Claim unique identifier` are the same column.
Columns the importer does not use are ignored and listed in the report.

A worked example with a professional, a denied, an inpatient and an oral claim
is in [samples/cpcds/claims.csv](samples/cpcds/claims.csv). The tests load it.

Required columns: `Claim unique identifier`, `Member id`, `Claim type`,
`Claim service start date`, `Claim billing provider NPI`.

| CPCDS column | CHO claim field | Notes |
|---|---|---|
| Claim unique identifier | `ClaimNumber`, EOB `identifier` (uc) | Also the reload key |
| Member id | `MemberId` (after the crosswalk), EOB `patient` | |
| Coverage id | `CoverageId`, EOB `insurance.coverage` | Optional; without it the EOB names the plan by display |
| Claim type | `ClaimType` | `inpatient-facility`, `outpatient-facility`, `institutional` → Institutional; `professional-nonclinician`, `professional`, `vision` → Professional; `oral`, `dental` → Dental; `pharmacy` refused (no pharmacy projection yet) |
| Claim service start / end date | `ServiceDateFrom` / `ServiceDateTo` | |
| Claim received date | `ReceivedDate`, `SubmittedDate` (EOB `created`) | |
| Claim paid date | `PaidDate`, adjudication `PaymentDate` (EOB `payment.date`) | |
| Claim bill facility type code + Claim service classification type code | `Institutional.FacilityTypeCode` | Decides inpatient vs outpatient; when missing, taken from the claim type with a warning |
| Claim frequency code | `ClaimFrequencyCode` | Default `1` |
| Member admission / discharge date, Claim diagnosis related group, admission type and source, Patient discharge status code | `Institutional.*` | |
| Claim processing status code | `Status` | `cancelled`, `entered-in-error`, `void` → Voided |
| Claim payment status code | `Status` | `paid`, `partiallypaid`, `denied`; when absent, a denial code on a claim that paid nothing means Denied, otherwise Paid |
| Claim payment denial code | adjudication `DenialReasonCode` (EOB adjustment reason, CARC) | |
| Claim billing provider NPI | `BillingProviderNPI` (EOB `provider`, `payee`) | Must be 10 digits |
| Claim billing provider network status | adjudication `NetworkTier` (EOB `billingnetworkstatus`) | `innetwork` / `outofnetwork` and common spellings |
| Claim performing (else attending) provider NPI | `RenderingProviderNPI` (EOB `careTeam`) | |
| Claim site of service NPI | `FacilityNPI` | |
| Claim total submitted amount | `TotalChargeAmount` (EOB `total` submitted) | Falls back to the sum of the lines |
| Claim total allowed amount | adjudication `AllowedAmount` (eligible) | |
| Claim payment amount (else amount paid to provider, else sum of line payments) | adjudication `PayerPayment` (benefit, `payment.amount`) | |
| Member paid deductible, Co-insurance liability amount, Copay amount, Member liability | adjudication deductible, coinsurance, copay, member liability | |
| Claim adjusted from identifier | `OriginalClaimReference` | |
| Claim adjusted to identifier | supersedes the claim | Loaded, readable by id, but no longer current, so search does not show both |
| Line number, Service (from) date, Service to date, Place of service code, Revenue center code, Number of units, National drug code, Quantity dispensed | `ClaimLines[]` | |
| Procedure code, Procedure description, Modifier Code-1..4 | line procedure | ICD-10-PCS codes (by `Procedure code type`) go to `Institutional.PrincipalProcedure` / `OtherProcedures` instead |
| Line submitted / allowed / payment amount, Line member liability | line `ChargeAmount`, line adjudication | |
| Line payment denial code | adjudication adjustment reasons | Group code is not in CPCDS, so it is left out |
| Tooth number, Tooth surface | line `Teeth` | Oral claims |
| Diagnosis code, Diagnosis description, Present on admission, Diagnosis code type, Diagnosis type | `DiagnosisCodes[]` | ICD-10-CM only; others are left off with a warning. One principal: the first flagged, else the first |

## What is refused, and why

The importer refuses a claim rather than load part of it. A claim missing a
line, or with an amount that does not parse, would show a member money that is
wrong, and a wrong EOB is worse than a late one. A claim is refused when:
a required value is empty or does not parse; rows disagree on the member or the
claim type; a professional or oral line has no procedure code (an institutional
line needs a procedure or a revenue code); it is a pharmacy claim; its member
is not in the crosswalk; or its claim number belongs to a claim CHO adjudicated
itself. One refused claim does not stop the rest of the file.

Things that do not change what the member sees are warnings: a non-ICD-10-CM
diagnosis, a missing bill type, a claim total that differs from its lines.

## Reloads

A core's nightly extract is usually a rolling window that repeats most of
yesterday's claims, so reloading must change nothing:

- Each claim's chain id (and FHIR EOB id) is `cpcds-` plus a hash of the tenant
  and the source claim id, so the same claim always gets the same id.
- Each claim's source rows, the member they resolved to, the line of business
  and the source system are hashed. A reload with the same hash writes nothing
  (`Unchanged`). Row order and ignored columns do not affect the hash.
- A changed claim (a late adjustment, a corrected member) gets a new version
  on the same chain and the old version is superseded, as a CHO adjustment
  would be. A member's app sees one EOB that changed.
- A reload also repairs a predecessor left current by an interrupted load.

Each imported claim carries `ExternalSource` (format, source system, source
claim id, source member id, content hash, batch id, file name, imported and
extracted times). Each claim also gets a `ClaimImportTransaction` row
(`Format = cpcds`), so the admin console's import log shows CPCDS loads beside
raw 837 uploads.

## Members and coverage

`POST /api/v1/enrollment/import/cpcds` (enrollment-import-service, permission
`enrollment:process`), multipart:

| Field | Required | Meaning |
|---|---|---|
| `members` | yes | The CPCDS member file. Required column: `Member id`. |
| `coverages` | no | The CPCDS coverage file. Required columns: `Member id`, `Plan identifier`, `Group id`. |
| `dryRun` | no | `true` reports what would load, including the plan-code gap report, and writes nothing. |

The two files are turned into the 834 enrollment shape and run through the same
import as an 834, so members, sponsors and coverage are created, updated and
audited exactly as they would be from one. Load members and coverage before
the claims that name them.

- **Member ids.** Every member, dependants included, is filed under the core's
  own member id. That is what makes the pass-through crosswalk right: the id on
  the claim file is the CHO member id, and a SMART patient token binds to it.
  A dependant is not a subscriber; it names its subscriber (`Subscriber id`,
  else the coverage row's) and its relationship (`self` 18, `spouse` 01,
  `child` 19, anything else G8).
- **Names, dates, gender.** `First name` / `Last name` / `Middle name`, or a
  single `Name` column (`Last, First Middle` or `First Middle Last`). A member
  with no first and last name is refused and the rest of the file loads.
  Dates are ISO (`2026-01-01`) or `yyyyMMdd`.
- **Coverage.** Each coverage row becomes a coverage line: `Plan identifier`
  is the plan code, `Group id` / `Group name` the sponsor, `Start date` and
  `End date` the benefit dates, and `Coverage type` picks health, dental
  (`dental`, `DEN`) or vision. Cancelled, entered-in-error and draft rows are
  skipped with a warning. A coverage row for a member not in the member file,
  or a member in two groups, is a row error.
- **Plan codes.** Coverage loads only for plan codes the tenant has mapped to
  a CHO benefit plan (the same mapping 834 loads use). Every call returns the
  plan-code gap report; run with `dryRun=true` first and map the gaps.
- **Reloads.** Every row is a create-or-update (834 maintenance `001`), so
  the daily full extract can be sent as is.

Known limits: member updates do not change name or date of birth (as for
834s); the claim file's `Coverage id` is the core's id, not the CHO coverage
id the import created, so an EOB's `insurance.coverage` reference does not
resolve to that Coverage yet.

## Member crosswalk

`ICoreMemberCrosswalk` turns the core's member id into the CHO member id the
EOB's `patient` names and a SMART patient token is bound to. The default is
pass-through (the core's id is CHO's). A crosswalk that knows the source system
but not the member returns null, and the claim is refused rather than filed
under a guess: a claim under the wrong member id is invisible to the member.
Because the member import files members under the core's ids, pass-through is
right for a plan that loads both files from the same core. A stored crosswalk
is only needed when CHO already holds members under other ids.

## The report

| Field | Meaning |
|---|---|
| `batchId` | One per file; on every claim and transaction it wrote |
| `rowsRead`, `claimsInFile` | |
| `created`, `updated`, `unchanged`, `rejected` | |
| `submittedAmountInFile`, `paidAmountInFile` | Sums over every claim in the file, for checking against the core's control totals |
| `paidAmountLoaded` | Sum over the claims now current in CHO from this file; the gap to `paidAmountInFile` is the rejected claims |
| `unrecognizedColumns`, `fileErrors` | |
| `claims[]` | Per claim: outcome, CHO id, version, member, file lines, errors, warnings |

## Not yet

- Pharmacy claims (needs a C4BB Pharmacy EOB projection).
- Line-level allowed and paid amounts are stored but the financial EOB still
  emits money at the header, as it does for native claims.
- A claim whose source id collides with a native CHO claim number is refused;
  plans whose core numbers overlap CHO's need a prefix in the extract.

## Roadmap

1. **This change:** claim file parser, mapping to the claim record (and so to
   the C4BB EOBs), idempotent reloads, per-file report, pass-through crosswalk.
2. Members and coverage from the CPCDS member and coverage files (done, see
   [Members and coverage](#members-and-coverage)). Next: practitioners and
   organizations from the provider file, and resolving the claim's core
   coverage id to the CHO Coverage.
3. File-drop trigger (watched storage location) and a tenant-level
   reconciliation and freshness report: last extract time, claims and money
   loaded per source, and lag.
4. X12 as the second source: EOBs from the 837/835 pairs and members from the
   834 files CHO already parses, through the same pipeline, crosswalk and
   report.
