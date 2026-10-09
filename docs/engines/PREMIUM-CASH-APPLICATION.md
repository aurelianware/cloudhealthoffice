# Premium Cash Application (820 and lockbox)

premium-billing-service receives group premium payments as X12 820 files or bank lockbox
files. It applies each payment to the open invoices its remittance detail names, and it
saves the invoice balances and the sponsor account balances as it posts.

Code: `Edi/Edi820Parser.cs`, `Edi/LockboxCsvParser.cs`, `Services/CashApplicationService.cs`,
`Controllers/RemittancesController.cs`.

## Table of contents

- [Endpoints](#endpoints)
- [820 mapping (005010X218)](#820-mapping-005010x218)
- [Lockbox CSV](#lockbox-csv)
- [Application rules](#application-rules)
- [Failures, retries and concurrency](#failures-retries-and-concurrency)
- [Exceptions queue](#exceptions-queue)
- [Balances](#balances)
- [ar-service cash postings](#ar-service-cash-postings)
- [Storage](#storage)
- [Next steps](#next-steps)

---

## Endpoints

| Method | Path | Permission | |
|---|---|---|---|
| POST | `/api/v1/remittances/820?fileName=` | finance:write | Body is the 820 file (`application/edi-x12`, `text/plain`) |
| POST | `/api/v1/remittances/lockbox?fileName=` | finance:write | Body is the CSV (`text/csv`) |
| GET | `/api/v1/remittances` | billing:read | Recorded payments (batches) |
| GET | `/api/v1/remittances/{id}` | billing:read | One batch with its per-item outcomes |
| GET | `/api/v1/remittances/exceptions?status=Open` | billing:read | The exceptions queue |
| POST | `/api/v1/remittances/exceptions/{id}/resolve` | finance:write | `{ action: ApplyToInvoice \| CreditSponsorAccount \| Dismiss, invoiceId?, groupNumber?, note }` |
| GET | `/api/v1/sponsor-accounts/{groupNumber}` | billing:read | Open invoice balance, unapplied credit, net balance, credit history |
| POST | `/api/v1/sponsor-accounts/{groupNumber}/refresh` | finance:write | Recompute the open balance; 404 for a group with no invoices or account |

An upload answers with three lists:
- `batches`: the payments it recorded;
- `duplicates`: payments that were already recorded;
- `rejected`: payments that could not be posted, including any that failed part-way.

A file that cannot be parsed returns 400, and nothing from it is posted. Files are limited to 5 MB.

## 820 mapping (005010X218)

| Segment | Used for |
|---|---|
| ISA | Element separator (position 4) and segment terminator (position 106) |
| ST / SE | One payment per transaction set; a SE01 count mismatch is a warning |
| BPR01 | Handling code. C, D, U and X post cash. I (remittance only) and P (prenote) are recorded as `NotPosted` |
| BPR02 / BPR04 / BPR16 | Payment amount, method (ACH, CHK, FWT…), payment date |
| BPR03 | C or D. **D (a debit) is never posted as cash**; the batch is recorded as `NotPosted` |
| TRN02 / TRN03 | Trace number (for BPR04 = CHK, the check number); originating company id (the payer id) |
| REF*38 (header) | Master policy (group) number. When present, only that group's invoices are paid |
| N1*PR | Payer name; N104 is the payer id when TRN03 is absent |
| ENT | Loop 2000A (organization summary) or 2000B (individual: ENT02 = 2J) |
| NM1*IL | Member name and id (NM109) in individual remittance |
| RMR | RMR01 qualifier, RMR02 reference, RMR04 paid, RMR05 billed |
| DTM*582 | Coverage period of the preceding RMR |
| ADX | Adjustment (amount, reason) reported with the preceding RMR. It is recorded, not posted |

Amounts are rounded to cents (half away from zero) in both parsers. A transaction set without
BPR or TRN, or with an amount or date that cannot be read, rejects the whole file.

## Lockbox CSV

The header row names these columns, in any order:
`batch, item, deposit_date, check_number, payer_id, check_amount, invoice_number, amount`.
`payer_name`, `lockbox` (lockbox number) and `group_number` are optional. Each row is one
remittance stub. Rows that share a deposit date, batch and item are one check, and
`check_amount` must be the same on every row of the check.

The check's trace number is `LBX-{lockbox}-{deposit yyyyMMdd}-{batch}-{item}-{check}`. Banks
restart batch and item numbers each day, so the date and check number keep one day's check
from being taken for another's. The lockbox goes through the same application as the 820.

## Application rules

Each payment is recorded first, as a `RemittanceBatch`. Its id is a hash of the tenant, source,
payer and trace number, so the same payment cannot be recorded twice. Re-uploading a file
reports its completed payments as duplicates and posts nothing.

1. **Whole-payment holds.** In each of these cases nothing in the payment is applied, and the
   whole payment goes to the queue as one item:
   - `PossibleDuplicate`: the same check or trace number, amount and date already arrived by
     the other source (820 vs lockbox). The payer ids of the two sources usually differ, so
     the payer is not part of this key. A person decides whether it really is a duplicate.
   - `NegativeLineInPayment`: any item is negative (a reversal or recoupment). Netting it
     automatically could credit an invoice with more than the money received.
   - `DetailExceedsPayment`: the items total more than the payment.

   So the cash applied to invoices never exceeds BPR02.
2. **Matching.** Each remaining item is matched on its reference to an invoice number.
   Only invoice qualifiers are matched: RMR01 IK, IV, OI or 11, or no qualifier (lockbox).
   Then:
   - **Exact match.** The item pays the balance, and the invoice becomes `Paid`.
   - **Partial payment.** The item pays less than the balance, and the invoice becomes
     `PartiallyPaid` with the rest still due.
   - **Overpayment.** The item pays the balance, and the rest becomes **unapplied credit** on the
     sponsor's account. The invoice balance never goes negative.
   - **Exceptions queue** for each of these:
     - no reference (`MissingReference`);
     - another qualifier, such as AZ or 1L (`UnsupportedReferenceQualifier`);
     - no invoice with that number (`InvoiceNotFound`);
     - only voided or written-off invoices (`InvoiceClosed`);
     - several open invoices with the number (`AmbiguousReference`);
     - an invoice of another group than REF*38 or `group_number` names (`GroupMismatch`).
   - Zero-amount items are skipped with a warning.
3. Money left over after the items is queued as `UnallocatedRemainder`.

For every posted batch (not `NotPosted`), applied + unapplied credit + exceptions = payment
amount. Each invoice payment records the batch, the item line, the trace number and the member
(individual remittance).

## Failures, retries and concurrency

- **Resume.** The batch is saved after each item. If posting fails part-way, the batch stays
  `Processing` with the items done so far, and the upload reports that payment under `rejected`
  (the rest of the file still posts). Uploading the file again resumes the payment at the next
  item. Every step is idempotent: a payment already on the invoice (same batch and line), a credit
  entry already on the account, and an exception already queued (its id is batch + line + reason)
  are not added again.
- **Invoices** are saved with optimistic concurrency: a version in Mongo, the ETag in Cosmos. A
  save of a stale copy fails, and cash application re-reads the invoice and recomputes the
  exact/partial/overpayment split from the fresh balance.
- **Other invoice writers re-read and re-apply on a conflict** (`InvoiceWrites.UpdateWithRetryAsync`,
  up to 5 attempts):
  - manual payments, voids and mark-sent: a conflict that persists returns **409** to the caller;
  - EFT settlement and ACH returns: settling an already Settled draft records its payment if it is
    missing, so a settle that failed after saving the draft can be retried;
  - delinquency runs: a conflicting invoice is re-evaluated and skipped if a payment took it out of
    overdue. A failing invoice is reported in `invoiceFailures` (HTTP 207) and the run continues.
- **Sponsor accounts** use the same optimistic concurrency, with retry.

## Exceptions queue

A person resolves each open item once:

- `ApplyToInvoice` posts it with the same rules, so any excess becomes unapplied credit;
- `CreditSponsorAccount` adds it to a group's unapplied credit. The group must have invoices or an
  account, so a mistyped group number is refused;
- `Dismiss` closes it, for example when the money was returned to the payer.

The resolver claims the item first with a conditional Open → Resolving update, so two people
acting at once cannot both post it. If posting fails, the item returns to Open. The resolver and
the note are recorded.

## Balances

- **Invoice:** `TotalPaid`, `BalanceDue` and the status are saved with every applied item.
- **Sponsor account** (`SponsorAccount`, one per group):
  - `OpenInvoiceBalance` is recomputed from the group's open invoices after posting, after a
    manual payment, and when an invoice is voided;
  - `UnappliedCredit` grows with overpayments and with exceptions credited to the group;
  - `NetBalance` = open − credit;
  - `LastPaymentAt` is the date of the last payment.

## ar-service cash postings

`POST /api/v1/ar/cash-postings/{id}/apply` used to set only the posting's status and
`AppliedAmount`, and no AR balance ever changed.

**Apply** now credits each application's `ArBalance` with a `CashReceipt` posting entry. The entry
moves `TotalCredits`, the sponsor or member credit split, and `ClosingBalance`.
- Every referenced balance must exist and belong to the application's GL account. All of them
  are checked before anything is written.
- Entry ids are fixed per posting and application (`cash-{posting}-{index}`). Applying again, or
  retrying after a failed save, does not credit twice.

**Void** of a partially applied posting debits back what the original entries credited. The
reversal id is `rev-{entry}`, so a retried void does not reverse twice.

**Balance saves** are versioned. A concurrent change is re-read and the credit is applied on top
of it. **Create** clears any client-supplied posted state (`PostedEntryId`, `PostedAt`,
`AppliedAmount`).

**Postings applied before this change (legacy).** A posting the old code applied has
applications without `PostedEntryId`, which were never credited. Finance may already have
corrected those balances by hand, so crediting them now could credit a balance twice.

- **Which postings:** status `PartiallyApplied` or `Applied`, an application with an amount, and
  no `PostedEntryId` on any application (`CashPostingLedger.IsLegacy`).
- **Guard:** ar-service refuses to apply or void such a posting. It returns 409
  `LegacyPostingRequiresReconciliation` until finance has reviewed it.
- **Reconciliation:** finance marks each application `CORRECTED_MANUALLY` or `APPLY_CREDIT`,
  and `tools/ArLegacyPostingReconciliation` carries the decisions out, with an audit record per
  application:
  - `CORRECTED_MANUALLY` sets the sentinel id `manual-{posting}-{index}`. Apply never credits
    that application and void never debits it.
  - `APPLY_CREDIT` posts the same `cash-{posting}-{index}` entry that apply would.
- **This is a deploy blocker.** See
  [AR legacy posting reconciliation](../operations/AR-LEGACY-POSTING-RECONCILIATION.md).

## Storage

MongoDB collections `RemittanceBatches`, `RemittanceExceptions` and `SponsorAccounts`. On Cosmos DB,
these are containers of the same names, partitioned by `/tenantId` like `PremiumInvoices`. They must
exist before the endpoints are used.

## Next steps

- **BAI2.** Only the CSV lockbox format is read. A BAI2 (or lockbox transmission) reader would
  produce `RemittanceAdvice` objects and use the same `CashApplicationService`.
- **Netting negative items.** Payments with a negative item are held whole. Netting them by
  invoice or group could be automated later.
- **Using unapplied credit.** Credit is held on the sponsor account. It is not yet applied
  automatically to the next invoice or refunded.
- **GL posting.** premium-billing does not yet post its cash receipts into ar-service.
- **Acknowledgments.** No 999 or TA1 is returned for an inbound 820.
- **ADX amounts** are recorded but not posted as invoice adjustments.
- **Payer-to-group check.** Payer ids are not mapped to groups; the group check relies on REF*38
  or the lockbox `group_number` column when present.
