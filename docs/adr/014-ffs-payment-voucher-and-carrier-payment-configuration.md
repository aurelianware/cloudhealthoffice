# ADR 014: FFS Payment Vouchers And Carrier Payment Configuration

## Status

Proposed

## Context

The FFS payment run was reviewed against the QNXT process it is modelled on:
an operator defines criteria (line of business, provider, claim, date range),
the system selects claims that are ready to pay, pays them, and the claims are
stamped with a check number as their pay status transitions.

### What already exists

Most of that process is built, and it is built in the right place.

| Capability | Where | State |
| --- | --- | --- |
| Criteria-driven claim selection | `PaymentRunCriteria` (LOB, provider NPI, service/submission date ranges, min/max amount, include/exclude claim IDs, member IDs, `GroupByProvider`, `MaxClaimsPerPayment`) | Complete |
| Payment run execution | `PaymentRunService` | Complete |
| Claim stamped and finalized | `POST /api/claims/{id}/remittance` → `IClaimFinalizationService` — stamps CheckNumber, PaymentDate, PayerPayment, PaymentRunId, EraEnvelopeId; Adjudicated → Paid; emits `ClaimVersionPaid` and `claims.finalized.v1` | Complete |
| Finalization safety | Idempotent on repeat CheckNumber; 409 on CheckNumber mismatch; 422 when not Paid-eligible | Complete |
| Claim void | `ReversalRunService` → cross-service void; `Voided` / `AlreadyVoided` | Complete |
| Reversal accounting | `ClaimAdjustment` rows in `PendingReversal` → 835 reversal envelopes, `CLP02="22"`, CAS sign-flipped | Complete |
| Rate resolution | `CloudHealthOffice.FeeScheduleEngine` | Complete |

Finalized claims are never mutated; corrections flow through reversal
adjustment claims so the accounting holds. That principle is already honoured
by the reversal path and this ADR does not change it.

`ffs-service` is a stub — one model (`FfsRateConfig`), no controllers, no
repositories, no tests, no Dockerfile, absent from the solution file and from
every CI and deploy matrix (`docs/deployment/KNOWN-GAPS.md`). Its model
docstring describes it as "a placeholder for the full FFS rate engine", which
is out of date: that engine was built as `CloudHealthOffice.FeeScheduleEngine`
and is consumed by `benefit-plan-service`. **No part of the work in this ADR
belongs in `ffs-service`.**

### What is missing

Four gaps, none of which is a missing service.

**1. The voucher is not a first-class record.** `Payment` carries the check
number and an embedded `ClaimPayments[]` array, and has no `PaymentRunId`. The
run linkage lives on `EraEnvelopeRecord` and on the claim. There is no
queryable per-claim payment record, so "every claim on voucher X and what it
paid" means unpacking embedded arrays across documents, and there is nothing at
voucher granularity to void, reissue or reconcile.

**2. `Payment` has no outbound lifecycle.** `PaymentStatus` is
`Received / Validated / Posted / Reconciled / Exception` — an inbound 835
posting lifecycle, reused for outbound payments. It cannot express a cheque
that was issued, cleared, stopped or voided.

**3. There is no payment configuration.** `TradingPartnerInfo.PayerRoutingNumber`
and `PayerAccountNumber` exist but are set nowhere in `Program.cs` or
`appsettings.json`. `PaymentMethod` is a bare `string` defaulting to `"ACH"`.
There is no Carrier entity, no LOB-level configuration, and no precedence
framework anywhere in the codebase — the only override chain is
`contract line → contract default → plan default`, hand-written with `??`
fallbacks inside `RateResolutionService`.

**4. `payment-service` has no Stripe rail**, although provider payout via
Stripe already exists in `capitation-service`.

### What the 835 forces

The 835 header carries exactly one `BPR` (one amount, one method) and exactly
one `TRN` (one trace). They are Table 1 header segments, not loops.

**One 835 = one payment = one payee = one trace number.**

