# Shared fee schedule library

New plans should not have to find, download and load public fee schedules
before they can price a claim. Aurelianware loads the common reference
schedules once into a shared library. Examples are Medicare PFS by locality,
the Medi-Cal physician rates and the TMHP static schedules. A tenant then
browses the library, opens a schedule's rates, and deploys a copy into its own
store at a percentage, either as a plan's default schedule or as a standalone
schedule for contracts to reference.

Code: `benefit-plan-service`, `Import/FeeSchedules/FeeScheduleLibraryService.cs`
and `Controllers/FeeScheduleLibraryController.cs`.

## Where the library lives

Library schedules are ordinary fee schedules stored under a reserved tenant id,
`FeeScheduleLibrary:TenantId`. The default is `cho-fee-schedule-library`. No
plan should ever be given that tenant id. Claims never price from the library
directly, because the engine only reads the claim's own tenant. A tenant always
prices from its deployed copy.

## API

Schedule ids contain colons and free-text names, so they are sent as a query
parameter or in the body.

| Call | Permission | What it does |
|---|---|---|
| `GET /api/v1/fee-schedule-library?jurisdiction=TX` | `benefits:read` | Library schedules, without lines. |
| `GET /api/v1/fee-schedule-library/schedule?id=…&code=992&skip=0&take=100` | `benefits:read` | One library schedule, its line count, and a page of lines filtered by code prefix. Each line shows the per-unit amount, with RVU lines computed from the locality's GPCIs and conversion factor the same way the engine computes them. |
| `POST /api/v1/fee-schedule-library/deploy` | `settings:manage` | Copies a library schedule into the caller's tenant at a percentage. Dry run by default. |
| `GET /api/v1/fee-schedules?jurisdiction=` | `benefits:read` | The tenant's own schedules (plan defaults, imports, deployed copies), without lines. |
| `GET /api/v1/fee-schedules/schedule?id=…` | `benefits:read` | One of the tenant's schedules with a page of lines. |
| `POST /api/v1/fee-schedule-library/publish` | `platform:admin` | Copies a schedule from the caller's tenant into the library. Dry run by default. |

### Deploy body

```json
{
  "sourceId": "cho-fee-schedule-library:Medicare PFS 2026 TX DALLAS (18):20260101",
  "percent": 110,
  "planId": "TX-STAR-2027",
  "name": null,
  "effectiveDate": "2027-01-01",
  "termDate": null,
  "licenseAcknowledged": true,
  "replace": false,
  "dryRun": true
}
```

- `percent` is a percentage of the library rates, from 1 to 1000. 100 keeps
  the same rates.
- `planId` is a plan code (every version of the plan) or a plan version id
  (that version only), the same as the importers. With `planId` the copy
  becomes that plan's default schedule (`{tenant}:plan-default:{key}`). Without
  it, give a `name`, and the copy is a standalone schedule
  (`{tenant}:{name}:{yyyyMMdd}`) that a provider contract can reference.
- A target that already exists is reported as `Exists` and left alone unless
  `replace` is true.
- The answer previews common codes (99213, 99214, 99203, 99285, 70450, 80053,
  85025, 97110 and E0601 where present, otherwise the first five lines), with
  the library amount and the deployed amount for each.

### What the percentage scales

A deploy is a copy, so a later library update does not change a tenant's
schedule. Every dollar value is multiplied:

- line `rate` and `facilityRate`, rounded to cents
- percentage-type line rates (percent of billed or of Medicare), kept to four places
- the schedule's `conversionFactor` (RVU schedules), `drgBaseRate`, `perDiemRate`
  and `percentOfMedicare`

So deploying a Medicare PFS locality at 110% prices every code at 110% of that
locality's Medicare amount. RVUs, GPCIs, DRG weights and edit indicators are
copied unchanged.

A library schedule that prices from another Medicare schedule
(`baseMpfsFeeScheduleId`) is deployed with a warning. That id points into the
library, so the tenant must deploy the Medicare schedule too and point the copy
at it.

### CPT licensing

Medicare PFS and most state physician schedules are keyed by CPT and HCPCS
codes. CPT is licensed by the AMA. Carrying codes and rates, with no
descriptors, through a shared library is a redistribution question that
Aurelianware's counsel should confirm. In the meantime:

- A schedule keyed by CPT or HCPCS (any `MedicareMpfs` schedule, or a code
  system naming CPT or HCPCS) deploys only with `licenseAcknowledged: true`.
  That is the tenant confirming it holds its own CPT license. A dry run does
  not need it.
- The deployed copy is marked `LicenseClassification = CustomerProvided`, and
  the confirmation is written to the audit log with the actor:
  `AUDIT fee schedule library deploy … cptLicenseAcknowledged=True actor=…`.

## Loading the library (platform administrators)

1. In an Aurelianware operations tenant, load the source with the existing
   importer. The importers are documented in `MEDICARE-PFS-IMPORTER.md` and
   `FEE-SCHEDULE-IMPORTER.md`, including the Medi-Cal and TMHP filter
   settings. Load it as a standalone schedule or as a plan default.
2. Check the result with `GET /api/v1/fee-schedules/schedule?id=…`.
3. `POST /api/v1/fee-schedule-library/publish` with `{"sourceId": "…",
   "name": "TMHP adult acute care 2026-10", "jurisdiction": "TX",
   "dryRun": false}`. The library copy drops the plan link, is marked
   `isGlobal`, and is named and dated as given. Publishing the same name and
   effective date again needs `replace: true`.

Recommended first library entries, all already proven against real files
(see the fee schedule import notes):

- Medicare PFS 2026, every locality (`allLocalities=true`)
- Medi-Cal physician rates, base plus TRI
- TMHP adult acute care (the filter for client ages through 999)

## Not yet

- There is no library entry versioning or withdrawal beyond replace. To retire
  an entry, replace it with a `termDate`.
- Child-age TMHP rates need their own entry, because the engine has no age dimension.
- There is no bulk deploy across many plans at once.
