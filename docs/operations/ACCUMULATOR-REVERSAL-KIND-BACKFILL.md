# Accumulator reversal-kind backfill runbook

One-off. Run once per environment, after the accumulator-service build that records
`ReversalKind` (PR #1278 follow-up 3) has been serving for a while, so that no new legacy rows
appear. The tool is `tools/AccumulatorReversalKindBackfill`; it follows the pattern of
[`AR-LEGACY-POSTING-RECONCILIATION.md`](./AR-LEGACY-POSTING-RECONCILIATION.md): read-only listing,
reviewed plan with a SHA-256, dry-run by default, append-only audit.

## Why

accumulator-service records why a claim's accumulators were backed out on its `ClaimReversed`
rows, `ClaimTombstoned` rows and reversal tombstones (`ReversalKind`: `Replacement`, `Void` or
`Denial`). Only a **replacement** makes a later replacement of the same original a "second
replacement", which is skipped (deductible not counted, reported as an orphan).

Records written before the kind existed have `ReversalKind` = null, and the service treats them
with the old rule: *a reversal whose source is another claim is a replacement*. That is right
for a frequency-7 replacement and wrong for a frequency-8 void: after a legacy void V1 of C1, a
real replacement C2 of C1 is still skipped. The backfill writes the kind on those records, so the
current rule applies to them too.

Nothing else reads the kind, and no amount changes. The only behavioural effect of the backfill
is that a replacement arriving after a legacy **void** is counted.

## What the tool looks at

Legacy items (no `ReversalKind`), in accumulator-service's database (`AccumulatorEvents`,
`AccumulatorProcessedClaims`):

| `item_type` | Record | Reversing claim (`source_claim_id`) |
|---|---|---|
| `ClaimReversedRow` | `ClaimReversed` event row | `SourceReference` |
| `ClaimTombstonedRow` | zero-delta `ClaimTombstoned` row | `SourceReference` |
| `ReversalMarker` | `{original}:reversal` marker, outcome `Reversed` or `ReversedBeforeApply` | the tombstone's `ResultingEventId`, or the source of the `ClaimReversed` row it names |
| `OriginalTombstoneMarker` | the original's own marker, outcome `ReversedBeforeApply` | `ResultingEventId` |

`NothingToReverse` markers are not listed: the replacement rule never reads them.

All items of one original claim are classified together and must agree on the reversing claim.

## How each original is classified

Evidence comes from accumulator-service itself and, when configured, read-only from
claims-service's `Claims` collection (status, `ClaimFrequencyCode`, `PredecessorVersionId`).

| Case | Proposed kind | Evidence required |
|---|---|---|
| Reversing claim = the original (its own reversal) | `Void` | claims-service status **Voided** |
| | `Denial` | claims-service status **Denied** |
| Reversing claim is another claim | `Replacement` | claims-service: frequency **7** and `PredecessorVersionId` = the original; **or**, when claims-service does not have it, accumulator-service processed it as a claim of its own (its marker, its `{id}:reversal` marker, or a `ClaimApplied` row — a frequency-8 void never gets any of these) |
| | `Void` | claims-service: frequency **8** and `PredecessorVersionId` = the original, **and** accumulator-service never processed it as a claim of its own |
| Anything else | `AMBIGUOUS` | — |

The rule is deliberately lopsided. Writing `Replacement` changes nothing (it is what the legacy
rule already assumes), so accumulator-service's own data is enough. Writing `Void` is the change
that lets a later replacement count — wrongly applied, it would let two replacements both count —
so it always needs claims-service evidence, and any sign that the "void" applied as a claim makes
the original `AMBIGUOUS`. Without claims-service evidence configured, no original is ever proposed
as `Void` or `Denial`.

`AMBIGUOUS` items are listed with the reason and **never written**. They keep the legacy rule. If
one matters (a replacement of that original is being skipped), handle it as an incident: decide
from the claim history, then correct the record by hand under a separate ticket.

## Settings

From the environment or `appsettings.json`, never the command line:

| Setting | Env var | Notes |
|---|---|---|
| `MongoDb:ConnectionString` | `MongoDb__ConnectionString` | accumulator-service's database, required |
| `MongoDb:DatabaseName` | `MongoDb__DatabaseName` | default `CloudHealthOffice` |
| `ClaimsMongoDb:DatabaseName` | `ClaimsMongoDb__DatabaseName` | claims-service's database; unset = no claims evidence |
| `ClaimsMongoDb:ConnectionString` | `ClaimsMongoDb__ConnectionString` | default: accumulator-service's connection string |
| `ClaimsMongoDb:UseTenantScoping` | `ClaimsMongoDb__UseTenantScoping` | as claims-service (`{db}_{tenant}`) |

