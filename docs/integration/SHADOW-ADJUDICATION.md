# Shadow adjudication

Re-adjudicates claims the plan's core system (QNXT, Facets, HealthRules, ...)
already adjudicated, in CHO's own claim pipeline, and records where CHO's
answer differs from the core's: allowed, paid, member liability, and denial
codes (CARC), claim by claim and line by line.

Nothing a member, a provider or a FHIR API sees changes. The claim keeps the
core's answer, its EOB is unchanged, no accumulator moves and no event is sent.
The comparisons live in their own collections.

What it is for:

- **Payment-integrity findings.** Claims where CHO, applying the plan's
  contracts and benefits, would have paid differently, with the dollar
  difference and the reason.
- **A migration rehearsal with no risk.** How much of the plan's real claim
  volume CHO can already adjudicate to the same answer, and, for the rest,
  which missing input stops it.

It reads claims the [CPCDS file-drop importer](CPCDS-FILE-DROP-IMPORTER.md)
loaded (claims with an external source). Load claims there first.

## How a shadow run stays read-only

The run uses the same `ClaimAdjudicationOrchestrator` as a real adjudication,
on a detached copy of the claim with the core's answer removed. It executes
only the stages listed in `ClaimAdjudicationOrchestrator.ShadowSafeStages`:

| Stage | Why it is safe |
|---|---|
| Scrubbing, TimelyFiling, DuplicateClaim, ProviderIntegrity, NetworkCredentialing, InpatientGrouping, Pricing, CoordinationOfBenefits, NcciEdits | They read reference data, plan settings and claim history only. DuplicateClaim already excludes the claim's own chain. |
| BenefitCalculation | Calls the benefit engine in Prospective mode with the core's accumulation ([Accumulators](#accumulators)): the engine neither reads nor writes CHO's accumulator store and prepares no commit. |
| PlanEdits | Calls the plan's own edit service, if the tenant configured one. |

Left out: **Persistence** (writes the claim's projection and the accumulator
outbox) and **AiExamination** (publishes a ClaimPended event and spends model
tokens). The run also drives no accumulator outbox and emits no adjudicated
event. The list is an allowlist: a stage added to the pipeline later is left out
of shadow runs until it is classified, and a unit test fails until it is.

## Endpoints (claims-service)

All require `claims:import`, the same permission as the CPCDS importer: the
results show the plan's payment differences.

### Turning it on for a plan

`GET /api/v1/claims/shadow-adjudication/settings` says whether runs are accepted
for the tenant now (`running`), whether the deployment allows them
(`enabledForDeployment`), the tenant's own choice (`enabled`, from `Stored` or
`Configuration`), `maxClaimsPerRun` and `tolerance`.

`PUT /api/v1/claims/shadow-adjudication/settings` with `{ "enabled": true }`
turns it on for the tenant without a redeploy, and `false` turns it off. A
saved choice replaces what `Adjudication:Shadow:Tenants` says for that tenant,
in either direction. `Adjudication:Shadow:Enabled` stays the deployment's
switch: while it is off, nothing runs, whatever a tenant saved.

In the portal: Operations → Shadow Adjudication (or Imports & EDI, Setup), "Shadow adjudication for this plan".
The Shadow Adjudication page also runs shadow adjudication on a loaded claim
file, lists the runs, and shows each run's report and discrepancy CSV.
Switching on "Shadow-adjudicate each claim file after it loads" there turns
shadow adjudication on for the plan too.

The laptop trial (`deploy/local/cpcds-trial`) and the hosted drop
(`deploy/gcp/render_manifests.py --cpcds-drop-bucket`) set
`Adjudication__Shadow__Enabled=true`, so there it is on for every plan unless
the plan turns it off.

### Runs

`POST /api/v1/claims/shadow-adjudication/runs`

```json
{ "importBatchId": "8f0c...", "limit": 200 }
```

or `{ "claimIds": ["cpcds-...", "cpcds-..."] }` (the `choClaimId` values from
the CPCDS import report). Runs synchronously over at most
`MaxClaimsPerRun` claims and returns the run's totals:

