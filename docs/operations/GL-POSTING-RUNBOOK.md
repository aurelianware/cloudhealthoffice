# Runbook: general ledger posting (payment runs, reversals, ACH transmission)

**Scope:** double-entry GL entries for FFS claims payments, posted by ar-service from
events payment-service publishes. ar-service also produces a period extract for the
client's ERP and a claims payable reconciliation. There is no direct ERP integration.
**Default:** off on both sides (`GlEvents:DispatchEnabled`, `GlPosting:Enabled`).

> This is the books. Every change of mapping, period or posting needs a second person to
> check it against the client's chart of accounts and close calendar.

## 1. How it works

```
payment-service                                             ar-service
---------------                                             ----------
payment run executes ─ PaymentRunExecuted ─┐
reversal run executes ─ ReversalRunExecuted ┤ outbox (in the same document write)
NACHA file transmitted ─ PaymentFileTransmitted ┤
payment voided before its file was sent ─ PaymentVoidedBeforeTransmission ┘
        │  GlEventDispatcher (at least once, backoff, never drops)
        └── POST /api/gl/events (service token, payment-service only) ──▶ register (one per event id)
                                                                         ├─ entry builder (pure)
                                                                         ├─ cutover check
                                                                         ├─ account mapping + chart
                                                                         ├─ period lease (serialized with the close)
                                                                         └─ journal (append-only) or PARKED
```

| Event | Entry (business key) | Debit | Credit | Entry date |
|---|---|---|---|---|
| PaymentRunExecuted | `accrual:{runId}` | ClaimsExpense: gross (net + offsets) | ClaimsPayable: net; ProviderReceivable: receivable offsets recovered | execution date |
| ReversalRunExecuted | `recoupment:{reversalRunId}` | ProviderReceivable | ClaimsExpense | execution date |
| PaymentFileTransmitted | `ach:{runId}` | ClaimsPayable: file credit total | AchInTransit | transmission date |
| PaymentVoidedBeforeTransmission | `void:{paymentId}` | ClaimsPayable: the payment | ProviderReceivable: the payment | void date |
| operator reversal | `{entryId}:reversal` | mirror of the original | | chosen date (open period) |
| repost (after a reversal) | `{key}:r{n}` | as the event's entry | | event date, or a later chosen date |

- **Balanced, money-exact.** Every entry has two or more lines. Each line has exactly
  one positive side, amounts are decimals with two places, and debits equal credits.
  The journal store re-checks this before every insert. Mongo stores the amounts as
  Decimal128.
- **Exactly once in the books.** Entries are deduplicated in two ways:
  - The register holds one record per event id. A redelivery returns the first
    outcome. The same id with a different payload is refused (409) and the first stands.
  - The journal entry id is the entry's **business key**. A second event about the same
    run cannot post twice; for example, a second transmitted file for a re-dated run is
    parked as `DuplicateBusinessKey`.
- **Never dropped: parked.** An event that cannot post yet is kept, with its reason:
  - `NeedsMapping`: a role is unmapped, or its account is missing, inactive, not yet
    effective, terminated, or duplicated in the chart.
  - `ClosedPeriod`: the entry date falls in a closed period.
  - `DuplicateBusinessKey`: the run already has this kind of entry from another event.
  - `InvalidPayload`: a negative or non-money amount, totals that do not add up, or a
    file with debits.
  - `PreCutover`: the run was executed before the tenant's `CutoverDate` (section 3a).
  - `AwaitingSourceEvent`: it depends on another event that has not settled: a void on its
    recoupment, or an undated transmission on its run's accrual. It is re-checked
    automatically (`waitingForEventId`).
  - `ManualPostingRequired`: a refund owed to a provider (an open item).
- **Append-only.** No entry is ever updated or deleted. Corrections are reversing
  entries (`POST /api/gl/entries/{id}/reverse`). An entry can be reversed only once, and
  a reversal cannot itself be reversed. The register record of an event can change
  (parked to posted or dismissed); the journal never does.
