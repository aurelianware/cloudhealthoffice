# PCP Assignment

Roadmap reference: 5.7 Phase 1.

## Service choice

PCP assignment lives in **coverage-service**, with member-service acting as a
thin proxy to keep portal calls on a single boundary.

Why coverage-service:

- Coverage already owns the `PcpNpi`, `PcpName`, `PcpAssignmentDate`,
  `PcpAssignmentMethod`, and `PreviousPcpNpi` fields used by eligibility,
  capitation, and downstream claims.
- Coverage has the `(Member -> Sponsor -> Plan -> LineOfBusiness)` linkage
  that PCP validation needs (network match by plan + LOB).
- The capitation panel-roster endpoint (`GET /by-pcp/{npi}`) already lives
  here; reusing it as the panel-count source avoids a second authoritative
  store.
- Multi-tenant partitioning keys are the same shape as the existing
  Coverage container (`tenantId` partition key).

Why not member-service:

- member-service is a Patient projector and identity store, not a benefits
  store. Putting plan/LOB-aware validation there would duplicate state.
- Network/panel checks need to call provider-service either way; the round
  trip is the same from either service.

## Data model

### `PcpAssignment` (new collection)

Effective-dated history. One row per assignment change. The current
assignment for a member is the row with `EndDate == null`.

| field                       | notes                                                   |
| --------------------------- | ------------------------------------------------------- |
| `tenantId`                  | partition key                                           |
| `id`                        | document id                                             |
| `memberId`                  |                                                         |
| `coverageId`                | which Coverage this assignment was attached to          |
| `providerId`                | provider-service opaque id (best-effort)                |
| `providerNpi`               | 10-digit NPI; the stable external id                    |
| `providerName`              | denormalized for display / FHIR                         |
| `effectiveDate`             |                                                         |
| `endDate`                   | null = current; stamped when superseded                 |
| `assignmentReason`          | free-text                                               |
| `assignmentSource`          | `MemberChoice \| AutoAssigned \| AdminAssigned`         |
| `networkStatusAtAssignment` | snapshot — see "Snapshot semantics" below               |
| `assignedBy`                | actor (user / system principal)                         |
| `createdDate`               |                                                         |

The denormalized `Coverage.Pcp*` fields stay — they are the read-path for
eligibility (270/271) and capitation roster joins. `PcpAssignment` is the
write log + audit log. Both update inside the same `AssignAsync` call.

### Snapshot semantics: `NetworkStatusAtAssignment`

This is the network status that was true **at the moment the assignment was
written**. It is never updated. If a provider later terminates their
network participation, the historical row still reads `InNetwork`, because
that is what was true the day the assignment was made. The audit trail
needs that property to be useful.

For live UI status, always re-fetch via provider-service. Do not surface
`networkStatusAtAssignment` as the live network indicator anywhere. The
field name is verbose on purpose; do not abbreviate it to `NetworkStatus`
in any DTO that escapes the history shape.

## Validation ladder

`PcpAssignmentService.AssignAsync` runs a deterministic, fail-fast
validation ladder. The first failure is what gets returned, logged, and
metric'd. The order of these checks is **API contract** — portal switches
on `code` and picks remediation flows. Reordering or renaming requires a
coordinated portal release.

| order | code                       | trigger                                                     |
| ----: | -------------------------- | ----------------------------------------------------------- |
|     0 | `NO_ACTIVE_COVERAGE`       | preflight; member has no active coverage on the date        |
|     0 | `INVALID_NPI`              | preflight; npi not 10 digits                                |
|     1 | `PROVIDER_NOT_FOUND`       | provider-service lookup returns null                        |
|     2 | `PROVIDER_INACTIVE`        | `Provider.Status != Active`                                 |
|     3 | `PROVIDER_NOT_CREDENTIALED`| `CredentialingStatus != Approved`                           |
|     4 | `NO_NETWORK_PARTICIPATION` | no active participation matching coverage's plan+LOB        |
|     5 | `NOT_ACCEPTING_PATIENTS`   | `PanelAccepted == false` (override) or `AcceptingNewPatients == false` |
|     6 | `LOB_NOT_ACCEPTED`         | LOB not in `AcceptedLobs` (when set)                        |
|     7 | `AGE_OUT_OF_RANGE`         | member age outside `Min/MaxAcceptedAgeYears`                |
|     8 | `PANEL_FULL`               | live panel count `>=` `PanelLimit`                          |

Codes 0 and the rest of the ladder are emitted as 400 ProblemDetails-shaped
`PcpValidationError { code, field, message, severity }`. The exception is
`NO_ACTIVE_COVERAGE`, which is a 404 because there is no resource to attach
the assignment to.

## Panel race

Step 8 reads the current panel count via `IPanelCounter`, then the service
writes the assignment row. Between those two operations, another assignment
can land and both can pass the limit check.

**Phase 1 (this PR): accept the race.** Panel limits in practice carry
slack — a provider with a 1,000 limit is not going to crash at 1,001. We
ship the validation as racy, document it here, and add a stub
`PcpPanelReconciliationJob` that scans for over-limit panels per tenant and
logs a warning. The reconciliation job has no scheduler bound to it in this
PR — the goal of the stub is to make the observability path exist before
the race actually bites.

