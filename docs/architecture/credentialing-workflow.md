# Credentialing workflow (capability 5.6)

Provider credentialing is an event-sourced workflow. The
`CredentialingEvents` stream is the system-of-record; the three flat
fields on `Provider` (`CredentialingStatus`, `CredentialingDate`,
`RecredentialingDueDate`) are a denormalized read-side projection
written by `IProviderRepository.UpdateCredentialingProjectionAsync` —
the same bypass pattern used by the integrity projection (5.4.5) and
the panel-gating defaults backfill (5.5).

This document explains the chain shape, the projection mapping, the
endpoint surface, the failure semantics, and the boundary of Phase 1
versus Phase 2.

## Why event-sourcing

Credentialing decisions are an audit-grade workflow:

- Regulators (NCQA, URAC, state DOI) require an immutable trail of
  who decided what and when. Decision authority, committee membership,
  and minute references are part of the audit; rewriting them after the
  fact is a compliance violation.
- Re-credentialing happens every 2-3 years and the new decision must
  link to its predecessor approval — chain linkage is a first-class
  requirement, not an afterthought.
- Submission, primary-source verification, committee review, and
  decision are operationally distinct events. Collapsing them into a
  single status field loses information that operators rely on
  (e.g. "we submitted three weeks ago and PSV completed yesterday;
  committee meets Friday").

The platform already runs three event publishers
(`ProviderVersionEventPublisher`,
`NetworkParticipationEventPublisher`,
`ProviderVerificationEventPublisher`); credentialing was the only
major workflow without one. The fourth publisher
(`CredentialingEventPublisher`) keeps the operational shape uniform.

## Event chain

Six event types, all in `Models/CredentialingEvent.cs`:

| Event | Purpose | Status effect |
|---|---|---|
| `ApplicationSubmitted` | Opens a new chain. EventId is the application identifier referenced by every downstream event. | → Pending |
| `PrimarySourceVerificationCompleted` | Records PSV against the open application. | (no change) |
| `CommitteeReviewScheduled` | Marks committee review on the calendar. | (no change) |
| `DecisionRecorded` | Closes the chain with Approved or Denied. Carries decision authority capture (committee members, minute reference). | → Approved / Denied |
| `RecredentialingTriggered` | Opens a re-credentialing chain linked to the predecessor approval. The new application follows in the same chain. | → Pending |
| `ApplicationWithdrawn` | Terminates the open chain. Projection reverts to the predecessor decision (or Unknown). | → predecessor |

Chain linkage:

- `ApplicationEventId` — set on every event after the opening
  `ApplicationSubmitted`. Ties PSV / committee / decision / withdrawal
  back to the application that opened the chain.
- `PredecessorEventId` — set on `RecredentialingTriggered` to point at
  the prior terminal `DecisionRecorded`. Lets the projector restore the
  prior approval if the re-cred application is withdrawn.

Idempotency keys are deterministic per event type — see the
`Build*EventId` factories on `CredentialingEvent`. Re-publishing the
same logical event collapses to the existing row via the publisher's
idempotency probe.

## Projection

`CredentialingProjector` is a pure function over the chain. It is the
single authority on the events → `CredentialingStatus` mapping; both
the read-side (`GET /credentialing/status`) and the write-side
projection patch use it to compute the value to store on `Provider`.

| Sequence | Projected status |
|---|---|
| `[]` | `Unknown` |
| `[ApplicationSubmitted]` | `Pending` |
| `[ApplicationSubmitted, PSV]` | `Pending` |
| `[ApplicationSubmitted, …, DecisionRecorded(Approved)]` (RecredDue future) | `Approved` |
| `[ApplicationSubmitted, …, DecisionRecorded(Approved)]` (RecredDue past) | `Expired` |
| `[ApplicationSubmitted, …, DecisionRecorded(Denied)]` | `Denied` |
| `[…, Approved, RecredentialingTriggered]` | `Pending` |
| `[…, Approved, RecredentialingTriggered, ApplicationSubmitted, …, DecisionRecorded(Approved)]` | `Approved` (new dates) |
| `[ApplicationSubmitted, ApplicationWithdrawn]` | `Unknown` |
| `[…, Approved, RecredentialingTriggered, ApplicationSubmitted, ApplicationWithdrawn]` | `Approved` (predecessor restored) |

`Suspended` is not derivable from the chain in Phase 1 — see
"Out-of-scope" below.

`Expired` is computed at projection time from `RecredentialingDueDate`.
The flat field on `Provider` is patched to the projector's at-write-time
verdict on each transition; readers who consult `GET /credentialing/status`
get a fresh evaluation each time. Between transitions, a stored value of
`Approved` will read as `Approved` even after the recredentialing-due
date elapses; the next transition (or a future hosted sweeper) reconciles.

### Synthesized applications

The legacy `PUT /providers/{id}/credentialing` shim (see "Endpoints"
below) synthesizes an `ApplicationSubmitted` event when no chain is
open, paired immediately with a `DecisionRecorded`. The synthesized
event is flagged `synthesizedForDelegatedAuthority=true` so audit
reviewers can distinguish it from a real submission.

The projector explicitly filters out synthesized applications when
computing "current open application" — defense in depth. Synthesized
applications are paired with a matching `DecisionRecorded` by
construction; if a future bug ever produces an orphaned one, the
projector won't get stuck reporting `Pending`.

## Endpoints

All routes are tenant-scoped via `HttpContext.Items["TenantId"]`,
populated by `TenantMiddleware`.

| Method | Route | Body | Response | Notes |
|---|---|---|---|---|
| `POST` | `/api/v1/providers/{id}/credentialing/applications` | `SubmitApplicationRequest` | `CredentialingEvent` (201) | Opens a chain; rejects when one is already open. |
| `POST` | `/api/v1/providers/{id}/credentialing/applications/{eventId}/withdraw` | `WithdrawApplicationRequest` | `CredentialingEvent` (200) | Terminates the open chain; reverts projection. |
| `POST` | `/api/v1/providers/{id}/credentialing/verifications` | `RecordPrimarySourceVerificationRequest` | `CredentialingEvent` (201) | Status-neutral. |
| `POST` | `/api/v1/providers/{id}/credentialing/committee-reviews` | `ScheduleCommitteeReviewRequest` | `CredentialingEvent` (201) | Status-neutral. |
| `POST` | `/api/v1/providers/{id}/credentialing/decisions` | `RecordDecisionRequest` | `CredentialingEvent` (201) | Closes the chain; patches the projection. |
| `POST` | `/api/v1/providers/{id}/credentialing/recredential` | `TriggerRecredentialingRequest` | `CredentialingEvent` (201) | Opens a re-cred chain; requires prior approval. |
| `GET` | `/api/v1/providers/{id}/credentialing/status` | — | `CredentialingProjectionResult` (200) | Projection over the chain at request time. |
| `GET` | `/api/v1/providers/{id}/credentialing/history` | — | `CredentialingHistoryPage` (200) | Newest-first paged chain; opaque cursor. |
| `PUT` | `/api/v1/providers/{id}/credentialing` | `CredentialingUpdateRequest` (legacy) | `Provider` (200) | Rewired through `RecordDecisionAsync` with `DelegatedAuthority`. Same DTO, same response shape; internal mechanism upgraded. |

Error mapping:

- `CredentialingValidationException` → 400 with
  `{ error: "credentialing_validation_failed", message }`.
- Publisher exhaustion (5 retries failed) → 503 with
  `{ error: "credentialing_publish_failed", message }`.
- Provider not found (legacy `PUT`) → 404.

## Decision authority capture

`DecisionRecordedPayload` carries four authority fields:

- `DecisionAuthorityType` — `CredentialingCommittee` |
  `MedicalDirector` | `DelegatedAuthority` | `AutoApproved`.
- `DecisionAuthorityId` — committee or actor identifier.
- `CommitteeMembers` — list of participating actor IDs (nullable;
  required for `CredentialingCommittee` path).
- `DecisionMinuteReference` — pointer to minutes document
  (`DocumentReference`); required for `CredentialingCommittee` path.

The `DelegatedAuthority` short-circuit (used by the legacy `PUT`)
skips the "open application required" guard and synthesizes the
missing application event so the legacy endpoint always succeeds on
Active providers. The synthesized application is flagged so audit
reviewers can distinguish it.

## Supporting documents

Phase 1 carries `DocumentReference` URIs as opaque metadata on event
payloads — there is no document upload service in this PR. The shape
maps cleanly to FHIR `DocumentReference` for a future projection:

- `Uri` → `DocumentReference.content.attachment.url`
- `DocumentType` → `DocumentReference.type.coding`
- `Sha256` → `DocumentReference.content.attachment.hash`

`DocumentType` is intentionally a free-form string (not a closed enum)
so future credentialing artifact categories don't require a code
change. The credentialing service does NOT validate URI reachability,
fetch the document, or recompute Sha256 — URI is opaque audit
metadata. Validation is a Phase 2 document-service responsibility.

## Failure semantics

Canonical write order in `CredentialingService`:

1. Read the chain ascending by `Version`.
2. Project the pre-state.
3. Validate the request.
4. Build the event with deterministic `EventId`.
5. Publish via `ICredentialingEventPublisher` (the system-of-record).
6. For status-changing events: re-project including the new event,
   then patch the flat-field projection on `Provider` via
   `UpdateCredentialingProjectionAsync`.

Failure modes:

- **Publisher write fails after retries**: the service throws
  `InvalidOperationException`; the controller surfaces 503. The chain
  is unchanged. Caller retries.
- **Projection patch returns false (no Active head)**: logged as a
  warning; the event is still appended. The next status-changing
  transition will reconcile, since each patch sends the projector's
  authoritative full-state verdict (not a delta).
- **Projection patch throws**: logged as an error; the event remains
  the system-of-record. Same recovery as above.

The chain has no destructive update path. Withdrawal is an event, not
a row removal.

## Cross-service consumers

Phase 1: none. `coverage-service`
(`PcpAssignmentService.cs`) and `benefit-plan-service`
(`AdjudicationController.cs`) read the flat `CredentialingStatus` on
`Provider` for gating; both continue to work unchanged.

Phase 2 (out of scope): an event subscriber for credentialing-driven
gating re-evaluations.

## Out-of-scope (Phase 2 backlog)

The following live in this section to make the boundary explicit:

- **Document file storage.** Phase 1 carries URIs only; a Phase 2
  document service handles upload, retention, and validation.
- **Suspended status as event-driven.** No `SuspensionRecorded` event
  type ships in Phase 1 — the enum value remains for read-side
  compatibility. Phase 2 lands the event type alongside appeals and
  peer review.
- **Appeals workflow.** Reversing a Denied decision via formal appeal
  is a credentialing concern but Phase 2.
- **Peer review.** Quality-driven review tied to credentialing is
  Phase 2.
- **Delegated credentialing for sponsor-managed rosters.** Distinct
  from `DecisionAuthorityType=DelegatedAuthority` (which is the legacy
  shim path). Sponsor-managed credentialing where the sponsor holds
  delegated authority for its own roster is Phase 2.
- **Auto-invocation of `ProviderVerificationOrchestrator` from PSV.**
  Phase 1 records PSV manually; Phase 2 wires the orchestrator into
  the workflow.
- **Hosted projection reconciler.** Today the flat-field projection
  reconciles on the next status-changing transition. A scheduled
  sweeper that reconciles between transitions is Phase 2.

## Auth posture

The new endpoints do not introduce additional authentication beyond
what the service already enforces (tenant resolution via
`TenantMiddleware`). Deployment-layer ACLs (NetworkPolicy + gateway
allowlist) gate access to credentialing routes; no controller-level
feature flag is appropriate because credentialing is a tenant-scoped
operational endpoint, not an admin tool.
