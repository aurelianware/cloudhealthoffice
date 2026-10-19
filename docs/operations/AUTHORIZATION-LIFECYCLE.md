# Authorization lifecycle and expiry

Ordinary reviewer status/response endpoints and the CHO backend enforce this
transition table. A forbidden transition returns HTTP 409 before the controller
changes the record or writes history.

| Current state | Allowed different states |
|---|---|
| Submitted | InReview, Pended, Approved, Modified, Denied, Cancelled |
| InReview | Pended, Approved, Modified, Denied, Cancelled |
| Pended | InReview, Approved, Modified, Denied, Cancelled |
| Approved / Modified | Cancelled, Expired |
| Denied / Cancelled / Expired | None |

Same-state reviewer decisions remain supported with validation and audit history.
Cancelled/Expired repeats are no-ops. Ordinary endpoints do not reopen records or
change a final decision status. Appeals/overturns, authorization amendments and
explicit reopening need their own authorized workflows; this change does not
invent them. Development fixture seeding remains an explicit development-only
administrative operation.

Cancellation uses the selected backend and appends actor/history instead of
writing directly to the local repository. RFAI completion returns Pended to
InReview, clears the stale header A4, and keeps the original decision deadline.

The registered expiry worker runs every 15 minutes and processes up to 200
eligible approvals per tenant per tick. Approved service-end and expiration
fields are inclusive UTC calendar dates; the first such date to end makes the
record eligible on the following UTC day. This date-based worker does not claim
time-of-day expiration. Records without an approved end/expiration date are not
assigned an invented expiry date. Claims validation continues to check its
approved period independently. Expired approvals can validate an explicit past
service date within the recorded covered period, including code and provider
checks. They cannot validate current/future services or an unspecified service
date. Cancellation remains invalid; expiry does not retroactively revoke coverage
for a service already provided within the approved period.

Startup ensures tenant/status indexes for expiration and approved service-end dates before the worker starts. Index build time is part of rollout readiness.

The worker lists tenants without an HTTP context. Each expiry is a Mongo patch
conditional on tenant, id, revision, approved/modified status and an ended period.
Status, audit actor/timestamp, history and revision change atomically.
The original clinical A1/A2 outcome remains on the record. Reporting resolves
clinical outcomes from decision history (with the stored code as a legacy fallback),
so expiry/withdrawal does not erase approval metrics, original turnaround or
automation provenance. Recent decisions expose both clinical `status` and current
`lifecycleStatus`. Expired approvals remain in ended/unused-authorization reports. A concurrent
period change or cancellation wins over a stale sweep. Unknown fields survive;
legacy null history is initialized safely. No documents are deleted.

Tenant lists are cached for one hour. One record/tenant failure does not stop
others. Database-per-tenant background enumeration (`MongoDb:UseTenantScoping=true`)
remains unsupported (D-38); this worker is explicitly disabled in that mode.
No production deployment was performed. Ordinary lifecycle/expiry implements
G-24/D-23 for the supported shared-database mode; independent appeals/overturn
integration and tenant-database sweeps remain separate work.
