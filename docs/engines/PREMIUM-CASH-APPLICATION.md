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
| POST | `/api/v1/sponsor-accounts/{groupNumber}/refresh` | finance:write | Recompute the open balance from the group's invoices |

An upload answers with `batches` (the payments it recorded), `duplicates` (payments that were
already recorded) and `rejected`. A file that cannot be parsed returns 400, and nothing from it
is posted. Files are limited to 5 MB.

## 820 mapping (005010X218)

| Segment | Used for |
|---|---|
| ISA | Element separator (position 4) and segment terminator (position 106) |
| ST / SE | One payment per transaction set; a SE01 count mismatch is a warning |
| BPR01 | Handling code. C, D, U and X post cash. I (remittance only) and P (prenote) are recorded as `NotPosted` |
| BPR02 / BPR04 / BPR16 | Payment amount, method (ACH, CHK, FWT…), payment date |
| TRN02 / TRN03 | Trace number; originating company id (the payer id) |
| N1*PR | Payer name; N104 is the payer id when TRN03 is absent |
| ENT | Loop 2000A (organization summary) or 2000B (individual: ENT02 = 2J) |
| NM1*IL | Member name and id (NM109) in individual remittance |
| RMR | RMR01 qualifier, RMR02 reference (matched to the invoice number), RMR04 paid, RMR05 billed |
| DTM*582 | Coverage period of the preceding RMR |
| ADX | Adjustment (amount, reason) reported with the preceding RMR. It is recorded, not posted |

A transaction set without BPR or TRN, or with an amount or date that cannot be read, rejects the whole file.

## Lockbox CSV

The header row names these columns, in any order: `batch, item, deposit_date, check_number,
payer_id, payer_name (optional), check_amount, invoice_number, amount`. Each row is one
remittance stub. Rows that share a batch and item are one check, and `check_amount` must be the
same on every row of the check. The check's trace number is `LBX-{batch}-{item}`. The
lockbox goes through the same application as the 820.

## Application rules

Each payment is recorded first, as a `RemittanceBatch`. Its id is a hash of the tenant, source,
payer and trace number, so the same payment cannot be recorded twice. Re-uploading a file
reports its payments as duplicates and posts nothing.

1. If the items total more than the payment, nothing is applied. The payment goes to the
   queue as `DetailExceedsPayment`.
2. Each item is matched on its reference to an invoice number, then:
   - **Exact match.** The item pays the balance, and the invoice becomes `Paid`.
   - **Partial payment.** The item pays less than the balance, and the invoice becomes
     `PartiallyPaid` with the rest still due.
   - **Overpayment.** The item pays the balance, and the rest becomes **unapplied credit** on the
     sponsor's account. The invoice balance never goes negative.
   - **Exceptions queue** for each of these:
     - no reference (`MissingReference`);
     - no invoice with that number (`InvoiceNotFound`);
     - only voided or written-off invoices (`InvoiceClosed`);
     - several open invoices with the number (`AmbiguousReference`);
     - a zero or negative amount (`NonPositiveAmount`).
3. Money left over after the items is queued as `UnallocatedRemainder`.

For every posted batch (not `NotPosted`), applied + unapplied credit + exceptions = payment amount. Each invoice payment
records the batch, the item line, the trace number and the member (individual remittance).

## Exceptions queue

A person resolves each open item once:

- `ApplyToInvoice` posts it with the same rules, so any excess becomes unapplied credit;
- `CreditSponsorAccount` adds it to a group's unapplied credit;
- `Dismiss` closes it, for example when the money was returned to the payer.

The resolver and the note are recorded.

## Balances

- **Invoice:** `TotalPaid`, `BalanceDue` and the status are saved with every applied item.
- **Sponsor account** (`SponsorAccount`, one per group):
  - `OpenInvoiceBalance` is recomputed from the group's open invoices after posting;
  - `UnappliedCredit` grows with overpayments and with exceptions credited to the group;
  - `NetBalance` = open − credit;
  - `LastPaymentAt` is the date of the last payment.

  Writes use optimistic concurrency (a version in Mongo, the ETag in Cosmos) and retry,
  so concurrent postings are not lost. A payment recorded by hand (`POST /premium-invoices/{id}/payments`)
  refreshes the account as well.

## ar-service cash postings

`POST /api/v1/ar/cash-postings/{id}/apply` used to set only the posting's status and
`AppliedAmount`, and no AR balance ever changed. It now credits each application's
`ArBalance` with a `CashReceipt` posting entry. The entry moves `TotalCredits`, the
sponsor or member credit split and the balance, and `ClosingBalance`. Every referenced
balance must exist and belong to the application's GL account, and all of them are checked
before anything is written. Each application records the entry it posted, and the entry id
is fixed per posting and application, so applying again or retrying after a failed save does
not credit twice. Voiding a partially applied posting debits its credits back.

## Storage

MongoDB collections `RemittanceBatches`, `RemittanceExceptions` and `SponsorAccounts`. On Cosmos DB,
these are containers of the same names, partitioned by `/tenantId` like `PremiumInvoices`. They must
exist before the endpoints are used.

## Next steps

- **BAI2.** Only the CSV lockbox format is read. A BAI2 (or lockbox transmission) reader would
  produce `RemittanceAdvice` objects and use the same `CashApplicationService`.
- **Using unapplied credit.** Credit is held on the sponsor account. It is not yet applied
  automatically to the next invoice or refunded.
- **GL posting.** premium-billing does not yet post its cash receipts into ar-service.
- **Acknowledgments.** No 999 or TA1 is returned for an inbound 820.
- **ADX amounts** are recorded but not posted as invoice adjustments.
