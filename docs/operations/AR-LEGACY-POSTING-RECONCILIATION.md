# AR legacy cash posting reconciliation runbook

**Deploy blocker.** Complete this runbook in each environment before deploying the ar-service
that includes PR #1271 (applying a cash posting credits AR balances) to it, or, when that build
is already deployed with the guard below, before anyone needs to apply or void a legacy posting.

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

For a legacy posting that is not yet reconciled, or one whose `LegacyReconciliation.Status` is
`PendingReview`:

- `POST .../apply` returns **409** with `code: "LegacyPostingRequiresReconciliation"` and credits
  nothing.
- `POST .../void` returns the same 409 and debits nothing. Voiding would drop the posting out of
  review while any manual correction stays on the balance.

After reconciliation each legacy application has a `PostedEntryId`, so the posting is no longer
legacy:

| Decision | `PostedEntryId` | Balance | Later apply | Later void |
|---|---|---|---|---|
| `CORRECTED_MANUALLY` | `manual-{posting}-{index}` (no such entry exists) | unchanged | not credited | not debited; finance reverses its own adjustment |
| `APPLY_CREDIT` | `cash-{posting}-{index}` (the entry apply itself would post) | credited | not credited again | debited back, like any other credit |

The decision is also recorded on the posting as `LegacyReconciliation` (reviewer, ticket, note,
operator, time, CSV hash and the per-application decisions).

## The tool

`tools/ArLegacyPostingReconciliation` is a .NET console tool, following the repository's
convention for one-off data tools (see `tools/FamilyRelationshipsBackfill`). It references
ar-service, so it uses the service's own models, its ledger rules (`CashPostingLedger`) and its
version-checked balance repository.

ar-service runs on MongoDB only; `Program.cs` refuses any other provider. Cosmos DB for MongoDB
is reached with the same Mongo connection string and needs nothing extra. The tool reads the
same settings as the service:

| Setting | Env var | Default |
|---|---|---|
| `MongoDb:ConnectionString` | `MongoDb__ConnectionString` | required |
| `MongoDb:DatabaseName` | `MongoDb__DatabaseName` | `CloudHealthOffice` |
| `MongoDb:UseTenantScoping` | `MongoDb__UseTenantScoping` | `false` (when `true`, data lives in `{DatabaseName}_{tenant}`) |

Use the values from the ar-service deployment of the environment, for example from its Key
Vault secret. The listing needs read access. Executing needs write access to `cash_postings`,
`ar_balances` and the two audit collections.

```bash
export MongoDb__ConnectionString='<ar-service connection string>'
alias arlegacy='dotnet run --project tools/ArLegacyPostingReconciliation --'
```

## Roles and sign-off

| Role | Does | Signs off |
|---|---|---|
| **Operator** (platform engineer with production DB access) | Runs the listing, the dry-run and the execute. Attaches outputs to the ticket. | That the dry-run shown to finance was produced from the exact CSV hash executed. |
| **Finance reviewer** (AR accountant) | Decides each application: was the balance already corrected by hand? | The decisions in the CSV (`reviewer`, `ticket` columns). |
| **Finance approver** (controller or AR lead, not the reviewer) | Reviews the marked CSV and the dry-run output. | Approval to execute: records the CSV SHA-256 in the ticket. |
| **Release owner** | Deploys. | That the verification step shows no legacy postings left (or only those explicitly deferred with the guard in place). |

Record every step in one finance ticket (for example `FIN-123`). That ticket id goes in the CSV.

## Steps

### 1. List the legacy postings (read-only)

```bash
arlegacy list --out legacy-postings-$(date -u +%Y%m%d).csv            # all tenants
arlegacy list --out legacy-postings.csv --tenant tenant-a,tenant-b      # some tenants
```

The listing only issues finds, so it changes nothing. It writes one row per legacy application:

- **Posting:** `tenant_id`, `posting_id`, `posting_number`, `receipt_date`, `posting_status`,
  `payer_type`, `payer_reference_id`, `payer_name`, `posting_amount`, `applied_amount`,
  `unapplied_amount`.
- **Application:** `application_index`, `ar_balance_id`, `gl_account_id`, `account_number`,
  `balance_period`, `amount_applied`, `application_memo`.
- **Hints for finance:**
  - `balance_found`;
  - `balance_closing_balance` (now);
  - `balance_manual_adjustment_credits`: the sum of `ManualAdjustment` credit entries on that
    balance, a hint only;
  - `balance_has_cash_entry`: should be `false`. `true` means ar-service already credited this
    application, so investigate before deciding.
- **Audit:** `posting_created_at`, `posting_created_by`, `posting_last_updated_at` (for a legacy
  posting, normally the time it was applied), and `applied_by`. `applied_by` is always
  `NOT_RECORDED`, because the old apply stored no actor. Use the API gateway or access logs
  around `posting_last_updated_at` if the person matters.
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
- `reviewed_at` (optional, `yyyy-MM-dd` or ISO 8601) and `note` (optional free text).

