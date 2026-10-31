# Authorization import

Most plans decide prior authorizations in a UM platform (Jiva, GuidingCare,
TruCare and similar) or in their core admin system. CMS-0057-F still needs
those authorizations in the APIs CHO serves:

- Patient Access, Provider Access and Payer-to-Payer (PDex prior authorization)
- the status a provider reads through the Prior Authorization API

CPCDS carries claims, not authorizations. This import loads the plan's
authorizations into CHO's authorization-service so those APIs serve them.

CHO does not review imported authorizations. It stores the source's answer,
and that record is **read-only** in CHO.

This is not the live PAS-to-278 bridge. Plans that decide authorizations in CHO
(the default, `Cms0057:Authorization:OperatingMode = Replace`) don't need it,
although they can still use it to load history from a system they are leaving.

## Endpoint

`POST /api/v1/authorizations/import/csv` (authorization-service, permission
`authorizations:import`, which tenant administrators and CHO services hold).
The request is `multipart/form-data`:

| Field | |
|---|---|
| `file` | The CSV: comma, pipe or tab delimited, header row first |
| `sourceSystem` | The system that decided the authorizations, e.g. `jiva`. Recorded on each one |
| `extractedAt` | When the source produced the extract (ISO 8601) |
| `lineOfBusiness` | For rows without `line_of_business`: Commercial (default), Medicare, Medicaid, Exchange, TRICARE or VA |
| `columnMap` | JSON object mapping a documented header to the file's own header, for any column the file names differently |
| `dryRun` | `true` parses, maps and reports without writing anything |

The answer reports, for each authorization, whether it was `Created`,
`Updated`, `Unchanged` or `Rejected`, along with its CHO id, the file lines it
came from, and any errors or warnings. A file the importer cannot read at all
returns 422. A file in which some authorizations were rejected returns 200, and
the report says which ones and why.

## File layout

There is one row per requested service line. The header columns repeat on every
row of the same authorization, and the first row's values are used. If later
rows disagree, the report includes a warning. A file without service-line
columns has one row per authorization.

Headers are matched without regard to case, spaces or punctuation, so
`Authorization Number`, `AUTHORIZATION_NUMBER` and `authorization number`
all match. Common alternatives such as `auth_number`, `subscriber_id` and
`request_date` are recognized too.

| Column | Required | |
|---|---|---|
| `authorization_number` | yes | The source's number. It is the identity: reloads match on it, and the PDex EOB id is `pa-{number}` |
| `member_id` | yes | The member id as the core knows it, the same one the CPCDS member file uses |
| `status` | yes | `approved`, `modified`, `denied`, `pended`, `in_review`, `submitted`, `cancelled` or `expired`. X12 HCR01 codes are also accepted: `A1`, `A2`, `A3`, `A4`, `A6` (modified) and `C` (cancelled) |
| `received_date` | yes | When the request was received. Decision timeframes are measured from it |
| `decision_date` | for decided statuses | When it was decided. If it is missing, timeframes can't be reported for that authorization |
| `requesting_provider_npi` | yes | 10 digits |
| `requesting_provider_name`, `servicing_provider_npi`, `servicing_provider_name`, `facility_npi`, `facility_name` | | |
| `request_type` | | `prior` (default), `concurrent`, `retrospective` or `referral` |
| `service_type_code` | | The X12 service type code (UM03), e.g. `1` or `2` |
| `level_of_service` | | `urgent` or `standard` |
| `line_of_business` | | Overrides the request's `lineOfBusiness` |
| `requested_from_date`, `requested_to_date` | | |
| `approved_from_date`, `approved_to_date`, `expiration_date` | | |
| `diagnosis_codes` | | ICD-10-CM codes, separated by `;`. Dots are optional, and the first code is principal |
| `denial_reason_code`, `denial_reason` | | |
| `patient_first_name`, `patient_last_name`, `patient_birth_date` | | |
| `line_number` | | Give every line one or none. Lines without one are numbered in file order |
| `procedure_code` | | CPT or HCPCS. A row without one is not a service line |
| `code_system` | | `NDC`, `RxNorm` or a FHIR system URI when the code is not CPT or HCPCS |
| `procedure_description`, `modifiers` (`;`-separated) | | |
| `requested_units` (default 1), `approved_units`, `unit_type` | | |
| `place_of_service`, `revenue_code` | | |
| `line_status` | | The line's decision, written the same way as `status` |
| `line_denial_reason_code`, `line_denial_reason` | | |

Dates can be `yyyy-MM-dd`, `yyyyMMdd`, `MM/dd/yyyy` or ISO 8601. A date
without a time zone is read as UTC.

A sample file is at [samples/authorizations/authorizations.csv](samples/authorizations/authorizations.csv) (synthetic data).

## Reloads

A UM extract is usually a rolling window, so reloading the same file must be
safe:

- **Unchanged.** Each authorization's source rows are hashed. If the hash is the
  same as last time, nothing is written.
