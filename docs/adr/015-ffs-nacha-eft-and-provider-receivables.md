# ADR 015: FFS NACHA CCD+ EFT And Provider Receivables

## Status

Proposed

Relates to ADR 014 (FFS payment vouchers and carrier payment configuration),
which is still under review in PR #1187 and is not on `main`. This ADR adds no
voucher or carrier model. It fills two Phase 1 gaps in the payment path that
already exists, and it follows the constraints ADR 014 records: one 835 = one
BPR = one TRN, and the CCD+ addenda carries the 835 TRN (CAQH CORE 370).

## Context

- Capitation has a NACHA credit file (`capitation-service` `NachaCreditFileService`,
  which is plain CCD with no addenda). FFS payment runs had no EFT file at all.
- A reversal run whose 835 nets below zero emits BPR02 = 0 with a negative
  PLB FB. Before this change it recorded the amount only on the run
  (`ReversalRun.OutstandingReceivables`), and nothing recovered it.
- Full provider bank numbers live only in provider-service, behind dual
  control (a proposed change has to be approved by a second user with
  `payments:approve`). Capitation reads them through a service-only endpoint
  that does not return the TIN.

## Decision

### 1. NACHA CCD+ credit file per FFS payment run

- `FfsNachaCreditFileBuilder` is pure and deterministic. It produces one batch:
  service class 220, SEC CCD, entry description `HCCLAIMPMT`, and company id =
  `Era:OriginatingCompanyId` (the 835 TRN03). Any other `Nacha:CompanyId` is
  refused. Each payee gets one type 6 entry followed by one type 7 addenda
  (type 05) holding `TRN*1*{TRN02}*{TRN03}\`. Records are 94 characters and the
  file is blocked by 10. The entry hash and the totals are computed from the
  entries.
- `FfsEftFileService` (`POST /api/paymentruns/{id}/eft-file`, `payments:approve`,
  service tokens refused) makes one credit per **payee TIN + approved account,
  per 835 trace**. The credit adds up that payee's payments and is net of
  receivable offsets, so it equals the payee's share of BPR02.
- Accounts come only from provider-service's new service-only endpoint
  `GET /api/v1/internal/providers/npi/{npi}/payee-account`. Only the
  `payment-service` client may call it. It returns the active approved account
  and the TIN recorded with that account. The capitation endpoint is unchanged.
- Check fallback is decided **at execution**, before the 835 is generated. A
  payee with no approved account, with EFT disabled, or with no TIN gets a CHK
  payment and a CHK BPR04, and is listed in `PaymentRun.CheckFallbacks`. If the
  account has gone by the time the file is generated, the payment is listed
  with `NeedsAttention` (its 835 already said ACH). If provider-service gives
  no answer, nothing is paid or generated.
- Idempotency: the file creation time is the run's execution time (to the
  minute), the effective date is the run's payment date, and both the file
  reference and the file name are derived from the run number. The first
  generation pins the SHA-256 on the run (`PaymentRun.EftFile`, last four
  digits only). A later generation must reproduce those bytes or it gets 409.
- Transmission is **not** wired yet. The next step is to hand the built file
  to the shared `CloudHealthOffice.NachaTransmission` dispatcher, as capitation
  does.

### 2. Provider receivable ledger

- `ProviderReceivableRecord` is stored in Mongo `ProviderReceivables`, and in
  memory on the dev-only Cosmos path, like `EraEnvelopes`. The id is derived
  from the origin 835, so recording the same 835 twice does nothing. It keeps
  the original, outstanding and recovered amounts, a status, and an
  append-only `Entries` history (Originated / Recovered / RecoveryReversed,
  each with run, payment, trace, time and approver). Every write is
  conditional on `Version`.
- A reversal 835 with a negative net opens a receivable against the PLB01
  provider, with reference = that 835's TRN02.
- A payment run recovers before each payment is inserted, oldest receivable
  first, up to the payment amount. Each recovery adds a positive PLB (`FB`, or
  `WO` via `Receivables:RecoveryAdjustmentCode`) referencing the origin trace,
  and lowers `TotalPaymentAmount`. BPR02 and the EFT credit drop by the same
  amount, and CLP04 stays the same. The receivable never goes negative:
  `ApplyRecovery` refuses more than is outstanding, and concurrent runs retry
  against the current version. If the payment insert fails, the recovery is
  reversed with its own ledger entry.
- `GET /api/receivables`, `/api/receivables/{id}` and `/api/receivables/aging`
  (buckets 0-30 / 31-60 / 61-90 / 91-120 / 120+) require `payments:read`.

### 3. ar-service: integration point, not wired

ar-service is the premium and member GL ledger (`ArBalance`, `ArAdjustment`,
`CashPosting`, keyed by GL account and period). It has no provider subledger
and no service-to-service posting endpoint, so there is no clean way to wire
it in this change. The integration point is the ledger's entries. Each
`Originated` and `Recovered` entry (with run, payment and trace) is the event
an AR posting needs: debit provider receivable / credit claims expense on
origination, and credit receivable / debit cash-clearing on recovery. When
ar-service gets a provider-receivable GL account and a service-client posting
endpoint, payment-service can publish these entries, keyed by `EntryId` so a
retry does not post twice.

## Consequences

- An 835 per trading partner can mix payees. When it does, an EFT credit no
  longer equals that 835's BPR02, and the run gets a warning. ADR 014's
  voucher model (one payee per 835) removes this case.
- If the service crashes after the ledger write but before the payment
  insert, the ledger is left with a `Recovered` entry whose payment does not
  exist. The entry is visible, but no job reconciles it yet.
- A receivable whose PLB01 NPI never receives another payment stays open. It
  shows up in the aging report, and refund requests or write-offs are follow-up
  work.
