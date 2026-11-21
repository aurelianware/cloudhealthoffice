# Connecting a plan's core to CHO: file drops first, live connectors optional

**Decided:** November 21, 2026 (Mark Phillips). Amends
[ADR 027](../adr/027-core-adapter-engagement-boundary.md).

A plan that keeps its core admin system (QNXT, Facets, HealthEdge HealthRules
Payer or a plan-built core) connects it to CHO in one of two ways. The
**daily file drop is the default** and the only path CHO leads with. **Live
core connectors are an optional, alternate integration point** for a plan that
would rather build them. They are off in every deployment unless it turns them
on.

| | File drop (default) | Live core connectors (optional) |
| --- | --- | --- |
| What it is | The core writes its usual extracts (CPCDS members, coverage and claims; UM authorizations as CSV or X12 278) to a per-plan folder or SFTP login. CHO loads them on a schedule into its own stores | A class behind a CHO domain interface (`IClaimAdapter`, `IProviderAdapter`, `IAuthorizationBackend`, ...) that answers each CHO request by calling the core |
| What it serves | Every CMS-0057-F API: Patient Access, Provider Access, Payer-to-Payer, Provider Directory and Prior Authorization data. Also shadow adjudication | CHO's internal REST APIs and the portal (claim and provider lookup, benefit plan views, eligibility, ID cards, real-time authorization create and status). **No CMS-0057-F API reads through a live connector** |
| What the plan needs | A scheduled extract job and SFTP keys. No QXI or Open Access license, no read replica, no vendor connector code | A licensed core API (for example QNXT QXI), credentials, a mapping contract and per-environment build and acceptance work |
| Freshness | The extract cadence, usually daily. The freshness report says when data is stale | Real time |
| Cost to stand up | Days to weeks: map columns, map plan codes, dry-run, load | A scoped engagement per plan and core version (QRE, workshop, SOW, build, acceptance) |
| Status | Ships: [CPCDS file drop](CPCDS-FILE-DROP-IMPORTER.md), [authorization import](AUTHORIZATION-IMPORT.md), [hosted SFTP](CPCDS-SFTP-DROP-GCP.md), [network roster import](NETWORK-ROSTER-IMPORTER.md) (API only, not yet in the drop) | Templates only. Every vendor method throws `NotImplementedException` until an engagement builds it |

## Why the file drop is the default

- **It is the path to the compliance date.** The CMS-0057-F APIs read CHO's
  own repositories, so a plan on QNXT needs its data loaded into CHO whatever
  else it builds. A live claims connector would not put one EOB on the Patient
  Access API.
- **Every core can do it.** Plans already produce flat extracts for their
  data warehouse, encounter submission and vendors such as Onyx. CPCDS is the
  dictionary the CARIN Blue Button IG was built from.
- **It is cheaper to sell and to support.** No vendor license, no access to
  the core's production database, no per-version connector to keep working
  through the core's upgrades.
- **Shadow adjudication runs on it.** CHO compares its own decision with the
  core's on the same imported claims, so a plan can see CHO adjudicate beside
  its core with no connector at all.

## When a plan might still want live connectors

- Staff or a portal must see the core's data as of this minute, not as of the
  last extract.
- CHO must write into the core in real time, for example authorizations
  created through the PAS API that the UM team works in the core.
- The plan already has a supported core API and would rather run an
  integration than an extract job.

These are scoped and priced as their own engagement under ADR 027, after the
file drop is live. Start from the [core adapter QRE](../features/questionnaires/qre-core-admin-adapter-questions.md)
and the [implementer guide](CORE-ADAPTER-IMPLEMENTER-GUIDE.md).

## Turning live connectors on

Live connectors are registered only when the deployment sets:

```json
"CoreIntegration": { "LiveConnectors": { "Enabled": true } }
```

(environment variable `CoreIntegration__LiveConnectors__Enabled=true`) on each
service that has them: claims-service, benefit-plan-service, provider-service,
idcard-service and authorization-service. The switch is read at startup.

| Service | Registered only with the switch on |
| --- | --- |
| claims-service | `QnxtClaimAdapter`, `FacetsClaimAdapter`, `HealthEdgeClaimAdapter` |
| benefit-plan-service | `QnxtBenefitPlanAdapter`, `FacetsBenefitPlanAdapter`, `HealthEdgeBenefitPlanAdapter` |
| provider-service | `QnxtProviderAdapter`, `FacetsProviderAdapter`, `HealthEdgeProviderAdapter`, `QnxtOrganizationAdapter`, `FacetsOrganizationAdapter` |
| idcard-service | `QnxtIdCardAdapter` (issue through CHO, mirror to QNXT) and the `QnxtMirrorReconciliationJob` |
| authorization-service | `QnxtAuthorizationBackend` (Augment mode) |
| benefit engine | `UseQnxtBenefitPlanProvider` / `UseQnxtAccumulatorService` were already opt-in builder calls; unchanged |

**With the switch off** (the default), a tenant whose configuration names a
vendor platform (`qnxt`, `facets` or `healthedge`) for a domain is refused
with `LiveCoreConnectorNotEnabledException`, which the shared middleware
answers as 501. It is never served CHO's own data instead, which closes known
gap 2 in the implementer guide for vendor keys. An unrecognized platform key
still uses `cho`, as before. In authorization-service, Augment mode naming
`qnxt` fails at resolution with a message that names the switch and the
authorization import.

No code was removed. The templates, the conformance suites and their tests
stay as they are, so an engagement can pick them up.

## What the file drop still needs for CMS-0057-F

The drop already loads members, coverage, claims, the providers claims name,
and authorizations. These are the gaps between it and serving all four
CMS-0057-F APIs from a plan's files, in the order we plan to close them:

1. **Score the acceptance suite against the file drop.** The public evidence
   reports a QNXT live-connector column (11 GAP, 10 N/A). Replace it with a
   "core via file drop" column that loads the sample CPCDS and authorization
   files and runs each scenario against what they loaded. This is the
   evidence a plan on another core asks for.
2. **Pharmacy claims.** The CPCDS importer refuses `pharmacy` claims today.
   Patient Access must include the plan's drug claims (only the prior
   authorization data excludes drugs). Add the CARIN BB pharmacy EOB
   projection and accept pharmacy rows.
3. **Network roster through the drop.** The [network roster importer](NETWORK-ROSTER-IMPORTER.md)
   works as an API call. Add a `roster` file kind so the Provider Directory
   and in-network decisions stay current from the same daily drop.
4. **EOB coverage reference.** An imported claim names the core's coverage id,
   so an EOB's `insurance.coverage` does not yet resolve to the Coverage the
   member import created. Resolve it through the member's coverage on the
   date of service.
5. **Clinical data.** Patient Access and Payer-to-Payer serve USCDI clinical
   resources, but today they are filled only by Payer-to-Payer imports. Add
   a clinical drop (FHIR NDJSON, the bulk export format most HIEs and lab
   vendors already produce) loaded through the existing clinical payload
   validator.
6. **Provider Access attribution from the member file.** Attribution comes
   from claims history and a configured panel. Read the PCP assignment from
   the member file (Medicaid extracts usually carry it; the CPCDS member
   import does not read it yet), so a PCP who has not seen the member yet is
   attributed too.

An X12 837/835 claim source through the same pipeline is also planned (known
gap 1 in the implementer guide).