`BPR07`/`BPR08` are the payer's routing and account — the funding account is a
per-voucher attribute that must be resolved *before* the 835 is generated.
`BPR10` and `TRN03` are the Originating Company Identifier.

Under CAQH CORE 370, the 835's `TRN02` must match the trace in the ACH CCD+
addenda so the provider can reassociate the deposit with the remittance
automatically. For CCD entries carrying the healthcare indicator, health plans
**must** include an addenda record containing the 835 TRN segment.

Industry practice is to send separate remittances per line of business, and to
pay on payee NPI/Payee ID plus TIN.

## Decision

### 1. A provider paid across two lines of business receives two payments and two 835s

This is the industry default and the standard pushes toward it. Consolidating
payment across LOBs while sending separate remittances would break
reassociation, because one instrument cannot carry two trace numbers.

### 2. Model the voucher explicitly

```
PaymentRun ──< PaymentBatch ──< PaymentVoucher ──< PaymentVoucherDetail
                (per rail:        (per payee per      (per claim)
                 NACHA file,       carrier+LOB;
                 check file,       = one 835
                 Stripe batch)     = one TRN)
```

- `PaymentVoucher` is what today's `Payment` already is in 835 terms: one
  payee, one amount, one trace, one 835.
- `PaymentVoucherDetail` is the reconciliation unit — the record joined from
  the claim side, rendered in a provider portal, and operated on by a void.
- `PaymentBatch` is the funding event the rail produces. It is the only level
  at which "one payment, many vouchers" is true, and only because a NACHA file
  contains many CCD+ entries, each its own trace.

Both voucher and detail are **append-only once the voucher is Issued.**

Outbound payments get their own lifecycle — `Draft → Issued → Settled`, with
`Voided`, `Stopped`, `Failed` — kept separate from the inbound `PaymentStatus`.
The payer-side outbound voucher and the provider-side 835 posting are different
aggregates that happen to share the 835 content shape.

### 3. Introduce `Carrier`, and resolve payment configuration by scope

`Carrier` is synonymous with a healthcare payer and has multiple lines of
business. It becomes a first-class entity because `BPR07`/`BPR08` (funding
bank), `N1*PR` (payer name and identifier) and `TRN03`/`BPR10` (originating
company identifier) are all carrier-level facts that must be stable and
auditable. `Tenant` cannot carry them: a tenant is the CHO subscriber and may
operate several carriers.

Configuration resolves most-specific-wins, **per field**, so a sponsor can
override the rail while still inheriting the carrier's funding account:

```
Carrier ──> LineOfBusiness ──> SponsorPolicy ──> Provider
```

Provider sits last deliberately: a provider who requests CCD+ EFT must win over
any carrier default (see below).

Fields: funding account reference, default rail, payer ID and name, check
number sequence (per carrier, not per run), and whether to split ERA per LOB
(default true).

**This is a narrow, typed resolution for payment configuration only — not a
general rules engine.** QNXT's carrier-rules machinery is decades of
accumulated generality and reproducing it is not warranted.

### 4. Extract the payout rail rather than rebuild it

`capitation-service` already has the mature rail model: `DisbursementMethod
{ NachaAch, StripeConnect, Check }`, `AchTraceNumber`, `StripeTransferId`,
`NachaFileReference`, `CheckNumber`, `SubmittedAt`, `ExpectedSettlementDate`,
ACH return codes, and `StripeConnectService` with transfer creation,
cancellation, reversal and webhook processing.

Extract it into `CloudHealthOffice.Infrastructure` behind an
`IProviderPayoutRail`. FFS and capitation then pay providers through one code
path, one webhook handler and one reversal path.

Funding account numbers are stored as **Key Vault secret references**, not
values. `premium-billing-service` already stores only last4 of routing and
account; `payment-service`'s `TradingPartnerInfo` holds full account numbers,
and that asymmetry should not be multiplied by per-carrier configuration.

