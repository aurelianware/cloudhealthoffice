# Network-Participation Panel-Gating Backfill

> Capability 5.5 — closes the `Provider.cs` TODO that asked every
> `NetworkParticipation` write surface to populate panel-gating fields.

## Why this exists

`NetworkParticipation` carries five panel-gating fields that gate
PCP-assignment in coverage-service:

| Field | Semantics |
| --- | --- |
| `PanelLimit` | max members assignable to this PCP under this participation; `null` = unlimited |
| `PanelAccepted` | accepts new PCP assignments; `null` = falls through to `AcceptingNewPatients` |
| `AcceptedLobs` | LOB subset for PCP acceptance; empty = accept any LOB covered by participation |
| `MinAcceptedAgeYears` | minimum member age; `null` = no floor |
| `MaxAcceptedAgeYears` | maximum member age; `null` = no ceiling |

When the fields landed (capability 5.7 design phase) every existing
participation hydrated with these values at their C# type defaults —
`null`, `null`, empty list, `null`, `null` — and consumers were told
to treat that shape as "legacy unconstrained." That kept the contract
working but left a real ambiguity: a participation with
`PanelLimit==null` could mean "operator deliberately left it
unlimited" or "this row pre-dates the panel-gating fields and nobody
has ever looked at it."

Capability 5.5 closes that ambiguity in two coordinated changes:

1. **Soft validation on every write surface.** Producers that elide
   panel-gating on a new write produce a structured warning + a
   Prometheus counter increment. The write still succeeds. Telemetry
   drives the eventual hard-validation cutover.
2. **One-time admin-triggered backfill.** An admin endpoint patches
   every participation that's still in the all-type-defaults shape so
   the read side can distinguish "explicitly legacy unconstrained"
   (touched by 5.5 backfill) from "actively populated" (touched by an
   authoring producer).

The backfill writes the same values the type-default behavior already
produced. The behavior change is purely auditability: every patched
row produces a `NetworkParticipationEvent` with
`EventType=PanelGatingBackfilled` and the operator's
`backfillRunId`, so regulators and incident responders can see when
each row was set.

## Soft validation contract

Three controller write surfaces invoke `IPanelGatingValidator.Inspect`
on every request:

- `ProvidersController.CreateProvider`
- `ProvidersController.UpdateProvider`
- `ProvidersController.AddNetworkParticipation`

`Inspect` walks each `NetworkParticipation` on the supplied provider
and emits a single structured warning per participation that has all
five panel-gating fields at their C# type defaults. The warning
payload:

```
PanelGatingFieldsMissing on participation write.
  caller=<CreateProvider|UpdateProvider|AddNetworkParticipation>
  tenantId=<tenant>
  providerId=<chain key>
  npi=<10-digit NPI>
  participationIndex=<int>
  planId=<optional>
  networkId=<optional>
  lineOfBusiness=<enum value>
```

…and a Prometheus counter:

```
provider_service_panel_gating_missing_writes_total{caller, tenant_id}
```

Operators dashboard this counter and watch it trend to zero. The
follow-up PR flips soft validation to hard validation (400 with
explanation, counter no longer needs to fire) once telemetry shows
zero soft-warning producers for a sustained window — typically 7+
days across all tenants.

`MpipController.BulkImport` is **not** a panel-gating write surface
(verified during the 5.5 plan phase — it materializes
`MpipProviderQualification` records only, never
`NetworkParticipation`). Future bulk-import or CAQH-sync paths must
add the same `Inspect` call before merge.

## Backfill — admin HTTP endpoint

Endpoint:

```
POST /api/v1/admin/providers/backfill-network-participations?tenantId={X}
  &maxProviders=10000        (optional, default unbounded up to options cap)
  &pageSize=100              (optional, default 100)
```

Gates (defence in depth, **mirrors capability 5.4.5**):

1. `NetworkParticipationBackfill:AdminBackfillEnabled` defaults to
   `false`. Until set to `true`, the endpoint returns
   `503 Service Unavailable` with a structured payload pointing
   operators at the configuration key. 503 (not 404) so a
   misconfigured route doesn't masquerade as "never registered."
2. Provider-service does not configure authentication on its own
   (`Program.cs` calls `UseAuthorization()` with no
   `AddAuthentication()`). The deployment layer (NetworkPolicy /
   gateway ACL / mTLS) is the load-bearing authorization. The flag
   is a tripwire, not authn. Operators must restrict access at the
   deployment layer **even when** the flag is enabled.

