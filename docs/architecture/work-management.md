# Work management

`work-management-service` is CHO's one work engine. Every kind of operational
work (claims pends first; appeals, prior-authorization reviews, 834 and payment
exceptions later) becomes a **work item** that is routed to one queue, scored on
one priority scale, and pulled by the people entitled to it through **My Work**.
An item always opens in its own domain screen (a claim in claim detail); the
domain pages keep their own search.

Code: `src/services/work-management-service`. Tests:
`src/services/work-management-service/WorkManagementService.Tests`.

## Three separate concerns

| Concern | Decided by | Never decided by |
|---|---|---|
| **Routing**: which queue an item is in | rules on the item's attributes (queue criteria) | people |
| **Entitlement**: which work *types* and actions a person may do | the CHO role (`ChoRolePermissions`) | queue membership |
| **Distribution**: who gets which item, when | queue membership + Get Next / supervisor push | routing rules |

### Routing

A queue (`WorkQueue`) has a work type, an `Order`, `IsActive`, criteria, an SLA
in days and priority weights. Each item lands in **exactly one** queue: the first
active queue of its work type, by `Order` (ties by name, then id), whose criteria
all hold; otherwise the work type's **fallback** queue. A work type with active
queues must have exactly one active fallback (validated on every create, update
and delete).

Claim criteria (`ClaimQueueCriteria`): pend codes (by default the claim's
*routing* pend code, `PendDetails.PendCode`; `MatchAnyPendCode` matches any pend
on the claim), lines of business, claim types, min / max amount (total charge),
billing provider NPIs and TINs, network tiers, expedited. Lists are "any of",
an empty list does not constrain, and every set criterion must hold. Other work
types' queues route by order alone in this phase.

A queue change applies to new work at once. **Recompute**
(`POST /api/v1/work/admin/recompute`) re-routes and re-scores the open items;
leases are kept.

### Default claim queues

Seeded per tenant the first time the tenant is used (deterministic ids, created
only when the tenant has no claim queue at all, so it is idempotent across
replicas and respects later deletions). They reproduce claims-service's work-queue
categories (`ClaimsController.MapPendReason`):

| Order | Queue | Pend codes |
|---|---|---|
| 10 | NCCI / MUE Edit Failures | NCCI, MUE |
| 20 | Prior Authorization | AUTH, NOAUTH, PAUNAVAIL |
| 30 | Provider Not Contracted | OON, NOCONTRACT |
| 40 | Coordination of Benefits | COB |
| 50 | Medical Review | MEDREVIEW, CLINICAL |
| 60 | Service Unavailable | SVCUNAVAIL, PLANUNAVAIL |
| 70 | Possible Duplicate | DUPLICATE |
| 80 | Pricing | PRICING |
| fallback | Other / Pending Review | everything else (RETROELIG, SUBRO, SPENDDOWN, ACCUMCOMMIT, …) |

All defaults: SLA 30 days, weights deadline 1, age 0.5, amount 0.25, expedited 1.

### Entitlement

Get Next only draws from queues whose work type the **caller's own token**
permits, whatever the memberships say:

| Work type | Permission | Phase |
|---|---|---|
| Claim | `claims:work` | 1 |
| Appeal | `appeals:write` | 2 |
| PriorAuth | `authorizations:decide` | 2 |
| Enrollment834 | `enrollment:process` | 3 |
| Payment | `payments:run` | 3 |

