# Claim Remittance Generation (Capability 5.10)

> **Status — Phase 1, May 2026.** Operator-initiated PaymentRun
> execution produces batched 835 ERAs, persists envelopes, and
> finalizes claims via a structured cross-service contract. First
> production transition of `ClaimVersionState.Submitted/Adjudicated → Paid`
> through the version-event chain. First production consumer of 5.7's
> `SuggestedCarc`/`SuggestedRarc` fields. See
> [`claim-adjudication-pipeline.md`](./claim-adjudication-pipeline.md)
> for the upstream pipeline that produces the Adjudicated state this
> capability acts on. **5.12b extends the `BatchEraGeneratorService`
> + `EraEnvelopeRecord` infrastructure to operator-initiated 835
> reversal envelopes** — see [`claim-reversal-run.md`](./claim-reversal-run.md)
> for the reversal-mode mechanics. PaymentRun continues to operate
> unchanged; reversal-mode is opt-in via `EraPaymentInput.IsReversal`.

## Why this exists

Before 5.10, claims that completed adjudication sat in `Approved` /
`PartiallyPaid` state with no path to `Paid`. The 5.1a `Paid`
`ClaimVersionState` value, the 5.7 `SuggestedCarc`/`SuggestedRarc`
fields, and the existing `payment-service` infrastructure
(`PaymentRunService`, `EraGeneratorService`,
`PaymentRunsController`) were all in place. What was missing:

- A **batched 835 generator** that aggregates N claims per
  trading-partner envelope (per-claim mode is hostile to provider
  workflows; providers expect one ERA per remittance run, not N)
- A **structured finalize contract** between payment-service and
  claims-service that advances the version-event chain, is
  idempotent on the second call, and rejects invalid source states
  (the existing `POST /remittance` endpoint did the legacy
  direct-write but skipped the version chain and had no idempotency)
- A **CARC/RARC mapping precedence rule** that consumes 5.7's
  per-line edit suggestions, falling back through claim-level denials
  to standard adjudication-time CARCs

5.10 ships these three surfaces additively. The Phase 1 boundary is
deliberate: 837 inbound parsing, sFTP transmission of generated
envelopes, 277 ack chaining, multi-envelope-per-file packing, and
per-claim 835 mode all stay deferred. 5.10 ships the EDI string;
trading partner transmission is Phase 2.

## Workflow shape

```
Operator: POST /api/payment-runs/execute   (or /api/payment-runs/{id}/execute)
                              │
                              ▼
   ┌──────────────────────────────────────────────────────────────┐
   │  PaymentRunService.ExecutePaymentRunAsync                    │
   │   1. Fetch Approved (and Denied) claims via /api/claims/search│
   │      (claims-service returns full Claim model with           │
   │       AdjudicationResult, PendDetails.EditFailures,          │
   │       ServiceLines)                                          │
   │   2. Filter post-fetch by SubmissionDate / amount /          │
   │      include / exclude / member criteria                     │
   │   3. Group claims by billing-provider NPI                    │
   │   4. Resolve trading partner per NPI via                     │
   │      ITradingPartnersClient (run-scoped cache)               │
   │   5. For each provider group: build Payment with             │
   │      ICarcRarcMappingService-applied CAS data,               │
   │      ServiceLine SVCs, ProviderAdjustments                   │
   │   6. IBatchEraGeneratorService.GenerateBatch(...)            │
   │      → one EraEnvelope per trading partner                   │
   │   7. Persist each EraEnvelopeRecord (Mongo / in-memory)      │
   │   8. For each claim: POST /api/claims/{id}/remittance        │
   │      → IClaimFinalizationService.FinalizeAsync               │
   │      → ClaimVersionPaid event + ClaimFinalizedEvent (Kafka)  │
   └──────────────────────────────────────────────────────────────┘
```

## Cross-service contract

### `POST /api/claims/{id}/remittance` (claims-service)

Existing endpoint refined by 5.10. Accepts `RemittanceUpdate`:

