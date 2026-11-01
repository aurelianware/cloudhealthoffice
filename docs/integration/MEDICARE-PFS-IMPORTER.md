# Medicare Physician Fee Schedule Importer

Loads the CMS Medicare Physician Fee Schedule (PFS) into the fee schedule
store claims adjudication prices from (`FeeSchedules`, read by
`RateResolutionService`). It writes one `MedicareMpfs` schedule per payment
locality. Each line holds the PPRRVU work, practice expense (facility and
non-facility) and malpractice RVUs. The schedule holds the locality's GPCIs
and the conversion factor. The engine prices a line as

```
(work RVU × work GPCI + PE RVU × PE GPCI + MP RVU × MP GPCI) × conversion factor
```

using the facility PE RVU in a facility place of service and the
non-facility PE RVU otherwise.

What it is for:

- **Medicare Advantage**: one locality's schedule as the plan's default
  (`planId`), for lines from providers without a contract.
- **Percent-of-Medicare contracts**: a contract term with
  `method: PercentOfMedicare` names a locality schedule id as its base.
- **Medicaid schedules defined as a percent of Medicare**: the Medicaid
  schedule's `baseMpfsFeeScheduleId` names a locality schedule id.

The Pricing API's RBRVS loader (`/api/v1/admin/fee-schedules/upload/rbrvs`)
still writes the Pricing API's own store. This importer is the path for
adjudication.

## Source files

CMS posts the PFS relative value files each quarter as a zip (`RVU26A` for
January 2026, `RVU26B` for April, and so on) on the
[PFS Relative Value Files](https://www.cms.gov/medicare/payment/fee-schedules/physician/pfs-relative-value-files)
page. Upload the zip unchanged. The importer reads two CSVs from it:

- `PPRRVU*.csv`: the RVUs, status code, NA indicators, multiple procedure,
  bilateral and assistant-at-surgery indicators and the conversion factor.
  Since CY2026 CMS posts two, a non-QPP file and a QPP file with the
  qualifying APM participant conversion factor. The non-QPP file is used by
  default; pass `qpp=true` for the other.
- `GPCI*.csv`: the work, PE and MP GPCIs per MAC and locality. The work GPCI
  "with 1.0 floor" is used when the file has both.

You can also upload the two CSVs as `pprrvu` and `gpci`.

## Endpoint

`POST /api/v1/fee-schedules/import/medicare-pfs` (benefit-plan-service),
multipart, permission `settings:manage`. **Dry run by default.**

| Parameter | Meaning |
|---|---|
| `rvuZip`, or `pprrvu` + `gpci` | The files (see above). |
| `year` | PFS calendar year. Required. |
| `localities` | Comma-separated `MAC-locality` keys from the GPCI file, e.g. `04412-18` (Dallas). |
| `states` | Comma-separated states: every locality in them. |
| `allLocalities` | Every locality in the GPCI file (at most 120 per request). |
| `effectiveDate` | Defaults to January 1 of `year`. Set it for a quarterly release. |
| `termDate` | Optional. |
| `release` | CMS release label, e.g. `RVU26A`, stored as the source version. |
| `conversionFactor` | Overrides the file's. Required when the file holds more than one. |
| `qpp` | Use the QPP PPRRVU file from the zip. |
| `planId` | Make the one selected locality's schedule this plan's default. Give the plan code (e.g. `MA-PPO-01`) so it applies to every version of the plan; a plan version id (GUID) pins it to that version. |
| `replace` | Replace existing schedules whose values differ. |
| `dryRun` | `false` to write. |

Schedule id: `{tenant}:medicare-pfs-{year}-{MAC}-{locality}:{yyyyMMdd}`, for
example `acme:medicare-pfs-2026-04412-18:20260101`. With `planId` it is the
plan-default id instead.

The report lists each locality with its GPCIs, schedule id, outcome and the
computed 99213 non-facility amount. Compare that amount with the
[CMS PFS Look-Up Tool](https://www.cms.gov/medicare/physician-fee-schedule/search)
after a dry run.

### Rows loaded and left out

- Status codes **A**, **R** and **T** are loaded. Every other status (B
  bundled, C carrier priced, I not valid for Medicare, N non-covered, X
  statutory exclusion, and so on) is left out with its reason, so a line for
  it pends.
- Rows with no RVUs (carrier priced) are left out.
- **NA indicator**: when CMS marks the non-facility PE "NA", the office
  setting is priced at the facility PE (and the other way round).
- `MULT PROC` becomes the multiple procedure indicator, so standard
  multiple-surgery reductions apply. `BILAT SURG` 1 turns the bilateral
  adjustment on. `ASST SURG` 1 means an assistant at surgery is not paid.
- Modifier rows (26, TC, 53) are separate lines, matched by modifier.

### Not applied (yet)

- The OPPS cap on non-facility imaging (`OPPSCAP` file).
- Status T's rule that it is paid only when no other service is billed.
- Anesthesia (`ANES` file: base units plus time, times the anesthesia CF).
- HPSA and other bonus payments, and the Medicare limiting charge.
- **Choosing the locality from the provider's address.** A claim is priced
  from the schedule its contract term or plan default names. A plan that
  spans localities names the right one per contract.

Schedules are per tenant, about 19,000 lines and roughly 2 to 3 MB each, so
load only the localities a tenant's plans and contracts use.
