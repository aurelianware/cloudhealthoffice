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
NACHA file transmitted ─ PaymentFileTransmitted ┘
        │  GlEventDispatcher (at least once, backoff, never drops)
        └── POST /api/gl/events (service token, payment-service only) ──▶ register (one per event id)
                                                                         ├─ entry builder (pure)
                                                                         ├─ account mapping + chart
                                                                         ├─ period check
                                                                         └─ journal (append-only) or PARKED
```

| Event | Entry (business key) | Debit | Credit | Entry date |
|---|---|---|---|---|
| PaymentRunExecuted | `accrual:{runId}` | ClaimsExpense: gross (net + offsets) | ClaimsPayable: net; ProviderReceivable: receivable offsets recovered | execution date |
| ReversalRunExecuted | `recoupment:{reversalRunId}` | ProviderReceivable | ClaimsExpense | execution date |
| PaymentFileTransmitted | `ach:{runId}` | ClaimsPayable: file credit total | AchInTransit | transmission date |
| operator reversal | `{entryId}:reversal` | mirror of the original | | chosen date (open period) |

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

## 2. Accounts (per tenant, validated at startup)

The accounts are those of the tenant's chart in ar-service (`GET /api/v1/ar/accounts`).
Every mapped number must exist exactly once there, be Active, and be effective on the
entry date.

```json
"GlPosting": {
  "Enabled": true,
  "Tenants": {
    "tenant-1": {
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
  ClaimsPayable/ProviderReceivable).

A tenant with no mapping is not an error: its events park as `NeedsMapping`.

## 3. Enabling

1. Create or verify the accounts in the tenant's chart (ar-service) and agree the mapping
   with the client's controller. Write it into ar-service `GlPosting` (section 2).
2. **ar-service:** set `GlPosting:Enabled=true` and restart.
3. **payment-service:**

   | Key | Value |
   |---|---|
   | `GlEvents:DispatchEnabled` | `true` (default false: events accumulate in the outboxes, nothing is sent) |
   | `GlEvents:ArServiceBaseUrl` | e.g. `http://ar-service` (must be in `ChoAuth:Outbound:Hosts`; `ar-service` is listed) |
   | `GlEvents:Interval`, `BatchSize`, `MaxBackoff` | defaults 30 s, 100, 1 h |
   | `ChoAuth:ServiceToken:*` | payment-service's service identity (already required) |

   The dispatcher needs MongoDB **without** `MongoDb:UseTenantScoping`, because it reads
   every tenant's outboxes from one database. Startup fails otherwise.
4. Events created while dispatch was off are delivered on the first cycles, oldest first.
   `GET /api/gl-events/unpublished` (payment-service, payments:read) lists what is still
   undelivered, with attempts and the last error.

## 4. Operating

| Call (ar-service) | Who | Purpose |
|---|---|---|
| `GET /api/gl/entries?period=yyyy-MM` | finance:read | the journal |
| `GET /api/gl/events?status=Parked` | finance:read | parked events and why |
| `POST /api/gl/events/{eventId}/retry` `{ "entryDate"?: "...", "reason"?: "..." }` | finance:write user | post a parked event once the mapping, chart or period is fixed. `entryDate` (with a reason, never earlier) moves a closed-period event to an open date |
| `POST /api/gl/events/{eventId}/dismiss` `{ "reason": "..." }` | finance:write user | decide not to post a parked event (e.g. a duplicate transmission confirmed against payment-service) |
| `POST /api/gl/entries/{id}/reverse` `{ "reason": "...", "entryDate"?: "..." }` | finance:write user | reversing entry, in an open period |
| `POST /api/gl/periods/{yyyy-MM}/close` `{ "reason": "..." }` | finance:write user | close a period (idempotent; never a future one; there is no reopen) |
| `GET /api/gl/extract?period=yyyy-MM&format=csv\|json` | finance:read | the ERP extract |
| `GET /api/gl/reconciliation?period=yyyy-MM` | finance:read | claims payable reconciliation |

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
3. **Close the period.**
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
| `FileDiffersFromAchPayments` | the file total differs from the run's ACH payments, because a payee moved to check after execution (its 835 said ACH). Tell the provider; that amount stays payable. |
| `EntryReversed` | an operator reversed one of the run's entries. |

`glClaimsPayableBalance` is credits minus debits on every ClaimsPayable line.
`runsOutstandingPayable` is the sum of accrued minus transmitted over all runs. They are
equal unless something posted to ClaimsPayable outside these entries.

## 7. Limitations

- **Payments not in the accrual.** The accrual lists the payments the run's execution
  created. If a payment insert failed with an unknown outcome (the run then fails), that
  payment is not in the accrual. Reconcile against payment-service for failed runs.
- **Overwritten publish marks.** A whole-document save of a run holding an older copy
  can clear an event's `publishedAt`. The event is then delivered again, and the
  register de-duplicates it (at least once, never lost).
- **Correcting a posted entry.** After a business entry is reversed, its business key
  stays taken. A re-post of the same run needs a new source event type (not built). For
  now, corrections are the reversal plus a manual journal in the ERP.
- **Entry dates are UTC calendar dates** of execution and transmission. A run executed
  late in the evening US time is dated the next day. Close periods with that in mind.
- **Closing races posting.** Closing a period is checked against each posting, not locked
  against it. An event being posted at the very moment its period is closed can still
  land in that period. Close only after the dispatcher has drained and the parked list is
  reviewed (month-end steps above).
- **Not built yet:**
  - settlement, returns, check cash, capitation and premium cash (see section 1);
  - multi-currency;
  - account dimensions beyond line of business on the entry;
  - Cosmos stores (ar-service is Mongo-only, and the payment-service dispatcher reads
    only from Mongo).