```json
{
  "controlNumber": "<paymentRunNumber>",
  "checkNumber": "0000001234",
  "paymentDate": "2026-05-04T00:00:00Z",
  "paymentAmount": 800.00,
  "paymentRunId": "<runId>",
  "eraEnvelopeId": "<envelopeId>"
}
```

**Non-zero `paymentAmount`** delegates to
`IClaimFinalizationService.FinalizeAsync` for the
Approved/PartiallyPaid → Paid transition. **Zero `paymentAmount`**
stays on the legacy direct-write Denied path until 5.12 introduces
the dedicated Denied-transition flow.

Outcomes:

| Outcome | HTTP | Behaviour |
|---|---|---|
| `Finalized` | 200 | Status → Paid; VersionState → Paid; `ClaimVersionPaid` event + Kafka `claims.finalized.v1` emitted |
| `AlreadyFinalized` | 200 | Same CheckNumber arrives twice; idempotent no-op (no UpdateAsync, no event re-emit) |
| `Conflict` | 409 | Different CheckNumber on already-Paid claim; structured error body |
| `InvalidSourceState` | 422 | Source not Approved/PartiallyPaid; structured error body |
| `NotFound` | 404 | Claim id unknown for tenant |

Idempotency relies on `(claim.Status, claim.AdjudicationResult.CheckNumber)`
serving as the natural key. The repository's terminal-state guard
(`Paid` is terminal) prevents accidental UpdateAsync calls — the
service short-circuits before reaching the repo when the claim is
already `Paid`.

### `GET /api/tradingpartners/by-npi/{tenantId}/{npi}/{environment}` (trading-partner-service)

New endpoint added by 5.10. Returns the `TradingPartner` whose
`BillingProviderNpis` list contains the given NPI. 404 when no match
exists. Multiple matches return the first by insertion order from
`GetByTenantAsync` (operator-configuration error surface; not
deduplicated server-side).

### `GET /api/v1/era-envelopes/{id}` and `/edi` (payment-service)

New read-only endpoints. Metadata projection (no inline EDI body) for
list and single-record GETs; `text/plain` raw EDI for the `/edi`
sub-resource. No regenerate / cancel / transmit endpoints — once
generated, an envelope is immutable.

## Batched 835 envelope structure

One ISA/IEA file per trading partner (Phase 1 simplification — no
multi-envelope-per-file). One ST/SE envelope per file. N CLP loops
per envelope, one per claim in the batch for that partner.

```
ISA  — Interchange control header                (control number = ticks[-9:])
GS   — Functional group header
ST   — Transaction set header (835)
BPR  — Financial information (envelope-wide sum)
TRN  — Reassociation trace number (first claim's check number)
DTM  — Production date
N1*PR — Payer identification (1000A loop)
N1*PE — Payee identification (1000B loop): the billing provider
        (837 2010AA name, N103 = XX, N104 = billing NPI). 5010 has no
        pay-to provider name or NPI: 837 2010AB is an address only
        (X12 RFI 1522/1606), and the NPI on the claim flows to the 835
        (X12 RFI 1559). N102 is cut to 60 characters.
N3 / N4 — Payee address: the 837 2010AB pay-to address, only when
        every payment in the envelope is to that payee at that address
        (omitted otherwise). Claims naming a 2010AC pay-to plan
        (subrogation demand) are left out of payment runs
        (PaymentRun.PayToPlanClaimIds): the plan, not the provider NPI,
        is the entity to be paid (X12 RFI 1107).
[2100 loop — repeated per claim; order per 005010X221A1]
   CLP  — Claim header (status code, amounts)
   CAS  — Claim-level adjustments (header CAS from CarcRarcMapper)
   NM1*QC — Patient (when MemberId present)
   NM1*82 — Rendering provider (when NPI present)
   MIA / MOA — Claim remark codes (RARCs): MIA05 + MIA20-23 for an
          institutional (837I) claim, MIA01 = 0; MOA03-07 otherwise
   DTM*050 — Claim received date (when present)
   [2110 loop — repeated per service line]
      SVC  — Service payment
      DTM*472/473 — Service dates
      CAS  — Line-level adjustments (per-line CAS from CarcRarcMapper)
      LQ*HE — Line remark codes (a CAS carries no RARC; CAS04 is a quantity)
PLB  — Provider-level adjustments (when batch carries PLB rows)
SE   — Transaction set trailer (count includes ST and SE)
GE / IEA  — Functional group / interchange trailers
```

