# BAI2 Bank Reconciliation Runbook

CHO reads the bank's BAI2 statement to do two things:
- confirm premium cash, and apply it when the match is certain;
- mark transmitted NACHA files as settled, and flag ACH returns.

Anything uncertain waits in a suspense queue for a person. Nothing is applied
on a weak match.

Code:
- `src/services/shared/CloudHealthOffice.BankReconciliation/`: parser, storage, runner, events.
- `premium-billing-service/BankReconciliation/`: premium cash rules.
- `payment-service/BankReconciliation/`: NACHA settlement rules.

## Where files go

| Account | Service | Endpoint | Permission |
|---|---|---|---|
| Premium deposit / lockbox account | premium-billing-service | `POST /api/v1/bank-statements/bai2?fileName=` | `finance:write` |
| Disbursement (ACH origination) account | payment-service | `POST /api/v1/bank-statements/bai2?fileName=` | `payments:run` |

The body is the BAI2 file (`text/plain`), at most 20 MB
(`BankReconciliation:MaxFileBytes`). The same file can go to both services:
- Each service decides each line once for itself, and ignores lines that are not its business.
- `BankReconciliation:AccountSuffixes` (last four digits) limits a service to its own accounts. Lines of other accounts are recorded as `Ignored`.

Reads (default read permission of the service):
- `GET /api/v1/bank-statements`: files received (no content).
- `GET /api/v1/bank-statements/{id}`: one file.
- `GET /api/v1/bank-statements/items?status=Suspense|Return|Matched|Applied|Ignored&statementId=`: the decisions. `Suspense` and `Return` are the work queue.
- payment-service also has `GET /api/v1/bank-statements/settlements/{fileReference}`: whether the bank shows that NACHA file as settled.

MongoDB is required, like every store since ADR 017. The service does not fall
back to memory, where idempotency would be lost on restart.

## What is checked: the whole file or nothing

The parser (`Bai2Parser`) handles these records:
- 01 / 02 / 03 / 16 / 88 / 49 / 98 / 99, in that structure.
- 88 continuations: for a 16 record they extend the text (joined by a space); for other records they extend the fields.
- Funds types 0 / 1 / 2 / Z, S (three amounts), V (value date and time) and D (distributions).
- Text that contains commas and slashes.
- Fixed-length records without line breaks (01 field 7). A file with neither
  line breaks nor a record length is split at each `/` that is followed by a
  record code and a comma.
- A 16 whose fields run into an 88 before its text keeps its bank and
  customer references.

Amounts are whole minor units: 2 implied decimals for USD, 0 for JPY and
similar currencies.

**Any** of the following rejects the file (`400`), and nothing in it is reconciled:
- 49 account control total ≠ the sum of the account's 03 and 16 amounts (03 status amounts may be signed);
- 49 record count ≠ the records from 03 to 49, continuations included;
- 98 group total ≠ the sum of its 49s; 98 account count or record count wrong;
- 99 file total ≠ the sum of its 98s; 99 group count or record count wrong;
- version ≠ 2, records out of order, an unknown record code, an orphan 88, a bad date, amount or funds type.

The errors name the line number and show account numbers masked (`****6789`).

## Idempotency and storage

- **File hash.** The file id is derived from the tenant and the SHA-256 of the
  bytes. Uploading the same bytes again returns `Duplicate` with the first
  outcome. For an accepted file, reconciliation resumes: lines already decided
  are skipped, and any of their events not yet delivered are sent again.
- **Bank file id.** For accepted files, sender + receiver + creation date + 01
  file id is unique per tenant (a unique index). A *different* file that
  reuses an id is rejected: "already used by a different file". There are two
  ways forward:
  - Ask the bank for a corrected file with a new file id.
  - Supersede the old statement, which takes two people:
    1. `POST /api/v1/bank-statements/{id}/supersede` with `{ "reason": "..." }`.
    2. A **different** user calls `POST /api/v1/bank-statements/{id}/supersede/approve`.

    From the request on, the statement is frozen until the request is
    approved, withdrawn or rejected:
    - its items cannot be resolved (including `ApplyToInvoice`);
    - re-ingesting it decides nothing;
    - a reconciliation run already under way when the request was made is
      stopped at its commit point. The premium matcher checks right before
      cash application posts, and the NACHA matcher right before a settlement
      is recorded. The line's claim is given up rather than posted.

    To unfreeze it: `POST /api/v1/bank-statements/{id}/supersede/withdraw`
    with `{ "reason": "..." }`. When the requester calls it, the request is
    Withdrawn; when anyone else with the permission calls it, the request is
    Rejected. A withdrawn or rejected request can be made again later, and
    every step is kept in the supersession's `history`.

    Approval checks every line **before** changing anything. It refuses if
    any line was applied, matched, settled or resolved, or if a run is
    deciding lines right now. It then saves the approval and dismisses the
    open lines, including claims an interrupted run abandoned. The dismissal
    is idempotent: if it stops part-way, approving again finishes it. The
    corrected file is accepted only once the dismissal is complete. The corrected
    file, even byte-for-byte the one refused earlier, is then accepted, under
    bank key `{key}#2`.

    Supersession is **refused** once any line of the statement was
    applied, matched, settled or resolved by any service. Those effects must
    be reversed through the normal workflows; then ask the bank for a new
    file id. A re-upload of a superseded file reconciles nothing.
    `GET /api/v1/bank-statements/{id}/supersession` shows the request.
