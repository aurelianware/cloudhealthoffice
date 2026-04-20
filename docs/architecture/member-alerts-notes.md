# Member Alerts & Notes (5.9)

Adds two member-bound resources to the member-service:

- **`MemberAlert`** — flags such as Litigation Hold, Custody Dispute, VIP,
  Do Not Contact. Projects to FHIR R4 `Flag`. End-dated rather than deleted.
- **`MemberNote`** — free-text notes scoped to a category (CustomerService,
  CareManagement, Appeals, Billing, Clinical). Projects to FHIR R4
  `Communication`. Immutable once created.

## Scope of this PR

- `MemberAlert` and `MemberNote` models with their enums.
- `IMemberAlertRepository` / `IMemberNoteRepository` (Cosmos + Mongo).
- `MemberAlertsController` at `/api/v1/members/{memberId}/alerts`.
- `MemberNotesController` at `/api/v1/members/{memberId}/notes`.
- `IFhirFlagProjector` + endpoint `/api/v1/members/{id}/fhir/Flag?status=active`.
- `IMemberAlertGuard` enforcing service-layer block rules; wired into
  `MembersController.TerminateMember`.
- New `MemberEventType` values for create/view audit on both resources.
- `JsonStringEnumConverter` registered on the MVC pipeline so enum payloads
  (`alertType`, `severity`, `category`) are sent and received as strings
  ("LitigationHold", "Critical", "CustomerService") rather than numbers.
- Portal: persistent `MemberAlertBanner` above member tabs; new `Notes`
  `MudTabPanel` with category filter, paged log, entry form.

## Why a separate container?

Alerts and notes have very different lifecycles from `Member`:

- Members are updated via 834 ingestion; alerts/notes are operator-driven.
- Notes can grow unbounded — embedding in `Member` would inflate every
  member read.
- Alerts need a query plane to compute the "active" set without rehydrating
  the member.

Separate containers (`MemberAlerts`, `MemberNotes`) partitioned by
`/tenantId` keep the read paths additive. Mongo collections share the same
shape via `MemberAlertRepositoryMongo` / `MemberNoteRepositoryMongo`.

## Lifecycle

### Alerts — end-dated, never deleted

| State           | Definition                                            |
|-----------------|-------------------------------------------------------|
| Active          | `StartDate <= now AND (EndDate IS NULL OR EndDate > now)` |
| Future          | `StartDate > now` (created with a future start)       |
| Ended (history) | `EndDate <= now`                                      |

`POST /alerts/{id}/end` sets `EndDate` and `EndedBy`. Ending an already-ended
alert is a no-op (200) — repeated end calls remain idempotent. The delete
endpoint is intentionally absent.

### Notes — append-only, immutable

The `IMemberNoteRepository` interface exposes only `CreateAsync`,
`GetByIdAsync`, and `ListByMemberAsync`. There is no Update or Delete
method. Corrections are written as **new notes** with:

```json
{
  "subject": "Correction: prior amount was $42.10, not $4210",
  "body": "...",
  "linkedResourceType": "MemberNote",
  "linkedResourceId": "<original note id>"
}
```

A reflection-based contract test in `MemberNotesControllerTests` fails the
build if anyone later adds an Update or Delete method to the interface.

## Audit

Audit lives on the existing `member-events` stream — that is the audit
mechanism per `member-foundation.md`. Five new event types are emitted:

| Event                       | Triggered by                               |
|-----------------------------|--------------------------------------------|
| `MemberAlertCreated` (6)    | `POST .../alerts`                          |
| `MemberAlertEnded`   (7)    | `POST .../alerts/{id}/end`                 |
| `MemberAlertViewed`  (8)    | `GET .../alerts`, `.../alerts/{id}`, `.../fhir/Flag` |
| `MemberNoteCreated`  (9)    | `POST .../notes`                           |
| `MemberNoteViewed`   (10)   | `GET .../notes`, `.../notes/{id}`          |

View events carry `{ scope, count }` so an auditor can reconstruct what was
displayed without storing the full payload.

View audit is **best-effort**: `PublishViewedAsync` in both controllers
catches non-cancellation exceptions so a failing audit sink does not turn
`GET /alerts` or `GET /notes` into a 5xx. The integrity-critical audit
events (`*Created` and `MemberAlertEnded`) continue to surface publisher
failures to the caller.

## Block rules

Active alerts can prevent specific actions. The rules table is the single
source of truth in code (`Services/MemberAlertGuard.cs`); changes here MUST
be mirrored in code, and vice versa.

A rule fires when an alert is (a) active, (b) of the matching type, and
(c) at severity **≥** `Min severity`. Lowering an alert's severity below the
threshold effectively informs-only without unblocking the portal hard
path — operators downgrade rather than end when they want the banner to
stay visible but want to permit an action.

| Alert type            | Min severity | Blocks action               | Surfaced as                                        |
|-----------------------|--------------|-----------------------------|----------------------------------------------------|
| `LitigationHold`      | Critical     | `Terminate`, `HardDelete`   | `409 ProblemDetails` (`type=member-alert-block`)   |
| `EligibilityDispute`  | Warning      | `Terminate`                 | `409 ProblemDetails`                               |
| `SecurityFreeze`      | Critical     | `UpdatePii`                 | `409 ProblemDetails`                               |
| `KnownFraudRisk`      | Critical     | `UpdatePii`, `NewEnrollment`| `409 ProblemDetails`                               |
| `DoNotContact`        | Warning      | `OutboundCommunication`     | `409 ProblemDetails` (enforced by comms-service)   |
| `HighRisk`            | —            | _informational only_        | banner only                                        |
| `VIP`                 | —            | _informational only_        | banner only                                        |
| `CustodyDispute`      | —            | _informational only_        | banner only                                        |
| `LanguageRequirement` | —            | _informational only_        | banner only                                        |
| `AccessibilityNeed`   | —            | _informational only_        | banner only                                        |

