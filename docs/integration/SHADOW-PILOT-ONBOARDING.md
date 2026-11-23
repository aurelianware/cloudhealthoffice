# Shadow pilot onboarding kit

What a health plan needs to know, send and sign to run CHO in shadow beside its
core (QNXT, Facets, HealthRules or a plan-built core) for 60 to 90 days, and
what it gets back. The mechanics are in
[SHADOW-ADJUDICATION.md](SHADOW-ADJUDICATION.md); this page is the plan-facing
checklist and timeline.

The output is a **reviewed worklist for the plan's claims operations and
payment-integrity teams**, not adjustments. CHO never writes to the core, never
contacts a provider or member, and never sends an encounter. The plan decides
which findings are real and runs approved adjustments through its own
mass-adjustment process.

## At a glance

| | |
|---|---|
| Length | 60 to 90 days on the plan's daily files |
| What the plan sends | Claims the core already adjudicated, members, authorizations, provider roster, fee schedules and contracts, benefit designs. Formats below |
| How it arrives | A key-only SFTP login into the plan's own folder, or files the plan's team drops in its own CHO deployment |
| Before any PHI moves | NDA, BAA (with the state Medicaid flow-down), the plan's security review, the hosted environment stood up for the plan |
| First report | Coverage report about 10 business days after a complete file set; first findings report by about day 30 (targets, see [Timeline](#timeline)) |
| What the plan gets | Payment-integrity findings in dollars (over and under), authorization findings both ways with the policy behind each, a measured answer on how much of the plan's volume CHO can carry, and what it would still need |
| What it costs the core | Nothing. Read-only extracts the core already produces; no connector, no write-back, no license |

## What the plan sends

Send what the core and UM platform already export. Every importer matches
headers loosely and takes a `columnMap` for columns named differently, so most
extracts load as they are. Run each one as a dry run first; the report names
every row it would reject and why.

| File | Needed for | Format options | How much | Guide |
|---|---|---|---|---|
| **Adjudicated claims** (professional and institutional, final status, with line amounts and line denial codes) | Everything: these are the claims CHO re-adjudicates | CPCDS CSV (claim, line, payment). Optional `Prior authorization number` / `Line prior authorization number` columns | The plan year to date, then daily. A rolling or full extract is fine, reloads are idempotent | [CPCDS importer](CPCDS-FILE-DROP-IMPORTER.md) |
| **Members and coverage** | Eligibility, plan and group on each claim | X12 834, or CPCDS member and coverage files | Everyone with a claim in the period, then daily changes | [Loading a plan's data](LOADING-A-PLANS-DATA.md#4-members-and-coverage) |
| **Authorizations** (approved, modified, denied, with service lines) | Authorization findings | UM platform CSV, or X12 278. Optional `review_criteria` column: the criteria or medical policy and version each decision applied | Every authorization whose period overlaps the claims, then daily | [Authorization import](AUTHORIZATION-IMPORT.md) |
| **Provider roster** (network participation and credentialing) | Network and credentialing checks | Roster CSV with a credentialed column | The whole network, then changes | [Network roster importer](NETWORK-ROSTER-IMPORTER.md) |
| **Fee schedules and provider contracts** | Pricing | Public schedules deployed from CHO's shared library (TMHP static schedules, Medicare PFS by locality) at the plan's percentage, plus the plan's contract roster and custom fee schedule CSVs | Every schedule a claim in the period priced against | [Contract roster](CONTRACT-ROSTER-IMPORTER.md), [fee schedule CSV](FEE-SCHEDULE-IMPORTER.md), [library](FEE-SCHEDULE-LIBRARY.md) |
| **Benefit designs** | Member cost share, limits | State program templates (Texas STAR, STAR+PLUS, STAR Kids, STAR Health, CHIP and others), adjusted from the plan's evidence of coverage; a crosswalk of the core's plan codes | One per product in scope | [State program templates](STATE-PROGRAM-BENEFIT-TEMPLATES.md), [plan-code mappings](LOADING-A-PLANS-DATA.md#2-plan-code-mappings) |
| PA list and medical policy (optional) | Which services need an authorization, and the policy version behind each finding | The plan's PA-required list as CSV, named with the policy it was digitized from and its version | The current list; a new import per policy version | [PA rules importer](PRIOR-AUTH-RULES-IMPORTER.md#policy-versions) |
| Accumulator starting balances (optional) | Deductible and out-of-pocket met before the first claim file | CSV, one row per member and as-of date | Only when the claim files do not reach back to the start of the plan year | [Starting balances](SHADOW-ADJUDICATION.md#starting-balances) |

Not needed: pharmacy claims (CHO does not compare pharmacy adjudication; they
can come for the CMS-0057-F APIs), remittances, capitation, or any live access
to the core.

### Things that make the findings better

- **The authorization number on the claim** when the core has it. Not required:
  QNXT and Facets attach authorizations by member, date and service, and a
  shadow run matches the same way when the number is missing, recording which
  authorization it used and how many covered the service.
- **The PA list with its medical policy version**, so each authorization finding
  names the version of the policy CHO applied. Without it, CHO's scope is the
  Medicaid inpatient floor plus the state's published list, and the finding
  says "No policy recorded".
- **Review criteria on authorizations** (InterQual, MCG, or the plan's own
  policy and version), so a reviewer sees what the UM decision rested on.
- **The core's control totals** for each claim file (claim count, billed, paid),
  so the import reconciles against them.

### Minimum necessary

Only what the table lists, only for the lines of business in the pilot. No
clinical notes, attachments or medical records. Member names and dates of birth
are not required for adjudication; send them only if the plan also wants the
CMS-0057-F APIs served from the same data.

## Before any PHI moves

| Step | Who | Notes |
|---|---|---|
| Mutual NDA | Plan and Aurelianware | Before any architecture or security material is shared |
| Security review | Plan's security team | Draft answers are in [PAYER-SECURITY-QUESTIONNAIRE.md](../compliance/security-readiness/PAYER-SECURITY-QUESTIONNAIRE.md); the posture is in [HIPAA-POSTURE.md](../compliance/security-readiness/HIPAA-POSTURE.md) |
| BAA | Plan (covered entity) and Aurelianware (business associate) | Template and approach in [BAA-APPROACH.md](../compliance/security-readiness/BAA-APPROACH.md). The plan's own paper is fine |
| State Medicaid flow-down | Plan's Medicaid contract terms passed to Aurelianware | For Texas Medicaid, the HHS Data Use Agreement subcontractor terms, including the breach notice clocks (see [BAA-APPROACH.md](../compliance/security-readiness/BAA-APPROACH.md) and the [incident procedure](../compliance/hipaa-hosted/INCIDENT-RESPONSE.md)) |
| Hosted environment | Aurelianware | The plan's tenant on the HIPAA-hosted GCP profile, under Google's BAA, with its own SFTP login and storage. See [HOSTED-ENVIRONMENT-APPLY.md](../compliance/security-readiness/HOSTED-ENVIRONMENT-APPLY.md) and [CPCDS-SFTP-DROP-GCP.md](CPCDS-SFTP-DROP-GCP.md) |
| Synthetic dry run | Both | The plan sends a synthetic or de-identified sample (or uses the [sample files](samples/)) to prove formats end to end before real data |

The full checklist Aurelianware works through is
[BAA-READY-CHECKLIST.md](../compliance/security-readiness/BAA-READY-CHECKLIST.md).

**Plan-hosted alternative.** A plan that would rather keep the data in its own
cloud can run CHO there (containers on Kubernetes). Then no PHI leaves the plan,
the BAA covers support access only, and the plan's team drops the files into its
own deployment. Setup takes longer than the hosted tenant.

**At the end.** The plan chooses: keep the tenant for a production decision, or
have every file, archive and result deleted, with written confirmation, as the
BAA requires.

## Timeline

Targets, counted from the day the BAA is signed and the hosted tenant is ready.
Each depends on a complete, readable file set; the first coverage report says
exactly what is missing.

| When | What happens | What the plan sees |
|---|---|---|
| Week 0 | SFTP login issued, column maps agreed on the synthetic sample | Dry-run reports for each file |
| Week 1 | Full file set loaded: plan year to date. Benefit templates and fee schedules set up, plan codes mapped | Import reports reconciled to the core's control totals |
| Week 2 (about 10 business days) | First full shadow run | **Coverage report**: how much of the volume CHO decided, how often it matched the core, and the ranked list of what to load next ("What to load next") |
| Weeks 3 to 4 | Gaps closed (contracts, plan codes, roster), daily drop turned on, every new batch shadow-run automatically | Match rate rising run over run |
| Day 30 | **First findings report** | Payment-integrity and authorization worklists, below |
| Days 30 to 90 | Daily runs; findings reviewed with the plan's claims ops every two weeks; their dispositions fed back | Updated worklists; confirmed versus dismissed findings |
| Day 60 to 90 | Readout | Dollars found (confirmed by the plan's review), match rate by line of business and claim type, and a go or no-go on carrying the core, with what it would still take |

## What the report contains

Each run produces a report, a discrepancy CSV and per-claim results (portal:
**Shadow Adjudication**; API and fields: [SHADOW-ADJUDICATION.md](SHADOW-ADJUDICATION.md#run-report)).

- **Coverage.** Claims compared, decided, matched; the match rate; claims CHO
  could not decide and why, with the core-paid dollars it cannot check yet.
- **Payment integrity, in dollars.** Possible overpayments (the core paid more
  than the plan's own contracts, fee schedules and benefits support) and
  possible underpayments, kept apart, by CHO stage, denial code, procedure and
  billing provider, with example claims.
- **Denial verification.** Every claim or line the core denied, checked by
  CHO: would pay (wrongful-denial risk, with what CHO would pay), denies for
  another reason, or confirms, grouped by the core's denial code with a confirm
  rate per code.
- **Authorization findings, both ways.** Claims the core denied for a missing
  authorization when an approved one covers the service, and claims the core paid
  without a valid one where the plan's rules require it. Each names the
  authorization CHO checked (and whether it came from the claim or was matched by
  member, date and service), why the service needed one, the PA rule and the
  **medical policy version** behind it, the rule set version, and the review
  criteria the authorization applied. A breakdown by policy shows which policy
  to open first.
- **The worklist (CSV).** One row per differing line: core and CHO amounts and
  denial codes, the difference, CHO's findings, and the authorization evidence.
  Formula-safe for spreadsheets. This is what claims ops reviews.
- **What to load next.** Each undecided reason with the file or setting that
  clears it.

Every finding is a lead for the plan's review, not a determination.

**Adjustment files.** CHO does not produce a file for a core's adjustment
import by default, because plans customize those imports. When a plan asks, the
layout for its core (a QNXT or Facets mass-adjustment batch, say) is scoped and
built for that plan from the reviewed worklist. The plan still runs it.

## Checklist

- [ ] NDA signed.
- [ ] Security review complete; BAA and state flow-down signed.
- [ ] Hosted tenant ready, SFTP key exchanged (or plan-hosted deployment up).
- [ ] Synthetic sample loaded end to end; column maps saved.
- [ ] Claims, members, authorizations, roster, fee schedules and contracts, benefit designs loaded for the plan year to date; control totals reconcile.
- [ ] Optional: PA list with policy name and version; review criteria on authorizations; starting balances.
- [ ] First shadow run; coverage report shared.
- [ ] Daily drop on, shadow after each import.
- [ ] First findings report at day 30; review cadence set with claims ops.
- [ ] Readout and the plan's keep-or-delete decision.

## Related

- [SHADOW-ADJUDICATION.md](SHADOW-ADJUDICATION.md): how a shadow run works.
- [LOADING-A-PLANS-DATA.md](LOADING-A-PLANS-DATA.md): every load, in order.
- [CPCDS-TRIAL-KIT.md](CPCDS-TRIAL-KIT.md): the same loads as one script, for a laptop trial on synthetic data.
- [CORE-INTEGRATION-OPTIONS.md](CORE-INTEGRATION-OPTIONS.md): why the file drop is the default.