- **Append-only.** `bank-statement-files` documents are only ever inserted.
  This includes rejected files, which are kept for audit. The file text is
  stored as FieldProtection ciphertext, bound to the tenant, the file id and
  the field. A `FieldProtection` key ring must be configured, as for NACHA:
  without one, outside Development, uploads fail rather than store
  plaintext. Responses and listings never include the content.
- `bank-statement-transactions`: one per 16 record, with a stable id. Only the
  masked account and its last four digits are stored.
- `bank-reconciliation-items`: one decision per (service, line), created once
  (`Pending`, then decided). A run that stopped part-way leaves `Pending`
  claims, which another run takes over after
  `BankReconciliation:PendingLeaseMinutes` (10).

Logs carry ids, type codes, amounts and masked accounts. They never carry
account numbers or 16 text, which may name payers. Every value that came from
a file or a request is cleaned of control characters before it is logged
(`SanitizeForLog` at each log site).

## Premium cash rules (premium-billing-service)

Config section `BankReconciliation:Premium`.

| Line | Decision |
|---|---|
| Type in `AchReturnTypeCodes` (257, 258, 266, 555, 564) | **Return**: flagged for review |
| Not a credit in `CashCreditTypeCodes` (115, 116, 142, 165, 166, 169, 195, 208, 301) | Ignored |
| 115 lockbox deposit | **Matched** when the lockbox batches recorded for the as-of date add up exactly to the deposit. If the line names a lockbox number, only that lockbox's batches count. Otherwise suspense |
| A reference (customer ref, bank ref or a word of the text) + amount + date (±`DateToleranceDays`, 1) equals a recorded 820/lockbox payment (its cross-source key) | **Matched**: the bank confirms that cash; nothing is posted |
| The references name exactly **one** open invoice, and the amount is **exactly** its balance, and the invoice has **no** 820/lockbox payment of the same amount within `PossibleDuplicateWindowDays` (7) | **Applied** through `ICashApplicationService` (source `BankStatement`), with the same posting, balances and sponsor account updates as an 820 |
| ... but such a payment exists | **Suspense** (`PossibleDuplicate`): the bank may be reporting money already posted under a reference or date it formats differently (a 15-digit ACH trace, a weekend between the 820 date and the bank date) |
| Anything else (no reference, amount ≠ balance, several invoices or payments) | **Suspense**, with candidates |

When the bank line is applied, its customer reference becomes the payment's
check/trace number (trace `BAI-{line id}`). Two guards stop an 820 or lockbox
payment for the same money that arrives later from posting it twice:
- If it has the same cross-source key, cash application holds the whole payment.
- If not, cash application holds the item as `PossibleDuplicate` when the
  invoice already has a bank-statement payment within 7 days whose applied
  amount **or** bank line amount equals the item. The bank line amount
  matters when the bank line overpaid the invoice and only the balance was applied. This is the one change to `CashApplicationService`.

Only reference tokens shaped like an invoice number are looked up as one
(`InvoiceNumberPattern`, default `^INV-[A-Z0-9][A-Z0-9-]{2,60}$`, matched in
full and case-insensitively). Other numbers in a reference or text never name
an invoice by coincidence.

**Crash recovery.** If a run applied a line and stopped before recording the
decision, the next run finds the line's own batch (`BAI-{line id}`) first and
records the line as Applied. It does not decide it again. To turn off automatic application entirely:
`AutoApplyExactInvoiceMatch=false`.

Working the queue:

```http
POST /api/v1/bank-statements/items/{id}/resolve
{ "action": "ApplyToInvoice", "invoiceId": "...", "note": "Sponsor confirmed partial payment" }
{ "action": "MarkMatched", "remittanceBatchIds": ["rmt-..."], "note": "Lockbox file loaded late" }
{ "action": "Dismiss", "note": "Interest credit, not premium" }
```

`ApplyToInvoice` is refused for a lockbox deposit (115): the deposit's checks
are applied from the lockbox file, so load that file and use `MarkMatched`.
`ApplyToInvoice` posts through cash application. It may be a partial payment,
and an overpayment becomes unapplied credit. Applying the same line twice
posts once: the batch id comes from the bank line.

