# Validate a deployment's CMS-0057-F APIs with Inferno

CHO runs the ONC Inferno conformance kits against itself every night, with
synthetic seed data. A plan wants a different answer: do **its** APIs pass,
with **its** members and claims, on **its** deployment? This runs the same
pinned kits against a CHO that is already running somewhere and holds the
plan's data, for example claims loaded from the plan's core with the
[CPCDS importer](CPCDS-FILE-DROP-IMPORTER.md).

```bash
./scripts/interop/fetch-inferno.sh us-core c4bb plan-net   # once: the kits at their pinned tags

export CHO_INTEROP_TARGET_URL=https://api.plan.example/fhir/r4
export CHO_INTEROP_TARGET_PATIENT_IDS=M-100234
export CHO_INTEROP_TARGET_TOKEN="$(cat member-app-token.txt)"
export CHO_INTEROP_TARGET_DIRECTORY_URL=https://api.plan.example/fhir/r4/directory/plan
export CHO_INTEROP_TARGET_LABEL="Example Health Plan"

./scripts/interop/run.sh deployment          # US Core, CARIN BB and Plan-Net
./scripts/interop/run.sh deployment c4bb     # or one kit: us-core | c4bb | plan-net
```

It needs Docker, the .NET 8 SDK and outbound HTTPS from the kit's containers
to the deployment. It does not need node, CHO's source to be built, or any
admin credential at the deployment.

## What runs

| Kit | CMS-0057-F API | Scenario | Needs |
|---|---|---|---|
| US Core 3.1.1 server | Patient Access (clinical) | `INFERNO-USCORE-SERVER-001` | patient ids, token |
| CARIN BB 2.0.0 financial | Patient Access (claims and EOBs) | `INFERNO-C4BB-SERVER-001` | patient ids, token |
| Plan-Net 1.2.0 | Provider Directory | `INFERNO-PLANNET-SERVER-001` | directory URL only |

The groups each kit runs, and the known kit issues not counted against CHO,
are the ones the nightly run uses (`interop/scenarios.json`), so a plan's
result is directly comparable with CHO's published one.

Bulk Data (Provider Access) and PDex (Payer-to-Payer) are **not** run yet.
Those kits authenticate themselves with SMART Backend Services, so the kit's
signing key has to be registered as a client of the deployment first. The
harness refuses them against a deployment rather than attempt that with
credentials it does not have.

## What changes from the nightly run

* **Only the kit starts.** CHO is already running.
* **Nothing is seeded.** Writing synthetic members into a plan's deployment
  would put fake people in its Patient Access API. The kit judges the data the
  plan loaded, so a failure can be a data problem (a member with no coverage,
  a claim with no paid amount) as well as an API one. That is the point.
* **The plan supplies the patient and the token.** Pick a member whose claims
  were loaded, and give their FHIR Patient id.

## Which token

The CARIN BB and US Core kits read as a member's app would. The token should
be the one a member's app gets: a SMART patient-launch token from the
deployment's smart-auth-service for a test member (`patient/*.read`). Such a
token reads only its own patient, so give that one patient id.

A CHO staff token that can read several members also works and lets one run
cover several patient ids, but it judges the API as staff see it, not as a
member's app does. Use the patient-launch token for the result you show
anyone.

The token is read only from the environment. It is not put on a command line,
in a test filter or in the evidence; the evidence describes the target by its
label, URL and the number of patient ids.

## Where the result goes

Evidence is written to `artifacts/deployment-validation/` (or
`CHO_INTEROP_ARTIFACTS`), never merged into CHO's own run document:
`run.json`, `junit.xml`, and the kit containers' logs for a run that did not
pass. The script prints the pass, fail and skip counts and every finding.

Inferno's failure messages can quote the data it read. Treat the evidence
directory as holding the plan's data: keep it where the plan's data is
allowed to be, and do not upload it to a public CI artifact.

## In the hosted pilot

For a tenant Aurelianware hosts, this runs inside the hosted environment
against the tenant's own endpoint, after the tenant's first files load and the
freshness report shows them fresh. A "Validate my APIs" button in the portal
that starts it and shows the result is a follow-up.