A membership can therefore only ever narrow what the role permits. (The admin
API cannot check another user's roles: users live in tenant-service. The check is
made where it matters, at distribution, on the puller's token.)

### Memberships and teams

Supervisor-managed. A membership names a user **or** a team, a queue, a `Rank`
(1 = first), `IsOverflow` (primary vs overflow) and an optional
`AuthorityLimit` (largest amount the member may work in that queue). A team's
memberships apply to every user in it. When a user reaches one queue several
ways, the strongest terms win: primary over overflow, the lowest rank, the
highest authority (no limit beats any limit). `GET
/api/v1/work/admin/users/{userId}/queues` shows the result in Get Next order.

## Distribution

### Get Next (`POST /api/v1/work/next[?workType=]`)

1. **Own active lease first.** If the caller already holds an unexpired lease,
   that item is returned (`resumed: true`): finish or release before taking more.
2. Otherwise the caller's effective queues, filtered by entitlement, are taken
   in two tiers: **primary, then overflow**. Within a tier, candidates from all
   its queues are merged and ordered by priority score (desc), then membership
   rank, then age, then id. A candidate is **Open**, or **Leased with its lease
   expired** (reclaimed; audited `LeaseReclaimed`), and within the membership's
   authority limit.
3. The candidate is leased with a **compare-and-swap** (below). If another caller
   won it, the next candidate is tried; candidates are re-read up to
   `GetNextRounds` (3) times. **204** when nothing is eligible.

The response carries the item (queue name, score, reason, due date, lease) and
`openUrl` (`/claims/{claimId}`), which the portal navigates to.

### Leases

Default 30 minutes (`WorkManagement:LeaseMinutes`). `renew` (holder only)
extends to now + lease duration (never shortens a longer assignment); `release`
(holder only) returns the item to its queue. An expired lease is not cleared by
a timer: the item simply becomes eligible again and the next Get Next reclaims it.

### Push (supervisor, `workqueue:assign`)

`assign` leases the item to the named user for `AssignedLeaseMinutes` (480) with
`assignedBy` = the supervisor (from the token, never the body), replacing any
holder (audited `Assigned` / `Reassigned`); `unassign` returns it to the queue.
Push is an override: the assignee needs no membership. The assignee's Get Next
returns the pushed item first (it is their lease).

### Concurrency

Every item write is conditional on the item's `Version`:

| Store | Mechanism |
|---|---|
| MongoDB | `ReplaceOne({_id, tenantId, version: expected}, item with version+1)`; `MatchedCount == 0` = lost |
| In-memory (Development / tests) | compare-and-swap under one lock |

Of N callers leasing the same item exactly one write succeeds. The tests run 24
parallel Get Next calls against the in-memory store and a real mongod and
assert no item is handed out twice and nothing eligible is left while a caller
came back empty.

Known limit: two concurrent Get Next calls **by the same user** can each lease an
item (the own-lease check is a read). Harmless (both are theirs; one can be
released) and not worth a per-user lock in phase 1.

### Advisory in phase 1

The work service is advisory: a lease tells colleagues who is working an item.
**claims-service does not check leases** when a claim is resolved; its
`ResolutionLock` remains the hard concurrency guard on the decision. Enforcing
the lease on the claims side (e.g. resolve refuses a claim leased to someone
else) is deferred.

## Priority (one scale for every work type)

```
due        = attributes.Deadline ?? (ReceivedDate ?? CreatedAt) + queue.SlaDays
deadline   = due ≤ now ? 150 + min(50, 10 × days overdue)        (150..200)
                       : 100 × max(0, 1 − days to due ÷ SlaDays)  (0..100)
age        = min(100, 2 × days since received)
amount     = min(100, 20 × log10(1 + amount))                    ($10k ≈ 80, $100k = 100)
expedited  = 100 if expedited else 0
score      = Σ queue weight × points                             (rounded to 2 places)
```

With default weights an overdue item outranks any in-time, non-expedited item.
The item stores the score, a human-readable `priorityReason` (the terms and
their inputs) and `dueAt`. Because the score rises as an item waits, open items
are re-scored every `RescoreIntervalMinutes` (15).

Expedited for claims: an institutional emergency / urgent admission (admission
type 1 or 2).

## Work items

One item per source per tenant (`{tenant}.claim-{claimId}`), reopened if the
source needs work again. Fields: work type, `sourceRef` (`claim:{id}`),
`sourceId`, `sourceNumber` (claim number), attribute snapshot (pend codes,
primary pend, reason, fingerprint, LOB, claim type, amount, billing NPI/TIN/name,
network tier, expedited, received / service date, member id), queue, score,
reason, status (`Open`, `Leased`, `Done`, `Cancelled`), assignee, assigned-by,
lease expiry, created / updated / due / completed, resolution, `version`,
`sourceVersion`. Every transition writes a `WorkAuditEvent` (Created, Rerouted,
Leased, LeaseReclaimed, Renewed, Released, Assigned, Reassigned, Unassigned,
Completed, Reopened).

## Events and reconcile (claims as the work source)

### Event contract: `claims.work.v1` (Kafka)

Produced by claims-service on the existing claim-event producer
(`ClaimEventPublisher`, the transport of `claims.pended.v1` /
`claims.finalized.v1`), keyed by claim id. Payload `ClaimWorkEvent`
(`src/services/shared/CloudHealthOffice.Events`):

* `eventType`: `claim.work.needed` or `claim.work.resolved`
* `sourceVersion`: UTC ticks when claims-service observed the transition
* tenant, claim id / number, claim status, `resolution`, `actorId`
* routing attributes: primary pend code, all pend codes, pend reason and
  fingerprint, pended at, LOB, claim type, total charge, billing NPI / TIN /
  name, network tier, expedited, received date, service date, member id

Minimum necessary: no member name, diagnoses or service lines.

| Transition (claims-service) | Event |
|---|---|
| An adjudication run ends Pended (first adjudication, a new adjustment version, an approval re-run that found a new pend) | work-needed |
| Examiner resolve / supervisor override of a Pended claim (Pay or Deny) | work-resolved (`Approved` / `Deny`) |
| PAUNAVAIL bulk re-run takes a Pended claim to Pay / Deny | work-resolved |

A Pended claim cannot be voided or finalized (those need Pay / Deny / Paid
first), so those paths are not work transitions. Publishing never blocks or
fails the claim write (same degraded-mode posture as the other claim events),
and claims-service never calls work-management: resolving a claim does not
depend on work-management being up.

### Consumption

`ClaimWorkEventConsumer` (same wiring as accumulator-service's
`ClaimFinalizedConsumer`): auto-commit off, offset committed after the event is
applied, a failure seeks back and retries. Application is **idempotent and
order-safe**: an event whose `sourceVersion` is not newer than the item's last
applied one is ignored (duplicates, redeliveries, late arrivals); a resolved
event for an unknown claim leaves a `Done` tombstone so a late work-needed
cannot open work for a resolved claim. A work-needed with unchanged content does
not rewrite the item (no version churn under Get Next).

### Reconcile

`POST /api/v1/work/admin/reconcile`, and the periodic job: pages through
claims-service's `GET /api/claims/work-queue/snapshots` (every Pended claim as a
work-needed payload, built by the same mapping as the events, all stamped with
the moment the read started), applies each, then closes open items whose claim
is no longer pended and whose last event is older than the reconcile start.
Nothing is closed unless every page was read. The background job uses
work-management-service's own CHO service token for the tenant.