- **Dates.** Two dates are kept:
  - The **entry date** is the accounting date and decides the period.
  - `postedAt` is when the entry was recorded.

  A parked closed-period event can be posted on a **later** date in an open period, with
  a reason. Both dates and the reason are kept on the entry.
- **Settlement seam.** Transmission credits `AchInTransit`. Bank settlement and ACH
  return events (from ack/return ingestion, not built yet) will move `AchInTransit` to
  `Cash`, or back to payable for a return. Until then, **map `AchInTransit` to the cash
  account** (or to a clearing account that you clear by hand).
- **Not posted yet (seams).**
  - Cash for **checks**: no check-issue or cleared event exists, so the check portion of
    a run stays in ClaimsPayable. The reconciliation shows it as outstanding.
  - Capitation disbursements and premium cash: capitation-service and
    premium-billing-service publish no GL events yet.

### Payments reversed before their file was sent

A reversal run can recoup a payment whose run's NACHA file has not gone to the bank yet
(the file is Pending, Failed, or never approved; never Transmitted, Transmitting or an
unresolved NeedsReview). Sending the file as approved would pay money that was already
recouped, so the transmission refuses it ("re-pin the file without it"). Then:

1. Once the reversal run has completed, a payments:approve user (not the run's maker or
   executor) calls `POST /api/paymentruns/{id}/eft-file/redate` with a reason. The same
   path as a re-date: the old file's record becomes Superseded, a new file `-R{n}` is
   pinned without the reversed payments (and keeps the old effective date if it can still
   be sent), and a different user must approve and send it. All other payees are paid.
2. In the same write, the new file lists each left-out payment in `voidedPayments`, the run
   gets a warning, and a `PaymentVoidedBeforeTransmission` GL event is queued.
3. The payment is marked `voidedBeforePaymentAt/By/FileReference`, and the provider
   receivable that the reversal opened is cancelled for that payment (entry
   `CancelledUnpaid`, status `Cancelled`). The provider was never paid, so it owes
   nothing, and no later run recovers it. If this step is interrupted, the next transmit
   attempt finishes it first. The voided payment is never rebuilt into the file.
4. **The 835.** The payment's 835 was already sent and said the payment would arrive. It
   is not rewritten. `voidedPayments` (on the pinned file and on the new file's
   transmission record) lists for each one the check/trace number, NPI, amount, trading
   partner and ISA13. **Tell each provider that this payment will not arrive.** The
   reversal 835 the provider also received shows the recoupment; the receivable it
   carried forward (PLB FB) was cancelled because nothing was paid.

GL: the recoupment already moved the expense to a provider receivable (Dr
ProviderReceivable / Cr ClaimsExpense). The void entry then clears the payable that will
never be paid against that receivable (Dr ClaimsPayable / Cr ProviderReceivable). The
expense is reversed once, there is no receivable for a payment never made, and the run's
payable nets to zero. It is not Dr ClaimsPayable / Cr ClaimsExpense, because that would
reverse the expense a second time.

**Receivables are held until the void, where this service sends the files.** The hold
applies only while bank transmission is on for the service (`BankTransmission:Enabled`) and
for the tenant (`paymentControls.nachaTransmission.enabled`). If the tenant's switch cannot
be read, the receivable is held. With transmission off (the default: files are downloaded
and sent by hand), nothing would ever release a hold, so receivables open as before (Open,
recovered by later runs). Re-pinning a hand-sent file without a reversed payment still
works, and the void cancels the receivable if nothing was recovered yet. If something was
already recovered, it is handled as described below (recovery reversed, refund owed).

In hold mode, when a reversal run recoups a payment whose NACHA file has not been recorded
as Transmitted (never generated, Pending, Failed, Transmitting or NeedsReview), the
receivable it opens is `PendingVoid`. Its `pendingPaymentIds` lists those
payments. A held receivable is never offset against later payments to the provider.
- The void cancels its part. Once no payment is pending, the remainder opens.
- If the file is later recorded as Transmitted (upload, reconcile, or a resolve saying the
  bank received it), the payments were paid. The receivable is released (`Activated`
  entry) and later runs recover it.