Note the direction difference: `premium-billing-service` *pulls* (Stripe
PaymentIntent) to collect premium; capitation *pushes* (Stripe Transfer). FFS
is a push, so capitation is the model to follow.

### 5. Stripe-paid providers receive an 835, with a stated limitation

Under HIPAA, electronic remittance advice must be the 835, so the real choice
is "835 or nothing". Providers cannot post without one. **Send it.**

`BPR04 = ACH` when funds genuinely reach the provider's bank by ACH. `TRN02`
carries a real, resolvable trace, and the same value is surfaced to the
provider out of band — remittance portal, email, statement descriptor.

**The limitation, stated plainly:** with Stripe Connect, Stripe is the ACH
originator, not the payer. Stripe's payout Trace ID is described in their
documentation as an identifier banking partners create, retrievable after the
fact — not a field the payer populates. No evidence was found that Stripe
supports supplying CCD+ addenda content on payouts. **This is an open question
to put to Stripe directly, recorded here as a risk rather than an assumption.**

If it is not supported, then for Stripe-paid providers the reassociation trace
cannot ride with the payment, those providers reassociate manually, and CHO is
not conformant with the CORE EFT rule for those payments.

Therefore: **Stripe is an opt-in convenience rail and can never be the only
rail.** NACHA ACH remains available for any provider who requests CCD+ EFT,
which is why Provider sits last in the resolution order. Stripe must not be
positioned as CORE-compliant provider EFT.

## Consequences

Positive:

- The voucher becomes queryable, reconcilable and voidable at the granularity
  operators and providers actually work at.
- Carrier-level identity is configured once and audited, rather than defaulted
  per call site.
- One payout rail implementation serves FFS and capitation, including reversal.
- Splitting per carrier and LOB produces conformant, reassociable remittances.

Tradeoffs:

- `Carrier` is a new entity that several services must become aware of.
- Voucher and detail as separate records cost writes and storage against the
  current embedded array.
- The Stripe rail ships with a known conformance gap that has to be disclosed
  to providers rather than papered over.
- A per-field resolution chain is more machinery than a flat config, and will
  be tempting to generalise. It should not be generalised beyond payment.

Not addressed here:

- Post-settlement recoupment (QNXT takeback) against future vouchers, as
  distinct from pre-settlement stop-pay. These are different operations and
  need their own decision.
- Whether `ffs-service` is deleted or reduced to owning `FfsRateConfig`.
  `FfsRateConfig.FeeScheduleId` and `FeeSchedulePercentage` duplicate the
  engine's `ProviderContract.DefaultFeeScheduleId` and `ProviderContractLine`,
  and the engine reads its own store — so an `FfsRateConfig` written today
  would be ignored by pricing. That duplication needs resolving before
  anything is built there.

## References

- `src/services/payment-service/Services/PaymentRunService.cs` — run execution
- `src/services/payment-service/Services/EraGeneratorService.cs` — 835 generation
- `src/services/payment-service/Services/ReversalRunService.cs` — void and reversal
- `src/services/capitation-service/Models/CapitationDisbursement.cs` — the rail model to extract
- `src/services/claims-service/Services/ClaimFinalizationService.cs` — the Paid transition
- `src/engines/CloudHealthOffice.FeeScheduleEngine` — rate resolution
- [CAQH CORE Payment & Remittance (CCD+/835) Reassociation Rule](https://www.caqh.org/sites/default/files/core/Payment-Remittance-Reassociation-CCD-835-Rule.pdf)
- [CMS — EFT and ERA Payment Remittance Reassociation Basics](https://www.cms.gov/files/document/eft-and-era-payment-remittance-reassociation-basics.pdf)
- [X12 RFI 2374 — Originating Company Identifier](https://x12.org/resources/requests-for-interpretation/rfi-2374-originating-company-identifier)
- [ADR 011](011-rules-and-evidence-model.md) — rules and evidence model
