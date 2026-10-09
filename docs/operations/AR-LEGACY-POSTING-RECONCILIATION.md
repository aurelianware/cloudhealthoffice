# AR legacy cash posting reconciliation runbook

**Deploy blocker.** PR #1271 (applying a cash posting credits AR balances) must never run in an
environment without the legacy posting guard. The only supported sequence, in each environment, is:

1. **Confirm** that no build with #1271 but without the guard ever ran there (step 0).
2. **Deploy** the ar-service build that contains the guard (it contains #1271 too). From then on,
   apply and void of a legacy posting return 409, so the time until reconciliation is safe.
3. **Run this runbook** (steps 1 to 6). The tool refuses to change anything until that build has
   started against the database.

Do **not** run the tool before deploying. The ar-service builds before this one break if the tool
writes first:

- their models do not ignore unknown fields, so every read of a posting or balance the tool has
  written (`Version`, `PostedEntryId`, `PostedAt`, `LegacyReconciliation`) fails with a
  `FormatException` (HTTP 500);
- the pre-#1271 build saves balances with an unconditional replace, so a concurrent adjustment can
  erase a credit the tool made while the posting stays marked reconciled.

## Why

Before PR #1271, `POST /api/v1/ar/cash-postings/{id}/apply` set the posting's `Status` and
`AppliedAmount` but credited no `ArBalance`. Since PR #1271, apply credits each application and
stores the credit's entry id in `CashApplication.PostedEntryId`.

A posting applied by the old code has applications with an amount and no `PostedEntryId`. Applying
it again under the new code would credit those applications for the first time. If finance has
already corrected those balances by hand (manual AR adjustments), the balance would be credited
twice.

## The guard (ar-service)

A posting is **legacy** when all of the following hold (`CashPostingLedger.IsLegacy`):

- its status is `PartiallyApplied` or `Applied`;
- at least one application has `AmountApplied > 0`;
- no application has a `PostedEntryId`.

The old code never set `PostedEntryId`, and the new code sets it on every application with an
amount when it applies. So an applied posting with no posted id at all was applied by the old code.

A posting with some posted ids and some unposted applications is different. The new code applied
it and it was given more applications later, so it is not legacy and apply credits only the new
applications. `Pending` postings were never applied. `Voided` postings can be neither applied nor
voided again, so they carry no hazard and are not listed.

For a legacy posting:

- `POST .../apply` returns **409** with `code: "LegacyPostingRequiresReconciliation"` and credits
  nothing. The portal shows "This cash posting cannot be applied yet … finance must reconcile it
  first", not "AR Service unavailable".
- `POST .../void` returns the same 409 and debits nothing. Voiding would drop the posting out of
  review while any manual correction stays on the balance.

After reconciliation each legacy application has a `PostedEntryId`, so the posting is no longer
legacy:

| Decision | `PostedEntryId` | Balance | Later apply | Later void |
|---|---|---|---|---|
| `CORRECTED_MANUALLY` | `manual-{posting}-{index}` (no such entry exists) | unchanged | not credited | not debited; finance reverses its own adjustment |
| `APPLY_CREDIT` | `cash-{posting}-{index}` (the entry apply itself would post) | credited | not credited again | debited back, like any other credit |

The decision is also recorded on the posting as `LegacyReconciliation`: status `Reconciled`,
reviewer, ticket, note, operator, time, CSV hash, run id, and the per-application decisions. A
posting that is not reconciled has no `LegacyReconciliation` at all (null).

### The capability marker

At startup the guard build writes a marker document: `ar_service_capabilities`, id
`cash-posting-ledger`, in the base database (`MongoDb:DatabaseName`). It lists the build's
capabilities (`ledger-credit-v1`, `legacy-reconciliation-guard-v1`, `ignore-extra-elements-v1`),
the build version, the host and when it last started. It writes in the background and retries
until the database is reachable, so startup never waits on it.

The tool reads the marker before every run and prints it. `--execute` is refused (exit 4) when the
marker is missing or lacks a capability. A dry-run only warns.

The marker shows that a guard-capable build **has started** against the database. It cannot show
that one is **still running**: an older build neither writes nor removes it. Before executing, the
release owner checks the deployed ar-service version against the `Build` and `last started` values
the tool prints (step 4).

### Rollback

Every persisted ar-service model now has `[BsonIgnoreExtraElements]`. A build with this change
reads documents carrying fields it does not know, so rolling back to it, or to any later build, is
safe after the tool has run.

Rolling back to a build **without** this change is not safe once the tool has written anything:

- before #1271, reads of reconciled postings and credited balances fail with 500, and balance saves
  are unconditional;
- #1271 without the guard has no 409, so legacy postings can be credited again.

There is no un-reconcile. If such a rollback seems necessary, roll forward instead, or stop
traffic to ar-service and escalate. Do not run the tool while an older build serves.

## The tool

`tools/ArLegacyPostingReconciliation` is a .NET console tool, following the repository's
convention for one-off data tools (see `tools/FamilyRelationshipsBackfill`). It references
ar-service, so it uses the service's own models, its ledger rules (`CashPostingLedger`) and its
version-checked balance repository.

ar-service runs on MongoDB only; `Program.cs` refuses any other provider. Cosmos DB for MongoDB is
reached with the same Mongo connection string and needs nothing extra. The tool reads the same
settings as the service, from the environment or `appsettings.json`, never from the command line:

| Setting | Env var | Default |
|---|---|---|
| `MongoDb:ConnectionString` | `MongoDb__ConnectionString` | required |
| `MongoDb:DatabaseName` | `MongoDb__DatabaseName` | `CloudHealthOffice` |
| `MongoDb:UseTenantScoping` | `MongoDb__UseTenantScoping` | `false` (when `true`, data lives in `{DatabaseName}_{tenant}`) |

Use the values from the ar-service deployment of the environment, for example from its Key Vault
secret. The listing needs read access. Executing needs write access to `cash_postings`,
`ar_balances`, the two audit collections, and read access to `ar_service_capabilities`.

```bash
export MongoDb__ConnectionString='<ar-service connection string>'
export MongoDb__DatabaseName='<ar-service database name>'
alias arlegacy='dotnet run --project tools/ArLegacyPostingReconciliation --'
```

Every command that touches the database:

- prints `Target: host …, database …` first;
- requires `--confirm-database <name>`, which must equal the configured database name exactly.
  Otherwise nothing is read or changed (exit 4).

Options are exact and lower-case. An unknown, mis-cased, repeated or value-less option is an error
(exit 2), never ignored. Exit codes:

| Code | Meaning |
|---|---|
| 0 | Done; nothing refused or failed. |
| 1 | Some posting was refused or failed (see the output). |
| 2 | Invalid arguments or CSV. Nothing changed. |
| 3 | The CSV hash is not the approved one. Nothing changed. |
| 4 | Wrong environment, CSV listed elsewhere, or no guard-capable ar-service. Nothing changed. |

## Roles and sign-off

| Role | Does | Signs off |
|---|---|---|
| **Operator** (platform engineer with production DB access) | Runs the listing, the dry-run and the execute. Attaches outputs to the ticket. | That the dry-run shown to finance was produced from the exact CSV hash executed, against the confirmed database. |
| **Finance reviewer** (AR accountant) | Decides each application: was the balance already corrected by hand? | The decisions in the CSV (`reviewer`, `ticket` columns). |
| **Finance approver** (controller or AR lead, not the reviewer) | Reviews the marked CSV and the dry-run output, and picks the run date (see [Accounting period](#accounting-period)). | Approval to execute: records the CSV SHA-256 in the ticket. |
| **Release owner** | Confirms step 0, deploys the guard build, and checks the running version before execute. | That no unguarded #1271 build ran, and that the verification step shows no legacy postings left (or only explicitly deferred ones). |

Record every step in one finance ticket (for example `FIN-123`). That ticket id goes in the CSV.

## Handling the CSV and logs (PHI and financial data)

The listing holds payer names and payer ids, which are member names and member ids when a member
paid, plus amounts and account numbers. The log files hold posting and payer ids. Treat all of
them as PHI:

- **Where they live:** keep them only in the restricted attachment area of the finance ticket, or a
  share limited to the four roles above. Never email them, post them in chat, put them in a pull
  request, or store them on a personal device.
- **Working copies:** the operator writes the files on the bastion or jump host used for database
  access, not a laptop. Finance edits the CSV in the restricted share; spreadsheet autosave and
  recovery copies must stay there too.
- **After the run:** once step 6 is signed off, the operator deletes every local copy (listing,
  reviewed CSV, logs, the `after.csv` from step 6) from the host and any download folders, and
  notes the deletion in the ticket. The ticket attachments are kept under the records retention
  policy for finance evidence.
- **Database audit:** the audit collections hold the same identifiers and are covered by the
  database's own access controls and retention.

## Steps

### 0. Confirm #1271 never ran without the guard (release owner, finance)

Check the ar-service deployment history of the environment: the CD pipeline runs, the image tags,
and `kubectl rollout history`. Look for any build that contains #1271 (balances credited on apply)
but not this change (no 409 guard; the `ar_service_capabilities` marker is absent).

**Why this matters.** During such a window, anyone could apply a legacy posting. That apply
credited its applications for the first time and gave them `cash-…` posted ids. Afterwards the
posting looks exactly like one the current code applied, so `list` does not show it and the guard
does not block it. If finance had already corrected those balances by hand, they are now credited
twice, and nothing in this runbook will find it.

- **No such build ran:** record that in the ticket and continue.
- **One did:** record the window (first deploy to replacement, UTC). Before continuing, finance
  reviews every posting created before the window that was credited during it:

  ```js
  // In each tenant database (or the base database without tenant scoping)
  db.cash_postings.find({
    CreatedAt: { $lt: ISODate("<window start>") },
    "Applications.PostedAt": { $gte: ISODate("<window start>"), $lte: ISODate("<window end>") }
  })
  ```

  For each one, finance checks the credited balances (`cash-{posting}-{index}` entries) against
  any manual adjustment for the same cash. Any double credit is corrected with a normal AR
  adjustment, recorded in the ticket.

### 1. Deploy the guard build, then list the legacy postings (read-only)

Deploy the ar-service build with the guard and wait until it serves. Then:

```bash
arlegacy list --confirm-database "$MongoDb__DatabaseName" --out legacy-postings-$(date -u +%Y%m%d).csv
arlegacy list --confirm-database "$MongoDb__DatabaseName" --out legacy-postings.csv --tenant tenant-a,tenant-b
```

The listing only issues finds, so it changes nothing. It writes one row per legacy application:

- **Posting:** `tenant_id`, `posting_id`, `posting_number`, `receipt_date`, `posting_status`,
  `payer_type`, `payer_reference_id`, `payer_name`, `posting_amount`, `applied_amount`,
  `unapplied_amount`.
- **Application:** `application_index`, `ar_balance_id`, `gl_account_id`, `account_number`,
  `balance_period`, `amount_applied`, `application_memo`.
- **Hints for finance:**
  - `balance_found`. When it is `false`, only `CORRECTED_MANUALLY` can be carried out; it is then
    recorded as balance missing;
  - `balance_closing_balance` (now);
  - `balance_manual_adjustment_credits`: the sum of `ManualAdjustment` credit entries on that
    balance, a hint only;
  - `balance_has_cash_entry`: should be `false`. `true` means ar-service already credited this
    application, so investigate before deciding.
- **Audit:** `posting_created_at`, `posting_created_by`, `posting_last_updated_at` (for a legacy
  posting, normally the time it was applied), and `applied_by`. `applied_by` is always
  `NOT_RECORDED`, because the old apply stored no actor. Use the API gateway or access logs
  around `posting_last_updated_at` if the person matters.
- **Source:** `source_database`, `source_host`, the environment the listing was taken from.
  `reconcile` refuses the CSV against any other environment (exit 4).
- **For finance to fill in:** `decision`, `reviewer`, `reviewed_at`, `ticket`, `note`.

If the file has no data rows, there is nothing to reconcile. Attach the empty listing to the
ticket and go to step 6.

### 2. Finance marks the CSV

For each row, finance establishes whether that balance was already corrected by hand for this
cash. Check AR adjustments (`ar_adjustments` and `ManualAdjustment` entries on the balance), the
GL and the bank deposit. Then fill in:

- `decision`: one of
  - `CORRECTED_MANUALLY`: the balance already reflects this cash. Mark the application posted
    and credit nothing.
  - `APPLY_CREDIT`: the balance does not reflect it. Credit it now, exactly as apply would.
  - leave it empty to defer the posting.
- `reviewer`: who decided. Use one reviewer per posting.
- `ticket`: the finance ticket. Use one ticket per posting.
- `reviewed_at` (optional): the date, written exactly `yyyy-MM-dd` (for example `2026-10-08`).
  Any other format refuses the posting.
- `note` (optional): free text.

Rules the tool enforces:

- A posting is all-or-nothing. Decide every application row of a posting, or none (a posting with
  no decisions is skipped and stays blocked by the guard).
- Do not edit any other column, and do not add, remove or rename columns. The tool compares the
  values with the live data and refuses the posting if they differ. It refuses the
  whole file (exit 2) for an unknown or repeated column, a row with too many or too few fields, or
  malformed quoting.
- Do not split a decision. If only part of an application's amount was corrected by hand, choose
  `CORRECTED_MANUALLY` and post the remaining difference as a normal AR adjustment, or the other
  way round. Record which in `note`.

Save as CSV (UTF-8). Spreadsheet tools may reformat amounts (`1,000.00`), dates (`10/8/2026`) or
ids. The tool refuses anything that no longer matches, so re-export from the listing if that
happens. Fields that start with `=`, `+`, `-`, `@`, a tab or a carriage return are written with a
leading `'`, so a spreadsheet does not run them as formulas. Leave that `'` in place.

### 3. Review and fix the hash

The approver reviews the marked CSV. When it is final, nobody edits it again:

```bash
arlegacy hash --csv legacy-postings-reviewed.csv      # or: sha256sum legacy-postings-reviewed.csv
```

Record the SHA-256 in the ticket. Any later edit changes the hash, and execute refuses it.

### 4. Dry-run (the default)

```bash
arlegacy reconcile --confirm-database "$MongoDb__DatabaseName" --csv legacy-postings-reviewed.csv --log dryrun.log
```

This changes nothing: no balance, no posting, no audit or run record. It prints:

- the target host and database;
- the ar-service marker: build, host, last start and capabilities, or a `WARNING` that execute
  will be refused. **The release owner compares the build with the version actually deployed now.**
- for each posting and application, what it would do, with the balance before and after (closing
  balance, total credits, sponsor and member split). For example:

```
app 0 -> balance bal-1 (GL gl-1) amount 600 APPLY_CREDIT: WOULD credit 600 as entry cash-cp-1-0; balance before [closing 1000 ...] after [closing 400 ...]
app 1 -> balance bal-2 (GL gl-1) amount 200 CORRECTED_MANUALLY: WOULD mark corrected manually, no credit; PostedEntryId := manual-cp-1-1; balance before [closing 500 ...] after [closing 500 ...]
```

Each posting ends with one outcome:

| Outcome | Meaning |
|---|---|
| `WOULD RECONCILE` / `RECONCILED` | Every row matches the live data. |
| `SKIPPED` | No decision. |
| `ALREADY_RECONCILED` | Reconciled earlier with the same decisions. Nothing to do. |
| `REFUSED` | Not applied. The reason is printed. |
| `FAILED` | Execution stopped part-way. Safe to run again after fixing the cause. |

A posting is refused when:

- it is not found;
- it is not legacy;
- an amount, id, number or status differs from the live data;
- an application lacks a row, or a row is not a legacy application;
- the decision is missing or unknown;
- the reviewer or ticket is missing, or the posting has more than one of either;
- `reviewed_at` is not `yyyy-MM-dd`;
- an `APPLY_CREDIT` balance is missing or on another GL account;
- `CORRECTED_MANUALLY` is chosen for a balance that already holds the ar-service credit entry;
- it was already reconciled with different decisions.

Attach `dryrun.log` to the ticket. Resolve every `REFUSED` with finance (re-list, re-mark,
re-hash) before executing, or accept it explicitly as deferred.

### 5. Execute with the hash

Run on the date finance chose (see [Accounting period](#accounting-period)):

```bash
arlegacy reconcile --confirm-database "$MongoDb__DatabaseName" --csv legacy-postings-reviewed.csv \
  --execute --sha256 <hash from step 3> --operator <your name> --log execute.log
```

Add `--tenant tenant-a` to limit a run to some tenants. A CSV with rows for any other tenant is
then refused whole.

Nothing is changed (exit 3 or 4) when:

- the file's SHA-256 is not the given one;
- the database is not confirmed;
- the CSV was listed elsewhere;
- the ar-service marker is missing.

Otherwise, for each posting the tool:

1. inserts an audit record per application, state `Intended`;
2. for `APPLY_CREDIT`:
   - records `CreditIntended`;
   - adds the controller's credit entry `cash-{posting}-{index}` through the service's versioned
     balance repository;
   - records `Credited` (and a run action) as soon as that balance save succeeds.

   A concurrent change to the balance is recorded as `ConcurrencyRetry`, then re-read and retried.
   An entry already on the balance is not added again (`AlreadyOnBalance`);
3. saves the posting (posted ids plus `LegacyReconciliation`), but only if it has not changed since
   it was read. If it has, the outcome is `FAILED` (`PostingSaveConflict`); run again;
4. records `PostingSaved` and sets the audit records to `Completed`.

**Re-running is safe.** A second run reports `ALREADY_RECONCILED` and changes nothing. A run that
stopped part-way is completed by running it again: credits already made are recognised by their
entry id and not repeated. The stopped run's audit records keep their history, including the
`Credited` event.

### 6. Verify

```bash
arlegacy list --confirm-database "$MongoDb__DatabaseName" --out after.csv     # expect no data rows (or only deferred postings)
```

Then spot-check in the database or through the API:

- each reconciled posting has `legacyReconciliation.status = Reconciled` in the API (in Mongo:
  `LegacyReconciliation.Status: 2`) and no application without a `PostedEntryId`;
- each `APPLY_CREDIT` balance has the `cash-{posting}-{index}` entry and the closing balance
  printed by the dry-run (plus any change made since);
- each `CORRECTED_MANUALLY` balance is unchanged;
- the run record (`ar_legacy_reconciliation_runs`) has `State: 2` (Finished) and no
  `CurrentPosting`.

Attach `execute.log` and `after.csv` to the ticket. The finance approver confirms the balances.
Then delete the local copies (see [Handling the CSV and logs](#handling-the-csv-and-logs-phi-and-financial-data)).

A deferred posting stays protected: apply and void return 409 until it is reconciled with a later
run of steps 1 to 6.

## Accounting period

The tool dates each credit entry (`PostedAt`) at the moment it executes. It does not back-date the
credit to the receipt date or the balance's period, and it does not check whether a period is
closed. Finance therefore picks the execution date:

- run on a date inside an **open** accounting period, the one the credits should land in;
- if an affected balance belongs to a period that is already closed, finance decides before
  executing: either `APPLY_CREDIT` with the credit recognised in the current open period, or
  `CORRECTED_MANUALLY` plus a normal AR adjustment in the open period. Record the choice in
  `note`.

## Where the audit lives

**Per application:** `ar_legacy_reconciliation_audit`, in the tenant's ar-service database. It is
append-only:

- The id is per run: `{runId}:{tenant}:{posting}:{index}`. A later run never replaces an earlier
  run's record.
- It is inserted before anything changes, then only gains `Events`, each with a time, a detail and
  the balance snapshot. The events are `Intended`, `CreditIntended`, `Credited`,
  `ConcurrencyRetry`, `AlreadyOnBalance`, `PostingSaved`, `PostingSaveConflict`, `Failed` and
  `ConfirmedByLaterRun`.
- Summary fields:
  - state (`Intended`, `Completed` or `Failed`);
  - who and when: operator, time, CSV SHA-256, host, database;
  - what: tenant, posting id and number, application index, balance id, whether the balance was
    missing, decision, amount, posted entry id;
  - the result: whether this run credited, and the balance before and after (closing balance,
    total credits, sponsor and member balance, version);
  - finance's input: reviewer, reviewed at, ticket and note.

**Per run:** `ar_legacy_reconciliation_runs`, in the base database. It is inserted when the run
starts and appended to as the run goes, so a run that stopped still shows what it did:

- state (`Running` or `Finished`): a `Running` run with no `FinishedAt` was interrupted;
- operator, host, database, tenant scoping, CSV hash and file name, the ar-service build, start
  and finish times, the log file path;
- `CurrentPosting`: the posting being worked on;
- `Actions`: every balance credited, with entry ids, amount and the balance after, recorded right
  after each save;
- `Postings`: the outcome and reasons for every posting, including refusals and skips.

**On the posting itself:** `LegacyReconciliation` records the decisions, reviewer, ticket,
operator, time, CSV hash and run id.

**Locally:** the log files (`dryrun.log`, `execute.log`) hold every line printed, with UTC
timestamps. Keep them with the ticket.

Do not delete the audit collections. They are the record of who changed which balance and why.