## CARC/RARC mapping precedence (Decision 6)

`CarcRarcMappingService` consumes a `ClaimAdjudicationSnapshot`
(detached from claims-service DLL coupling) and emits
`ClaimAdjustment` (header CAS) and per-line
`ServiceLineAdjustment` lists.

1. **Standard adjudication adjustments** from
   `AdjudicationResult.AdjustmentReasons` always emit at the header
   (PR-1 deductible, PR-2 coinsurance, PR-3 copay, CO-45 contractual)
2. **Header denial** from `AdjudicationResult.DenialReasonCode`
   appends to header CAS only when no entry already carries that
   reason code (avoid double-CAS for the same reason)
3. **Per-line edit failures** from `PendDetails.EditFailures`
   (5.7-populated) emit at 2110 keyed by `AffectedLineNumbers`,
   carrying `SuggestedCarc` (CARC) and `SuggestedRarc` (RARC, when
   present)

Fallback CARC `237` (mirrors 5.11 EOB projector default) only fires
when an edit failure has `SuggestedCarc=null`. The fallback never
overrides an explicit CARC from the precedence chain above.

### Line-level CAS (2110) and balancing

`Era835ClaimPaymentBuilder` turns the mapper output and the claim into
the 835's adjustments:

- A line remitted with an SVC loop carries its own CAS: the line's
  adjudication adjustments (`claimLines[].adjudicationResult.adjustmentReasons`,
  filled by claims-service: CO-45, PR-1/2/3, OA-23, CO denial CARCs),
  grouped by group code, up to six reason/amount/quantity triplets per
  CAS (the quantity is empty: `CAS*CO*45*100.00**253*50.00`). A RARC on
  an adjustment (`remarkCode`, read when claims-service sends it) goes in
  `LQ*HE` after the line's CAS.
- NCCI edit CARCs for the line are added only when the line does not
  already carry that group and CARC; they never double count the money,
  and every distinct RARC of a matching edit still reaches `LQ*HE`.
- **Fallback for lines without adjustment detail** (claims adjudicated
  before claims-service populated line adjustments): on a single-line
  claim the claim-level adjustments become that line's CAS; whatever
  SVC02 - SVC03 is still unexplained goes to the adjustment carrying the
  line's NCCI edit CARC (matched by group and CARC) if it has one, else to
  one `CO-45` (on a denial, CO with the denial CARC).
  So every line balances, at the cost of reporting member cost share on
  such older multi-line claims as CO-45.
- When the lines carry CAS, the claim-level adjustments (the claim's
  totals of the same amounts) are not repeated in the header; only a
  denial CARC no line carries stays there. A claim remitted at claim
  level (no SVC) keeps the claim-level adjustments in its header CAS.
- On a denial the header entry with the denial CARC, whatever amount it
  arrived with, carries what the other adjustments leave unexplained.

Generation checks, once lines carry CAS, that every line satisfies
SVC02 - sum(line CAS) = SVC03 (a line without CAS must then be paid in
full) and the claim CLP03 - sum(CAS, claim and lines) = CLP04; for a
claim remitted at claim level (no SVC) with header CAS, that CLP03 -
sum(CAS) = CLP04. It throws otherwise. A claim whose SVC loops carry no CAS
(payments recorded before line CAS, which keep the claim-level CAS in the
header) is not checked. A payment run checks the same before reserving: a claim that
would fail is not paid and is listed in `UnbalancedServiceLineClaimIds`.

### Reversals (CLP02 = 22)

A reversal's claim and line loops are the original remittance's, built
by `Era835ClaimPaymentBuilder.BuildReversal` from the claim payment
payment-service recorded for the predecessor:

- CLP02 = `22`; CLP03 (charge), CLP04 (paid) and CLP05 (patient
  responsibility) negated; every claim-level CAS amount negated.
