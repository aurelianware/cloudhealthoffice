# CHO implementation guide: loading a plan's data

This guide is for the implementer who brings a health plan's data into CHO so
its CMS-0057-F APIs serve the plan's real members, claims, authorizations and
network. It assumes the plan keeps its core admin system (QNXT, Facets,
HealthRules, or a plan-built core) and its UM platform, and loads CHO from
them. That is the [Implementation Blueprint](../implementation/IMPLEMENTATION-BLUEPRINT.md)'s
Layer 1.

The order of the loads matters. Each load refers to what earlier loads put in
place: coverage names a benefit plan, a claim names a member and providers, and
an authorization names a member. Load in this order, run each load as a dry run
first, and close its gaps before moving on.

| Step | What | From | How | Status |
|---|---|---|---|---|
| 1 | Benefit plans | SBCs and contracts, CMS QHP PUFs, state program templates | Templates, QHP importer or the plans API, then publish | Ships |
| 2 | Plan-code mappings | The core's plan codes | `plan-code-mappings` API | Ships |
| 3 | Providers and network | The core's provider and network tables, or credentialing | Providers and networks APIs; roster file import planned | Partly ships |
| 4 | Members and coverage | CPCDS member and coverage files, or 834 | Enrollment import, or the daily drop | Ships |
| 5 | Claims | CPCDS claim files | Claims import, or the daily drop | Ships |
| 6 | Authorizations | The UM platform's export | Authorization import | In review ([#1683](https://github.com/aurelianware/cloudhealthoffice/pull/1683)) |
| 7 | Validate | | Inferno against the deployment | Ships |

Every import shares these behaviors:

- **The tenant comes from the caller's token.** It never comes from the file.
- **`dryRun=true` writes nothing.** It returns the full report: what would be
  created, updated, left unchanged or rejected, and why.
- **Reloads are safe.** Every import is idempotent, so the core's daily full or
  rolling extract can be sent as it is.
- **A file with some bad rows still loads the rest.** The report names each
  rejected row and its file line. Only a file that can't be read at all is
  refused as a whole (422).
- **Headers are matched loosely.** Case, spaces and punctuation are ignored.
  When a file names a column differently, send a `columnMap`
  (`{"documented header": "the file's header"}`).
- **The core's identifiers are kept.** Member ids, claim ids and authorization
  numbers stay as the core assigned them, so a member, a provider or a support
  call sees the same numbers everywhere.

## 1. Benefit plans

Coverage can only point at a benefit plan CHO has, so plans come first. Start
from whichever source is closest to the plan's products:

| Source | Use for | Guide |
|---|---|---|
| State program templates | Medicaid and CHIP (TX, CA, FL, AZ, NY today) | [STATE-PROGRAM-BENEFIT-TEMPLATES.md](STATE-PROGRAM-BENEFIT-TEMPLATES.md) |
| CMS QHP PUF importer | ACA marketplace plans on the federal platform | [QHP-PUF-BENEFIT-PLAN-IMPORTER.md](QHP-PUF-BENEFIT-PLAN-IMPORTER.md) |
| Plans API | Everything else (commercial group, Medicare Advantage) | `POST /api/v1/plans/drafts`, then add benefits |

Templates and the PUF importer create **Draft** plans. The plan's benefits
team checks every value marked `verify` against its contract or Evidence of
Coverage, then publishes each plan
(`POST /api/v1/plans/{planId}/versions/{versionId}/publish`). Nothing a
member sees is drawn from a draft.

## 2. Plan-code mappings

The core names plans by its own codes: the 834 HD04 value, or the CPCDS
`Plan identifier`. Map each code to a CHO plan with
`POST /api/v1/plan-code-mappings` (or `/bulk`). Coverage loads only for mapped
codes.

The member import's dry run (step 4) returns a **plan-code gap report**
listing every code in the files that is not mapped yet. Run it, map the gaps,
and run it again until it's empty.

## 3. Providers and network

There are two different needs here.

