# Shadow adjudication

Re-adjudicates claims the plan's core system (QNXT, Facets, HealthRules, ...)
already adjudicated, in CHO's own claim pipeline, and records where CHO's
answer differs from the core's: allowed, paid, member liability, and denial
codes (CARC), claim by claim and line by line.

Nothing a member, a provider or a FHIR API sees changes. The claim keeps the
core's answer, its EOB is unchanged, no accumulator moves and no event is sent.
The comparisons live in their own collections.

It reads claims the [CPCDS file-drop importer](CPCDS-FILE-DROP-IMPORTER.md)
loaded (claims with an external source). Load claims there first.

## Why a plan runs it

A plan will rarely buy a second claims engine for its own sake. A shadow run
earns its place by answering questions the plan already pays to answer:

- **A migration rehearsal with no risk.** A parallel run is standard practice
  before any core conversion; no plan switches off QNXT or Facets without
  proof that the new system pays its claims the same way. A shadow run gives
  that proof from the first file instead of after months of configuration:
  how much of the plan's real claim volume CHO can already adjudicate to the
  same answer, and, for the rest, which missing input stops it. The plan can
  stop at any point and the core never noticed.
- **Payment-integrity findings, in dollars.** Claims where CHO, applying the
  plan's contracts, fee schedules and benefits, would have paid differently,
  with the dollar difference and the reason. The report keeps possible
  overpayments (`corePaidMore`) and possible underpayments (`corePaidLess`)
  apart. Plans already pay payment-integrity vendors for leads like these,
  often for a share of what is recovered.