- Each SVC repeats the original line with SVC02 and SVC03 negated and its
  CAS amounts negated, so the line still balances in the negative
  (`SVC*HC:99213*-200.00*-120.00**1~CAS*CO*45*-50.00~CAS*PR*1*-20.00**2*-10.00~LQ*HE*N130`).
  Remark codes (MOA/MIA, `LQ*HE`), units, dates and identifiers are
  repeated unchanged; a zero amount stays `0.00`.
- The adjustments are the recorded ones when the original carried them
  (its lines carry CAS, or, remitted at claim level, its header does).
  An original recorded before line CAS existed gets the adjustments the
  builder derives from the predecessor claim, priced at the recorded
  charge and paid amounts, with the fallbacks above: a single-line claim
  takes the claim-level CAS; any other line gets its NCCI edit CARC, else
  `CO-45` (CO with the denial CARC on a denial, CLP02 = 4). So reversals
  of payments made before line CAS still generate.

The reversal run applies the same per-line and per-claim check before
reserving: a reversal whose adjustments would not balance is not
recouped and is listed in `ReversalRun.UnbalancedServiceLineClaimIds`,
and 835 generation refuses it; this includes a claim-level reversal whose
header CAS does not explain the charge. Stored reversal payments from
before this change (positive CLP03, no line CAS) are not checked, so their
835s still regenerate.
Denials are never recorded as payments, so a reversal run reverses only
paid claims; a recorded denial (CLP02 = 4) would reverse with CLP04 =
0.00 and its denial CAS negated.

## Check number allocation

A PaymentRun allocates **one check number per trading partner envelope**.
When `GroupByProvider=true` and multiple providers route to the same
trading partner, all of their `Payment` records share that single check
number — keeping the envelope's BPR/TRN consistent with every CLP loop's
finalize CheckNumber. Provider groups whose NPI doesn't resolve to a
trading partner allocate their own check (preserves legacy per-payment
semantics for the `PaymentsController` GET 835 endpoint) but are
excluded from envelope emission and from finalization. The
`PaymentRun.Warnings` collection captures these "no trading partner
configured" cases so an operator can fix the configuration and re-run.

`PaymentRun.CheckNumberStart` / `CheckNumberEnd` reflect the contiguous
range actually allocated by the run — start equals the first allocated
number, end equals `NextCheckNumber - 1`.

## Trading partner resolution (Decision 14)

5.10 adds a `BillingProviderNpis: List<string>` field to
`TradingPartner`. Operator configures which NPIs route to which
partner; payment-service resolves per-NPI via the new lookup
endpoint. Run-scoped resolution cache (no global singleton — each
PaymentRun execution is a fresh batch and the cardinality of unique
NPIs per run is bounded).

When no trading partner is configured for an NPI, that claim's
payment is generated and persisted (operator visibility), but
**excluded from batched 835 emission** with a warning recorded on
the PaymentRun (`Warnings` list). The claim is **not finalized**
until trading partner config is fixed and the run is re-executed —
the operator-initiated workflow is intentional friction here.

BPR banking detail (`PayerRoutingNumber`, `PayerAccountNumber`,
`PayeeRoutingNumber`, `PayeeAccountNumber`) is **not surfaced on
the trading-partner-service API in Phase 1**. Those flow from
payment-service `IConfiguration` (`Era:Payer*` / `Era:Payee*` keys),
overridden per trading partner only via env-scoped deployment
configuration. Phase 2 may surface bank fields on TradingPartner.
An ACH BPR also needs BPR10, the originating company identifier
(`Era:OriginatingCompanyId`: exactly 10 characters, typically `1` +
the payer's TIN; optional BPR11 `Era:OriginatingCompanySupplementalCode`,
9 characters). TRN03 carries the same value and is required for every
payment method (CHK and NON too); it is never synthesised from the payer
id. When it is missing, or `Era:PayerRoutingNumber` is set and any ACH BPR
field is missing, payment and reversal runs fail before reserving or
paying any claim, and generation throws, rather than emitting a
misaligned BPR or a made-up TRN03. Payment method "Check" (any case) is
emitted as CHK.