The endpoint is tenant-scoped. Each call operates on one tenant. To
backfill an entire fleet, scripts iterate the endpoint across tenant
ids externally — the service stays focused on per-tenant operation
and the blast radius of a single misbehaving call is bounded.

Response payload (`NetworkParticipationBackfillResult`):

```jsonc
{
  "tenantId": "tenant-a",
  "backfillRunId": "01HE7Z…ULID",
  "providersInspected": 2450,
  "participationsInspected": 9876,
  "participationsBackfilled": 612,
  "participationsSkipped": 9264,
  "participationsFailed": 0,
  "etagConflicts": 0,
  "startedAt": "2026-04-27T18:00:00Z",
  "completedAt": "2026-04-27T18:00:42Z"
}
```

`backfillRunId` is a ULID minted per call. Every event the run emits
carries the same `backfillRunId` so an operator can correlate every
audit entry produced by a single invocation.

## Why "operational backfill — one-time exemption"

The backfill writes panel-gating fields on Active rows. The Provider
versioning model (capability 5.1) treats Active rows as immutable;
`UpdateAsync` rejects writes that target a non-Draft row with
`ProviderVersionStateException`. Capability 5.4.5 already established
a documented exemption for "projection metadata" (integrity scores,
last-verified timestamps) — fields that get computed externally and
projected onto the Active row without producing a new version.

Capability 5.5 mirrors that pattern with one tighter constraint:
**only the one-time backfill is exempt; going-forward CRUD writes
through `UpdateAsync` are not.** The repository method is
`IProviderRepository.UpdatePanelGatingDefaultsAsync` and it bypasses
the `UpdateAsync` state guard via:

- **Cosmos** — `PatchItemAsync` with five `PatchOperation.Set` ops on
  the positional participation slot. Conditional on the row's
  `_etag` so a concurrent CRUD write moves the row out from under the
  backfill (counted as `EtagConflicts` and retried on next operator
  run).
- **Mongo** — `FindOneAndUpdateAsync` with `$set` on
  `NetworkParticipations.{idx}.{field}`, sorted by
  `VersionNumber DESC` so amendments hit the latest head.

Identity-field writes through `UpdateAsync` against the same row
continue to throw, verified by
`UpdatePanelGatingDefaultsTests.UpdateAsync_against_active_still_throws_after_panel_gating_patch`.

See `docs/architecture/provider-versioning.md` "Operational backfill
— one-time exemption" for the architectural rationale.

## Idempotency rule

A participation is **eligible** for backfill when **all five**
panel-gating fields are at their C# type defaults:

```csharp
PanelLimit            == null
&& PanelAccepted      == null
&& (AcceptedLobs == null || AcceptedLobs.Count == 0)
&& MinAcceptedAgeYears == null
&& MaxAcceptedAgeYears == null
```

Implemented as `PanelGatingFields.IsAtTypeDefaults(participation)`.
Used as both:

- The **service-layer authoritative filter** before each patch.
- The shape the **storage-layer query** approximates (a superset
  filter — false-positive page entries result in a no-op skip, never
  data corruption).

A participation that has any field non-default is treated as already
touched by panel-gating-aware code and skipped. **Rerun behavior**:
because this backfill writes the panel-gating fields to their type
defaults, a patched row still satisfies
`PanelGatingFields.IsAtTypeDefaults(participation)`. Reruns can
therefore select and patch the same legacy row again until some
panel-gating-aware write stores a non-default value. Repeated
invocation is **safe at the document-state level** (the patch is
value-preserving — same defaults written), but operators should not
expect "zero patches after the first successful run." Reruns also
re-emit `PanelGatingBackfilled` audit events with a fresh
`backfillRunId` per invocation; those events are intentionally
distinct so each operator-triggered run has an independent audit
record.

## Participation addressing — positional indexing

`NetworkParticipation` does not carry a stable `ParticipationId`
field. The backfill addresses participations by zero-based array
index within the head-Active provider's
`NetworkParticipations` list. Stability characteristics:

- **Within a single Provider chain row:** the array order is
  immutable — a row published Active does not get re-ordered without
  going through the version chain (which the backfill does not
  trigger). So an index that resolves on read remains valid until the
  patch lands.
