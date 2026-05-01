# Network &amp; Credentialing Enforcement (Capability 5.6)

Status: 5.6 — Claims Phase 1
Services: `src/services/provider-service`, `src/services/claims-service`
Depends on: Provider 5.6 (event-sourced credentialing), BP 5.5 (NetworkTier
as Organization reference), BP 5.10 (`HttpProviderIntegrityGate` cached-or-live
exemplar), Claims 5.5 (adjudication pipeline)
Related: `claim-adjudication-pipeline.md`, `credentialing-workflow.md`,
`network-tier-organization-reference.md`,
`integrity-score-consumption.md`

## Why this capability

The 5.5 adjudication pipeline shipped with five stub stages awaiting
real implementations. 5.6 is the deferred ChatGPT-flagged enforcement
work — the consumer that finally exercises the as-of-date capability
of `CredentialingProjector` (Provider 5.6) and the cross-service
membership-lookup contract that BP 5.5's `IOrganizationLookupClient`
explicitly deferred.

Two enforcement gates land at adjudication time, both keyed on the
**billing provider NPI**:

1. **Network membership** — is the billing provider an active member
   of one of the resolved benefit plan's tiers, on the claim's service
   date?
2. **Credentialing status** — is the billing provider Approved (per
   the event-sourced credentialing chain) on the claim's service date?

Both checks anchor to the **earliest service date** across the claim's
header and lines (Decision 3 — most-restrictive interpretation; protects
against credentialing gaps mid-claim).

## Cross-service surface

### provider-service additions

#### `GET /api/v1/networks/{id}/members/{npi}`

Single-membership lookup. Sibling to the existing
`/api/v1/networks/{id}/roster` endpoint.

| Param | Source | Required | Notes |
|---|---|---|---|
| `id` | route | yes | Network organization id |
| `npi` | route | yes | Billing provider NPI |
| `asOf` | query | no | ISO-8601 UTC; defaults to UtcNow |

**Status codes:**

- `200 OK` with `IsActiveMember=true` when a participation row covers
  `asOf`.
- `200 OK` with `IsActiveMember=false` when a participation row exists
  but the supplied date falls outside the `[EffectiveDate, TerminationDate)`
  window. `ParticipationStatus` distinguishes `terminated`, `future`,
  `inactive`.
- `404 Not Found` only when no participation row for the NPI exists in
  the network at all.
- `400 Bad Request` on missing/invalid `npi` route segment.

Body-shaped status (200 with a boolean) keeps the consumer-side cache
key stable across the active/inactive distinction — a single 5-minute
TTL covers both branches without cache-key churn at the boundary.

#### `GET /api/v1/providers/{id}/credentialing/status-as-of`

Sibling action on the existing `CredentialingController` (already
provider-rooted at `/api/v1/providers/{id}/credentialing/...`).

| Param | Source | Required | Notes |
|---|---|---|---|
| `id` | route | yes | Provider id (chain key) |
| `asOfDate` | query | yes | ISO-8601 UTC; treated as UTC if Kind=Unspecified |

Returns a trimmed projection (`CredentialingStatusResponse`) — drops
the operational fields (`CurrentApplicationEventId`,
`ApplicationSubmittedAt`, `EventCount`, `LatestVersion`) that belong
to the admin `/status` and `/history` surfaces.

The underlying `CredentialingProjector.Project(events, asOf)` already
supports arbitrary `asOf` (Provider 5.6 was built specifically for this
consumer). The new method is a 4-line sibling of `GetCurrentStatusAsync`
that passes the caller-supplied date through.

### claims-service additions

