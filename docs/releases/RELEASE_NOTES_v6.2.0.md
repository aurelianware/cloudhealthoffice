# v6.2.0 — Dr. Strangelove

**Release date:** TBD
**Previous release:** [v6.1.1](./RELEASE_NOTES_v6.1.1.md) (October 31, 2026)

This release covers the pull requests merged since v6.1.1 (#1659 to #1691).
It is a minor version. Two APIs that were new or partial in 6.1 changed
shape (see [Upgrading from 6.1](#upgrading-from-61)), and everything new that
changes behaviour is off until a deployment or a tenant turns it on.

6.1 finished the CMS-0057-F APIs. 6.2 is about the plans that keep their
core administration system: their data now gets into CHO through a daily
file drop, and Inferno checks the result. It also proves Payer-to-Payer with
an outside test kit: the Inferno PDex kit now judges CHO's HRex
`$member-match`.

"How I learned to stop worrying and love the core": keep QNXT, Facets or
whatever adjudicates today, and let CHO serve the APIs from its data.

---

## Highlights

### Load a plan's data from its core: the CPCDS daily drop

A plan on another core sends CHO the CARIN CPCDS files it already produces,
and CHO serves Patient Access, Provider Access and Payer-to-Payer from them.

- **Members, coverage, claims and providers** load from CPCDS files
  (#1664 to #1667). Claims are idempotent by claim chain and content hash,
  link to the CHO Coverage the member held on the date of service, and the
  NPIs they name become resolvable reference providers, kept out of the
  public directory.
- **Prior authorizations** load from X12 278 responses or a CSV a UM platform
  exports (#1683, #1685). They are read-only in CHO and served by every API
  that carries PA data, including the Prior Authorization API's status.
- **The daily drop** (#1674 to #1676, #1679, #1680): claims-service watches
  each tenant's `inbound` folder or Cloud Storage prefix, loads files in
  dependency order, and reports whether the tenant's data is current and adds
  up. A tenant turns its drop on from saved settings, without a redeploy.
- **Hosted on GCP** (#1681, #1682): a private drop bucket, SFTPGo on GKE with
  SSH-key-only logins scoped to each plan's prefix, and logins created from
  the keys a plan registers. Every dropped file is archived as received in
  document-service, with ten-year retention (#1686).
- **The portal's Imports & EDI console** (#1688) shows each drop run, its
  files and errors, the freshness report, and the drop and SFTP setup.
- **`docs/integration/LOADING-A-PLANS-DATA.md`** (#1684) walks through
  loading a plan in order, through to a go-live checklist.

### Shadow adjudication (#1668)

CHO can re-adjudicate claims the core already adjudicated, without touching
member-facing data, and record a line-level comparison with the core's
result. It is a payment-integrity report and a migration rehearsal. Expect
most claims to come back "CHO undecided" at first: CHO does not yet have a
plan's provider contracts, fee schedules or accumulators, and the comparison
names the missing input. Off unless `Adjudication:Shadow:Enabled`; a tenant
can run it after each drop (`ShadowAfterImport`).

### Benefit plans from public data (#1671 to #1673, #1678)

- **QHP importer:** reads the CMS Exchange QHP public use files. It is a dry
  run by default, and a real run creates Draft plans only. It has been run
  on synthetic samples, not yet on the PY2027 files.
- **State Medicaid and CHIP templates:** 25 templates for Texas, California,
  Florida, Arizona and New York. Each value is marked `sourced`, with its
  citation, or `verify`. Most covered-service values are still `verify`, so a
  template is a starting point to check against the plan's state contract,
  not a certified benefit design.

### Payer-to-Payer, proven by the Inferno PDex kit

- **`Patient/$member-match` takes the HRex form** (#1659): a FHIR
  `Parameters` request, answered with the HRex `MemberIdentifier`. Before
  this, only CHO's own JSON shape was understood, so another payer's
  conforming request was refused. A consent that names the member by a local
  reference to `MemberPatient`, as HRex does, is now resolved.
- **The requesting payer can find the matched Patient** by the returned
  identifier (`Patient?identifier=`, PDex requirement 35, #1661), only for a
  member whose consent naming that payer is in force.
- **CHO's outbound consent** (CHO as the new payer) now follows
  `hrex-consent`: the source role, the member as performer, and a source
  reference (#1670).
- **The Inferno PDex payer-server kit** runs nightly (#1660, #1662, #1663,
  #1669, #1687). It authenticates itself with SMART Backend Services as a
  registered requesting payer and judges the one-match and no-match cases.

### DTR `$questionnaire-package` (DTR 2.2.0) and a CQL-prepopulated form (#1690)

`Questionnaire/$questionnaire-package` now follows DTR 2.2.0: it takes the
member's `coverage` and answers with the DTR package Parameters (the
Questionnaire, a draft QuestionnaireResponse, CQL Libraries with ELM and
expanded ValueSets). The MRI prior authorization form is the first with CQL
prepopulation.

### CapabilityStatement

`$questionnaire-package` and the profiles CHO serves are declared (#1690,
#1691): US Core and CARIN BB on Patient, Coverage, Practitioner and
Organization, and the CARIN BB claim-type profiles and PDex prior
authorization on ExplanationOfBenefit.

### Testing and site

- `./scripts/interop/run.sh deployment` runs the US Core, CARIN BB and
  Plan-Net kits against a CHO that is already deployed, on that plan's own
  data (#1677).
- Website insight: "Medicaid CMS-0057-F blueprint, built and tested" (#1689).

---

## Upgrading from 6.1

No configuration change is required. Things to know:

- **`$questionnaire-package` changed shape (DTR 2.2.0).** It now requires a
  `coverage` parameter and answers with Parameters (`packagebundle`,
  `outcome`) instead of a bare Bundle. A questionnaire that is not found is a
  warning in a 200, not a 404. A caller that sends only `questionnaire` gets
  a 400 naming the missing coverage.
- **`Patient/$member-match` still accepts CHO's JSON form** and answers it
  with a Bundle, as before. The HRex `Parameters` form is answered with
  `Parameters`.
- **The CPCDS drop API sends states as names** (`"Failed"`, `"Stale"`), not
  numbers. It is new since 6.1.1, so only early adopters of the drop are
  affected.

---

## Where this release has been validated

- **Inferno kits against the seeded `interop-cho` profile:** US Core 3.1.1,
  CARIN BB 2.0.0, Da Vinci Plan-Net and Bulk Data 2.0.0 all **pass**, apart
  from the known kit issues listed in the
  [v6.1.0 notes](./RELEASE_NOTES_v6.1.0.md#where-this-release-has-been-validated)
  (DaVinci Interop run
  [41230716146](https://github.com/aurelianware/cloudhealthoffice/actions/runs/41230716146),
  `main` at f750c75).
- **Inferno PDex payer-server kit** (`davinci-pdex-test-kit` v0.13.2):
  TBD. In run 41230716146, CHO's `$member-match` answers passed (one match,
  and 422 for no match). The only failure was a minimal-data check on the
  harness's own request, fixed in #1687. To be filled in from the run on
  this release's commit.
- **Google Cloud (GKE):** the demo is deployed on every merge to `main`. The
  hosted CPCDS drop's Terraform (`enable_cpcds_drops`) has not been applied
  to it.
- **Azure:** automatic deploys are still paused (`AZURE_DEPLOYMENTS_ENABLED`)
  behind the #1217 deployment checklist.

---

## Off by default

New in 6.2, and doing nothing until a deployment or tenant turns them on:

| Feature | Switch |
|---|---|
| CPCDS daily drop | `CpcdsDrop:Enabled`, then each tenant's `CpcdsDrop:Tenants` entry or its saved drop settings |
| Drop from Cloud Storage | `CpcdsDrop:Store = Gcs`, `CpcdsDrop:Gcs:Bucket` |
| Hosted drop on GCP (bucket, SFTPGo) | `enable_cpcds_drops` in `infrastructure/gcp`; `render_manifests.py --cpcds-drop-bucket` |
| Hosted SFTP logins | `CpcdsSftp:Enabled` (tenant-service) |
| Archive of dropped files | `CpcdsDrop:Archive:Enabled` (on in the GCP render) |
| Shadow adjudication | `Adjudication:Shadow:Enabled` and `Adjudication:Shadow:Tenants`; per tenant after a drop, `ShadowAfterImport` |
| QHP import | Dry run unless the request asks for a real run, which creates Drafts only |

The CPCDS, authorization and template imports are endpoints a caller with the
import permission must call; nothing runs on its own. Everything in the
[v6.1.0 list](./RELEASE_NOTES_v6.1.0.md#off-by-default) is still off by
default too.

---

## Not yet done for CMS-0057-F

The API requirements take effect on January 1, 2027. Still open after 6.2:

- **Hosted Inferno for the export groups** against an HTTPS deployment (G-60).
- **System-level `$export`** still answers 501.
- **No X12 278 bridge** for FHIR prior authorization; CHO relies on HHS
  enforcement discretion. Importing a UM system's 278 decisions (above) is
  not that bridge.
- **Per-issuer scope limits** (G-39).
- **The CRD-to-DTR launch** is the next DTR step.

A plan must still supply the `PA-DECISION-EXTENSION` letter template, its
`PublicProviderDirectory` entry and its counsel's reading of "more than
once" for the Patient Access usage figures. `docs/epa-gap-matrix.md` gives
the status of every requirement.

---

**Full changelog:** [CHANGELOG.md](../../CHANGELOG.md) ·
[v6.1.1...v6.2.0](https://github.com/aurelianware/cloudhealthoffice/compare/v6.1.1...v6.2.0)