accumulator-service data lives in its base database even with tenant scoping (its Kafka consumer
has no request tenant). The tool needs read access to claims-service and read/write access to
`AccumulatorEvents`, `AccumulatorProcessedClaims` and the two audit collections in
accumulator-service's database. **MongoDB / Cosmos DB for MongoDB only**: an accumulator-service
running on the Cosmos native SDK is not supported by this tool.

```bash
export MongoDb__ConnectionString='<accumulator-service connection string>'
export MongoDb__DatabaseName='<accumulator-service database>'
export ClaimsMongoDb__DatabaseName='<claims-service database>'
alias kindfill='dotnet run --project tools/AccumulatorReversalKindBackfill --'
```

Every command that touches the database prints `Target: …` first and requires
`--confirm-database <name>`, equal to the configured database. Options are exact and lower-case;
an unknown, mis-cased, repeated or value-less option is an error.

| Exit | Meaning |
|---|---|
| 0 | Done; nothing refused or failed (`AMBIGUOUS` rows are reported, not failures). |
| 1 | Some row was refused or failed. |
| 2 | Invalid arguments or plan. Nothing changed. |
| 3 | The plan's SHA-256 is not the reviewed one. Nothing changed. |
| 4 | Wrong environment: database not confirmed, or the plan was listed elsewhere. Nothing changed. |

## Steps

1. **List** (read-only):

   ```bash
   kindfill list --confirm-database "$MongoDb__DatabaseName" --out reversal-kinds-$(date -u +%Y%m%d).csv
   ```

   One row per legacy item: `tenant_id`, `item_type`, `item_id`, `original_claim_id`,
   `source_claim_id`, `aggregate_id`, `outcome`, `proposed_kind`, `evidence`, `source_database`,
   `source_host`. The file holds member and claim ids: keep it in the restricted attachment area
   of the ticket only, never in chat, email or a pull request, and delete local copies after
   step 5.

2. **Review.** The reviewer (claims operations) reads every `evidence` and checks a sample of
   `Void` originals in claims-service. Rows may be **removed** to defer them — but always every
   row of an original or none (the tool refuses an original whose plan lacks one of its records).
   Do not edit any value: a changed `proposed_kind` is refused unless the live evidence gives the
   same kind, and an `AMBIGUOUS` row is never written whatever it says.

3. **Hash** the final plan and record the SHA-256 in the ticket:

   ```bash
   kindfill hash --csv reversal-kinds-reviewed.csv
   ```

4. **Dry-run** (the default; writes nothing — no record, no audit):

   ```bash
   kindfill apply --confirm-database "$MongoDb__DatabaseName" --csv reversal-kinds-reviewed.csv --log dryrun.log
   ```

   Each row ends `WouldSet`, `AlreadySet`, `Ambiguous` (left untouched, with the reason) or
   `Refused` (the record changed since the listing, the evidence now gives another kind, the plan
   misses one of the original's records, …). Resolve every `Refused` (list again) or accept it.

5. **Execute** with the reviewed hash:

   ```bash
   kindfill apply --confirm-database "$MongoDb__DatabaseName" --csv reversal-kinds-reviewed.csv \
     --execute --sha256 <hash from step 3> --operator <your name> --log execute.log
   ```

   For each row the tool re-checks the live record (still no kind, same original and reversing
   claim, the evidence still gives the planned kind), inserts an `Intended` audit record, sets the
   kind with an update conditional on it still being null, and inserts a `Completed` record.

6. **Verify:** `kindfill list …` again — only `AMBIGUOUS` (and deferred) originals remain.

**Re-running is safe.** Records already set are reported `AlreadySet` and nothing is written (no
new audit record). A run that stopped part-way is completed by running it again.

## Audit

Append-only, in accumulator-service's database (records are inserted, never updated or deleted):

- `accumulator_reversal_kind_backfill_audit` — per written record: `Intended`, then `Completed`
  (or `Raced` when the conditional write found the kind already set by someone else), with the
  run id, tenant, item type and id, original and reversing claim, kind before / after, evidence,
  operator, plan SHA-256, host, database and time.
- `accumulator_reversal_kind_backfill_runs` — `Started` and `Finished` records per execute run,
  with operator, plan file and hash, host, database, log file and the outcome counts.

Do not delete them.

## Rollback

The tool only sets a field the running accumulator-service already reads; a build from before
follow-up 3 ignores it (`[BsonIgnoreExtraElements]`). To undo one record, unset its
`ReversalKind` by hand (it then falls back to the legacy rule) and note it in the ticket.