**Periodic reconcile default: automatic.** On exactly when the service has no
Kafka (`Kafka:BootstrapServers` empty), i.e. when it cannot receive events: the
local kind deploy (empty `kafka-secret`) and any environment whose bus is
in-memory. CHO's in-memory `IMessageBus` is process-local, so it could never
carry events across services; that is why claim work events ride Kafka and why
reconcile is the fallback. `WorkManagement:Reconcile:Enabled` forces it on or
off; `IntervalMinutes` (2), `PageSize` (500), `Tenants` (reconciled besides the
tenants already known). The AKS manifest forces it on (every 5 minutes) because
claims-service's own manifest does not wire `Kafka__BootstrapServers` yet, so
work events may not be produced there; turn it off once claims-service publishes. The **GKE demo** (Kafka on by default) also turns the
periodic reconcile on for tenant `demo` (`deploy/gcp/render_manifests.py`): it
picks up claims pended before the service started (seeded data) and repairs
anything lost.

### Enrollment exceptions (`Enrollment834`)

enrollment-import-service has no event stream yet, so its work flows by
reconcile alone: `WorkManagement:EnrollmentReconcile` (`Enabled` true,
`IntervalMinutes` 2, `PageSize` 500) pages through
`GET api/v1/enrollment/exceptions/work-snapshots` and applies each workable
exception as an item (`PrimaryPendCode` = the reason code, so routing uses the
same criteria as claims), then closes open items whose exception is resolved or
held. `POST admin/reconcile/enrollment` runs it on demand. Default queues are in
[enrollment-operations.md](enrollment-operations.md) §9.2; they are seeded with
the claim queues, and demo seed users get no membership in them until the
exception screen exists.

