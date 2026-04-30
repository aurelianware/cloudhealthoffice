# Claim Submission API

## Why

Capability 5.3 ships the canonical V1 surface for claim submission.
Before 5.3, the only submission path was the legacy
`POST /api/claims` on `ClaimsController`, which:

- Accepted the internal domain `Claim` model directly (coupling
  callers to internal shape changes)
- Emitted no `ClaimVersionEvent` — the version-event chain shipped
  in 5.1a had zero production consumers
- Had no tenant-routing seam, so future vendor adapters had no
  hook on the write path

5.3 introduces `POST /api/v1/claims` accepting `AdapterClaim` (the
vendor-neutral DTO from 5.2) and orchestrating: structural
validation → adapter call → `ClaimVersionSubmitted` event
emission. The legacy `POST /api/claims` is marked `[Obsolete]` and
internally routes through the same orchestration path so the
audit chain has no gaps for legacy-submitted claims while the
endpoint remains operational. Capability 5.13 (Phase 1 closer)
removes the legacy controller entirely.

## Topology

```
                     ┌───────────────────────────────────┐
   POST /api/v1/claims   │                                   │   POST /api/claims
   (AdapterClaim,        │                                   │   (Claim, [Obsolete],
    canonical)           ▼                                   ▼   Deprecation header)
                ┌────────────────────┐         ┌────────────────────┐
                │ ClaimsV1Controller │         │  ClaimsController  │
                │       (5.3)        │         │     (legacy)       │
                └─────────┬──────────┘         └─────────┬──────────┘
                          │  AdapterClaim                │ Claim → AdapterClaim.From()
                          ▼                              ▼
                          ┌──────────────────────────────────┐
                          │     IClaimSubmissionService      │
                          │     (canonical orchestrator)     │
                          └──────────────────┬───────────────┘
                                             │
                       ┌─────────────────────┼─────────────────────┐
                       ▼                     ▼                     ▼
              Validate (structural)   IClaimAdapter.SubmitClaimAsync   IClaimVersionEventPublisher
                                       (tenant-routed via factory)     .PublishVersionSubmittedAsync
                                                                       (Mongo append-only)
```

## Interface

```csharp
public interface IClaimSubmissionService
{
    Task<ClaimSubmissionResult> SubmitAsync(
        AdapterClaim claim,
        string tenantId,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default);
}
```

The result is structured rather than exception-based on
validation failure so the controller maps to HTTP status codes
without catching exceptions on the happy path:

```csharp
public class ClaimSubmissionResult
{
    public bool Success { get; set; }
    public AdapterClaim? Claim { get; set; }                  // populated on success
    public ClaimSubmissionFailureKind? FailureKind { get; set; }
    public IReadOnlyList<ValidationError> Errors { get; set; } = Array.Empty<ValidationError>();
}

public enum ClaimSubmissionFailureKind
{
    Validation = 1,        // → controller maps to 400
    NotImplemented = 2,    // → controller maps to 501
}
```

## Validation set

5.3's submission service validates structural shape only.
Eligibility checks, code coherence, member-plan validity, NCCI
prevalidation, and COB checks are the scope of capability 5.4
(Pre-Adjudication Scrubbing). 5.4 will wire `ClaimsScrubEngine`
into the submission flow as a pipeline stage between the adapter
call and the event emission; the submission service shape is
deliberately narrow so 5.4's refactor stays small.

| Field | Rule | Error code |
|---|---|---|
| `MemberId` | non-empty | `Required` |
| `BillingProviderNPI` | non-empty | `Required` |
| `ServiceDateFrom`/`ServiceDateTo` | from <= to (when both set) | `InvalidDateRange` |
| `ClaimLines` | at least one line | `MinCount` |
| `ClaimLines[i].ProcedureCode` | non-empty per line | `Required` |
| `TotalChargeAmount` | recomputed as sum of line charges | (silent overwrite) |

Caller-supplied `TotalChargeAmount` is overwritten by the
computed sum before validation runs — same semantic as the legacy
controller pre-5.3.

## Event emission semantics

On successful submission, the service emits a
`ClaimVersionEvent` of type `ClaimVersionSubmitted` via
`IClaimVersionEventPublisher.PublishVersionSubmittedAsync`. The
publisher (5.1a) is the system-of-record for the audit chain;
events are appended to a Mongo `ClaimVersionEvents` collection
with a deterministic `EventId="submitted:{Id}"` for idempotency.

