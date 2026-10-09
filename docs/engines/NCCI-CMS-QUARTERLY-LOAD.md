# Loading the CMS NCCI PTP and MUE quarterly files

The NCCI engine (`src/engines/CloudHealthOffice.NcciEngine`) ships with a
small built-in seed (`NcciSeedData`, Q1 2025, about 30 pairs and 25 MUEs).
For real adjudication, load the public CMS quarterly tables every quarter.
This page covers where to get them, how to load them, and what the loader
does with them.

## Where to get the files

CMS publishes the Medicare NCCI tables free of charge each quarter (January,
April, July, October) at
<https://www.cms.gov/medicare/coding-billing/national-correct-coding-initiative-ncci-edits>.
Downloading requires accepting the AMA CPT license on the CMS page; the files
contain CPT codes and are licensed for that use only.

| Table | CMS page | Files in the ZIP | `kind` | `setting` |
| --- | --- | --- | --- | --- |
| PTP edits, practitioner | *Medicare NCCI Procedure to Procedure (PTP) Edits* → Practitioner PTP Edits | 4 tab-delimited `.txt` parts (`…-f1.txt` … `…-f4.txt`), plus `.xlsx` | `ptp` | `practitioner` |
| PTP edits, outpatient hospital | same page → Hospital Outpatient PTP Edits | tab-delimited `.txt` part(s), plus `.xlsx` | `ptp` | `outpatient-hospital` |
| MUE, practitioner | *Medically Unlikely Edits* → Practitioner Services MUE Table | `.csv` (and `.xlsx`) | `mue` | `practitioner` |
| MUE, outpatient hospital | same page → Outpatient Hospital Services MUE Table | `.csv` (and `.xlsx`) | `mue` | `outpatient-hospital` |

The DME supplier MUE table and the Medicaid NCCI tables are not loaded by
this loader.

Unzip the download and load the `.txt` (PTP) or `.csv` (MUE) files. If you
only have the `.xlsx`, save each sheet as CSV; the parser accepts
comma-separated input with the same columns.

## Layouts the parser reads

**PTP** — seven columns per data row, in this order:

1. Column 1 code
2. Column 2 code
3. `*=in existence prior to 1996` — `*` or blank
4. Effective Date — `YYYYMMDD`
5. Deletion Date — `YYYYMMDD`, or `*` for "no data" (still active)
6. Modifier indicator — `0` not allowed, `1` allowed, `9` not applicable
7. PTP Edit Rationale

**MUE** — header `HCPCS/CPT Code, {Setting} Services MUE Values, MUE
Adjudication Indicator, MUE Rationale`. The indicator cell holds the MAI digit
and text (`1 Line Edit`, `2 Date of Service Edit: Policy`, `3 Date of Service
Edit: Clinical`). When the header row is present, columns are found by name.

Both files open with a copyright/disclaimer preamble. Lines before the first
data row are skipped. After it, any non-blank line that does not parse (bad
code, date, modifier indicator, MUE value or MAI) is counted and reported back
with its line number; it is not loaded.

Small synthetic samples in these layouts are in
`tests/CloudHealthOffice.NcciEngine.Tests/Fixtures/cms/`. They are test data,
not CMS data.

## Triggering a load

benefit-plan-service hosts the admin endpoint next to the existing NCCI
endpoints. It needs the service's default write permission (`settings:manage`)
and always loads into the tenant in the caller's token.

```bash
curl -X POST "$BENEFIT_PLAN_URL/api/v1/ncci/cms-load" \
  -H "Authorization: Bearer $TOKEN" \
  -F file=@ccipra-v324r0-f1.txt \
  -F quarter=2026Q4 \
  -F kind=ptp \
  -F setting=practitioner \
  -F part=f1
```

Repeat for `f2`–`f4`, the outpatient hospital PTP file, and the two MUE files.
Form fields:

| Field | Values |
| --- | --- |
| `file` | the CMS file (max 95 MB — the NGINX ingress caps bodies at 100 MB) |
| `quarter` | `YYYYQn`, e.g. `2026Q4` |
| `kind` | `ptp` or `mue` |
| `setting` | `practitioner` or `outpatient-hospital` (`oph` also accepted) |
| `part` | optional; use for a table CMS splits over several files |
| `force` | optional `true` to reload a file already loaded |

The response reports `rowsLoaded`, `rowsRejected`, the first rejections,
`rowsExpired` (MUE codes the new table dropped), the file's SHA-256 and
`alreadyLoaded`.

## What the loader does

- **Streaming.** The upload is copied once to a temporary file (deleted on
  close) while its SHA-256 is computed, then parsed from disk. PTP rows are
  written in batches of 5,000, so a full table is never held in memory.
- **Stable ids.** PTP rows are keyed by tenant, setting, both codes and the
  CMS effective date; MUE rows by tenant, setting, code and quarter start.
  Loading the same file again replaces documents rather than duplicating them.
- **Ledger.** One `NcciLoadRecord` per (tenant, quarter, kind, setting, part)
  stores the file's SHA-256 and counts (Mongo `ncci_load_ledger`, Cosmos
  `NcciLoadLedger`). It is written last, after the rows and the version: an
  identical re-run is a no-op, a corrected CMS re-publication (different hash)
  reloads, and a load that failed part-way is retried in full.
- **PTP snapshots.** Each PTP file is a full snapshot of its slot (setting +
  part). Rows keep the CMS effective and deletion dates; the deletion date is
  exclusive (the edit no longer applies on that date). After a load, rows the
  same slot wrote before but this file omits are deleted when they came from
  the same quarter (a correction) or ended at the quarter start when they came
  from an earlier quarter. PTP files carry full history, so loading a quarter
  older than one already loaded for that setting is refused (400).
- **MUE snapshots.** MUE rows carry no dates in the CMS file, so they take the
  quarter's first day and, when a later quarter is already loaded, end at that
  quarter's start. Rows of the same quarter the file omits are deleted;
  earlier quarters' rows for codes the file omits end at this quarter's start.
  Loading Q4 after Q1 therefore never leaves a Q4-only code active in Q1. A
  load with `part` is not treated as a full table and reconciles nothing.
- **Settings.** Practitioner rows apply to 837P claims and outpatient hospital
  rows to 837I claims. Seed rows (no setting) apply to a setting only until a
  CMS table of that kind is loaded for it; after that the CMS table is
  authoritative, so seed rows cannot shadow CMS rows or resurface after a CMS
  row ends. Claim types CMS publishes no table for (e.g. 837D) see seed rows only.
- **Version.** `GET /api/v1/ncci/version` moves to the newest quarter loaded,
  with pair and MUE counts summed over that quarter's ledger. Back-filling an
  older MUE quarter does not move it backward.
- **Cache, across processes.** Every load writes a new `LoadStamp` on the
  version record. Lookup caches (benefit-plan-service replicas and
  claims-service) key entries by stamp and re-read the version at most every
  60 seconds, so all processes use the new tables within a minute of a load.

## Operational notes

- Load all of a quarter's files before its effective date; claims are edited
  against whatever is loaded when they adjudicate.
- Uploads are limited to 95 MB because the NGINX ingress caps request bodies
  at 100 MB (`infrastructure/k8s/nginx-ingress-config.yaml`,
  `infrastructure/helm/nginx-ingress-values.yaml`). CMS ships the large
  practitioner PTP table as several part files; load each with its `part`.
- On Cosmos, the host creates any missing NCCI container (`NcciPairs`,
  `MueEntries`, `NcciVersion`, `NcciLoadLedger`, partition key `/tenantId`) at
  startup. The version record has id `current`.
- A full practitioner PTP table holds over a million rows. The Mongo backend
  writes in unordered bulk batches of 1,000; the Cosmos backend upserts one
  item at a time, which is slow and RU-heavy for a full table.
