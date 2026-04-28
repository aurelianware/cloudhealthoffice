# NetworkTier as Reference to Organization (Capability 5.5)

> Replaces the legacy embedded NPI snapshot on `BenefitPlan.NetworkTier`
> with a reference to the canonical `Organization` entity in
> provider-service (capability 5.3). This is the realization of the
> "Provider 5.3 unblock" called out in
> [`network-as-organization.md`](./network-as-organization.md).

## Why

Until 5.5, each `BenefitPlan.NetworkTier` carried an embedded
`ProviderNpis: List<string>` snapshot of the providers in that tier.
That shape had three problems:

1. **It didn't scale.** A plan with 50,000 in-network providers
   carried a 50,000-NPI list inside the plan document. Cosmos document
   size limits (2MB), Mongo working-set efficiency, and JSON
   serialization all suffered.
2. **It didn't reference the canonical network.** Provider 5.3 made
   `Organization` (network) a first-class versioned entity in
   provider-service. The `NetworkParticipation.NetworkId` field on
   `Provider` points to it. But `BenefitPlan.NetworkTier` had no link
   to the same `Organization` — the two services had parallel,
   disconnected concepts of "network."
3. **Update propagation was wrong.** When a network's roster changed
   (provider joins/leaves), every `BenefitPlan` referencing that
   network needed its `ProviderNpis` list updated. There was no
   automated propagation; lists drifted from reality.

A repo-wide audit during the 5.5 plan phase turned up a fourth signal:
`tier.ProviderNpis` was never consulted by any production code path.
Adjudication's network-tier dimension is an enum
(`{InNetwork, OutOfNetwork, OutOfArea}`) classified upstream by claims
scrubbing; the `MemberBenefitView` projection picks the lowest-tier
name without touching the NPI list. The embedded list was dormant
data.

5.5 introduces the reference model and treats the legacy field as
exactly that — legacy data preserved during a defined migration
window, then removed.

## What changed

### Model

`NetworkTier` (`src/services/benefit-plan-service/Models/BenefitPlan.cs`):

- Adds `NetworkId: string?` — chain-key reference to
  `Organization.OrganizationId` in provider-service.
- Marks `ProviderNpis: List<string>` `[Obsolete]` and freezes it as a
  legacy data carrier. Removed in a follow-up PR after telemetry
  confirms zero remaining legacy-shape rows.
- Adds matching XML doc that describes the reference semantics, the
  null-NetworkId soft-validation contract, and the migration window.

The wire-format DTO `AdapterNetworkTier`
(`Models/BenefitPlanAdapterResponse.cs`) carries the same field
addition; the `From` / `ToNetworkTier` mappings round-trip both fields
during the migration window.

### Cross-service contract

```text
benefit-plan-service                              provider-service
        │                                                 │
        │  GET /api/v1/networks/{id}                      │
        │  via HttpClient("ProviderService")              │
        ├────────────────────────────────────────────────►│
        │                                                 │
        │  Organization (head Active version)             │
        │◄────────────────────────────────────────────────┤
```

`IOrganizationLookupClient`
(`Services/IOrganizationLookupClient.cs`) is the read seam. It reuses
the `HttpClient("ProviderService")` registration introduced by
capability 5.10 (`HttpProviderIntegrityGate`); no new typed client is
required. The single method —
`GetOrganizationAsync(networkId, ct)` — returns a small projection
(`organizationId`, `name`, `effectiveDate`, `terminationDate`) and is
deliberately resilient: 404, 5xx, and transport failures all surface
as `null` rather than throwing. Callers apply policy.

### What is **not** in scope

The capability ships only the read-side method that the backfill
needs — `GetOrganizationAsync`. A per-claim membership check
(`IsProviderInNetworkAsync`) and its in-process cache are deferred to
the capability that actually consumes them (likely claims-service
Phase 1). That decision was reached during the 5.5 plan phase: with
no current adjudication or member-view consumer of NPI-membership
checks, building cache + telemetry + a provider-service contract
addition for zero present-day callers violates the codebase's stated
YAGNI posture. See "Plan-First record" below.

## Soft validation

Every benefit-plan write surface inspects the supplied tiers and
records a structured warning + a Prometheus counter for each tier
that lacks a `NetworkId`. Mirrors Provider 5.5's panel-gating
soft-validation pattern.