- **Providers named on claims.** These are needed for EOBs to resolve. Each
  claim load creates the NPIs it names from NPPES as **reference-only**
  providers, which are readable through FHIR but never published in the public
  directory. Nothing needs to be prepared. See
  [CPCDS-FILE-DROP-IMPORTER.md](CPCDS-FILE-DROP-IMPORTER.md#providers-named-on-claims).
- **The plan's network.** This is needed for the Provider Directory API
  (Plan-Net): contracted providers, their locations and specialties, network
  participation, and whether they're accepting new patients. Today it is loaded
  through the provider and network APIs (`POST /api/v1/providers/drafts`,
  `POST /api/v1/networks`). A **roster file import** that loads it from the
  core's or credentialing system's export, through the same drop, is planned.
  A provider first created as reference-only becomes a directory provider when
  the roster includes it (`referenceOnly: false`).

The public directory is configured per plan (`PublicProviderDirectory`; see
[davinci.md](../interop/davinci.md)) and is open without a token, as
CMS-0057-F requires.

## 4. Members and coverage

| Source | Endpoint (enrollment-import-service) |
|---|---|
| CPCDS member and coverage files | `POST /api/v1/enrollment/import/cpcds` (`members`, `coverages`, `dryRun`) |
| X12 834 | `POST /api/v1/enrollment/import/raw834`, or `POST /api/v1/enrollment/import` with a parsed 834 |

Both run through the same 834 import, so members, sponsors and coverage are
created and audited the same way. Every member, including dependants, is filed
under the core's member id. That is the id claims and authorizations name, and
the id a member's SMART token binds to. Details are in
[CPCDS-FILE-DROP-IMPORTER.md](CPCDS-FILE-DROP-IMPORTER.md#members-and-coverage).

## 5. Claims

`POST /api/v1/claims/import/cpcds` (claims-service, `claims:import`) loads
claims the core adjudicated. CHO stores the core's answer and does not
re-adjudicate. Claims are served as CARIN Blue Button ExplanationOfBenefits
through Patient Access and Provider Access.

- A reload with the same content changes nothing.
- A changed claim, such as a late adjustment, becomes a new version on the
  same EOB id.
- Load members and coverage first, so each claim links to its CHO coverage.

Optionally, CHO can re-adjudicate imported claims in shadow and compare them
with the core's results. See [SHADOW-ADJUDICATION.md](SHADOW-ADJUDICATION.md).

X12 837 and 835 as a second claims source are planned.

## 6. Authorizations

`POST /api/v1/authorizations/import/csv` (authorization-service,
`authorizations:import`) loads the authorizations the plan's UM platform
decided, so PDex and the Prior Authorization API serve them. Imported
authorizations are read-only in CHO. See
[AUTHORIZATION-IMPORT.md](AUTHORIZATION-IMPORT.md).

- Plans that decide authorizations in CHO don't need this step. That is the
  default, `Cms0057:Authorization:OperatingMode = Replace`.
- X12 278 files, a real-time push endpoint for each decision, and CORE
  real-time 278 inbound are planned.

## The daily drop

Once each load has worked by hand, the daily drop takes over. The core writes
its extracts to the tenant's `inbound` folder, and claims-service picks them
up on a schedule. It loads members and coverage first, then the providers each
claim file names, then the claims. Each run is recorded with its counts and
errors, and the files move to `processed` or `failed`.

- **How it works:** folder layout, file naming, settings and the freshness
  report are in [CPCDS-FILE-DROP-IMPORTER.md](CPCDS-FILE-DROP-IMPORTER.md#the-daily-file-drop).
- **Turning it on:** a tenant turns its own drop on with
  `PUT /api/v1/claims/import/cpcds/drops/settings`, with no redeploy.
- **When Aurelianware hosts CHO:** each plan gets a key-only SFTP login into
  its folder. See [CPCDS-SFTP-DROP-GCP.md](CPCDS-SFTP-DROP-GCP.md).
- **Staying current:** `GET /api/v1/claims/import/cpcds/drops/report` reports
  whether the data is fresh (Fresh, Stale, Failing or NeverLoaded) and
  reconciles the counts and amounts loaded against the files.

Authorization and roster files will join the drop when their imports do.

## 7. Validate

Run the same pinned Inferno kits CHO runs nightly, but against the
deployment, with the plan's own patients and a token the deployment issued:
US Core, CARIN Blue Button and Plan-Net. See
[VALIDATE-A-DEPLOYMENT.md](VALIDATE-A-DEPLOYMENT.md). The evidence it writes
can hold plan data, so keep it with the plan.

## Checklist

- [ ] Benefit plans published for every product in scope; no `verify` values left.
- [ ] Plan-code gap report empty.
- [ ] Network loaded; the public directory returns the plan's providers.
- [ ] Member and coverage dry run clean, then loaded.
- [ ] Claims loaded; the import report's counts and amounts reconcile with the core's own control totals.
- [ ] Authorizations loaded (UM platform plans), with at least the reporting year for CMS-0057-F metrics.
- [ ] Daily drop on, and the freshness report Fresh for a week.
- [ ] Inferno US Core, CARIN BB and Plan-Net pass against the deployment with the plan's own members.

## Related

- [Implementation Blueprint](../implementation/IMPLEMENTATION-BLUEPRINT.md): phases, teams and decision gates.
- [Core adapter implementer guide](CORE-ADAPTER-IMPLEMENTER-GUIDE.md): live adapters, the alternative to loading data into CHO.
- [Core data contract](CORE-DATA-CONTRACT.md): what an SI agrees and delivers per domain.