- **Updated.** A different hash means the source changed: a decision arrived,
  dates were extended, or a line was corrected. CHO replaces the content and
  keeps the CHO id. When the status moved, it appends a status-history entry
  (source `import`) dated at the source's decision date.
- **Refused.** If the authorization number belongs to an authorization CHO
  received or decided itself, the import refuses it. An import never
  overwrites native work.

Imported ids are deterministic (`imp-` plus a hash of tenant and number), so
rebuilding the store gives every authorization the same id it had before.

## Read-only in CHO

An imported authorization carries its provenance: format, source system, file,
batch, and when it was first and last loaded. API responses show
`"imported": true` and `"importedFrom"`.

CHO never changes an imported authorization itself:

- The decision, status, 278-response and cancel endpoints answer 409.
- Decision-timeframe extensions answer 409.
- It is not in the open-request queue, and the SLA watchdog never escalates it.
- The expiry worker never expires it. The source's status stands.

If something needs to change, change it in the source and reload.

Imported authorizations do take part in:

- **The PDex prior-authorization EOBs and the Prior Authorization API status.**
  This is the reason for the import.
- **CMS-0057-F prior authorization metrics.** These use the source's received
  and decision dates. Load at least the reporting year for the plan's public
  metrics to be complete.
- **Retention.** The same policy applies as for native authorizations.

## X12 278 files

`POST /api/v1/authorizations/import/x12-278` takes the same multipart fields,
except `columnMap`. The file is an X12 interchange (`ISA` to `IEA`) of
005010X217 278 transactions, from a core or a clearinghouse. A sample is at
[samples/authorizations/authorizations-278.x12](samples/authorizations/authorizations-278.x12)
(synthetic data).

- **What is an authorization.** Each response (`BHT02 = 11`) patient event
  (`HL` level `EV`, loop 2000E) is one authorization. It is identified by the
  certification number (`HCR02`, on the event or on a service line), or else by
  the administrative reference (`REF*NT`).
- **Status.** The status is the event's `HCR01`. Without one, it is worked out
  from the lines' `HCR01`s: all certified means approved, all denied means
  denied, a pend anywhere means pended, and anything else means modified.
  `CT` (contact payer) is stored as pended, and `NA` (no action required) is
  not loaded.
- **Requests and responses.** A request (`BHT02 = 13`) in the same file is
  paired with its response by the event's trace number (`TRN02` and `TRN03`,
  which the response echoes). The request's `BHT04` and `BHT05` give when it
  was received, and the response's give when it was decided. A response
  without its request still loads, with a warning that its decision timeframe
  can't be reported. A request with no response has no decision and no number,
  so it is reported and not loaded.
- **Field mapping.**

  | Field | X12 source |
  |---|---|
  | Member | `2010C NM109`, or a dependent's own id in `2010D` |
  | Patient name and birth date | `NM1` and `DMG` |
  | Requesting provider | `2010B NM109` (`XX`) |
  | Servicing provider | `2010EA` `SJ`, `71`, `72` or `DK` |
  | Facility | `2010EA` `FA` |
  | Request type | `UM01` (`SC` is a referral, `AR` renewal or revision is concurrent review) |
  | Service type | `UM03` |
  | Level of service | `UM06` |
  | Requested dates | `DTP*472` (or `435` admission) |
  | Certified period | `DTP*AAH` |
  | Expiration | `DTP*036` |
  | Diagnoses | `HI` (ICD-10-CM qualifiers only) |
  | Lines | `SV1`, `SV2` and `SV3`, with the request's quantity as requested and the response's (or `HSD02`) as approved |
  | Denial reason | `HCR03` |
- **Line of business.** The 278 doesn't carry one, so it comes from the
  request's `lineOfBusiness` (or the drop tenant's setting).
- **Reloads.** The content hash covers the event and the parties above it,
  never the envelope. The same response re-sent with new ISA, GS, ST or BHT
  control numbers is `Unchanged`.

## In the daily drop

Authorization files go in the same `{tenant}/inbound` folder as the CPCDS
extracts ([CPCDS-FILE-DROP-IMPORTER.md](CPCDS-FILE-DROP-IMPORTER.md)).

- **Recognized by name.** A file whose name contains `auth` or `278` is an
  authorization file, for example `jiva_authorizations_20261101.csv` or
  `um_278_20261101.x12`.
- **Routed by content.** A file that starts with `ISA` goes to the 278 import,
  and anything else goes to the CSV import.
- **Order and settings.** Authorization files load after members and coverage,
  with the tenant's drop `SourceSystem` and `LineOfBusiness`.
- **Reported in the run.** Each file is a step in the run, with its counts, and
  each rejected authorization appears with its errors.

## Not yet

- **A real-time push endpoint.** The UM system would send each decision as it
  is made, so the Prior Authorization API's status never waits for the nightly
  file.
- **CORE real-time 278 inbound.** This is only for plans where CHO decides.