Counter:

```
cho.benefit_plan.network_tier_missing_networkid_writes.total{cho.caller, cho.tenant_id}
```

`cho.caller` values: `CreatePlan | UpdatePlan | CreateDraft |
AmendPublished | PublishAndSupersede`.

Structured warning:

```
NetworkTierNetworkIdMissing on plan write.
  caller=<CreatePlan|UpdatePlan|CreateDraft|AmendPublished|PublishAndSupersede>
  tenantId=<tenant>
  planId=<chain key>
  versionId=<per-version id>
  tierIndex=<int>
  tierName=<operator label>
  tierLevel=<int>
```

Operators dashboard the counter and watch it trend to zero. The
follow-up PR flips soft validation to hard validation (400 with
explanation, counter no longer needs to fire) once telemetry shows
zero soft-warning producers across all tenants for a sustained window
— typically 7+ days.

## Backfill — admin HTTP endpoint

```
POST /api/v1/admin/benefit-plans/backfill-network-tiers?tenantId={X}

{
  "mappings": [
    { "planId": "plan-001", "tierName": "In-Network",     "networkId": "org-aetna-ppo-fl-2025" },
    { "planId": "plan-001", "tierName": "Out-of-Network", "networkId": "org-non-network" },
    { "planId": "plan-002", "tierName": "Preferred",      "networkId": "org-cigna-pref-2025" }
  ]
}
```

### Authorization

Two layers, both required:

1. **Deployment-layer ACL** (NetworkPolicy / gateway). Load-bearing.
2. **Feature flag** `NetworkTierBackfill:AdminBackfillEnabled = true`.
   Defence-in-depth tripwire. When false the controller returns 503
   (not 404) so operators see "endpoint exists, intentionally gated"
   rather than "route was never registered."

### Mapping strategy — operator-driven (Decision 5b)

The request body carries an explicit
`(planId, tierName) → networkId` dictionary. The service does not
auto-resolve from any embedded NPI snapshot. Two reasons:

1. Tenant data quality varies — auto-resolution against
   provider-service requires that the Provider/Organization data is
   already correct in that tenant's CHO instance. For pilot tenants
   newly onboarded, this isn't guaranteed.
2. Since `ProviderNpis` was never consulted in adjudication, a tenant
   may carry seeded-but-stale values that don't reflect the canonical
   roster. Auto-resolution against stale snapshots would compound the
   problem.

Operators submit the mapping intentionally; the service validates
each `networkId` resolves in provider-service (records `unresolved` if
not) before writing.

### Idempotency

The backfill is rerun-safe. A tier with a non-null `NetworkId` is
counted under `skipped` and not re-patched. A rerun therefore
produces a deterministic outcome: previously-mapped tiers stay
mapped, newly-supplied mappings become applied, unresolved or
unmappable tiers stay null and surface in the result issues list.

### Outcomes

```
{
  "backfillRunId": "<guid>",
  "mappingsSubmitted": 3,
  "patched":    2,
  "skipped":    0,
  "notFound":   0,
  "unresolved": 1,
  "failed":     0,
  "issues": [
    {
      "planId": "plan-002",
      "tierName": "Preferred",
      "networkId": "org-cigna-pref-2025",
      "outcome": "unresolved",
      "detail": "Organization not resolvable in provider-service."
    }
  ]
}
```

Counter:

```
cho.benefit_plan.network_tier.backfill.outcomes.total{cho.outcome, cho.tenant_id}
```

`cho.outcome` values: `patched | skipped | not_found | unresolved | failed`.

## Version-immutability bypass

The backfill writes to existing Published `BenefitPlan` rows. The
default `UpdateAsync` path rejects writes against Published versions
(create an amendment). 5.5 adds a sibling repository method that
writes only the `NetworkTiers` collection on the head Active row via
field-scoped patch (Cosmos `PatchItemAsync` `Set("/networkTiers", tiers)`;
Mongo `FindOneAndUpdateAsync` with sort-by-`VersionNumber` and `$set`
for the head-row patch in one round trip) — no PlanVersion row
created, no `PlanVersionEvent` emitted.