Rules the tool enforces:

- A posting is all-or-nothing. Decide every application row of a posting, or none (a posting with
  no decisions is skipped and stays blocked by the guard).
- Do not edit any other column. The tool compares them with the live data and refuses the posting
  if they differ.
- Do not split a decision. If only part of an application's amount was corrected by hand, choose
  `CORRECTED_MANUALLY` and post the remaining difference as a normal AR adjustment, or the other
  way round. Record which in `note`.

Save as CSV (UTF-8). Spreadsheet tools may reformat amounts (`1,000.00`) or ids. The tool refuses
anything that no longer matches, so re-export from the listing if that happens.

### 3. Review and fix the hash

The approver reviews the marked CSV. When it is final, nobody edits it again:

```bash
arlegacy hash --csv legacy-postings-reviewed.csv      # or: sha256sum legacy-postings-reviewed.csv
```

Record the SHA-256 in the ticket. Any later edit changes the hash, and execute refuses it.

### 4. Dry-run (the default)

```bash
arlegacy reconcile --csv legacy-postings-reviewed.csv --log dryrun.log
```

This changes nothing: no balance, no posting, no audit record. For each posting and application
it prints what it would do, with the balance before and after (closing balance, total credits,
sponsor and member split). For example:

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

A posting is refused when:

- it is not found;
- it is not legacy;
- an amount, id, number or status differs from the live data;
- an application lacks a row, or a row is not a legacy application;
- the decision is missing or unknown;
- the reviewer or ticket is missing, or the posting has more than one of either;
- a balance is missing or on another GL account;
- `CORRECTED_MANUALLY` is chosen for a balance that already holds the ar-service credit entry;
- it was already reconciled with different decisions.

Exit code: 0 when nothing is refused or failed, 1 otherwise. Attach `dryrun.log` to the ticket.
Resolve every `REFUSED` with finance (re-list, re-mark, re-hash) before executing, or accept it
explicitly as deferred.

### 5. Execute with the hash

```bash
arlegacy reconcile --csv legacy-postings-reviewed.csv \
  --execute --sha256 <hash from step 3> --operator <your name> --log execute.log
```

- If the file's SHA-256 is not the given one, nothing is changed (exit 3).
- For each posting the tool:
  1. writes an `Intended` audit record per application;
  2. for `APPLY_CREDIT`, adds the controller's credit entry `cash-{posting}-{index}` through the
     service's versioned balance repository. A concurrent change is re-read and retried, and an
     entry already on the balance is not added again;
  3. saves the posting (posted ids plus `LegacyReconciliation`), but only if it has not changed
     since it was read;
  4. marks the audit records `Completed` with the before and after balances.
- **Re-running is safe.** A second run reports `ALREADY_RECONCILED` and changes nothing. A run
  that stopped part-way is completed by running it again: credits already made are recognised
  by their entry id.

### 6. Verify

```bash
arlegacy list --out after.csv     # expect no data rows (or only deliberately deferred postings)
```

Then spot-check in the database or through the API:

- each reconciled posting has `legacyReconciliation.status = Reconciled` in the API (in Mongo:
  `LegacyReconciliation.Status: 2`) and no application without a `PostedEntryId`;
- each `APPLY_CREDIT` balance has the `cash-{posting}-{index}` entry and the closing balance
  printed by the dry-run;
- each `CORRECTED_MANUALLY` balance is unchanged.

Attach `execute.log` and `after.csv` to the ticket. The finance approver confirms the balances.

### 7. Deploy

The release owner deploys ar-service once the ticket shows the listing, the approved hash, the
dry-run, the execute log and an empty (or explicitly deferred) `after.csv`. A deferred posting
stays protected: apply and void return 409 until it is reconciled with a later run of this
runbook.

## Where the audit lives

Each application's record is in `ar_legacy_reconciliation_audit`, in the tenant's ar-service
database:

- The id is fixed: `{tenant}:{posting}:{index}`, so there is one record per application however
  often the tool runs.
- Fields: run id, state (`Intended` or `Completed`), operator, time, CSV SHA-256, tenant, posting
  id and number, application index, balance id, decision, amount, posted entry id, whether this
  run credited, the balance before and after (closing balance, total credits, sponsor and member
  balance, version), reviewer, reviewed at, ticket and note.

Each execute run is recorded in `ar_legacy_reconciliation_runs`, in the base database:

- operator, CSV hash and file name, start and finish times, the log file path;
- the outcome and reasons for every posting, including refusals and skips.

On the posting itself, `legacyReconciliation` records the decision, reviewer, ticket, operator,
time and CSV hash.

The local log files (`dryrun.log`, `execute.log`) hold every line printed, with UTC timestamps.
Keep them with the ticket.

Do not delete the audit collections. They are the record of who changed which balance and why.
