# CPCDS trial kit: validate a plan's CMS-0057-F APIs on its own data

This kit is for a health plan that runs its claims on another core (QNXT,
Facets, HealthRules or a plan-built system) and wants to know, before
January 1, whether its CMS-0057-F APIs would pass with **its own** members,
claims and authorizations. The plan exports CPCDS files from its core and
loads them into CHO. Then it runs the same pinned Inferno kits CHO runs
every night, against that CHO.

There are two ways to run it:

| | Mode A: the plan hosts CHO | Mode B: Aurelianware hosts CHO |
|---|---|---|
| Where CHO runs | The plan's cloud or data center | Aurelianware's GCP, one tenant per plan |
| How files arrive | The plan's own folder or bucket, or the import API | Key-only SFTP into the plan's folder ([CPCDS-SFTP-DROP-GCP.md](CPCDS-SFTP-DROP-GCP.md)) |
| Who sees the data | Only the plan | The plan and Aurelianware, under a BAA |
| This guide | All of it | Steps 1 to 6 still apply. Aurelianware runs CHO and the plan uses the portal |

This guide covers Mode A. Everything in it uses features that already ship.
The kit consists of:

- `scripts/cpcds/trial.sh`: dry-runs and loads a folder of extracts, and runs Inferno.
- `deploy/local/cpcds-trial/docker-compose.cpcds.yml`: the laptop stack.
- Sample files in `docs/integration/samples/`.

## What the plan needs

- A CHO deployment, or a laptop with Docker for a first try with the sample
  files.
- CPCDS extracts from the core: members, coverages and claims. The
  [CPCDS importer](CPCDS-FILE-DROP-IMPORTER.md) documents the columns.
  Authorizations from the UM platform are optional, as CSV or X12 278. See
  [AUTHORIZATION-IMPORT.md](AUTHORIZATION-IMPORT.md).
- The plan's benefit plans in CHO, and a mapping from each core plan code
  ([LOADING-A-PLANS-DATA.md](LOADING-A-PLANS-DATA.md), steps 1 and 2).
- `bash` 4 or newer, `curl` and `jq`. For step 5, also Docker and the .NET 8
  SDK.

## On a laptop, with the sample files

This shows the whole flow end to end with synthetic data. It takes about 15
minutes once the images are built.

```bash
docker compose -f docker-compose.yml -f deploy/local/cpcds-trial/docker-compose.cpcds.yml \
  --profile core --profile finance --profile fhir up -d

mkdir -p trial && cp docs/integration/samples/cpcds/*.csv docs/integration/samples/authorizations/* trial/
./scripts/cpcds/trial.sh check trial --local     # dry run: nothing is written
```

The dry run lists the sample plan codes as **unmapped**. That is the first
real task with a plan's own files too. Create or import the benefit plans,
map the codes (`POST /api/v1/plan-code-mappings`), and run `check` again
until it is clean. Then:

```bash
./scripts/cpcds/trial.sh load trial --local
```

`--local` signs a development token (`scripts/dev-token.sh`) that only a
Development stack accepts. Never put real member data on this stack. It keeps
documents in memory and trusts a public signing key.

## On the plan's deployment

### 1. Reach the import endpoints

CHO's services are internal to the cluster. Run the kit from a machine that
can reach them, or port-forward:

```bash
kubectl -n cloudhealthoffice port-forward svc/claims-service 5001:80 &
kubectl -n cloudhealthoffice port-forward svc/enrollment-import-service 5011:80 &
kubectl -n cloudhealthoffice port-forward svc/authorization-service 5005:80 &
```

Point the kit at other addresses with `CHO_CLAIMS_URL`, `CHO_ENROLLMENT_URL`
and `CHO_AUTHORIZATION_URL`.

### 2. Get a token