| Field | Meaning |
|---|---|
| `claimsCompared` | Claims with a stored result. |
| `countsByKind` | How many of each kind (below). |
| `undecidedRate` | Share of the comparable claims CHO could not decide: the migration-readiness number. |
| `matchRate` | Share of the claims CHO decided that matched the core. |
| `netPaidVariance` | Sum of CHO paid minus core paid over decided claims. Positive: CHO would pay more. |
| `absolutePaidVariance` | Sum of the absolute paid differences. |
| `skipped` | Claims not found, or not loaded from a core. |
| `failed` | Claims whose run threw (logged). |

**Whole files, in the background.** Add `"background": true` (with an
`importBatchId`) and the call answers `202 Accepted` with the queued run at
once. A worker in claims-service then adjudicates every claim of the batch (up
to `MaxClaimsPerBackgroundRun`, 50,000), `MaxClaimsPerRun` at a time, saving
the results and `claimsProcessed` after each page. `status` goes `Queued`,
`Running`, then `Completed` or `Failed` (with `error`). A replica that stops
mid-run leaves its lease (`LeaseDuration`, 5 minutes) to lapse, and the next
one resumes after the claims already compared. The automatic run after each
CPCDS file drop and the portal's "Run shadow" both use this.

`GET /api/v1/claims/shadow-adjudication/runs` lists recent runs,
`GET .../runs/{runId}` returns one, and `GET .../runs/{runId}/results?kind=PaidVariance&limit=200`
returns per-claim comparisons, largest paid difference first.

### Run report

`GET .../runs/{runId}/report?top=10` is what a plan reads after a run:

| Field | Meaning |
|---|---|
| `comparableClaims`, `decidedClaims` | Claims whose core answer is final, and the ones CHO decided. |
| `corePaidMore` | Decided claims where the core paid more than CHO would have (possible overpayments): claims and dollars. |
| `corePaidLess` | Decided claims where the core paid less (possible underpayments). |
| `byKind` | Every kind: claims, net and absolute paid variance, core paid. |
| `undecidedByGap` | Why CHO could not decide, by gap code, most claims first, with the core-paid dollars CHO cannot check yet. This is the to-do list for loading inputs. |
| `heldForProviderScreening` | Decided claims CHO would still hold because it could not screen a provider (`PROVIDERSCREEN`, see [Provider screening](#provider-screening)). They count as decided. |
| `byChoStage` | The CHO stage behind each non-matching decision (its first finding; Pricing for an allowed difference and BenefitCalculation for a payment difference when no stage reported one), largest dollars first. |
| `byDenialCodes` | Denial disagreements as `core CARC → CHO CARC` (`paid` when that side paid). |
| `byProcedure` | Procedure codes (or `REV` revenue codes) on lines that differ, with line counts and line dollars. |
| `byBillingProvider` | Billing NPIs on non-matching decided claims. |

Each row carries up to five example claim numbers, biggest difference first.

### Discrepancy CSV

`GET .../runs/{runId}/discrepancies.csv?includeUndecided=false` downloads the
differences for a payment-integrity team: one row per differing line, or one
claim-level row when the claim differs but no single line does (an outcome
mismatch with no line amounts, say). Matches and claims that were not
comparable are left out. `includeUndecided=true` adds the claims CHO could not
decide, with the gap code and no CHO amounts. Text fields that start with `=`,
`+`, `-` or `@` get a leading apostrophe so a spreadsheet does not run them as
formulas.

## Kinds

One kind per claim, in this order of precedence:

| Kind | Meaning |
|---|---|
| `NotComparable` | The core's claim is not final (pended, voided). CHO is not run. |
| `ChoUndecided` | CHO pended or rejected the claim, or denied it only because the member is not loaded. `choPendCode` names the gap: `NOCONTRACT` (no contract or fee schedule rate), `PRICING`, `PLANUNAVAIL` (the member's coverage names no CHO plan, usually an unmapped core plan code), `REJECT:<stage>`, `MEMBERNOTLOADED`, ... |
| `OutcomeMismatch` | One side paid, the other denied. |
| `DenialCodeMismatch` | Both denied, with different CARCs (or a line both denied, with different codes). |
| `AllowedVariance` | Allowed differs on the claim or a line: pricing. |
| `PaidVariance` | Allowed agrees, payment differs (or a line one side denied): benefits or accumulators. |
| `MemberLiabilityVariance` | Allowed and paid agree, the member's share differs. |
| `Match` | Same outcome, same amounts within `Tolerance`. |

Each result also carries the line comparisons (`lines[].differences`: Allowed,
Paid, MemberLiability, LineOutcome, DenialCode) and every CHO stage finding.

Line denial codes: the core's come from the CPCDS "Line payment denial code"
the importer stores; CHO's are the line's adjustments other than the member's
share (group PR) and the contractual write-down (CARC 45).

## Provider screening

CHO checks every billing and rendering provider against the federal exclusion
lists (OIG LEIE, SAM) and NPPES before it pays, and holds a claim for review
(`MEDREVIEW`) when it cannot clear the provider. A new deployment has screened
no one, so without screening every claim would be held.

In a shadow run, a claim whose only hold is that screening is compared on what
CHO decides once the provider clears. The result says so:
`heldForProviderScreening` is true and `choPendCode` is `PROVIDERSCREEN`; the
report counts these claims in `heldForProviderScreening`, and the portal and
`trial.sh shadow` say how many there are. A confirmed exclusion is a denial and
is compared as one. A screening hold together with any other pend stays
`ChoUndecided`.

To screen providers, run provider-verification-service with
`ProviderVerification__ExclusionScreening__Leie__Enabled=true` (it downloads the
OIG list from oig.hhs.gov and refreshes it daily; SAM needs an API key), and set
`Services__ProviderVerificationServiceUrl` and `Services__ProviderServiceUrl` on
benefit-plan-service. The CPCDS laptop trial does both. On GKE,
`render_manifests.py --leie-screening` turns LEIE on. Providers that are not in
NPPES (the synthetic sample NPIs, for one) are still held after screening.

## Accumulators

The core priced each claim against what the member had already met toward the
deductible and out-of-pocket maximum. CHO's accumulator store holds nothing for
a plan that is not live on CHO, so a shadow run never uses it. It starts each
claim from the core's own account instead:

- The member's earlier claims loaded from the core (`externalSource` set, paid
  or partly paid, current version) in the same calendar year and on the same
  benefit plan. Each counts its deductible and its out-of-pocket amount
  (`oopAppliedAmount`, or the member liability), in network or out of network
  as the core recorded it.
- "Earlier" is the core's order: claims the core adjudicated before this one
  (same day: the earlier service date). Without adjudication dates, the earlier
  service date.
- Claims of other family members count toward the family deductible and
  out-of-pocket maximum when they carry the same `subscriberId`. CPCDS claim
  files name only the patient, so imported claims do not yet; load family
  totals in a [starting balance](#starting-balances).

### Starting balances

When the files do not reach back to the start of the plan year, load the
core's own accumulators as of a date. A balance stands in for every core claim
of its network tier the core had adjudicated by that date; claims after it add
to it. For each claim, CHO uses the member's latest balance dated before the
core adjudicated the claim.

`POST /api/v1/claims/shadow-adjudication/accumulator-balances/import`
(`claims:import`, multipart: `file`, optional `sourceSystem` for the member
crosswalk, `dryRun`). `trial.sh` loads any file whose name contains `balance`.
CSV with a header row (comma, pipe or tab; case, spaces and underscores in the
header are ignored):

| Column | |
|---|---|
| `member_id` | The core's member id (the crosswalk maps it) |
| `as_of_date` | `yyyy-MM-dd`: the balance includes everything the core adjudicated through that day |
| `network` | `in-network` (default) or `out-of-network` |
| `individual_deductible_met`, `individual_oop_met` | The member's own |
| `family_deductible_met`, `family_oop_met` | The whole family's, this member included (optional) |

```csv
member_id,as_of_date,network,individual_deductible_met,individual_oop_met,family_deductible_met,family_oop_met
M100,2026-06-30,in-network,400.00,650.00,900.00,1200.00
```

Reloading replaces a balance for the same member, date and network. Balances
are kept in claims-service's `ShadowAccumulatorBalances` collection and never
reach CHO's accumulator store.

### Limits

Only what was loaded counts: a member with neither a balance nor the year's
earlier claims starts at zero. Accumulators reset on January 1; a plan year
that starts on another date is not handled. A benefit-plan-service older than
claims-service ignores the history; the claim then pends `SVCUNAVAIL` rather
than being compared against CHO's store.

## What CHO needs to decide a plan's claims

Expect most claims to be `ChoUndecided` until these are loaded. The run tells
you which one is missing.
The portal's Shadow Adjudication page lists each reason with what to load and
links to the page or guide that loads it ("What to load next").

| Input | Loaded through |
|---|---|
| Members, coverage, plan id, group | 834 (`enrollment-import-service`), or the CPCDS member and coverage files |
| Provider network and credentialing | provider-service: each plan's in-network tier names a network (`networkTiers[].networkId`), and the provider's network participation puts it in that network. Without it every provider is out of network |
| Service categories | `POST /api/v1/service-category-mappings/seed-system-defaults` once per tenant (benefit-plan-service, needs `ServiceCategoryMapping__AdminWriteEnabled=true`). Without them a lab test or X-ray billed from an office is costed as an office visit |
| Provider screening | See [Provider screening](#provider-screening) |
| Medicare physician fee schedule | [Medicare PFS importer](MEDICARE-PFS-IMPORTER.md), one schedule per locality (the PricingApi uploads do not reach adjudication) |
| Provider contracts and custom fee schedules | [Contract roster importer](CONTRACT-ROSTER-IMPORTER.md) and [fee schedule CSV importer](FEE-SCHEDULE-IMPORTER.md) |
| Benefit plan design | benefit-plan-service API / portal |
| Accumulators to date | The core's earlier claims in the same files ([Accumulators](#accumulators)). Load the whole plan year, or the core's [starting balances](#starting-balances), so a mid-year claim meets the deductible where the core did |
| Prior authorizations | authorization-service |

## Configuration

`Adjudication:Shadow` in claims-service:

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | The deployment's switch. Runs are refused (409) unless true. `true` in the CPCDS trial and hosted drop profiles. |
| `Tenants` | `[]` | When set, only these tenants run, unless a tenant saved its own choice (`PUT .../settings`), which wins. Empty: every tenant. |
| `MaxClaimsPerRun` | `200` | Most claims one synchronous run adjudicates; the page size of a background run. |
| `MaxClaimsPerBackgroundRun` | `50000` | Most claims one background run adjudicates. |
| `WorkerInterval` | `00:00:10` | How often the worker looks for queued runs. |
| `LeaseDuration` | `00:05:00` | How long a replica holds a running run between pages. |
| `Tolerance` | `0.01` | Dollar difference treated as equal. |

A shadow run calls the same services a real adjudication does (benefit-plan,
pricing, provider, coverage, member, authorization), once per claim.

## Roadmap

1. **This change:** shadow mode in the pipeline, the comparer, the result
   store and the run endpoints.
2. Run report: top reasons by count and dollars (gap code, CHO stage, CARC,
   procedure, provider) and the discrepancy CSV.
3. Provider contract and custom fee schedule loader: the contract roster and
   fee schedule CSV importers.
4. Accumulators: derived from the imported claims, plus the core's starting
   balances, held apart from live balances (done).
5. Automatic run after each CPCDS file-drop batch; re-run after a configuration
   change to show the gap closing.
6. Benefit plan template import and a portal report page.
