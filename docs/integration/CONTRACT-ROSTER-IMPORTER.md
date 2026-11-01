# Provider Contract Roster Importer

Loads a plan's provider contracts from a CSV into the contracts claims are
priced from (the Fee Schedule Engine's `ProviderContract` documents, read by
`RateResolutionService`). After the import, a contracted provider's claim lines
are priced by the terms of its contract (a fee schedule, a percent of
Medicare, a percent of billed charges, a case rate or a per diem) instead of
the plan's default schedule. This is the contract input that
[shadow adjudication](SHADOW-ADJUDICATION.md) was missing.

Plans hold their contracts in one of two places, and `systemOfRecord` says which:

- **`core` (default): the plan's core admin system** (QNXT, Facets, ...) holds
  the contracts, and the import is a **pricing copy**. Each contract is tagged
  `origin: import:{source}`, and the next import of the same source replaces
  it. Contracts written any other way (the ADR 016 backfill, an engine seed,
  CHO legal contracts) are left alone unless you pass `replaceNonImported=true`.
  No legal contract records are created in provider-contracts-service.
- **`cho`: CHO holds the contracts.** Each NPI and contract number becomes a
  legal contract in provider-contracts-service, the same record the contracts
  portal edits. The import creates it (or replaces its rate sheet), activates
  it, and then writes that contract's pricing projection
  (`GET /api/v1/contracts/{id}/pricing-contract`) to the store claims are
  priced from, tagged `origin: legal`. See [CHO mode](#cho-mode) below.

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
| `systemOfRecord` | `core` (default) or `cho`. |

## File layout

One row per provider, plan and reimbursement term. Rows with the same NPI and
plan become one contract, and its terms keep file order. The engine prices a
line by the **first** term whose scope and dates match, so put narrow terms
(a code range, a claim type) before a catch-all term. The header can sit
below title rows. Column names are matched ignoring case and punctuation.

| Column | Required | Values |
|---|---|---|
| `NPI` | yes | 10-digit NPI with a valid check digit. Claims are matched by rendering NPI, then billing NPI. |
| `Plan` | yes | A plan code (`planId` of the benefit plan, e.g. `STAR-TX-01`): the contract applies to every version of the plan and survives an amendment. Or a plan version id (GUID): that version only, and it wins over a plan-code contract for claims on that version. |
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
| `Contract Number` | in `cho` mode | Rows with the same NPI and number are one CHO contract. Optional in `core` mode. |
| `Line of Business`, `Description` | no | Carried on the contract or term. |
| `Provider Name`, `Provider Type` | no | `cho` mode only, for a new CHO contract. Type `organization` or `individual` (default). The NPI stands in for a missing name. |

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
  `Replaced`, `Unchanged` and `Protected` for the contracts claims are
  priced from.
- `legalContracts` (`cho` mode): one entry per CHO contract with its id and
  outcome: `WouldCreate`, `WouldReplaceRates`, `Created`, `RatesReplaced`,
  `Unchanged`, `Rejected` or `Failed`, and a `detail` saying why.

## CHO mode

`systemOfRecord=cho` also needs the caller's `contracts:write`: the caller's
own token is forwarded to provider-contracts-service, which checks it. Without
it every contract comes back `Failed` with that reason.

For each NPI and contract number:

1. The contract is looked up by number. A number that already belongs to
   another NPI, or a terminated or expired contract, is `Rejected`.
2. A new contract is created with the file's provider, plans, line of
   business, network status and effective date. For an existing contract only
   the **rate sheet** is replaced. Everything else on it (TIN, dates,
   signatory, plans) is the contracts portal's to change. Plans in the file
   that the contract does not list are named in `detail`; add them in the
   portal.
3. A new, draft or suspended contract is activated.
4. Its pricing projection is written for each plan. A projection slot held by
   a pricing copy from this importer, or by this same contract, is replaced.
   A slot held by a different CHO contract is `Protected` unless
   `replaceNonImported=true`.

A CHO contract has one rate sheet for all of its plans, so rows under one
number whose plans carry different terms are `Rejected`. Give each plan its
own contract number instead.

A dry run only looks contracts up; it creates and writes nothing.


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

- **Plan versions.** Claims carry the plan version id and the plan code.
  Pricing looks for a contract (and a plan default schedule) keyed to the
  version first, then to the plan code. Rows given by plan code keep
  pricing after the plan is amended; rows given by version id must be
  re-imported for the new version.
- Contracts not in a later file are not deleted.
- **CHO mode, later portal edits.** A rate change made in the contracts
  portal after the import does not yet re-project to pricing by itself
  (ADR 016 phase 4). Keep the roster file as the source of rates: a re-run
  replaces the contract's rate sheet with the file's and writes the projection
  again, so a rate edited only in the portal is overwritten by the next run.
- With the ADR 016 dual-read window open for a tenant, writes go to the
  canonical contract collection, which pays only after that tenant's cutover.
- Provider directory data (locations, specialties, networks for Plan-Net) is
  not part of this file. That is the roster import for provider-service.
