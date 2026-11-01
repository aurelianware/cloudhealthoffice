# Fee Schedule CSV Importer

Loads a flat-rate fee schedule file into the fee schedule store that claims
adjudication prices from (`FeeSchedules`, read by `RateResolutionService` in
benefit-plan-service). It is the fee schedule counterpart of the benefit plan
importers ([QHP PUF](QHP-PUF-BENEFIT-PLAN-IMPORTER.md) and the state Medicaid
templates): a plan seeded from public benefit data also needs the rates its
claims are paid at.

Typical inputs:

- a **state Medicaid fee schedule** (Texas TMHP, Medi-Cal, AHCCCS, Florida AHCA,
  eMedNY, ...), saved as CSV, as the default schedule of a Medicaid managed care
  plan;
- a **plan's contracted rate sheet** (for a provider contract term to name by
  id), the missing input that leaves shadow adjudication lines `ChoUndecided`
  (see [SHADOW-ADJUDICATION.md](SHADOW-ADJUDICATION.md));
- any other per-code dollar schedule, such as a CMS schedule already priced
  for one locality or state (DMEPOS, clinical lab).

## Endpoint

`POST /api/v1/fee-schedules/import/csv` (benefit-plan-service), multipart
field `file` (CSV, or a `.zip` holding one). Permission `settings:manage`.
**Dry run by default**: nothing is written until `dryRun=false`.

| Query parameter | Required | Meaning |
|---|---|---|
| `name` | yes | Schedule name, e.g. `TX Medicaid Physician 2026-09`. |
| `effectiveDate` | yes | Date the rates take effect (`2026-09-01`). Rows dated later are skipped. |
| `termDate` | no | Last day + 1. |
| `type` | no | `Medicaid`, `Commercial` or `Custom` (default). All are flat rates per unit; `Medicaid` reports `RateSource.Medicaid`. |
| `planId` | no | Make it this benefit plan's **default schedule** (prices lines whose provider has no contract). Uses the same id as `PUT /api/v1/plans/{planId}/default-fee-schedule`, so one default per plan. Without it the schedule is standalone, id `{tenant}:{name slug}:{yyyyMMdd}`, for a contract term's `feeScheduleId`. |
| `sourceType`, `sourceId`, `sourceVersion`, `jurisdiction` | no | Provenance stored on the schedule (`sourceType` defaults to `PublicGovernment` for Medicaid, `PayerContract` otherwise). |
| `filter` | no, repeatable | `Column=Value`, keep only matching rows, e.g. `filter=Provider Type=Physician`. State exports often hold several programs or provider types in one file. |
| `codeColumn`, `rateColumn`, `facilityRateColumn`, `modifierColumn`, `revenueCodeColumn` | no | Name a column when the aliases below do not find it. |
| `onDuplicate` | no | `Reject` (default), `First`, `Lowest` or `Highest`, for codes that still have more than one rate. |
| `keepZeroRates` | no | Load `$0.00` rows (off: a state's $0 usually means manually priced or not payable). |
| `replace` | no | Replace an existing schedule with the same id that has different rates. |
| `includeLines` | no | Return every line in the report, not the first 25. |

### How the file is read

- **Header row found by alias** in the first 30 rows, so title and note rows
  above it are fine. Names are compared ignoring case, spaces and punctuation:
  code `Procedure Code`, `Proc Code`, `HCPCS`, `CPT`, `CPT/HCPCS`, `Code`, ...;
  rate `Non-Facility Fee`, `Max Fee`, `Maximum Allowable`, `Medicaid Rate`,
  `Fee`, `Rate`, `Amount`, ...; facility rate `Facility Fee`, `Facility Rate`;
  also `Modifier`/`Mod`, `Modifier 2`, `Revenue Code`, `Effective Date`,
  `End Date`, `Mult Proc`. The report's `columns` says which column was read
  for each role.
- **Codes**: 5-character CPT, HCPCS or CDT codes; revenue-code-only lines
  (blank procedure code) price 837I lines by revenue code.
- **Not loaded** (counted in `skipped` with examples): by report, manually
  priced, not covered, `N/A`, `*`, blank, `$0.00`, negative or unreadable
  rates; two modifiers (the engine matches one per rate); rates effective
  after `effectiveDate` or ended before it. A code that is not loaded has no
  rate line, so its claim lines are not priced from the schedule and pend.
- **Effective-dated history**: when a code has several rows, the latest one in
  force on `effectiveDate` wins. Rows that still disagree are conflicts:
  rejected by default with the codes listed, so a missing `filter` is not
  silently resolved.
- **Re-import** of the same file is `Unchanged` (checksum match). A changed
  file needs `replace=true`.
- At most 50,000 lines per schedule.

### Example: Texas Medicaid physician schedule as a STAR plan's default

```bash
# 1. Dry run: check columns, skips and duplicates
curl -X POST "$CHO/api/v1/fee-schedules/import/csv?name=TX%20Medicaid%20Physician%202026-09&effectiveDate=2026-09-01&type=Medicaid&jurisdiction=TX&sourceId=tx-tmhp-physician&sourceVersion=2026-10-01&planId=$PLAN&filter=Provider%20Type%3DPhysician" \
  -H "Authorization: Bearer $TOKEN" -F file=@tmhp-physician.csv | jq '{outcome, columns, linesLoaded, skipped, duplicateKeys, errors}'

# 2. Write it
curl -X POST "...same query...&dryRun=false" -H "Authorization: Bearer $TOKEN" -F file=@tmhp-physician.csv
```

A synthetic sample is in [samples/fee-schedules](samples/fee-schedules/state-medicaid-physician-sample.csv).

## Which public source fits which plan

| Plan | Default fee schedule (uncontracted providers) | Public source | Load path |
|---|---|---|---|
| Medicaid managed care (TX STAR/CHIP, Medi-Cal, AHCCCS, FL SMMC, NY) | State Medicaid FFS schedule, by program and provider type. Out-of-network payment rules are set by the state contract; check it. | TMHP fee schedules (Texas), DHCS Medi-Cal Rates file (California), each state agency's fee schedule pages | **This importer** |
| Medicare Advantage | Original Medicare rates for the provider's locality | CMS PFS RVU files (PPRRVU + GPCI + conversion factor), OPPS Addendum B, ASC Addendum AA, DMEPOS, CLFS, IPPS | PFS: [Medicare PFS importer](MEDICARE-PFS-IMPORTER.md). Per-state DMEPOS and CLFS: this importer with column overrides. |
| Exchange / commercial | No public default; the plan's own contracts | Transparency in Coverage in-network rate files (the plan's own) | Future: contract import from the plan's TiC file |

### Not yet loadable into the claims store

- **Medicare PFS by locality** now loads with the
  [Medicare PFS importer](MEDICARE-PFS-IMPORTER.md) (one RVU schedule per
  locality, the base that percent-of-Medicare terms and Medicaid schedules
  reference). The Pricing API's own CMS loaders still write only the Pricing
  API's store.
- **IPPS (MS-DRG)** needs each hospital's base rate as well as the weights.
- **OPPS** packaging and status-indicator rules are not modelled; loading
  Addendum B as flat rates is only an approximation.
- Fee schedules are per tenant (`GetByIdAsync` filters by tenant), so a public
  schedule is loaded into each tenant that uses it.
