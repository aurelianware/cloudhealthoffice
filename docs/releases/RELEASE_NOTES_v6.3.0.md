# v6.3.0 — TBD

**Release date:** TBD
**Previous release:** [v6.2.0](./RELEASE_NOTES_v6.2.0.md) (October 31, 2026)

This release covers the pull requests merged since v6.2.0 (#1696 to #1840).
It is a minor version. The line-of-business consolidation renames a few
values that API clients can see (see [Upgrading from 6.2](#upgrading-from-62)).
Stored records are rewritten at startup, and everything new that changes
behaviour is off until a deployment or tenant turns it on.

6.2 loaded a plan's data from its core every day. 6.3 does two things with
it. First, **shadow in a day**: a plan that keeps QNXT, Facets or another
core drops its CPCDS files and sees, claim by claim, where CHO agrees with
its core and what to load next where it can't decide. Second, the
**Texas and California Medicaid build**: encounters, Medicaid pricing,
third-party liability, the finance close and the state reports, for both
states.

---

## Highlights

### Shadow in a day (#1751, #1817 to #1829, #1834, #1835)

- **Per plan, from the portal.** Shadow adjudication is switched on or off
  per plan without a redeploy (`GET/PUT /api/v1/claims/shadow-adjudication/settings`).
  The new **Shadow Adjudication** page (Operations → Shadow Adjudication,
  `claims:import`) shows the switch, the runs and their results.
- **Whole files in the background.** A run takes an import batch and
  re-adjudicates every claim in it asynchronously. The 200-claim
  synchronous limit no longer applies.
- **A report a payment-integrity team can use.** Each run gives comparable
  and decided counts, and where the core paid more or less than CHO would
  have, in claims and dollars. It lists the top reasons by kind, gap code,
  stage and CARC pair, and exports a discrepancy CSV.
- **What to load next.** Every reason CHO could not decide a claim
  (NOCONTRACT, MEMBERNOTLOADED, PLANUNAVAIL, NETWORK, NOAUTH, ...) comes with
  the file or setting that closes it.
- **Starting from the core's own balances.** Deductible and out-of-pocket
  amounts start from the member's earlier claims in the core and from the
  core's accumulator balances file. The core's network roster loads into
  provider-service. Members from an enrollment feed keep their status
  instead of staying Pending (#1822). A plan with no service categories of
  its own falls back to the system defaults.
- **The trial kit runs it.** `./scripts/cpcds/trial.sh shadow` turns shadow
  on, queues a run for every file `load` loaded, and saves each report. On a
  laptop, `sample-setup.sh` loads what the sample claims need, so the trial
  shows matches as well as gaps.

### Texas and California Medicaid

Each item below is built for both states. Gap numbers refer to the
November 1 Medicaid core-admin assessment.

- **Encounters (gap 1).** A shared state route framework writes 005010
  837P/837I encounters to HHSC via TMHP and to DHCS. It reconciles 999 and
  277CA/277 responses per record, and has a rework queue for held-back or
  rejected encounters. Originals, replacements and voids come from
  `claims.finalized.v1` (#1722, #1725, #1729, #1734).
- **Medicaid pricing (gap 2).**
  - A seam that calls the plan's own licensed APR-DRG and EAPG grouper.
  - APR-DRG payment formulas for Texas and Medi-Cal.
  - FQHC/RHC encounters, nursing facility per diem net of patient
    liability, and Texas outpatient hospital pricing.
  - Texas inpatient stays priced from HHSC's own weight, SDA and interim
    rate files.
  - Texas freestanding psychiatric hospitals paid by per diem, with the
    adult IMD limit.
- **Third-party liability (gap 3).** A TPL store and state file loader.
  Cost avoidance when other coverage is on file. State pay-and-chase rules,
  recovery cases on California's 12-month clock, and plan-found coverage
  reported to the state.
- **Finance close (gap 4).**
  - Contract changes now sync to capitation rate configs; this endpoint
    answered 501 before.
  - State capitation reconciled by member, month and rate cell from rates,
    rosters and 820s, including the state withhold.
  - IBNR from a lag triangle, the 42 CFR 438.8 Medicaid MLR, and 1099-MISC
    box 6 totals.
- **Reports (gap 5).**
  - The portal **Reports** page is no longer empty: claims, UM, and appeals
    and grievances reports, each with CSV.
  - A nightly claims warehouse extract goes to the tenant's drop.
  - Texas HHSC files: member appeals and complaints (UMCM 5.24.6, 5.24.5),
    MLR Report (5.3.13.2), STAR Claims Lag (5.6.2.11), TPL/TPR (5.3.4.1).
    Finance uploads HHSC's current workbook and CHO fills it in.
  - The Medi-Cal grievance log (22 CCR 53858(e)).
- **California rules.** Medi-Cal grievances follow California's 30-day or
  72-hour clocks with no extension. DHCS APL 26-008 (Prop 56 supplemental
  payments) and APL 26-009 (adult dental for state-only members, in claims
  and prior authorization) are applied.

### Pricing inputs and prior-auth rules from the plan's own files

- **Medicare PFS by locality** loads from the CMS RVU zip as published, so
  percent-of-Medicare terms have a base (#1698, #1708).
- **Provider contract rosters.** The core can stay the system of record for
  contracts, or CHO can be (#1699, #1702).
- **Pricing by plan code.** Contracts and plan defaults keep pricing after a
  plan is amended (#1703).
- **State fee schedules as published.** DHCS Medi-Cal Rates and TMHP static
  fee schedules load without reformatting (#1711, #1714).
- **One source for "is prior auth required?".** The plan's PA list, Texas
  TMPPM rules and the Medi-Cal TAR and Non-Benefit List load into the PA
  rule store. Claims can read the same rule set that CRD and PAS answer
  from (#1705, #1709, #1715, #1716).

### Claims operations

- **Retro rate reprocessing.** Fee schedule history and pricing provenance;
  an impact report that dry-runs re-pricing the paid claims a change
  touches; approval-gated adjustment runs; plan rules for minimum amount and
  lookback. The portal page is Operations → Rate Change Reprocessing.
- **Claim entry in the portal.** **Correct claim** on paid and denied
  claims, a working **Submit Claim** form (CMS-1500), and **Reverse claim**,
  which really voids the claim. Professional claims only.
- **Onboarding.** A plan template gallery, a shared fee schedule library a
  tenant deploys at a percentage, and provider names, specialty and network
  status on the provider grid.

### CMS-0057-F

- **Six of six Inferno kits pass nightly.** The DTR payer-server kit (DTR
  2.2.0) joins US Core, CARIN BB, Plan-Net, Bulk Data and PDex (#1756,
  #1760).
- **DTR without a DTR app.** The provider portal's DTR page renders the
  plan's forms for practices whose EHR has no DTR client, adaptive forms
  included, and submits the PAS request that carries the answers (#1755,
  #1813).
- **PA Timeliness Challenge.** 10,000 synthetic requests on the GKE demo:
  2,601 of 2,601 checks matched (#1778, #1780, #1781, #1815).

### Platform

- **One line-of-business type** across 17 services, taken from the plan's
  program at intake. Medicaid rules now reach claims that arrived as
  Commercial before (#1773 to #1798).
- **A restarted claims-service takes no claims until it can reach** the
  services adjudication calls (#1837, issue #1824). Readiness waits up to
  60 seconds.
- **MongoDB runs as a single-node replica set** on GKE and in compose, so
  coverage writes in a transaction work (#1826).
- **The Trivy security scan installs from Trivy's GitHub release**, with a
  pinned SHA-256 (#1838).

---

## Upgrading from 6.2

No configuration change is required. Things to know:

- **Line-of-business values API clients can see.**
  - Payment runs say `Exchange` where they said `Marketplace`.
    `Marketplace` is still accepted on `POST api/paymentruns`.
  - Appeals: a Part D case is `lineOfBusiness: Medicare` with
    `benefitType: PartDDrug`. `MedicarePartD` is refused with a 400, and
    TRICARE, VA and a missing line of business are refused at intake.
  - Eligibility inquiries accept names, not numbers. A coverage with no line
    of business reports `Unknown`, not `Commercial`.
  - Stored payment runs, appeals and inquiries are rewritten once at
    startup.
  - coverage-service, claims-service and authorization-service now call
    tenant-service (`Services:TenantService`, default
    `http://tenant-service`).
- **claims-service readiness.** `/health/ready` now waits until its upstream
  services answer, for at most `Adjudication:Warmup:MaxWait` (60 s). A
  deploy whose rollout timeout is shorter than that should raise it. Set
  `Adjudication:Warmup:Enabled=false` to turn this off.
- **MongoDB replica set.** A self-managed MongoDB must be a replica set (one
  member is enough) for coverage writes. The GKE and compose manifests
  already are.

---

## Where this release has been validated

- **Inferno kits against the seeded `interop-cho` profile**, DaVinci Interop
  run [41980723227](https://github.com/aurelianware/cloudhealthoffice/actions/runs/41980723227)
  (nightly, `main` at 021805c): all six kits **pass**.

  | Kit | Version | Result |
  |---|---|---|
  | US Core 3.1.1 | v1.1.6 | Passed |
  | CARIN BB 2.0.0 | v0.16.3 | Passed (5 known kit issues) |
  | Da Vinci Plan-Net 1.1.0 | v0.13.4 | Passed (known forward-chain kit issues) |
  | Bulk Data 2.0.0 | v0.13.2 | Passed |
  | Da Vinci PDex payer server | v0.13.2 | Passed |
  | Da Vinci DTR payer server (DTR 2.2.0) | v0.18.0 | Passed (3 known kit issues) |

  Each known kit issue is recorded with its reason in `interop/scenarios.json`.
- **PA Timeliness Challenge on the GKE demo:** 2,601 of 2,601 checks matched
  (image 7a0fbed), published at `/evidence/pa-timeliness-challenge`.
- **Shadow in a day on the GKE demo (2026-11-06):** the CPCDS sample files
  loaded with `sample-setup.sh` and `trial.sh load`, then `trial.sh shadow`
  compared 4 claims with no failures. CHO decided 2 of the 4 and matched the
  core on 1 (PRO-1001 matched; PRO-1002 denied on both sides with a different
  denial code). The other 2 (INP-2001, DEN-3001) were left undecided with
  `PLANUNAVAIL`, because the samples carry no benefit plan for their core plan
  codes, and the report says which plan templates to load. The first run
  denied all 4: the demo runs as a Testing host, where network and
  credentialing checks fail closed for providers the sample setup does not
  credential. The results above come from a rerun with soft validation. A fix
  so the trial does not need that rerun is in progress.
- **Million Claim Challenge after a claims-service restart (#1824):** TBD.
- **Azure:** automatic deploys are still paused (`AZURE_DEPLOYMENTS_ENABLED`)
  behind the #1217 deployment checklist.

---

## Off by default

New in 6.3, and doing nothing until a deployment or tenant turns it on:

| Feature | Switch |
|---|---|
| Shadow adjudication per plan | `Adjudication:Shadow:Enabled` for the deployment (on in the GCP render with a CPCDS drop), then the plan's switch in the portal |
| Texas and California encounter routes | the route enabled, receiver identities set, and the tenant's TPID and plan payer ID configured |
| APR-DRG grouping and Texas DRG pricing | `pricing.inpatient.drgClassification = aprDrg`, `pricing.inpatient.medicaidDrgMethod = texas`; a licensed grouper configured |
| Medicaid TPL cost avoidance | `Adjudication:ThirdPartyLiability:Mode` |
| Claims read the PA rule set | `ClaimsAdjudication:PriorAuth:UseRuleSet` |
| Claims warehouse extract | a tenant admin with `extracts:manage`, naming the data-use agreement |
| Imports (Medicare PFS, contracts, fee schedules, PA rules, HHSC files) | dry run unless the request asks for a real run |

Everything in the [v6.2.0 list](./RELEASE_NOTES_v6.2.0.md#off-by-default) is
still off by default too.

---

## Not yet done

- **Texas and California values marked "verify"** (rates, lookbacks, recoup
  minimums and some state layouts) need checking against a plan's contract
  and the state's current companion guides before real use.
- **Medicaid gaps 6 and 7:** print and mail delivery of notices, PCP
  auto-assignment, network adequacy and the 274.
- **The hosted HIPAA environment** is written but not applied. It waits for
  a signed pilot.
- **CMS-0057-F:** system-level `$export` still answers 501. There is no
  X12 278 bridge (CHO relies on HHS enforcement discretion), and
  per-issuer scope limits (G-39) are still open.

---

**Full changelog:** [CHANGELOG.md](../../CHANGELOG.md) ·
[v6.2.0...v6.3.0](https://github.com/aurelianware/cloudhealthoffice/compare/v6.2.0...v6.3.0)
