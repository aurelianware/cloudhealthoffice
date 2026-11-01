# Provider Contract Roster Importer

Loads a plan's provider contracts from a CSV into the contracts claims are
priced from (the Fee Schedule Engine's `ProviderContract` documents, read by
`RateResolutionService`). After the import, a contracted provider's claim lines
are priced by the terms of its contract (a fee schedule, a percent of
Medicare, a percent of billed charges, a case rate or a per diem) instead of
the plan's default schedule. This is the contract input that
[shadow adjudication](SHADOW-ADJUDICATION.md) was missing.

The imported contracts are a **pricing copy**. The plan's core admin system
(QNXT, Facets, ...) stays the system of record for its contracts. Each
contract is tagged `origin: import:{source}`, and the next import of the same
source replaces it. Contracts written any other way (the ADR 016 backfill, an
engine seed) are left alone unless you pass `replaceNonImported=true`. Legal
contract records in provider-contracts-service are not created.

## Endpoint

`POST /api/v1/pricing-contracts/import/csv` (benefit-plan-service), multipart
field `file` (CSV or a `.zip` holding one). Permission `settings:manage`.
**Dry run by default.**

| Query parameter | Meaning |
|---|---|
| `source` | Short label for where the file came from, e.g. `qnxt`. It is stored as the origin `import:qnxt`. |
| `replaceNonImported` | Replace contracts that were not written by this importer. |
| `dryRun` | `false` to write. |
| `includeContracts` | Return every mapped contract, not the first 20. |

## File layout

One row per provider, plan and reimbursement term. Rows with the same NPI and
plan become one contract, and its terms keep file order. The engine prices a
line by the **first** term whose scope and dates match, so put narrow terms
(a code range, a claim type) before a catch-all term. The header can sit
below title rows. Column names are matched ignoring case and punctuation.

| Column | Required | Values |
|---|---|---|
| `NPI` | yes | 10-digit NPI with a valid check digit. Claims are matched by rendering NPI, then billing NPI. |
| `Plan` | yes | CHO benefit plan id, or a plan code (`planId` of a published plan, resolved to the version in effect today). |
| `Network Status` | yes | `in`, `par`, `out`, `non-par`. |
| `Effective Date` | yes | `yyyy-MM-dd` or `MM/dd/yyyy`. |
| `Term Date` | no | Same formats. |
| `Method` | yes | `fee_schedule`, `percent_of_medicare`, `percent_of_billed`, `case_rate`, `per_diem`. Capitation is refused: set it in the contracts portal. |
| `Fee Schedule ID` | for fee_schedule, case_rate, per_diem | Id of a schedule in this tenant, from the [fee schedule CSV importer](FEE-SCHEDULE-IMPORTER.md) or the [Medicare PFS importer](MEDICARE-PFS-IMPORTER.md). For `percent_of_medicare` it is the Medicare base, for example `acme:medicare-pfs-2026-04412-18:20260101`. `Base Schedule ID` works too. |
| `Percent` | for percent terms | A percentage: `110` or `110%` is 110%. Values under 10 are refused, so a ratio such as `1.10` is not misread. |
| `Multiplier` | no | For schedule terms: `1.05` or `105%`. Default 1.0. |
| `Code From`, `Code To` | no | Procedure code range of the term. A single code if only `Code From` is given. |
| `Claim Type` | no | `professional`, `institutional`, `inpatient`, `outpatient`; blank means any. |
| `Lesser Of Billed` | no | `Y` to pay the lesser of the rate and the billed charge. |
| `Term Effective Date`, `Term End Date` | no | Dates of the term inside the contract. Without them the row's contract dates bound the term. |
| `Contract Number`, `Line of Business`, `Description` | no | Carried on the contract or term. |

TINs are not imported: the engine's contract store is read by more services
than the contracts API, and ADR 016 keeps TINs out of it.

A synthetic sample is in [samples/contracts](samples/contracts/contract-roster-sample.csv).

## Report

- `columns`: which file column was read for each role.
- `rowErrors`: rejected rows with the reason (bad NPI, unknown plan, unknown
  network status, missing or misread percent, capitation, invalid term).
  Good rows still load.
- `missingFeeSchedules`: schedule ids the file names that the tenant does
  not hold. Lines priced by those terms pend until the schedules are
  imported, so load the fee schedules first.
- `outcomes`: counts of `WouldCreate`, `WouldReplace`, `Created`,
  `Replaced`, `Unchanged` and `Protected`.

## Order of loading for a plan

1. Benefit plans, published.
2. Fee schedules: the state Medicaid schedule or the plan's rate sheets
   ([CSV importer](FEE-SCHEDULE-IMPORTER.md)), and Medicare PFS localities
   ([Medicare PFS importer](MEDICARE-PFS-IMPORTER.md)) if any contract pays a
   percent of Medicare.
3. The plan's default schedule (`planId` on either importer) for
   uncontracted providers.
4. This contract roster.

## Limits

- **Plan version ids.** Contracts and plan default schedules name the plan
  version id that claims resolve to. Amending a published plan creates a
  new version with a new id, so re-run this import (and the plan default
  import) after a plan is amended.
- Contracts not in a later file are not deleted.
- With the ADR 016 dual-read window open for a tenant, writes go to the
  canonical contract collection, which pays only after that tenant's cutover.
- Provider directory data (locations, specialties, networks for Plan-Net) is
  not part of this file. That is the roster import for provider-service.