| Type | Role |
|---|---|
| `IProviderMembershipClient` | HTTP client over the membership endpoint |
| `HttpProviderMembershipClient` | Live-fetch implementation |
| `CachingProviderMembershipClient` | 5-minute in-process TTL decorator |
| `ICredentialingStatusClient` | HTTP client over the credentialing endpoint |
| `HttpCredentialingStatusClient` | Live-fetch implementation |
| `CachingCredentialingStatusClient` | 1-hour in-process TTL decorator |
| `NetworkCredentialingStage` | Replaces `NetworkCredentialingStubStage` |
| `EnforcementOutcome` | Per-check audit-grade outcome |
| `NetworkEnforcementMode` / `CredentialingEnforcementMode` | Policy enums |
| `TenantEnforcementPolicyOptions` | Bound from `Adjudication:Enforcement:*` |
| `UpstreamClientNames` | Constants class for named HTTP clients |

## Time-anchor semantics

The stage uses **`min(claim.ServiceDateFrom, claim.ClaimLines[*].ServiceDateFrom)`**
as the authoritative `asOf` for both checks. Rationale:

- A claim line predating the header is operationally rare but legal
  (claim spans multiple service dates; header captures the latest).
- Earliest-wins is the conservative interpretation — a provider
  credentialed on May 10 doesn't auto-pay a line dated May 1 just
  because the claim header is dated May 15.
- The policy is testable: see
  `NetworkCredentialingStageTests.EarliestServiceDate_picks_min_across_header_and_lines`.

## Network resolution from plan tiers

`ResolvedBenefitPlan` carries the plan's `NetworkTier[]`, populated by
`HttpBenefitPlanResolver` from `BenefitPlan.networkTiers` on the wire.
Each tier carries `{ TierName, TierLevel, NetworkId? }`. The stage:

1. Sorts tiers by `TierLevel` ascending (1 = best).
2. Skips tiers whose `NetworkId` is null (legacy-shape rows still in
   the BP 5.5 → hard-validation rollout window).
3. For each remaining tier, calls `IProviderMembershipClient`.
4. **First active match wins** — the matched tier is recorded on
   `ClaimAdjudicationContext.MatchedNetworkTier` for downstream
   consumption.

If no tier matches, the claim is out-of-network. The stage applies the
configured `NetworkEnforcementMode` and **skips the credentialing
check** — out-of-network providers' credentialing status is irrelevant
to the enforcement decision in Phase 1.

## Fail-mode policy matrix

| Mode | Behavior on failed check |
|---|---|
| `FailClosed` (default in production) | Stage emits `Deny`; pipeline short-circuits to `PersistenceStage` |
| `FailOpen` | Stage emits `Pend`; pipeline continues so subsequent stages can decorate before human review |
| `SoftValidation` | Stage emits `Observe` outcome on the audit trail; final result is `Pass`; useful during initial rollout |

Both `NetworkMode` and `CredentialingMode` are independent. Phase 1 is
service-wide; per-tenant override is deferred to Phase 2 alongside
`AdjudicationPipelineOptions`.

`appsettings.json` defaults to `FailClosed` for production; `Development`
defaults to `SoftValidation` so local pipelines don't block on a
disconnected provider-service.

## Caching shape

Both clients mirror BP 5.10 `HttpProviderIntegrityGate`'s pattern:

- `IMemoryCache` per pod (no distributed invalidation; coherence via
  TTL expiry).
- Cache keys namespaced by resolution path (`cached-or-live` vs
  `force` for force-refresh callers).
- Day-bucketed `asOf` so re-evaluating the same date doesn't churn
  cache keys but different service dates resolve independently.
- Negative results (`null` from upstream — degraded transport) are
  NOT cached. The "definitively not a member" 404 path produces a
  non-null `NetworkMembership` with `IsActiveMember=false` which IS
  cached normally.

| Domain | TTL | Why |
|---|---|---|
| Membership | 5 minutes | Roster row terminations don't emit explicit events; shorter TTL bounds staleness window |
| Credentialing | 1 hour | Transitions are explicit, audit-trailed events on the projection chain; longer TTL is operationally safe |

## Pipeline integration

The stage replaces `NetworkCredentialingStubStage` directly in DI
registration. It preserves the stub's `Order = 200`, `Name = "NetworkCredentialing"`,
`IsRequired = false` so:

- The orchestrator iteration order is unchanged.
- The per-tenant `AdjudicationPipelineOptions.EnabledStages["NetworkCredentialing"]`
  config keeps working unchanged across the swap.
- A 5.5 deployment that disabled the stub disables the real stage too —
  the convention 5.5 documented holds across capability replacements.

```
┌─────────────────────────────────────────────────────────┐
│ Order  Stage                       Disposition           │
├─────────────────────────────────────────────────────────┤
│  100   ScrubbingStubStage          stub (5.4 replaces)   │
│  200   NetworkCredentialingStage   ← 5.6 ships this      │
│  300   BenefitCalculationStage     real (5.5)            │
│  400   NcciEditsStubStage          stub (5.7 replaces)   │
│  500   CoordinationOfBenefitsStub  stub (5.8 replaces)   │
│  600   AiExaminationStubStage      stub (5.9 replaces)   │
│  999   PersistenceStage            real (5.5, required)  │
└─────────────────────────────────────────────────────────┘
```

`BenefitCalculationStage` reads `context.MatchedNetworkTier` for
cost-share tiering in a follow-up PR (Phase 2 — 5.5 currently
hard-codes `NetworkTier.InNetwork` per the comment in
`BenefitCalculationStage`).

## Telemetry

| Metric | Dimensions |
|---|---|
| `cho.claims.enforcement.cache` (counter) | `cho.client` (`ProviderMembership` / `Credentialing`), `cho.path` (`hit` / `miss` / `live` / `force`) |
| `cho.claims.enforcement.outcome` (counter) | `cho.check` (`membership` / `credentialing`), `cho.outcome` (`allow` / `deny` / `pend` / `observe`) |
| `cho.claims.enforcement.degraded` (counter) | `cho.client`, `cho.mode` |

Telemetry wiring follows the BP 5.10 `HttpProviderIntegrityGate`
pattern; the namespaces share the `cho.claims.*` root with the existing
`cho.claims.adjudication.outcome.total` counter from 5.5.

## Recovery posture

- **provider-service endpoint regression** — caught by the
  provider-service test suite; revert restores the prior endpoint
  surface.
- **Stage replacement breaks pipeline** — caught by
  `ClaimAdjudicationOrchestratorTests` from 5.5 + the new stage and
  end-to-end tests.
- **Cache returns stale data during outage** — bounded by TTL
  (5-minute membership, 1-hour credentialing); manual cache clear via
  pod restart.
- **Provider-service degraded** — `FailClosed` produces predictable
  denial behavior; `FailOpen` preserves availability via human-review
  pend; `SoftValidation` preserves throughput at the cost of audit-only
  enforcement.
- **Wrong service date used as `asOf`** — caught by integration tests
  with mixed line-level service dates.
- **Network tier traversal misorders** — caught by the stage's
  first-tier-wins test (`FirstTierMatches_skips_lower_priority_tiers`).

Worst-case rollback: revert this PR. The stub stage class remains on
disk; re-registering it in `Program.cs` restores 5.5 baseline behavior
without a redeploy of provider-service. No data changes; no migration
to undo.

## Out of scope

- **Rendering provider NPI enforcement** — Decision 9 limits 5.6 to
  billing-NPI checks. Rendering provider relationships are 5.4
  (scrubbing) and 5.7 (NCCI) territory.
- **Per-tenant policy override** — deferred to Phase 2 alongside the
  same surface for `AdjudicationPipelineOptions`.
- **Real network-tier cost-share routing** — `BenefitCalculationStage`
  still hard-codes `NetworkTier.InNetwork`; 5.6 populates
  `context.MatchedNetworkTier` so the follow-up PR can wire it
  through.
- **OON credentialing enforcement** — out-of-network providers'
  credentialing is not checked. If pilot operations surface a need,
  add a separate capability that introduces an NPI→providerId lookup
  for the credentialing client.