- **Authorizations checked against claims.** Claims the core denied for a
  missing authorization when one is on file, and claims it paid without a valid
  one where one was required (`authorizationMismatches`; see
  [Prior authorization](#prior-authorization)). In Medicaid managed care both
  are audit exposure: the state's contract sets the authorization rules, the
  plan's encounters show what was paid, and auditors test whether payments
  were supported.

Every finding is a lead for the plan's review, not a determination.

### What the plan does with the findings

The run report and the [discrepancy CSV](#discrepancy-csv) are a worklist for
the plan's claims operations and payment-integrity teams. They review it,
decide which findings are real, and run the approved adjustments through the
core's own mass-adjustment process, the way they handle any payment-integrity
vendor's findings.

**CHO never writes to the core.** Every adjustment changes a provider payment
and an 835, and in Medicaid the encounter sent to the state, so it stays with
the people accountable for those. A file laid out for one core's adjustment
import (QNXT, Facets) is not built; it is scoped per plan when one asks, since
plans customize those imports.

### A shadow pilot

A pilot has two phases. **Historical first, for quick dollars:** 6 to 12
months of claims the core already paid, with the contracts, fee schedules,
benefits and authorizations in effect on each date of service, shadow-run as
monthly files; it ends with a payment-integrity report and an
authorization-mismatch report in dollars within weeks. How far back a finding
can still be recovered is set by the plan's provider contracts and state rules.
**Concurrent if the plan wants proof CHO can carry its core:** 60 to 90 days on
the plan's daily files, ending with a measured answer, line of business by line
of business, with the list of what CHO would still need. What the plan sends,
the security and BAA steps before any data moves, the timeline to the first
report and what the report contains are in the
[shadow pilot onboarding kit](SHADOW-PILOT-ONBOARDING.md). The public
version of this argument is
[Why run shadow adjudication?](https://cloudhealthoffice.com/insights/cms-0057-f/why-shadow-adjudication)
(`src/site/insights/cms-0057-f/why-shadow-adjudication.html`).

### Denial verification

Every denial the core made gets a verdict from CHO: each claim the core denied,
and each line it denied on a claim it paid.

| Verdict | Meaning | Why it matters |
|---|---|---|
| CHO would pay | Applying the plan's own contracts, benefits and authorizations, CHO pays the claim or line | Wrongful-denial risk: an appeal, provider abrasion, in Medicaid a complaint the state may see. `choWouldPayAmount` is what CHO would have paid |
| CHO denies for another reason | CHO denies too, with a different CARC | The denial may stand while the reason on the remittance is wrong, which weakens it on appeal |
| CHO confirms | CHO denies with the same CARC | Evidence the denial holds up |
| CHO undecided | CHO could not decide the claim yet | "What to load next" says why |

`byCoreDenialCode` groups the verdicts by the core's CARC, the most
CHO-would-pay dollars first, with the confirm rate (confirmed of decided) and
the codes CHO used instead. A denial reason with a low confirm rate is the one
to look at first. Authorization denials appear here too, and with their
evidence in `authorizationMismatches`. Every verdict is a lead for the plan's
review, not a determination.

## Recovery worklist

Each finished run adds its payment differences to a worklist the plan's
claims and payment-integrity teams work through (portal: **Recovery
Worklist**, under Shadow Adjudication). CHO never writes to the core and never
contacts a provider: approved findings go through the plan's own
mass-adjustment process, and the plan records here what came back.

- **One finding per claim** (`ShadowFindings`, keyed by tenant and claim
  chain): a decided claim whose payment differs by more than the tolerance.
  `direction` is `CorePaidMore` (possible overpayment, to recover) or
  `CorePaidLess` (possible underpayment, to pay). A later run refreshes a
  finding not yet reviewed; once reviewed it only records whether the claim
  still differs (`stillDiffers`).
- **Review status:** `New` → `Approved` or `Rejected`; `Approved` →
  `InRecovery`, `Recovered` or `Rejected`; `InRecovery` → `Recovered`,
  `Appealed` or `Rejected`; `Appealed` → `InRecovery`, `Recovered` or
  `Rejected`; `Rejected` → `New` (reopen). `Recovered` needs `recoveredAmount`
  (at most the finding's amount) and is final. Every change keeps who, when
  and the note. Changes that race another reviewer's are refused (409).
- **Recoupment lookback:** a default in days for the plan and an override per
  billing NPI (with its contract number), measured from the core's paid date.
  CHO does not know these limits on its own; the plan enters them from its
  provider contracts and state rules. Each open overpayment reads `Past`,
  `DueSoon` (30 days), `Within` or `Unknown` (nothing entered).
- **Summary:** overpayments and underpayments found (not rejected), confirmed
  by review, recovered and the recovery rate (recovered of confirmed
  overpayment dollars), totals by status, open overpayments past or near the
  lookback, and billing providers ranked by open overpayment dollars.
- **Export:** a plain CSV, one row per claim, the core's own claim id first,
  with the adjustment amount (CHO paid minus core paid), reason, status, who
  approved it and the lookback deadline. It is not laid out for any one
  core's import; that layout is scoped per plan when asked. Formula-safe.

| Endpoint | Permission | |
|---|---|---|
| `GET .../findings?status=&direction=&billingProviderNpi=&lookback=&limit=` | `claims:import` | Findings, largest amount first, each with its lookback state and the statuses it can move to |
| `GET .../findings/{id}` | `claims:import` | One finding |
| `GET .../findings/summary?topProviders=25` | `claims:import` | The totals above |
| `POST .../findings/{id}/status` | `claims:adjust` | `{ "status": "Recovered", "recoveredAmount": 25.00, "note": "offset on the 12/1 remit" }` |
| `GET .../findings/export.csv?status=Approved` | `claims:import` | The CSV (repeat `status` for several) |
| `GET`/`PUT .../findings/lookback` | `claims:import` / `claims:adjust` | `{ "defaultDays": 365, "providers": [ { "npi": "1234567890", "contractNumber": "K-1007", "days": 180 } ] }` |
| `POST .../runs/{runId}/findings` | `claims:import` | Adds a completed run's differences again (safe to repeat) |

`Adjudication:Shadow:AddFindingsAfterRun` (default `true`) adds each run's
differences automatically.

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

**Quick start and nightly runs in the portal.** The Shadow Adjudication page
takes a sample upload: a CPCDS claim file (a month is a good first sample, up
to 200 MB), and optionally CPCDS members and coverage and the UM platform's
authorizations. It loads them through the same importers the file drop uses
(enrollment-import-service, authorization-service, claims-service, each with
its own import permission), switches shadow adjudication on for the plan if
it is off, and queues a background run on the claims; the report opens when
the run finishes, usually the same day. "Run it every night" is one switch:
it turns on the plan's file drop with a shadow run after each claim file
(`ShadowAfterImport`) and shadow adjudication for the plan. The SFTP login
and folder are set up in Imports & EDI, Setup.
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

### Trend

`GET .../trend?days=90&bucket=week` (`day` or `week`, weeks from Monday UTC;
`days` at most 730) rolls the completed runs up by when they ran: runs, claims
compared, comparable, decided and matched claims, `decidedRate`, `matchRate`,
and `corePaidMoreAmount` and `corePaidLessAmount` (from each run's net and
absolute paid differences). An import batch run more than once counts its
latest run only. The portal shows the last 90 days by week once there are two
weeks of runs.

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
| `authorizationMismatches` | Claims one side denied for prior authorization and the other did not: "Core denied for authorization, CHO would pay" and "Core paid, CHO denies for authorization", with claims and dollars. See [Prior authorization](#prior-authorization). |
| `authorizationMismatchesByPolicy` | The same claims by the medical policy (and version) behind CHO's check, as the plan's PA rules name it ("No policy recorded" when they name none). Tells a reviewer which policy to open first. |
| `authorizationsMatchedWithoutNumber` | Comparable claims that carried no authorization number where CHO found an approved authorization by member, date, service and provider. |
| `denialVerification` | Every core denial checked against CHO (see [Denial verification](#denial-verification)): `coreDenials`, the four groups `choWouldPay`, `choDeniesForAnotherReason`, `choConfirms`, `choUndecided` (denials, claims, billed, what CHO would pay, example claims), `confirmRate`, and `byCoreDenialCode` with the same counts per CARC and the codes CHO used instead. |
| `byProcedure` | Procedure codes (or `REV` revenue codes) on lines that differ, with line counts and line dollars. |
| `byBillingProvider` | Billing NPIs on non-matching decided claims. |

Each row carries up to five example claim numbers, biggest difference first.

### Discrepancy CSV

`GET .../runs/{runId}/discrepancies.csv?includeUndecided=false` downloads the
differences for a payment-integrity team: one row per differing line, or one
claim-level row when the claim differs but no single line does (an outcome
mismatch with no line amounts, say). Matches and claims that were not
comparable are left out. `includeUndecided=true` adds the claims CHO could not
decide, with the gap code and no CHO amounts. The last seven columns are the
evidence for an authorization finding:

| Column | Meaning |
|---|---|
| `PriorAuthorizationNumber` | The line's number from the core's file, else the claim's. |
| `AuthorizationFinding` | One of the two `authorizationMismatches` keys, or empty. |
| `ChoAuthorizationNumber` | The authorization CHO checked: the claim's, or the one it matched. |
| `ChoAuthorizationSource` | `Claim line`, `Claim`, `Matched by member, date and service` (with the candidate count when more than one approval covered the service) or `None`. |
| `AuthorizationRequiredBy` | Why the service needed one: `Medicaid inpatient (POS 21)`, `Plan benefit flag` or `PA rule set`. |
| `MedicalPolicy` | The medical policy and version the PA rule was digitized from, its effective date, the rule id and the rule set version, e.g. `Plan medical policy MP-RAD-014 v4 (effective 2026-01-01); rule import:advanced-imaging; rule set 3f9a01c2d4e7`. |
| `AuthorizationCriteria` | The clinical criteria or policy the authorization's decision applied, when the plan's UM file gave it (`review_criteria`). |

A claim-level row joins its lines' distinct values with ` | `. Each result
also carries the same evidence as `priorAuthorizationEvidence[]`, one entry per
line that needed an authorization, with the check's outcome (`Valid`,
`Invalid`, `NotFound`, `Missing`, `Unavailable`, `ExemptionReview`) and reason.
Text fields that start with `=`,
`+`, `-` or `@` get a leading apostrophe so a spreadsheet does not run them as
formulas.

## Kinds

One kind per claim, in this order of precedence:

| Kind | Meaning |
|---|---|
| `NotComparable` | The core's claim is not final (pended, voided). CHO is not run. |
| `ChoUndecided` | CHO pended or rejected the claim, or denied it only because the member is not loaded. `choPendCode` names the gap: `NOCONTRACT` (no contract or fee schedule rate), `PRICING`, `PLANUNAVAIL` (the member's coverage names no CHO plan, usually an unmapped core plan code), `REJECT:<stage>`, `MEMBERNOTLOADED`, `NETWORK` (see [Network and credentialing](#network-and-credentialing)), ... |
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

## Prior authorization

A shadow run checks authorizations the way a live CHO adjudication does: each
line in CHO's prior-authorization scope is looked up in authorization-service by
the authorization number on the claim (the line's, else the claim's), for the
member, procedure, date of service and provider. When a line in scope has no
number, or a number that is not valid for the service, CHO denies the claim
(CARC 197); a line in scope only through the PA rule set pends `NOAUTH` for
review instead, which a shadow run counts as undecided.

For that to mean anything in a shadow run, CHO needs the plan's
authorizations, loaded through the [authorization import](AUTHORIZATION-IMPORT.md)
(`*auth*` files in the drop).

The number the core matched each claim to helps, in the claim file's optional
`Prior authorization number` / `Line prior authorization number` columns
([CPCDS importer](CPCDS-FILE-DROP-IMPORTER.md)), but it is not required. QNXT
and Facets attach authorizations to claims by member, date and service, and
their claim extracts often leave the number off. When a line in scope has no
number, a shadow run asks authorization-service
(`GET /api/authorizations/match`) for an approved authorization that covers it:

- the same member;
- an approved period (approved dates, else requested dates, and the expiration
  date) that includes the line's date of service;
- a service line approving the procedure, when the authorization lists
  services (an admission approval with no service lines matches on member and
  dates); a line with no decision of its own carries the authorization's;
- the provider, when the authorization names one (servicing, requesting or
  facility NPI; an institutional claim is matched on its billing NPI);
- status Approved or Modified, or an approval that has since expired when the
  service fell inside it.

One match or several, the line is authorized and the evidence names the one
used (the most specific first) and how many covered it. None: CHO denies 197
and the evidence says no approved authorization covers the service. No answer
from authorization-service: the claim pends `PAUNAVAIL`, as for a failed
lookup. Live adjudication does not match by default
(`ClaimsAdjudication:PriorAuth:MatchWithoutNumber`); shadow runs do
(`MatchWithoutNumberInShadow`, default `true`).

The scope is the institutional Medicaid inpatient floor (place of service 21),
plus plan-flagged procedure codes (`ClaimsAdjudication:PriorAuth:UsePlanFlaggedCodes`)
and the plan's PA rule set (`UseRuleSet`) where a deployment turns them on.

A disagreement is reported in `authorizationMismatches` and in the CSV's
`AuthorizationFinding` column when one side denied for authorization (CARC 15,
39, 62, 197 or 198) and the other did not. Both directions are leads for the
plan's review, not determinations.

### The policy behind a finding

Every line CHO checked records why it needed an authorization and under which
version of which policy, so a reviewer can tell whether a finding is the core
applying an old policy or CHO applying a new one:

- **The PA rule and its medical policy.** A plan's PA list loads through the
  [prior-auth rules importer](PRIOR-AUTH-RULES-IMPORTER.md) with
  `policySource`, `policyVersion` and `policyEffectiveDate`, naming the medical
  policy (or state manual edition) it was digitized from. Every rule carries
  them; the state manual promotion fills them from the manual edition.
- **The rule set version.** A fingerprint of the rule set's content
  (`ruleSetVersion` on the required-services list). It changes when any rule
  the check reads changes, and not when an unchanged list is reloaded.
- **The authorization's review criteria.** The criteria or policy the UM
  decision applied (InterQual, MCG, the plan's own policy and version), from
  the authorization file's optional `review_criteria` column.

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

## Network and credentialing

Testing and Production hosts (the GKE demo among them) run the network and
credentialing checks fail-closed (`Adjudication:Enforcement:NetworkMode` and
`CredentialingMode` = `FailClosed`): a claim whose billing provider is not in
the plan's network, or not credentialed on the date of service, is denied
(CARC 242 or B7). A Development stack only observes them (`SoftValidation`).

Before the plan's roster is loaded those denials say nothing about the claim, so
a shadow run counts a network or credentialing denial as `ChoUndecided` when:

- the claim has no CHO benefit plan (`PLANUNAVAIL`, usually an unmapped core plan code);
- the plan's in-network tier names no network (`NETWORK`);
- the provider has no credentialing record at all, status `Unknown` (`NETWORK`).

Load the core's roster with the [network roster importer](NETWORK-ROSTER-IMPORTER.md)
(with its credentialed column) to clear them. A provider in none of the plan's
networks, or whose credentialing is pending, denied, expired or suspended, is a
finding and is compared as the denial it is. Live adjudication is unchanged.

Credentialing is read from the provider's credentialing history (a recorded
decision), not the provider record's `credentialingStatus` field, so a provider
created with `credentialingStatus: "Approved"` and no decision is still `Unknown`.

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
| Provider network and credentialing | The core's roster through the [network roster importer](NETWORK-ROSTER-IMPORTER.md) (`trial.sh` loads `*roster*` files). Each plan's in-network tier names a network (`networkTiers[].networkId`), and the provider's network participation puts it in that network. Without it every provider is out of network |
| Service categories | `POST /api/v1/service-category-mappings/seed-system-defaults` once per tenant (benefit-plan-service, needs `ServiceCategoryMapping__AdminWriteEnabled=true`). Optional: until a plan has categories of its own, benefit-plan-service reads the system defaults (`ServiceCategoryMapping:FallBackToSystemDefaults`, on by default), so a lab test or X-ray billed from an office is still costed as lab or imaging |
| Provider screening | See [Provider screening](#provider-screening) |
| Medicare physician fee schedule | [Medicare PFS importer](MEDICARE-PFS-IMPORTER.md), one schedule per locality (the PricingApi uploads do not reach adjudication) |
| Provider contracts and custom fee schedules | [Contract roster importer](CONTRACT-ROSTER-IMPORTER.md) and [fee schedule CSV importer](FEE-SCHEDULE-IMPORTER.md) |
| Benefit plan design | benefit-plan-service API / portal |
| Accumulators to date | The core's earlier claims in the same files ([Accumulators](#accumulators)). Load the whole plan year, or the core's [starting balances](#starting-balances), so a mid-year claim meets the deductible where the core did |
| Prior authorizations | The [authorization import](AUTHORIZATION-IMPORT.md), and the authorization number on each claim ([Prior authorization](#prior-authorization)) |

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

`ClaimsAdjudication:PriorAuth` (shared with live adjudication):

| Setting | Default | Meaning |
|---|---|---|
| `MatchWithoutNumberInShadow` | `true` | A shadow run matches a line with no authorization number to an approved authorization (see [Prior authorization](#prior-authorization)). |
| `MatchWithoutNumber` | `false` | The same for live adjudication, for a plan whose providers are not required to put the number on the claim. |
| `UsePlanFlaggedCodes`, `UseRuleSet` | `false` | Widen the scope beyond the Medicaid inpatient floor. |

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