A zero-pay ERA (BPR02 = 0) is notification only: BPR01 = H and BPR04 =
NON whatever the run's payment method, with no bank details.

A claim with a negative payerPayment is never paid
(`PaymentRun.NegativePlanPaidClaimIds`); zero-pay claims are paid as zero.
A claim with no service lines, or whose lines carry no paid amount
(claim-level-only adjudication), is paid and remitted at claim level, CLP
without SVC loops (see the service-line rule below).

Every generated 835 must balance: BPR02 = sum of CLP04 - sum of PLB, and
for a claim with service lines, sum of SVC03 = CLP04; otherwise generation
throws.

The BPR/TRN element layout and these configuration checks live in
`CloudHealthOffice.Infrastructure.Edi.Era835FinancialSegmentBuilder`, shared
with capitation-service's `CapitationEraService`, which therefore also needs
`Era:OriginatingCompanyId` (BPR10/TRN03; optional
`Era:OriginatingCompanySupplementalCode` for BPR11) and fails
`POST /api/v1/capitation/statements/{id}/era` with 500 without it.

The amount paid (CLP04) is the plan's payment, claims-service's
`adjudicationResult.payerPayment` (published as PlanPaid, finalized by
ClaimFinalizationService); never the allowed amount and never the billed
charge. CLP03 is `totalChargeAmount`; CLP05 is
`adjudicationResult.patientResponsibility`. The run searches
`status=5` (claims-service Approved; payment-service's `ClaimStatus`
mirrors claims-service's numeric values) and refuses any other status the
search returns. A claim without an adjudication result / payer payment is
never paid: it is excluded before reservation, stays Approved, and is
listed in `PaymentRun.MissingPlanPaidAmountClaimIds`.
A line's SVC03 is its paid amount (`claimLines[].adjudicationResult.paidAmount`),
never its charge. Service lines are remitted by this rule:

| Lines' paid amounts | 835 |
|---|---|
| no service lines, or **no** line carries a paid amount | claim-level remittance: CLP (CLP04 = payer payment) with no SVC loops (005010X221A1: the 2110 loop is situational) |
| every line carries one and they add up to CLP04 | CLP + one SVC per line |
| some lines carry one; with 0 for the others they add up to CLP04 | CLP + one SVC per line, SVC03 = 0 for the unpriced lines |
| anything else | not paid: excluded before reservation, listed in `PaymentRun.UnbalancedServiceLineClaimIds` |

The benefit engine, MPIP and the repository's financial normalization all
set payerPayment = sum of line paid amounts, so engine-adjudicated claims
remit with SVC loops; claims adjudicated only at claim level (adjudication
or inbound remittance endpoints that set payerPayment without line results)
remit at claim level. Either way CLP04 still adds up to BPR02.

### Denials

Providers receive a remittance for denials too. With
`PaymentRunCriteria.IncludeDeniedClaims` (default `true`) a run also
searches `status=6` (claims-service Denied, with the run's other criteria)
and remits each denied claim not yet remitted as a zero-pay claim in the
same trading partner's 835:

- CLP02 = `4` (denied), CLP03 = total charge, CLP04 = `0.00`, CLP05 =
  patient responsibility.
- Header CAS from the claim's adjudication (`adjustmentReasons`, then
  `denialReasonCode` as `CO`); the denial CARC carries the charge no other
  adjustment explains, so CLP03 - CLP04 = the CAS amounts.
- RARCs (`adjudicationResult.remarkCodes`, at most five; a header CAS
  cannot carry a RARC) in MIA05/MIA20-MIA23 for an institutional claim
  (claims-service `claimType` = 2), MOA03-MOA07 otherwise.
- Lines follow the table above with CLP04 = 0: a denied claim whose lines
  carry no paid amount is remitted at claim level; one whose lines pay more
  than 0 is not remitted and is listed in `UnbalancedServiceLineClaimIds`.

No Payment, no claim reservation and no claims-service call is made for a
denial (Denied is final in claims-service). A denial adds 0 to BPR02, and a
partner's envelope keeps its payment's check number as TRN02. A partner with
only denials gets a non-payment 835: BPR01 = `H`, BPR02 = `0.00`, BPR04 =
`NON` (whatever the run's payment method; every zero-amount 835 is NON),
BPR16 = payment date, TRN02 = `<run number>-D<n>`.

A denial is remitted once: a denied claim already listed in a non-reversal
835 envelope (`EraEnvelopeRecord.ClaimIds`), or holding a payment, is
skipped silently. Remitted denials are listed in
`PaymentRun.RemittedDeniedClaimIds`. A denial without a denial reason code
or any adjustment reason is not remitted (no CARC is made up) and is listed
in `PaymentRun.DeniedWithoutReasonClaimIds`; one whose provider has no
trading partner is listed in `NeedsTradingPartnerClaimIds`. Both are picked
up by a later run once corrected.

Limits: the "already remitted" check reads the envelope store; there is no
reservation, so two runs executing at the same moment can both remit the
same denial (no money moves; the provider receives the denial twice).
claims-service's search returns at most 1000 claims per status, newest
submitted first, and Denied claims never leave that status, so scope runs
(service or submission dates) when the Denied backlog is large. The first
run after this ships remits every earlier denial its criteria match; set
`IncludeDeniedClaims = false` or narrow the dates to avoid sending that
backlog.

A reversal recoups the amount payment-service recorded for the
predecessor's original claim payment (claim and line amounts and CAS,
negated; see [Reversals](#reversals-clp02--22)), never its approved or
billed amount. With no single recorded payment the
claim is not reversed (`ReversalRun.MissingPaidAmountClaimIds`); with an
unbalanced recorded payment, `ReversalRun.UnbalancedServiceLineClaimIds`.

BPR02 is never negative (the shared builder refuses it). An 835 whose
claims and PLBs net below zero (a reversal recoupment, or payments and
reversals netting negative for one partner) is BPR01 = `H`, BPR02 = `0.00`,
BPR04 = `NON`, with a PLB forward-balance adjustment (`FB`, reference = the
835's trace number, amount = the negative net) so that
sum(CLP04) - sum(PLB) = BPR02 = 0. The balance owed is recorded on the
envelope (`ForwardBalanceAmount`) and the reversal run
(`OutstandingReceivables`, `OutstandingReceivableAmount`); there is no
receivable ledger to recover it from a later 835 yet (follow-up).
Capitation 835s with a negative NetPayable follow the same rule. In a
capitation 835 each member's CLP04 is net of the withhold (CAS CO-45), so
the withhold is not also a PLB; statement adjustments are PLBs with the
sign flipped (a credit to the provider is a negative PLB, a recoupment a
positive one), and generation refuses a statement whose
sum(CLP04) - sum(PLB) is not BPR02. Its 2100 loop is CLP, CAS, NM1*QC,
DTM*232 (period start; 150 is a 2110 qualifier), AMT, QTY.

## Persistence shape (Decision 4 / 15)

`EraEnvelopeRecord` lives in a separate `EraEnvelopes` MongoDB
collection. `EdiContent` stored inline. Typical 835 envelope size:

| Claims in envelope | Approx EDI bytes |
|---|---|
| 1 | ~0.5 KB |
| 50 | ~25 KB |
| 200 | ~100 KB |
| 500 | ~250 KB |

All well under the MongoDB 16MB document limit. Phase 2 may move to
blob storage if envelope sizes grow or if retention rules favor blob
lifecycle policies.

Tenant scoping mirrors `Payment` / `PaymentRun` (TenantId filter on
every query, set by the repository from request context). No
documentType discriminator — separate collection per type matches
the existing payment-service pattern.

## Telemetry (Decision 8 — adjusted)

The Plan-First gate flagged that the prompt's Decision 8 referred to
a `claim-version-events` Service Bus topic that does not exist.
ClaimVersionEvents are persisted to the Mongo `ClaimVersionEvents`
collection (system-of-record); downstream notification flows through
the Kafka `claims.finalized.v1` topic. 5.10 telemetry surfaces:

```
cho.payment.run.execute{outcome="success|partial|failed"}
cho.payment.run.claims_count
cho.payment.run.envelopes_count
cho.payment.era.batch_generation{outcome="success|failed"}
cho.payment.era.envelope_size_bytes
cho.payment.era.claims_per_envelope
cho.payment.finalize_call{outcome="success|conflict|invalid_state|error"}
cho.claims.finalization.transition{from="Approved|PartiallyPaid", to="Paid"}
cho.claims.version_events.append{type="ClaimVersionPaid"}
cho.claims.kafka.emit{topic="claims.finalized.v1"}
```

Phase 1 wires the `ILogger`-based observability (LogInformation per
envelope, per finalize, per partner-resolution miss). OpenTelemetry
metric counters land in 5.13 (Phase 1 closer) when the `cho.*`
namespace gets its full audit pass.

## Phase 2 deferred work

- 837 inbound parser
- sFTP / Availity transmission of generated 835 envelopes (5.10
  produces the EDI string; transmission is separate)
- 277 ack chaining for 835 transmission
- Multi-envelope-per-file packing
- Per-claim 835 mode (current per-payment `EraGeneratorService`
  retained for backward-compat and audit/replay paths)
- Resubmission / correction workflow (5.12 Adjustment Workflow)
- Trading partner BPR banking field surface
- Blob-storage envelope persistence with retention policies

## Integration with prior capabilities

| Capability | Integration |
|---|---|
| 5.1a (Versioning) | First production `Paid` transition through the version chain |
| 5.5 (Adjudication pipeline) | PersistenceStage hands off Adjudicated claims; 5.10 finalizes from Approved/PartiallyPaid |
| 5.7 (NCCI edits) | First production consumer of `SuggestedCarc`/`SuggestedRarc` |
| 5.8 (CoB) | Phase 2 — pended CoB claims stay out of finalize scope |
| 5.9 (AI examination) | No direct consumption (operator-initiated workflow per Decision 1) |
| 5.11 (FHIR EOB) | EOB projection automatically reflects post-finalize Paid state on next read |
| accumulator-service | Receives Kafka `claims.finalized.v1` for Paid transitions; updates member-year accumulators |
| 5.12 (Adjustment Workflow) | Builds on `Paid` as a stable terminal state for the predecessor chain |
| 5.13 (Phase 1 closer) | Documents `POST /remittance`, `POST /api/payment-runs/*`, `GET /api/v1/era-envelopes/*` as canonical V1 surface |

## Recovery posture

- **Batched 835 produces invalid EDI**: caught by
  `BatchEraGeneratorServiceTests` SE01 segment-count assertion;
  revert restores per-payment `EraGeneratorService` path
- **Finalize endpoint causes infinite loop or double-emit**:
  caught by `ClaimFinalizationServiceTests.AlreadyFinalized*`
  tests; revert removes the service registration cleanly
- **Cross-service finalize call fails silently**: caught by
  `PaymentRunServiceBatchedTests.FinalizesEachClaim*` tests with
  explicit response code assertions; PaymentRunService aggregates
  failures into `PaymentRun.Warnings`
- **TradingPartner resolution miss**: caught by
  `PaymentRunServiceBatchedTests.UnresolvedTradingPartner_AddsWarning`;
  per-run warnings array captures partial failures
- **CARC/RARC precedence wrong**: caught by
  `CarcRarcMappingServiceTests` with explicit fixture coverage of
  each precedence branch
- **EraEnvelope persistence size exceeds Mongo limits**: not
  expected at Phase 1 batch sizes; Phase 2 sizing review will
  introduce blob storage if needed
- **Existing per-claim Generate835 broken by refactor**: preserved
  by Decision 3; `EraGeneratorServiceTests` remain green

Worst-case rollback: revert this PR. claims-service
`POST /remittance` reverts to legacy direct-write semantics;
PaymentRunService restored to per-claim flow; EraEnvelope
persistence path removed; `PaymentRun.EraEnvelopeIds` preserved as
harmless empty list. No data changes; no migration.