**Phase 2: per-provider distributed lock.** Acquire a Redis lock on
`pcp-assignment:{tenantId}:{npi}` before the capacity check and hold it
through the write. Tracked under Addendum A.7.2 (Redis primitives).
`TODO(addendum-a)` markers are in the code at the panel-check site and on
the reconciliation stub.

**Not chosen: optimistic ETag on the panel counter.** Most correct (no
race, no waiting for a lock) but requires capitation-service to expose a
versioned counter. Revisit if the lock approach becomes painful.

## CareTeam

`ICareTeamProjector` projects an aggregate of `CareTeamMember` entries
into a FHIR R4 CareTeam aligned to the US Core 6.1 CareTeam profile.

```csharp
JsonObject Project(string memberId, Coverage? coverage, IEnumerable<CareTeamMember> members);
```

`CareTeamMember { Role, PractitionerNpi, DisplayName, EffectiveDate, EndDate, Source }`
is decoupled from `PcpAssignment` so future sources (specialists, care
managers, behavioral health) populate the same projector without taking a
dependency on the PCP types.

Today only the PCP source is wired (`CareTeamMember.FromPcp`). The
projector intentionally emits no placeholder participants; an empty member
list yields a `proposed` status CareTeam with no `participant[]` entries.

`status` mapping:

- `inactive` if the underlying coverage is `Terminated`, or every
  participant has been ended.
- `proposed` if there are no participants.
- `active` otherwise.

`participant[].role` uses the FHIR `practitioner-role` CodeSystem (`doctor`
for PCP/Specialist, `ict` for care manager). `participant[].member` is a
`Practitioner` Reference shaped as an identifier-only Reference using the
NPI system `http://hl7.org/fhir/sid/us-npi`, since coverage-service does
not own logical Practitioner ids.

## Provider-service contract changes

`Provider.NetworkParticipation` gains five fields to gate PCP validation:

| field                  | semantics                                                         |
| ---------------------- | ----------------------------------------------------------------- |
| `PanelLimit`           | max members assignable to this participation; null = unlimited    |
| `PanelAccepted`        | overrides `AcceptingNewPatients` for PCP only; null = inherit     |
| `AcceptedLobs`         | LOBs accepted as PCP; empty = accept any LOB on the participation |
| `MinAcceptedAgeYears`  | minimum member age; null = no floor                               |
| `MaxAcceptedAgeYears`  | maximum member age; null = no ceiling                             |

### Migration plan

- **Schema**: pure additions on an embedded sub-document. No DDL needed for
  Cosmos / Mongo; existing documents read back with `null` for new fields.
- **Backfill**: `null` is treated as legacy unconstrained — panel open,
  any LOB the participation already covers, no age limits. This preserves
  current behavior for every participation that was loaded before this
  migration.
- **Forward writes**: every write path that emits a `NetworkParticipation`
  needs to populate these fields going forward:
  - `ProvidersController.AddNetworkParticipation` (existing endpoint —
    accepts whatever the caller sends; needs the portal Provider edit dialog
    updated to capture these inputs).
  - any bulk-import / CAQH-sync path that materializes participations.
  - future `CreateEditProviderDialog.razor` edits.

  `TODO(provider-service)` markers are in `Provider.NetworkParticipation`
  for the next person on that surface.
- **Backfill window**: network ops can run a one-time backfill once the
  fields land. Until then, every Phase-1 PCP assignment behaves as if the
  participation has unlimited panel and accepts the member's LOB and age —
  i.e., it gates only on credentialing, network participation, and
  `AcceptingNewPatients`. That is the same effective contract we shipped
  pre-5.7, so no regressions.

## Member-service ↔ coverage-service contract

`PUT /api/v1/members/{id}/pcp` now propagates 400 from coverage-service
verbatim. The `HttpCoverageServiceClient.AssignPcpAsync` call returns an
`AssignPcpOutcome` instead of throwing on 400 — the validation error body
travels through to the portal so it can localize off `code`.

503 / connectivity issues continue to throw `DownstreamUnavailableException`
and surface as `503` ProblemDetails on the member-service edge.

New endpoint `GET /api/v1/members/{id}/pcp/history` proxies through to the
coverage-service `member/{id}/pcp/history` endpoint.

## Portal

- `PcpSearchDialog.razor` replaces `AssignPcpDialog.razor`. Each row in
  the provider table renders a Network chip and a Panel chip. Live panel
  capacity is a Phase-2 enhancement once provider-service exposes panel
  counts on `ProviderListItem` — a tooltip on the chip notes this.
- The PCP tab in `MemberDetailsDialog.razor` now shows an "Assignment
  History" subsection backed by the new history endpoint, with a column
  for `NetworkStatusAtAssignment` (rendered as a chip and labeled
  "Network @ Assignment" so it does not get confused with live status).

## FHIR

- `Patient.generalPractitioner[]` is emitted by `FhirPatientProjector.Project`
  when the caller passes a `MemberPcpResponse`. `MembersController.GetFhirPatient`
  fetches the current PCP best-effort and forwards it in. Failures degrade
  silently — the resource omits the optional rather than 503'ing the read.
- `GET /api/v1/coverage/member/{id}/care-team` returns a US Core CareTeam
  resource. Today it has at most one PCP participant.