- For a file sent outside this system, a person releases it:
  `POST /api/receivables/{id}/release` `{ "reason": "..." }` (payments:approve, a user).
  **This includes a tenant with bank transmission on whose file was nevertheless sent by
  hand** (downloaded and uploaded to the bank outside payment-service). No Transmitted
  record exists for that file, so every receivable held for its payments stays
  `PendingVoid` until it is released this way, one by one. Check
  `GET /api/receivables?status=PendingVoid` after any manual send.

A receivable opened before this existed may already have been partly recovered when the
void completes. The completion then reverses the newest recoveries until the cancellation
fits. The ledger entry of each reversed recovery is marked with the void (`[void {paymentId}]`).
Every completion rebuilds the refunds owed from those marked entries, so a refund is never
lost if the completion is interrupted after the ledger write. Each refund is recorded in
four places:
- the notice's `refundsOwed`;
- a run warning;
- a critical audit log;
- a `ProviderRefundOwed` GL event.

ar-service never posts that event. It parks it as `ManualPostingRequired`, and the
reconciliation lists it under `openItems` (with `openItemsTotal`). The report is not
`balanced` while any open item remains. **The provider is owed those amounts back: refund
them, post the refund in the GL by hand** (for example Dr ProviderReceivable / Cr
ClaimsPayable or cash), then dismiss the event with a reason naming the posting. That
closes the open item.

Completing a void never blocks the file. An outage is retried on the next transmit. If the
ledger refuses (for example the receivable keeps changing), the notice is marked
`needsAttention` with the problem, a run warning is added, and the payment stays out of the
file.

The automatic re-pin is refused, and nothing changes, when a person must decide:
- the payment recovered receivables;
- only some of its claims were reversed;
- its claims were reversed in different reversal runs or 835s;
- the reversed amount differs from the payment;
- the reversal opened no receivable for the payee.