## NACHA settlement rules (payment-service)

Config section `BankReconciliation:Nacha`.

| Line | Decision |
|---|---|
| Type in `AchReturnTypeCodes` | **Return**. The candidates are files transmitted in the last `ReturnLookbackDays` (60). Nothing is settled |
| Debit in `SettlementDebitTypeCodes` (451, 455, 466, 469) | Settles a **credit** file (provider payments) with `TotalCreditAmount` = amount |
| Credit in `SettlementCreditTypeCodes` (165, 166, 169) | Settles a **debit** file with `TotalDebitAmount` = amount |
| Anything else | Ignored |

The candidates are files that meet all of these:
- `Transmitted` (not `NeedsReview`, `Failed`, `Pending` or `Superseded`);
- not already settled;
- effective entry date within as-of − `SettlementWindowDays` (3) to as-of + 1.

How a candidate is chosen:
- One candidate: it is settled.
- Several: a file reference or run number in the line's references or text picks one; otherwise the line goes to suspense.
- A company id from `CompanyIds` (default `Era:OriginatingCompanyId`) in the text is noted on the decision, but it never widens a match.

Settlement is recorded in `NachaFileSettlements`: one per file, insert-only,
so a file is settled by one line at most. The transmission record itself is
not modified.

Working the queue:

```http
POST /api/v1/bank-statements/items/{id}/resolve
{ "action": "MarkSettled", "fileReference": "FFS-PR-12", "note": "Bank netted a fee" }
{ "action": "Resolve", "relatedIds": ["payment-id"], "note": "R03 return; payment voided and reissued by check" }
{ "action": "Dismiss", "note": "Not an ACH settlement" }
```

ACH returns need the bank's return detail (R-code, trace number) to find the
entry. BAI2 gives only the amount and text, so a person matches the return,
then voids or reissues the payment through the payment workflows.

## GL posting seam

GL posting (#1286) is now on main. This seam is not wired to it yet: the
default logging sink stays registered, and journaling BAI2 settlement and
cash confirmation is a follow-up.


Every decision, and every resolution, raises a `BankReconciliationEvent` on
`IBankReconciliationEventSink`. The event says which service, which item, its
kind (PremiumCash, LockboxDeposit, NachaSettlement, AchReturn), its status, the
type code, direction, amount, as-of date and the ids it matched. It carries no
account number and no text.

The default sink only logs. GL posting registers its own sink (registration
is `TryAdd`, so the first one registered wins). Delivery is at least once:
an item stays unpublished (`EventPublishedAt` null) until the sink accepts
its event, and the next ingest of that file retries it. A sink must
deduplicate on `EventId`.

## Interim and final reports

The 02 as-of-date modifier says what kind of report it is: 1 = interim
previous-day, 2 = final previous-day, 3 = interim same-day, 4 = final
same-day.

The same bank transaction often arrives twice, first in an intraday
report and then in the final one. It is recognised by account, type code,
bank reference, amount and as-of date. The copy is recorded as `Ignored`
("already reconciled as ..."), not decided again, and raises no event. That
happens **only** when both of these hold:
- the two lines are in different statements;
- one is from an interim report (modifier 1 or 3) and the other from a final report (2 or 4).

Any other match, such as the same reference twice in one file or in two
final reports, goes to **Suspense** (`PossibleDuplicate`) for a person to
decide. It is never dropped.

Some references do not identify a transaction:
- the placeholders listed in `BankReconciliation:PlaceholderBankReferences` (`NONREF`, `NONE`, `N/A`, `NA`, `UNKNOWN`);
- all-zero references;
- references shorter than 4 characters.

Lines with such a reference, or with none, are never compared this way.

To reconcile only final reports, set `BankReconciliation:InterimReports=Skip`:
lines of modifier 1 and 3 groups are then `Ignored`.

## Type codes

Banks differ in which codes they use. Check your bank's BAI2 guide and
override the lists in configuration. The defaults are in `Bai2TypeCodes`.

A transaction's direction comes from the standard ranges: 100–399 credit,
400–699 debit, anything else status. To give custom codes (9xx) a direction,
list them in `BankReconciliation:CreditTypeCodes` and `DebitTypeCodes`. These
lists are checked before the ranges. Then add them to the service's matching
lists (`Premium:CashCreditTypeCodes`, `Nacha:SettlementDebitTypeCodes`, ...).

## Limitations

- No automatic matching of ACH return entries to individual payments: there is no return detail in BAI2.
- No premium-side matching of the premium NACHA debit drafts' settlement. premium-billing-service's own drafts can be reconciled the same way later.
- Group status 2 (deletion) and 3 (correction) lines always go to suspense.
- An undelivered event is retried only when the same file is ingested again.
