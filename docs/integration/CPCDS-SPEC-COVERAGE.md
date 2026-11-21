# CPCDS coverage: what CHO reads and what it does not

The CARIN Common Payer Consumer Data Set (CPCDS) is the data dictionary the
CARIN Blue Button IG was built from. A plan's CPCDS feed is usually three flat
files: members, coverage and claims (one row per claim line). This page lists
every CPCDS element in those files and what CHO does with it, so a plan can
see before it sends anything which of its columns will be used.

The element names are the CPCDS names as the CARIN sample data and Synthea's
CPCDS exporter spell them. Headers match ignoring case, spaces and
punctuation, and a plan whose extract names a column differently gives a
column map instead of renaming its file
([Plugging in an existing feed](#plugging-in-an-existing-feed)).

**How to read the status column**

- **Read**: loaded into CHO and served, on the EOB or the member's record.
- **Read (stored)**: loaded and kept on the claim, but the EOB does not
  carry it.
- **Not read**: ignored. The import report lists it under
  `unrecognizedColumns`. An ignored column never refuses a row.

The importers themselves are documented in
[CPCDS-FILE-DROP-IMPORTER.md](CPCDS-FILE-DROP-IMPORTER.md).

## Members

| CPCDS element | Status | Where it goes |
|---|---|---|
| Member id | Read (required) | The CHO member id (members are filed under the core's id) |
| Name, or First name / Last name / Middle name | Read | Member name |
| Date of birth | Read | |
| Gender code | Read | |
| Home address line, city, state, zip code | Read | Member address |
| Subscriber id, Relationship to subscriber | Read | Subscriber link and relationship (self, spouse, child, other) |
| Date of death | Not read | |
| Race code, Ethnicity | Not read | |
| County, Country | Not read | |

## Coverage

| CPCDS element | Status | Where it goes |
|---|---|---|
| Member id | Read (required) | |
| Plan identifier | Read (required) | The plan code, mapped to a CHO benefit plan |
| Group id | Read (required) | The sponsor |
| Group name, Plan name | Read | |
| Coverage type | Read | Health, dental or vision line |
| Coverage status | Read | Cancelled, entered-in-error and draft rows are skipped |
| Start date, End date | Read | Benefit dates |
| Subscriber id, Relationship to subscriber | Read | |
| Coverage id | Read (stored) | The core's id, kept for tracing |
| Dependent number | Not read | |
| Payer identifier | Not read | CHO is the payer for the tenant |

## Claims: header

| CPCDS element | Status | Where it goes |
|---|---|---|
| Claim unique identifier | Read (required) | Claim number, EOB `identifier` (uc) |
| Member id | Read (required) | EOB `patient` |
| Claim type | Read (required) | Professional, institutional (inpatient or outpatient), oral, vision (as professional) and pharmacy |
| Claim service start date | Read (required) | |
| Claim service end date | Read | |
| Claim received date | Read | EOB `created`, supportingInfo `clmrecvddate` |
| Claim paid date | Read | EOB `payment.date` |
| Coverage id | Read (stored) | The EOB references the CHO Coverage held on the date of service |
| Claim adjusted from / to identifier | Read | Version chain: the adjusted-to claim supersedes |
| Claim frequency code | Read | |
| Claim processing status code, Claim payment status code | Read | Status (paid, partially paid, denied, voided) |
| Claim payment denial code | Read | CARC adjustment reason; for pharmacy, the NCPDP reject code |
| Member admission date, Member discharge date | Read | Institutional admission period |
| Claim bill facility type code, Claim service classification type code | Read | Type of bill: inpatient or outpatient |
| Claim diagnosis related group | Read | |
| Claim inpatient admission type code, Claim source inpatient admission code, Patient discharge status code | Read | |
| Claim billing provider NPI | Read (required) | EOB `provider`, `payee` |
| Claim billing provider network status | Read | `billingnetworkstatus`, `benefitpaymentstatus` |
| Claim performing provider NPI (else attending) | Read | EOB `careTeam` (rendering) |
| Claim performing provider network status | Read | Used when the billing provider's is empty |
| Claim site of service NPI | Read | Facility NPI |
| Claim referring provider NPI, Claim attending provider NPI | Read | Created as reference-only providers so references resolve |
| Claim prescribing provider NPI | Read | Pharmacy: EOB `careTeam` (prescribing) |
| Claim total submitted amount | Read | EOB `total` submitted |
| Claim total allowed amount | Read | `eligible` |
| Claim payment amount (else Claim amount paid to provider) | Read | `benefit`, `payment.amount` |
| Member paid deductible, Co-insurance liability amount, Copay amount, Member liability | Read | `deductible`, `coinsurance`, `copay`, `memberliability` |
| Days supply | Read (required for pharmacy) | supportingInfo `dayssupply` |
| DAW product selection code | Read | supportingInfo `dawcode` |
| Refill number | Read | supportingInfo `refillnum` |
| Refills authorized | Read | supportingInfo `refillsauthorized` (a CARIN BB element; not every CPCDS extract has it) |
| Prescription origin code | Read | supportingInfo `rxorigincode` |
| Plan reported brand-generic code | Read | supportingInfo `brandgenericindicator` |
| Compound code | Read | supportingInfo `compoundcode`; `2` is the compound literal |
| RX service reference number | Read (stored) | |
| Pharmacy service type code, Patient residence code | Read (stored) | |
| Patient account number, Medical record number | Not read | |
| Claim payer identifier, Claim primary payer identifier | Not read | CHO is the payer; coordination of benefits is not loaded from CPCDS |
| Claim payee type code, Claim payee | Not read | The payee is the billing provider |
| Claim attending / site of service / referring / prescribing provider network status | Not read | Only the billing and performing statuses have an EOB element |
| Claim PCP NPI | Not read | |
| Amount paid by patient, Member reimbursement | Not read | |
| Claim primary payer paid amount, Claim discount amount, Claim disallowed amount | Not read | `noncovered`, `discount` and `priorpayerpaid` totals are not emitted yet |

## Claims: lines

| CPCDS element | Status | Where it goes |
|---|---|---|
| Line number | Read (required) | `item.sequence` |
| Service (from) date, Service to date | Read | `item.serviced[x]` |
| Place of service code | Read | `item.location` (professional and oral) |
| Revenue center code | Read | `item.revenue` (institutional) |
| Procedure code, Procedure description, Procedure code type | Read | `item.productOrService`; ICD-10-PCS codes go to the institutional procedures |
| Procedure date, Procedure type | Read | Institutional procedures |
| Modifier Code-1 to 4 | Read | `item.modifier` |
| Number of units | Read | `item.quantity` |
| National drug code | Read | Drug detail; for pharmacy, `item.productOrService` |
| Quantity dispensed | Read | Drug quantity |
| Line submitted amount | Read | `item.net`, `submitted` |
| Line allowed amount, Line payment amount, Line member liability | Read (stored) | Kept per line; the EOB states money at the header |
| Line payment denial code | Read | Adjustment reason; for pharmacy, `rejectreason` |
| Tooth number, Tooth surface | Read (stored) | Kept on the line; the oral EOB does not emit tooth and surface yet |
| Type of service, Allowed number of units, Quantity qualifier code, Line benefit payment status | Not read | |
| Line disallowed amount, Line member reimbursement, Line amount paid by patient, Drug cost | Not read | |
| Line amount paid to provider, Line patient deductible, Line primary payer paid amount | Not read | |
| Line coinsurance amount, Line copay amount, Line discount amount | Not read | Header amounts are used |

## Claims: diagnoses

| CPCDS element | Status | Where it goes |
|---|---|---|
| Diagnosis code, Diagnosis description | Read | `diagnosis` (ICD-10-CM only; others are left off with a warning) |
| Diagnosis code type, Diagnosis type | Read | Principal, admitting, other |
| Present on admission | Read | |
| Is E code | Not read | |

## Providers

CPCDS names providers by NPI only. The provider import reads the billing,
performing, attending, referring, site-of-service and prescribing NPIs from the
claim file and describes any CHO does not have from NPPES, as reference-only
providers, so the EOBs' references resolve. Which providers are **in network**
is not in CPCDS: it comes from the plan's network roster, which travels the
same drop ([NETWORK-ROSTER-IMPORTER.md](NETWORK-ROSTER-IMPORTER.md)).

## Plugging in an existing feed

QNXT and Facets do not write CPCDS themselves. Plans that have a CPCDS feed get
it from an interoperability product (TriZetto's Interoperability Data Hub,
Edifecs, Smile and others) or a custom extract, and the header spellings vary
between them. The steps below take such a feed from first file to daily load
without changing it.

1. **Dry-run each file.** Send the member and coverage files to
   `POST /api/v1/enrollment/import/cpcds` and the claim file to
   `POST /api/v1/claims/import/cpcds`, each with `dryRun=true`. Nothing is
   written. The reports list `unrecognizedColumns`, missing required columns,
   unmapped plan codes and every claim that would be refused, with why.
2. **Write one column map.** For every required or useful column the report
   lists as unrecognized, add `"CPCDS name": "the file's header"`. One map
   serves the member, coverage and claim files: each import ignores the entries
   for columns it does not read.
3. **Map the plan codes** the member dry run lists as unmapped, as for 834
   loads.
4. **Turn the drop on** with the map:
   `PUT /api/v1/claims/import/cpcds/drops/settings` with `sourceSystem`,
   `lineOfBusiness`, `columnMap` and `expectedEveryHours`, then register the
   SFTP key ([CPCDS-SFTP-DROP-GCP.md](CPCDS-SFTP-DROP-GCP.md)).
5. **Drop the files.** Names that contain `member`, `coverage`, `roster`,
   `claim` (pharmacy claim files included) and `auth` are recognized. The
   first run's report and the freshness report
   (`GET .../drops/report`) say what loaded and whether the totals reconcile
   with the core's control totals.

A pharmacy feed from a PBM is a claim file like any other: name it with
`claim` in it (`pbm_pharmacy_claims_20261101.csv`) and it loads in the same
run, under the same map.
