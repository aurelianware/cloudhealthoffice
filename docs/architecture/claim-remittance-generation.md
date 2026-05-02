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
   │   1. Fetch Approved claims via /api/claims/search            │
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
N1*PE — Payee identification (1000B loop)
[2100 loop — repeated per claim]
   CLP  — Claim header (status code, amounts)
   NM1*QC — Patient (when MemberId present)
   NM1*82 — Rendering provider (when NPI present)
   DTM*050 — Claim received date (when present)
   CAS  — Claim-level adjustments (header CAS from CarcRarcMapper)
   [2110 loop — repeated per service line]
      SVC  — Service payment
      DTM*472/473 — Service dates
      CAS  — Line-level adjustments (per-line CAS from CarcRarcMapper)
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