Identity-bearing field writes still go through `UpdateAsync` and
respect version-state enforcement. The exemption is documented in
[`plan-versioning.md`](./plan-versioning.md) "Projection metadata —
exempt from versioning"; the framework lifts directly from
[`provider-versioning.md`](./provider-versioning.md).

## Plan-First record

The Plan-First gate that preceded this capability surfaced two
material premise corrections that shaped the final scope:

1. **Adjudication does not consult `tier.ProviderNpis`.**
   `AdjudicationController.cs:884`'s `NetworkTier` property is the
   `CloudHealthOffice.BenefitEngine.Domain.NetworkTier { InNetwork,
   OutOfNetwork, OutOfArea }` enum, not the embedded class. There is
   no `ProviderNpis.Contains(npi)` site anywhere in `src/`.
2. **`MemberBenefitView` projection does not consult
   `tier.ProviderNpis` either.** `BenefitViewService.cs:66-68` selects
   the in-network tier name by tier-level ranking; no NPI lookup
   flows through the projection.

With both adjudication and member-view paths uninvolved, the
capability scope reduced to the model change + backfill +
write-time validation client. The cache, the per-claim membership
check, and any provider-service contract addition required to support
them were all deferred to the consumer that genuinely needs them.

## What this capability unblocks

- **5.8 — FHIR `InsurancePlan` projection.** `InsurancePlan.network`
  references `Organization` resources. After 5.5 each
  `NetworkTier.NetworkId` becomes an `InsurancePlan.network`
  reference resolving via fhir-service to provider-service's
  `Organization` endpoint.
- **Future claims-service / FFS membership-check capability.**
  Whichever service first needs "is NPI X in network Y?" can build
  its own typed client (cache TTL + telemetry tuned to its access
  pattern) and add the corresponding endpoint on provider-service
  rather than inheriting an over-fitted contract from 5.5.

## Recovery posture

- **Backfill operator-supplied mapping wrong.** Re-run with the
  corrected mapping only to continue patching tiers that have not yet
  been written; previously-patched tiers are skipped (the rerun is
  idempotent on already-mapped tiers). A tier whose existing
  `NetworkId` was set from the wrong mapping is **not** corrected by
  rerun under the 5.5 backfill semantics — recovery requires either a
  manual amendment of the affected plan or a follow-up "force
  overwrite" mode (not shipped in 5.5; defer to first reported
  incident).
- **`UpdateNetworkTiersAsync` interpreted as version-identity
  change.** Fast revert; the sibling-method bypass is fully
  orthogonal to the identity-write path. Tests pin the orthogonality.
- **Existing legacy `tier.ProviderNpis` data.** Preserved during
  migration; never consulted; field removal deferred to a follow-up
  PR once telemetry confirms zero remaining legacy-shape rows.
- **Worst-case rollback: revert this PR.** Adjudication has nothing
  to fall back to (it was never consuming NPI lists), so revert
  affects only the model surface and the backfill endpoint. Any
  `NetworkId` values written by the backfill stay present (extra
  field; no harm).

## See also

- [`network-as-organization.md`](./network-as-organization.md) —
  Provider 5.3 first-class network entity.
- [`plan-versioning.md`](./plan-versioning.md) — projection-metadata
  exemption framework.
- [`provider-versioning.md`](./provider-versioning.md) — same
  exemption framework on the provider side; 5.5 replicates the
  pattern.
- [`integrity-score-consumption.md`](./integrity-score-consumption.md)
  — companion read-side client (`HttpProviderIntegrityGate`); shares
  the same `HttpClient("ProviderService")` registration.
- [`network-participation-backfill.md`](./network-participation-backfill.md)
  — Provider 5.5 backfill pattern this capability mirrors.
- [`fhir-insuranceplan-projection.md`](./fhir-insuranceplan-projection.md) —
  Capability BP 5.8. The `NetworkTier.NetworkId` field this capability
  introduced is the source for `InsurancePlan.network[]` references in
  the FHIR projection. Each tier with a non-null `NetworkId` projects
  as `Organization/{networkId}`, dereferenceable via fhir-service's
  `/fhir/r4/Organization/{id}` endpoint (which proxies to
  provider-service per Provider 5.9). End-to-end Plan-Net navigation
  resolves through this chain.