The kit needs a CHO token for a tenant administrator of the plan's tenant,
issued by the deployment's token-service. That administrator holds
`claims:import` and `authorizations:import`. Set it as `CHO_TOKEN`. The
tenant comes from the token, never from the files.

### 3. Dry run

```bash
export CHO_TOKEN=...                  # tenant administrator
export CHO_SOURCE_SYSTEM=qnxt         # recorded on each claim and authorization
export CHO_LINE_OF_BUSINESS=Medicaid  # for rows that don't name one

./scripts/cpcds/trial.sh check /secure/extracts/2026-11-01
```

The folder's files are sorted by name, the same way the daily drop sorts them:

| File name contains | Loaded as |
|---|---|
| `member` | CPCDS members (one file) |
| `coverage` | CPCDS coverages (one file, with the member file) |
| `claim` | CPCDS claims (any number) |
| `auth` or `278` | Authorizations. An X12 file (starting `ISA`) goes to the 278 import, anything else to the CSV import |

The kit sends each file with `dryRun=true` and prints:

- what would be created, updated, left unchanged or refused
- the unmapped plan codes
- the first errors in each file

The full reports go to `./cpcds-trial-out/`, one JSON file per import. The
exit status is 0 when the folder is clean, 1 when something was refused, and
2 when the kit could not run.

Fix and repeat until the check is clean. Usually that means mapping plan
codes, adding a `columnMap` for headers the core names differently, or
correcting the extract.

### 4. Load

```bash
./scripts/cpcds/trial.sh load /secure/extracts/2026-11-01
```

It loads members and coverage first, then claims, then authorizations. That
is the order the daily drop uses, so every claim and authorization finds its
member. Reloading the same files is safe: unchanged records are reported
`Unchanged` and nothing is written.

### 5. Validate with Inferno

```bash
./scripts/interop/fetch-inferno.sh us-core c4bb plan-net   # once

export CHO_INTEROP_TARGET_URL=https://fhir.plan.example/fhir/r4
export CHO_INTEROP_TARGET_PATIENT_IDS=<a loaded member's FHIR Patient id>
export CHO_INTEROP_TARGET_TOKEN="$(cat member-app-token.txt)"
export CHO_INTEROP_TARGET_DIRECTORY_URL=https://fhir.plan.example/fhir/r4/directory/<plan>
./scripts/cpcds/trial.sh validate
```

This runs US Core, CARIN Blue Button and Plan-Net with the plan's own
member. [VALIDATE-A-DEPLOYMENT.md](VALIDATE-A-DEPLOYMENT.md) explains:

- which token to use (a member app's SMART token)
- why nothing is seeded
- how to read a failure that comes from the data rather than the API

### 6. Keep it current

Once a load works by hand, the core can write the same files to the daily
drop instead. To set that up, turn the drop on, either in the portal under
**Imports & EDI**, **Setup**, or with `PUT /api/v1/claims/import/cpcds/drops/settings`.
Point the core's export at `{tenant}/inbound`. Then watch freshness and
reconciliation on **Imports & EDI**, **Daily drop**. Each file is kept as
received in the document repository for ten years. See
[CPCDS-FILE-DROP-IMPORTER.md](CPCDS-FILE-DROP-IMPORTER.md#the-daily-file-drop).

## What to share with Aurelianware

Share the Inferno summary only: the pass, fail and skip counts for each kit,
and the failing test ids. The kit's evidence folder and the import reports
in `cpcds-trial-out/` contain member data. Keep them inside the plan.

## Checklist

- [ ] Benefit plans in CHO and every core plan code mapped. `check` shows no unmapped plan codes.
- [ ] `check` exits 0 on a full extract.
- [ ] `load` exits 0, and the claim counts and paid amounts match the core's own control totals.
- [ ] Authorizations loaded, if the plan uses its own UM platform.
- [ ] Inferno US Core, CARIN BB and Plan-Net run against the deployment with a loaded member.
- [ ] Daily drop on, and freshness showing Fresh.