For those cases a finance user excludes the payment:
`POST /api/paymentruns/{id}/eft-file/exclude-payments` `{ "paymentIds": [...], "reason": "..." }`
(payments:approve). It follows the re-date rules: the user must be neither the run's maker
nor its executor, the drop is probed for a Failed file, and someone else approves and sends
the new file. The other payees are paid, with the same void, receivable, 835-notice and GL
handling, plus:
- **Partly reversed:** only the reversed part is relieved (`payableRelieved`). The rest
  (`unpaidRemainder`) stays payable and is still owed to the provider. Its claims stay with
  this run (they are finalized as paid), so no later run pays them.
  - The GL reconciliation lists it as an open item, `VoidRemainderUnpaid`, and is not
    balanced until it is resolved.
  - Pay the remainder another way (for example a manual check). Then a second user (not the
    one who excluded the payment, nor the run's maker or executor) records it, once:
    `POST /api/paymentruns/{id}/eft-file/voided-payments/{paymentId}/reissue-remainder`
    `{ "method": "Check", "reference": "CHK-…", "reason": "..." }`. The write is
    conditional on that notice having no reissue yet: of two concurrent requests, one
    records and the other gets 409, changing nothing.
  - That queues `VoidRemainderReissued`. The GL posts Dr ClaimsPayable / Cr Cash against the
    payable the original accrual left, so nothing is accrued twice. The open item then closes.
    Map the `Cash` role first, or the reissue parks `NeedsMapping` and the item stays open.
- **Offsets:** the receivables the payment recovered are owed again (its recoveries are
  undone, since it is never made). The GL void entry is the payable net of those offsets.

A reversal still running is always refused (wait for it). Excluding a payment that has no
completed reversal is refused.

**Cutover:** a void follows its recoupment, not its payment run.
- It posts here whenever the recouping reversal run's `ReversalRunExecuted` posted here.
- If that event is `PreCutover`, the void is PreCutover too.
- While the recoupment has not arrived, or is parked for another reason, the void parks as
  `AwaitingSourceEvent` ("waiting for its recoupment"). It is re-checked automatically when
  the recoupment posts or parks PreCutover; nobody needs to retry it.

A transmission event without the run's execution date waits the same way for the run's
accrual.

## 2. Accounts (per tenant, validated at startup)

The accounts are those of the tenant's chart in ar-service (`GET /api/v1/ar/accounts`).
Every mapped number must exist exactly once there, be Active, and be effective on the
entry date.

```json
"GlPosting": {
  "Enabled": true,
  "Tenants": {
    "tenant-1": {
      "CutoverDate": "2026-06-01",
      "Accounts": {
        "ClaimsExpense": "5100",
        "ClaimsPayable": "2100",
        "AchInTransit": "1015",
        "ProviderReceivable": "1250",
        "Cash": "1010"
      }
    }
  }
}
```

The startup check fails ar-service on any of these:
- an unknown role;
- an account number that is not 1 to 20 letters, digits, `.` or `-`;
- two roles on opposite sides of one entry sharing an account (ClaimsExpense/ClaimsPayable,
  ClaimsExpense/ProviderReceivable, ClaimsPayable/AchInTransit,
  ClaimsPayable/ProviderReceivable);
- while `Enabled` is true, a tenant without `CutoverDate`, or one given as a time rather
  than a date.

A tenant with no mapping is not an error: its events park as `NeedsMapping`.

## 3. Enabling

1. Create or verify the accounts in the tenant's chart (ar-service) and agree the mapping
   with the client's controller. Write it into ar-service `GlPosting` (section 2).
2. Agree the **cutover date** and post the **opening balances** (section 3a).
3. **ar-service:** set `GlPosting:Enabled=true` and restart.
4. **payment-service:**

   | Key | Value |
   |---|---|
   | `GlEvents:DispatchEnabled` | `true` (default false: events accumulate in the outboxes, nothing is sent) |
   | `GlEvents:ArServiceBaseUrl` | e.g. `http://ar-service` (must be in `ChoAuth:Outbound:Hosts`; `ar-service` is listed) |
   | `GlEvents:Interval`, `BatchSize`, `MaxBackoff` | defaults 30 s, 100, 1 h |
   | `ChoAuth:ServiceToken:*` | payment-service's service identity (already required) |

   The dispatcher needs MongoDB **without** `MongoDb:UseTenantScoping`, because it reads
   every tenant's outboxes from one database. Startup fails otherwise.
5. Events created while dispatch was off are delivered on the first cycles, oldest first.
   Each cycle reads at most `BatchSize` due events per collection, through an index on the
   outbox's `PublishedAt`, and skips events that are backing off.
   `GET /api/gl-events/unpublished?limit=…` (payment-service, payments:read; default 500,
   at most 5000) lists what is still undelivered, with attempts and the last error.

### 3a. Cutover and opening balances

`GlPosting:Tenants:{t}:CutoverDate` (a date, required while posting is enabled) is the
first day this GL owns. Every event about a run executed **before** it is registered and
listed, but parked as `PreCutover`, not posted:
- the accrual of the run;
- its transmitted file, even when it is sent after the cutover (its voids follow their
  recoupment instead; see "Payments reversed before their file was sent");
- reversal runs executed before the cutover.

The run's execution date comes from the event. Older transmission events lack it; for
those the run's accrual event decides. If the accrual has not arrived yet, the event parks
as `AwaitingSourceEvent` and is re-checked automatically when the accrual arrives.

Opening-balance step, once, before enabling:
1. Choose the cutover date with the controller: the first day of an open period, after the
   last run settled in the legacy books.
2. In the ERP (or as a manual journal outside this GL), post the opening balances as of
   the cutover for ClaimsPayable, ProviderReceivable and AchInTransit/clearing, from the
   legacy books. Pre-cutover runs whose files go to the bank after the cutover stay in
   the legacy books: their transmission must be recorded there, because this GL parks it.
3. After enabling, `GET /api/gl/events?status=Parked` lists the `PreCutover` events.
   Compare their totals with the opening balances. A pre-cutover event the controller
   decides belongs here instead, because it was left out of the opening balance, is
   posted with `POST /api/gl/events/{id}/retry`
   `{ "includeBeforeCutover": true, "reason": "..." }`. The entry records this.
   Otherwise, leave it parked or dismiss it with a reason.
4. The reconciliation shows pre-cutover runs with the informational flag `PreCutover` and
   excludes them from the differences.

## 4. Operating

| Call (ar-service) | Who | Purpose |
|---|---|---|
| `GET /api/gl/entries?period=yyyy-MM&after=…&limit=…` | finance:read | the journal, by entry id, in pages (default 500, at most 5000; `after` = last id of the previous page) |
| `GET /api/gl/events?status=Parked&after=…&limit=…` | finance:read | parked events and why (paged the same way) |
| `POST /api/gl/events/{eventId}/retry` `{ "entryDate"?: "...", "reason"?: "...", "includeBeforeCutover"?: true }` | finance:write user | post a parked event once the mapping, chart or period is fixed. `entryDate` (with a reason, never earlier) moves a closed-period event to an open date. `includeBeforeCutover` (with a reason) posts a `PreCutover` event (section 3a) |
| `POST /api/gl/events/{eventId}/repost` `{ "reason": "...", "entryDate"?: "..." }` | finance:write user, not the one who reversed the entry it replaces (403) | after the latest entry of the event's business key was reversed: post the event (parked, e.g. `DuplicateBusinessKey`, or posted) as `{key}:r{n}`, naming the reversed entry in `repostOf`. Refused while that entry stands; nothing changes if it cannot post |
| `POST /api/gl/events/{eventId}/dismiss` `{ "reason": "..." }` | finance:write user | decide not to post a parked event (e.g. a duplicate transmission confirmed against payment-service) |
| `POST /api/gl/entries/{id}/reverse` `{ "reason": "...", "entryDate"?: "..." }` | finance:write user | reversing entry, in an open period |
| `POST /api/gl/periods/{yyyy-MM}/close` `{ "reason": "..." }` | finance:write user | close a period (idempotent; never a future one; there is no reopen). 409 while an entry is being posted into it: close again in a moment. Records the period's extract control (SHA-256, counts, totals) |
| `GET /api/gl/periods` | finance:read | periods with status and, once closed, the extract control |
| `GET /api/gl/extract?period=yyyy-MM&format=csv\|json` | finance:read | the ERP extract. For a closed period it must match the control recorded at close, or 409 `GlExtractIntegrity` (nothing returned; escalate) |
| `GET /api/gl/reconciliation?period=yyyy-MM&offset=…&limit=…` | finance:read | claims payable reconciliation; runs paged (default 200), totals over the whole ledger, `totalRuns` and `runsNeedingAttention` over all pages |

Every GL write is refused with 409 `GlPostingDisabled` while posting is off. Reads still
work. Service tokens cannot make GL corrections.

### Month end

1. **Parked events.** `GET /api/gl/events?status=Parked` must be empty for the period,
   or each event must be explained. Fix mappings and retry. Dismiss duplicates only after
   checking payment-service: the run's transmission record, and the re-dated or
   superseded file history.
2. **Reconciliation.** `GET /api/gl/reconciliation?period=…`: the `difference` must be
   0, and every run flag other than `PayableOutstanding` needs an explanation
   (section 6).
3. **Close the period.** A post and the close are serialized: each post holds a lease in
   its period while it writes. The close is one conditional write that succeeds only while
   the period is open and no live lease is held. A post therefore lands before the close,
   or is refused after it and parks as `ClosedPeriod`. A post that took more than a minute
   from lease to write is abandoned (nothing written, retried), well inside its five-minute
   lease, so an expired lease never hides a write in progress. The close records the
   extract's SHA-256 and totals; the closed period's extract can never differ.
4. **Extract.** `GET /api/gl/extract?period=…`. Load it into the ERP and check the
   control totals: the `CONTROL` row gives entries, lines, total debit, total credit and
   the SHA-256 of the header and rows. The same period always yields the same bytes,
   because rows are ordered by entry date, entry id and line, use invariant formatting,
   and carry no timestamp. The JSON form carries the same rows and control. A field
   starting with `= + - @` is prefixed with `'` (spreadsheet safety).

## 5. Disabling / emergency stop

- Setting `GlEvents:DispatchEnabled=false` stops delivery. Events stay in the outboxes.
- Setting `GlPosting:Enabled=false` makes ar-service refuse events (409). The dispatcher
  keeps them and retries with backoff. Nothing is lost either way.

## 6. Reconciliation flags

| Flag | Meaning / action |
|---|---|
| `PayableOutstanding` | informational: accrued and not yet transmitted. This covers check payments (no cash event yet) and ACH runs whose file has not been sent. |
| `AccrualParked` / `TransmissionParked` | the event is parked; fix and retry. |
| `AccrualMissing` | a file was transmitted for a run whose accrual never reached the GL. Check `GET /api/gl-events/unpublished` in payment-service. |
| `AccrualAmountMismatch` / `TransmissionAmountMismatch` | the posted amount differs from the event's own total. Escalate. |
| `DuplicateTransmission` | two transmitted files for one run reached the GL. Only one posted. Verify in payment-service and dismiss the other. |
| `TransmittedMoreThanAccrued` | more cash went out than the run accrued. Escalate. |
| `FileDiffersFromAchPayments` | the file's credit total is not the sum of the run's payments that the file names (`paymentIds` on PaymentFileTransmitted), or the file names a payment the run did not accrue. Escalate. A payee paid by check after execution (check fallback) is **not** a difference: it is not in the file's ids, and its amount stays payable (`PayableOutstanding`). For older events without ids, the comparison is with the run's ACH payments. |
| `EntryReversed` | an operator reversed one of the run's entries and nothing replaces it. A repost (`{key}:r{n}`) clears it; reconciliation counts every entry of a key with its reversals and reposts. |
| `VoidParked` | a PaymentVoidedBeforeTransmission event is parked: the payable of a payment that will never be paid is not relieved. Fix and retry. |

**Open items** (`openItems`, report level). The report is not balanced while any is open.
- Refunds owed to providers (`ProviderRefundOwed`, parked `ManualPostingRequired`): post each
  by hand, then dismiss its event naming the posting.
- Unreversed remainders of excluded payments (`VoidRemainderUnpaid`): pay the remainder and
  record it with reissue-remainder (see "Payments reversed before their file was sent").
| `PreCutover` | informational: the run is before the GL cutover and is in the opening balances (section 3a). |

`glClaimsPayableBalance` is credits minus debits on every ClaimsPayable line.
`runsOutstandingPayable` is the sum, over all runs, of accrued minus transmitted minus
voided. They are equal unless something posted to ClaimsPayable outside these entries.

## 7. Limitations

- **Payments not in the accrual.** The accrual lists the payments the run's execution
  created. If a payment insert failed with an unknown outcome (the run then fails), that
  payment is not in the accrual. Reconcile against payment-service for failed runs.
- **Overwritten publish marks.** A whole-document save of a run holding an older copy
  can clear an event's `publishedAt`. The event is then delivered again, and the
  register de-duplicates it (at least once, never lost).
- **Correcting a posted entry.** Reverse it, then repost the right event (`repost`). The
  journal keeps one live entry per business key.
- **Entry dates are UTC calendar dates** of execution and transmission. A run executed
  late in the evening US time is dated the next day. Close periods with that in mind.
- **Period close and posting.** These are serialized by leases (month end, step 3). The
  one assumption is that a write does not stall for over four minutes between its
  freshness check and the database. If it did, the extract check at close would catch it
  (409 `GlExtractIntegrity`).
- **Not built yet:**
  - settlement, returns, check cash, capitation and premium cash (see section 1);
  - multi-currency;
  - account dimensions beyond line of business on the entry;
  - per-tenant databases. payment-service and ar-service require MongoDB; with
    `MongoDb:UseTenantScoping` payment-service's `GET /api/gl-events/unpublished` answers
    501 and there is no dispatcher. Events still accumulate in the documents' outboxes.