Block enforcement is centralised in `IMemberAlertGuard.EvaluateAsync` so
every action that needs to be guarded asks the same service. The evaluator
calls `ct.ThrowIfCancellationRequested()` before and after the repository
lookup so a cancelled caller surfaces as `OperationCanceledException`
rather than running a wasted query.

In this PR the only wired block is `Terminate` from `MembersController` —
both the DELETE and POST `/terminate` variants call the guard before
mutating state. The 409 ProblemDetails carries `alertId`, `alertType`,
`severity`, `action`, and (when set) `requiredAction` so the portal can
render an actionable error rather than a raw status code.

The other actions (`UpdatePii`, `NewEnrollment`, `OutboundCommunication`,
`HardDelete`) are reserved on the `MemberAlertAction` enum and ready to be
plumbed in by their owning endpoints / services in follow-up PRs.

## FHIR Flag projection

`/api/v1/members/{memberId}/fhir/Flag?status=active` returns a `Bundle` of
US Core `Flag` resources. Mapping (see `Services/FhirFlagProjector.cs`):

| FHIR `Flag` field             | `MemberAlert` source                           |
|-------------------------------|------------------------------------------------|
| `id`                          | `Id`                                           |
| `status`                      | `IsActive() ? "active" : "inactive"`           |
| `category[].coding[].code`    | severity → `safety` / `admin` / `clinical`     |
| `code.coding[].code`          | `AlertType` (CHO `CodeSystem/member-alert-type`) |
| `code.text`                   | `Reason`                                       |
| `subject.identifier`          | `MemberId` (system: `urn:cho:member-id`)       |
| `period.start` / `period.end` | `StartDate` / `EndDate`                        |
| `extension[required-action]`  | `RequiredAction` (CHO StructureDefinition)     |
| `meta.profile`                | `us-core-flag`                                 |

Severity → `category` mapping uses the standard
`http://terminology.hl7.org/CodeSystem/flag-category` value set; the alert
**type** rides on `code` so consumers don't have to inspect category to
discover what kind of flag it is.

## Portal

### `Shared/MemberAlertBanner.razor`

Persistent banner above the member-detail tabs. Renders **only active**
alerts as MudAlerts color-coded by severity:

| Severity   | MudBlazor color |
|------------|------------------|
| Critical   | `Severity.Error`   |
| Warning    | `Severity.Warning` |
| Info       | `Severity.Info`    |

Bubbles `OnAlertCountChanged(int)` so the host can react (badge counts,
disabling actions). Failures degrade silently to an empty banner — the
banner is informational and must not block the dialog from rendering.

### `Notes` tab in `MemberDetailsDialog.razor`

- Category filter (`All` + the five categories).
- Paged log (newest first) with `Load more` continuation.
- Inline entry form (Category / Subject / Body) — submission triggers a
  `Snackbar` and reloads the page so the new note appears at the top.
- Lazy load: notes are fetched on first tab activation, not eagerly with
  the rest of the dialog.

## Acceptance verification

| Acceptance criterion                                              | Verified by                                                                |
|-------------------------------------------------------------------|-----------------------------------------------------------------------------|
| Active alerts render banner; expired don't                        | `MemberAlertsControllerTests.ListAlerts_StatusActive_ExcludesEndDated`     |
| Block rules enforced (LitigationHold blocks terminate → 409)      | `MembersControllerTests.TerminateMember_*_BlockedByActiveLitigationHold_Returns409` |
| Note creation records audit event                                  | `MemberNotesControllerTests.CreateNote_PersistsAndEmitsAuditEvent`         |
| Note is immutable                                                  | `MemberNotesControllerTests.NoteRepository_HasNoUpdateOrDeleteMethod_EnforcesImmutability` |
| Alert view records audit event                                     | `MemberAlertsControllerTests.ListAlerts_EmitsViewedAuditEvent`             |
| Note view records audit event                                      | `MemberNotesControllerTests.ListNotes_EmitsViewedAuditEvent`               |
| FHIR Flag projection returns Bundle of Flag resources              | `MemberAlertsControllerTests.GetFhirFlags_ReturnsBundleWithFhirContentType`, `FhirFlagProjectorTests.ProjectBundle_*` |
| End-dated LitigationHold no longer blocks termination              | `MembersControllerTests.TerminateMember_EndedLitigationHold_AllowsTermination` |
| LitigationHold at Warning severity does not block termination      | `MemberAlertGuardTests.Terminate_LitigationHoldAtWarning_DoesNotBlock`      |
| EligibilityDispute at Warning blocks, at Info does not             | `MemberAlertGuardTests.Terminate_EligibilityDisputeAtWarning_Blocks`, `..._AtInfo_DoesNotBlock` |
| `EvaluateAsync` honors cancellation                                | `MemberAlertGuardTests.EvaluateAsync_CancelledToken_Throws`                |
| Audit publisher failure does not fail `GET /alerts`                | `MemberAlertsControllerTests.ListAlerts_AuditPublisherFailure_DoesNotFailRead` |