## API and permissions

Base `/api/v1/work`. Tenant and actor always from the CHO token.

| Endpoint | Permission |
|---|---|
| `POST next`, `GET mine`, `POST {id}/release`, `POST {id}/renew` | `workqueue:work` |
| `GET` (list: `queueId`, `status`, `assignedTo`, `workType`, paged, score order), `POST {id}/assign {userId}`, `POST {id}/unassign`, `GET assignees` | `workqueue:assign` |
| `GET queues` (with open / leased counts), `GET {id}` (with audit) | `workqueue:read` |
| `admin/queues` CRUD, `admin/queues/reorder`, `admin/teams` CRUD, `admin/memberships` CRUD, `admin/users/{userId}/queues`, `admin/recompute`, `admin/reconcile` | `workqueue:admin` |

Roles: ClaimsExaminer `workqueue:read`, `workqueue:work`; ClaimsSupervisor adds
`workqueue:assign`, `workqueue:reassign`, `workqueue:admin` (supervisors manage
their teams' memberships and the queue configuration); TenantAdmin all through
`*:*`; ComplianceOfficer `workqueue:read` through `*:read`. The four names are
constants on `ChoRolePermissions` (`WorkQueueRead/Work/Assign/Admin`), reusing
the `workqueue:*` names claims-service and the role catalogue already had.

## Portal

* **My Work** (`/my-work`, top of Operations, `workqueue:work`): Get Next → the
  item's `openUrl`; a friendly empty state on 204; the items I hold with queue,
  priority reason, due date, lease, Open / Renew / Release.
* **Work Queues** (`/work-queues`): the pend-category cards and claims list stay
  (claims-service); queue counts, priority and assignee / lease come from
  work-management, and the supervisor reassign select assigns / unassigns
  through it (it previously called a claims-service stub).
* **Admin › Work Management** (`/admin/work-management`, `workqueue:admin`):
  queues (criteria, order, SLA, weights, active, fallback), teams, memberships,
  effective queues for a user, Recompute, Reconcile.

## Configuration

`WorkManagement:LeaseMinutes` (30), `AssignedLeaseMinutes` (480),
`CandidatesPerQueue` (25), `GetNextRounds` (3), `RescoreIntervalMinutes` (15,
0 = off), `Reconcile:*` (above), `SeedMemberUserIds` (demo only: users given a
primary membership in every active claim queue of a tenant where they have none,
checked once per tenant per process; the GKE demo seeds `local-demo-admin`). Store: MongoDB (`MongoDb:ConnectionString`);
without it, the in-memory store, Development / Testing hosts only (any other host
refuses to start).

## Phase plan

1. **Claims pends** (this phase): the service, claim events + reconcile, My Work,
   Work Queues reassign, admin configuration.
2. **Appeals and prior-authorization reviews**: appeals-service and
   authorization-service publish work needed / resolved (their own outbox /
   events), appeal and PA criteria, `openUrl` to their screens; regulatory
   deadlines feed `attributes.deadline` (expedited 72-hour PA, appeal clocks).
3. **834 enrollment and payment exceptions**: enrollment-import and
   payment-service exceptions as work. *Enrollment built 2026-10-22 (reconcile
   only; see above).*

Forecasting arrival, projecting deadline breach and suggesting where people
should work are designed in
[workforce-optimization.md](workforce-optimization.md), which reads this
service's queues, due dates, memberships and audit stream and adds an hourly
`WorkQueueSnapshot` series to it. Nothing there is built.

Deferred from phase 1: lease enforcement in claims-service (the resolve refusing
a claim leased to someone else); per-user Get Next serialization; criteria for
non-claim work types; a user directory for the admin pickers (user ids are typed;
the reassign picker lists users named by memberships); claims-service has no
billing-provider TIN on the claim, so TIN criteria only match once a source
provides it.
