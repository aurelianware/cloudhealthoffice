# Claims warehouse extract

A nightly flat-file copy of a tenant's claims for the plan's own warehouse or BI tool. It is the first data extract described in [portal-reporting.md §6](../architecture/portal-reporting.md#6-data-extracts-for-the-plans-own-analytics).

## What the plan gets

Each run writes one folder to the tenant's drop:

```
/outbound/extracts/claims/{runId}/
  claims.csv        one row per claim version
  claim-lines.csv   one row per line of each version
  manifest.json     written last: window, row counts, sizes, SHA-256, columns, agreement
```

The plan collects it with the SFTP login it already uses for its core extracts. `/outbound` is list-and-download only for that login. A login created before `/outbound` existed gets it the next time its keys are saved on the data drop setup page.

**Which rows a run includes:**
- Every claim version, except drafts, whose `LastUpdatedDate` is after the previous run's window end and at or before this run's window end.
- The window ends five minutes before the run starts (`SettleDelay`), so a write in flight is picked up next time.
- The first run, or a run started with `full=true`, includes everything.

**How to load it:**
- Treat rows as changes. A version appears again in a later run whenever it changes.
- Upsert `claims.csv` by `ClaimId`, keeping the row with the latest `LastUpdatedDate`.
- Replace that version's lines with the rows in `claim-lines.csv`.
- A claim's versions share `ClaimVersionId`. The current version has an empty `SupersededAt`.
- Wait for `manifest.json` before loading a folder, and check each file's SHA-256 against it.

**Format:**
- Files are UTF-8 CSV (RFC 4180, CRLF), with a header row. Values are written as stored, with no spreadsheet formula escaping.
- Instants are UTC ISO 8601. Service dates are calendar dates.
- The columns are fixed and listed in the manifest. `schemaVersion` changes when a column is added, removed or changes meaning.

## Controls

The files carry PHI for every member, so:
- Only a tenant admin can turn the extract on (`extracts:manage`).
- It cannot be turned on without naming the data-use agreement it is delivered under.
- Every run is recorded with the following, and the record is never deleted by the extract:
  - its window and who started it (`schedule` or a user);
  - the agreement;
  - each file's rows, bytes, SHA-256 and location;
  - the outcome.

## API (claims-service)

| Call | What it does |
| --- | --- |
| `GET /api/claims/extracts/claims` | Settings and progress: `enabled`, `dataUseAgreement`, `deliveredThrough`, `lastSucceededAt`, `running`, `folder` |
| `PUT /api/claims/extracts/claims` | `{ "enabled": true, "dataUseAgreement": "DUA-2026-014" }` |
| `POST /api/claims/extracts/claims/runs?full=false` | Starts a run now (202). 409 if the extract is off or already running |
| `GET /api/claims/extracts/claims/runs?limit=20` | Past runs, newest first |

## Deployment

```json
"ClaimExtract": {
  "Enabled": true,
  "RunAtUtc": "06:00:00"
}
```

- `Enabled` turns on the nightly run for every tenant that turned its extract on. Manual runs through the API work either way.
- The extract writes to the drop's storage: `CpcdsDrop:Store`, with `CpcdsDrop:Root` for a folder or `CpcdsDrop:Gcs:Bucket` for the hosted bucket.
- With several replicas, a run leases the tenant (`LeaseDuration`, default 4 hours), so only one replica runs it.

## Limits of this first slice

- **Claims only.** Members, authorizations, and appeals and grievances follow the same pattern.
- **Change detection uses `LastUpdatedDate`.** Every business write to a claim stamps it. Writes that only clear outbox bookkeeping do not, and they change nothing the extract carries.
- **Plan-owned destinations are not covered yet.** That means a plan's own S3, Blob or GCS bucket. Parquet is also not offered yet.
- **No portal screen yet.** A tenant admin uses the API.