- **Across CRUD writes:** a concurrent `UpdateAsync` can overwrite
  the participations array. The conditional `_etag` patch detects the
  conflict and the row is counted as `EtagConflicts` and skipped.
  Operators rerun the endpoint to pick up missed rows.
- **Across operator runs:** the `backfillRunId` ULID scopes each
  event so two separate runs of the backfill produce distinct events
  for the same row. Because the backfill writes the panel-gating
  fields to their documented type defaults, and eligibility is also
  defined in terms of those defaults, a rerun can still treat a
  previously patched row as eligible. That makes reruns safe at the
  document-state level (they reapply the same values) but **not**
  skip-based idempotent at the event stream level — a later run may
  patch the same row again and emit another distinct event. The only
  way a row "drops out" of eligibility is when a panel-gating-aware
  write surface (CreateProvider/UpdateProvider/AddNetworkParticipation
  with populated values) stores a non-default value.

A future PR (Phase 2 prerequisite for capabilities 5.7-5.10) is
likely to introduce a stable `ParticipationId` ULID for FHIR
PractitionerRole projection and credentialing references. That's
explicitly out of scope for 5.5; positional indexing is sufficient
for a one-time operation.

## Event emission

Each successful patch produces a deterministic
`NetworkParticipationEvent` written to the
`ProviderParticipationEvents` Mongo collection (parallel to
`ProviderVerificationEvents` from capability 5.4.5):

```jsonc
{
  "_id": "{tenantId}:{providerId}:backfilled:{providerId}:{participationIndex}:{backfillRunId}",
  "partitionKey": "{tenantId}:{providerId}",
  "tenantId": "...",
  "providerId": "...",
  "eventId": "backfilled:{providerId}:{participationIndex}:{backfillRunId}",
  "eventType": "PanelGatingBackfilled",
  "version": 1,
  "schemaVersion": 1,
  "occurredAt": "2026-04-27T18:00:01Z",
  "participationIndex": 2,
  "planId": "plan-a",
  "networkId": "net-1",
  "lineOfBusiness": "Commercial",
  "actorId": "admin:backfill-network-participations",
  "correlationId": "<trace-id>",
  "backfillRunId": "01HE7Z…ULID"
}
```

Mongo `_id` is scoped to `PartitionKey:EventId` (lesson learned in
5.4.5: two tenants with the same `providerId:index:runId` would
otherwise collide). `(TenantId, ProviderId, EventId)` UNIQUE index
remains the primary idempotency guard.

Event publication is **best-effort**: a failure inside
`PublishPanelGatingBackfilledAsync` is logged at warning level but
does not roll back the patch. The patch is the source of truth;
re-running the backfill is a no-op for already-patched rows so an
event re-emission would not duplicate audit entries (the row no
longer matches the eligibility filter).

## Recovery posture

| Failure mode | Behavior |
| --- | --- |
| Admin endpoint hangs | Observable via Prometheus; feature-flag-gated; tenant-scoped so blast radius bounded |
| Single repository patch fails | Caught, logged, batch continues; failed rows logged for retry; counted as `participationsFailed` |
| Etag conflict (concurrent CRUD) | Skipped silently; counted as `EtagConflicts`; retried on next operator-triggered run |
| Event publication fails | Logged warning; patch already landed; no rollback. A subsequent run is value-preserving (re-applies the same defaults) and emits a fresh event under a new `backfillRunId`. |
| Soft-validation telemetry too noisy | Configurable verbosity (`NetworkParticipationBackfill:SoftValidationLogLevel`); tunable post-merge without code change |

Worst-case rollback: revert the PR. The backfill stops; existing
participations retain whatever values they had. Going-forward writes
lose soft-validation telemetry but continue to work. The portal
panel-gating tab disappears but underlying fields are still
nullable. No data corruption.

## Out of scope

This PR does not change:

- `coverage-service` PCP-assignment consumer logic
- Provider identity fields or version-chain logic (5.1)
- Roster API (5.4 / PR #710)
- Verification write-back (5.4.5 / PR #711)
- Adapter pattern (5.2 / PR 7.3)
- `IntegrityProjectionWorker` or hosted-service infrastructure
- `Organization` entity (5.3)
- `LineOfBusiness` enum cleanup (PR #705 conventions — flagged for a
  separate follow-up)
- Inline edit of panel-gating fields in `CreateEditProviderDialog`
  (a separate UI capability; this PR adds read-only audit visibility)