5.3 preserves the 5.1a payload shape — `versionId`,
`versionNumber`, `claimNumber`, `submittedDate`. Expanding the
payload is **not** in 5.3's scope; the first downstream consumer
is capability 5.5's `ClaimAdjudicationOrchestrator`, and any
payload-shape decision is properly made when 5.5 declares its
trigger needs.

### Degraded-mode posture

The Mongo claim row in the main store is the system of record
for the claim itself; `IClaimVersionEventPublisher` is the
notification stream for the audit chain. If event emission fails
(Mongo outage, persistent index conflict), the submission service
**logs loudly and returns success** — same posture as the Kafka
`IClaimEventPublisher` documented at
`Services/ClaimEventPublisher.cs:18-22`. Operators see the error
log; the audit chain may have a gap for the affected claim that
can be backfilled from logs if needed.

The submission's success contract is "the canonical claim row
exists in the store"; the audit-chain emission is best-effort
notification on top of that.

## Tenant routing

Submission goes through `ClaimAdapterFactory.GetAdapterAsync(tenantId)`
which consults tenant-service configuration cached for 5 minutes.
For the current production tenant set the factory always resolves
to `ChoClaimAdapter` — a near pass-through over `IClaimRepository`.

Tenants configured for `qnxt`, `facets`, or `healthedge` resolve
to stub adapters that throw `NotImplementedException`. The
submission service catches this and returns:

```csharp
ClaimSubmissionResult.AdapterNotImplemented(...)
```

…which the controller maps to `501 Not Implemented` with a
structured error body:

```json
{
  "error": "Claim submission is not implemented for this tenant's configured platform",
  "errors": [{ "field": "", "code": "AdapterNotImplemented", "message": "..." }]
}
```

`501` is the semantically correct status for "this tenant's
configuration points at a vendor adapter we don't support yet."
`500` would conflate with bug-shaped failures; `503` would
conflate with transient unavailability.

## Legacy deprecation path

The legacy `POST /api/claims` continues to function until
capability 5.13 removes it. To keep the audit chain continuous
during the deprecation window:

1. The endpoint is marked `[Obsolete("Use POST /api/v1/claims …")]`
2. Every response carries `Deprecation: true` and
   `Link: </api/v1/claims>; rel="successor-version"` per
   RFC 8594 / RFC 9745
3. The controller method internally calls
   `IClaimSubmissionService.SubmitAsync(...)` — same orchestrator
   the V1 endpoint uses. The legacy controller maps `Claim →
   AdapterClaim` via `AdapterClaim.From(claim)`, calls the
   service, and maps the response back via `result.Claim.ToClaim()`
   for the 201 contract pre-existing callers depend on. The 5.2
   round-trip mapper is loss-less per
   `SubmitClaimAsync_round_trips_AdapterClaim_losslessly`.

The `Sunset` header is intentionally omitted until 5.13 schedules
removal; emitting `Sunset` with a placeholder date would be
misleading.

## What stays unchanged

- **Domain `Claim` model** — 5.1a settled
- **`IClaimRepository`** — interface and implementations
  unchanged (5.1a)
- **`IClaimAdapter`** — interface unchanged (5.2)
- **`AdapterClaim` round-trip mappers** — unchanged (5.2)
- **`IClaimVersionEventPublisher`** — interface and implementation
  unchanged (5.1a). Payload shape decision deferred to capability
  5.5 when there's a real consumer
- **Kafka `IClaimEventPublisher`** — `ClaimPendedEvent` and
  `ClaimFinalizedEvent` continue to emit from PUT paths exactly
  as before; submission emission is Mongo-only
- **`accumulator-service` consumer contract** — unchanged
- **`ClaimAcknowledgmentService`** — 277CA generation stays
  pull-shaped; event-driven 277CA emission is deferred to a
  Phase 2 enhancement when there's a real consumer
- **837 inbound parser** — not introduced in 5.3; Phase 2
  capability when a real customer integration requires it
- **`ClaimsV1Controller` GET response shape** — `EobSearchResponse`
  preserved exactly. Adapter migration is an internal-only
  change; portal Member Details dialog sees an identical
  contract
- **`IExplanationOfBenefitProjector`** — accepts domain `Claim`
  (unchanged); the V1 GET round-trips `AdapterClaim → Claim` via
  `.ToClaim()` before projection. Capability 5.11 may evolve the
  projector to consume `AdapterClaim` directly

## Cross-references

- `claim-versioning.md` — `ClaimVersionEvent` chain semantics
  (5.1a)
- `claim-adapter-pattern.md` — tenant-routed adapters (5.2)
- `adjudication-api-stabilization.md` — Phase 1 closer scope
  (5.13 removes the legacy controller)
