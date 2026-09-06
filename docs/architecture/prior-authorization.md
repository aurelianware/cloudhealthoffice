# Prior Authorization (Da Vinci PAS)

CMS-0057-F Prior Authorization as Cloud Health Office implements it: the
`Claim/$submit` request/response, the `Claim/$inquire` status inquiry, and how
both project onto the one authorization record.

Acceptance scenarios: **PAS-01** (CRD), **PAS-02** (DTR), **PAS-03** (submit),
**PAS-04** ([inquiry](#claiminquire)), **PAS-05** (coded denial), **PAS-06**
(decision timeframe), **PAS-07** (CDex — still PARTIAL), **PAS-08** (drug
exclusion).

## Topology

```
POST fhir/r4/Claim/$submit          POST fhir/r4/Claim/$inquire
  └─ PasController                    └─ PasController
       ├─ ValidateAndExtractClaim          ├─ ValidateAndExtractInquiryClaim
       ├─ Cms0057ComplianceChecker         ├─ IPriorAuthorizationInquiryService
       ├─ IPasAutoAdjudicator              │    ├─ tenant match
       ├─ PasResponseBuilder               │    ├─ corroborating key match
       └─ persist ─────────┐               │    └─ IPriorAuthorizationStore (read-only)
                           │               └─ PasResponseBuilder.BuildInquiryResponse
                           ▼                          ▲
                 authorization-service ───────────────┘
                 (the one authoritative record)
```

Both operations run through one controller, one response builder, and one
authorization record. `$inquire` adds **no** store and **no** status field of
its own — it is a read projection of the state `$submit` wrote and the rest of
the platform updates.

## `Claim/$inquire`

**Route:** `POST /fhir/r4/Claim/$inquire`
**Content:** `application/fhir+json`

### Request

A PAS Bundle carrying a `Claim` with `use = preauthorization`:

| Element | Purpose |
| --- | --- |
| `Claim.identifier[].value` | the authorization number (the `preAuthRef` issued at submit) |
| `Claim.insurance[].preAuthRef` | accepted as an alternative source of the same number |
| `Claim.patient.reference` | corroborating key — `Patient/pat-001` or `pat-001` |
| `Claim.provider.identifier.value` | corroborating key — requesting provider NPI |

The bundle is validated with the same guards as `$submit`: entry cap, resource
type allowlist, and a `Claim` that must be present. It does **not** require
`$submit`'s full provider/insurance detail — an inquiry names an authorization,
it does not restate the request.

### Lookup semantics

The authorization number **alone is never sufficient**. Numbers are structured
(`PAS-yyyyMMdd-xxxxxxxx`) and therefore guessable at the margins, so an inquiry
must also carry a corroborating key — the member or the requesting provider NPI
— and that key must match the stored record. A supplied key that does *not*
match refuses even when another one does: naming the wrong member for a real
authorization is guessing, and guessing must not get a different answer than a
miss.

Tenant comes from the authenticated context and must match the record. The
check is applied in fhir-service on the record itself, so it holds even if the
tenant header propagation to authorization-service is ever lost.

### Status mapping

Deterministic and total over the authorization status enum:

| CHO status | X12 278 | `ClaimResponse.status` | `outcome` | `disposition` | reviewAction |
| --- | --- | --- | --- | --- | --- |
| Submitted | — | active | queued | `pending` | — |
| InReview | — | active | queued | `pending` | — |
| Pended | A4 | active | queued | `pended-additional-information` | A4 |
| Approved | A1 | active | complete | `approved` | A1 |
| Modified | A2 | active | partial | `modified` | A2 |
| Denied | A3 | active | complete | `denied` | A3 |
| Expired | — | active | complete | `expired` | — |
| Cancelled | — | cancelled | complete | `cancelled` | — |

`outcome` carries the coarse machine answer — still working, decided, partially
decided — and `disposition` the specific one, so a caller can distinguish
**pending** from **pended-for-additional-information** from **approved** from
**denied**. An unrecognised status reads as still in progress rather than as an
approval CHO cannot vouch for.

The A4 reviewAction reports that a decision is outstanding pending information.
It is **not** a CDex exchange — see [Limitations](#limitations).

### Response

A Bundle carrying a `ClaimResponse` on the PAS profile, populated only with what
CHO can say truthfully:

`identifier` and `preAuthRef` (the authorization number) · `status` · `outcome` ·
`disposition` · `use = preauthorization` · `patient` · `insurer` ·
`preAuthPeriod` (when an approved period or expiry exists) · the reviewAction
extension (when a review decision exists) · `error` with the coded denial reason
(on denials) · `processNote` with the pend reason (when the record holds one) ·
`item[].adjudication` for requested and, where decided, approved units ·
`meta.lastUpdated` from the record's own last-updated instant, so a caller can
see the state is current.

### Status freshness

Every inquiry reads live committed state. There is no submission-time snapshot
and no cache, so a status changed after submission — pended, approved, denied,
cancelled — is the status returned by the next inquiry.

### Read-only semantics

Read-only **by contract**: `IPriorAuthorizationStore` exposes a single lookup
method and no write method at all, asserted by a structural test. An inquiry
therefore cannot create a record, move a status, restart a decision clock, emit
a duplicate transaction, or cause a payer submission — however many times it is
repeated. CHO's status derives from a stored adjudication record, not a live
re-query, so an inquiry never turns into an outbound X12 transaction.

### Error responses

| Condition | Response |
| --- | --- |
| Malformed bundle, wrong `Claim.use`, disallowed resource type | `400` + `OperationOutcome` |
| Missing authorization identifier | `400` + `OperationOutcome` naming what is missing |
| Missing corroborating key | `400` + `OperationOutcome` naming what is missing |
| Unknown / wrong tenant / not the caller's | one uniform `404` + `OperationOutcome` |

The split matters: a defect in the **request** is the caller's to fix and says
nothing about what exists, so it is described plainly. A refusal about a
**record** is uniform. Collapsing the first into the second would tell a caller
who forgot an identifier that their authorization does not exist.

No response carries store, query, or implementation detail.

### Authorization controls

As a POST operation, `$inquire` is governed by the same controls as `$submit`:

* **authentication** — `[Authorize]` on the controller;
* **SMART scope** — `SmartScopeEnforcementMiddleware` requires `*/Claim.read`;
* **tenant** — from the authenticated context, never the request body;
* **corroborating key** — the lookup rule above.

It is deliberately **not** routed through the Provider Access consent gate.
That gate governs a provider reading a member's clinical record; PAS is a
system-to-system transaction between the submitter and the payer about the
submitter's *own* request. Forcing a member's Provider-Access consent onto a
provider asking after their own prior-authorization would be the wrong control
in the wrong place — which is why the corroborating key, not a consent, is what
binds an inquiry to its authorization. See
[Provider Access](provider-access.md) for what that gate does cover.

### Anti-enumeration

Unknown authorization, wrong tenant, and not-your-authorization all return one
identical `404` FHIR `OperationOutcome`. A caller cannot tell them apart, so the
identifier space cannot be probed for which authorizations exist. The
distinguishing category (`NotFound`, `TenantMismatch`, `NotAuthorizedForCaller`,
`MissingIdentifier`, `MissingCorroboratingKey`) is kept in the audit record.

### Audit

Each inquiry logs tenant, caller, the authorization number asked about, the
outcome category, and the status returned. Never the Claim, the ClaimResponse,
demographics, clinical content, tokens, or credentials. CR/LF is stripped from
ids reaching a log line (CWE-117).

## What `$submit` changed for inquiry

Making status inquirable required fixing the write side:

* **every outcome now carries a tracking handle.** `preAuthRef` was set only on
  approvals, and a pended submission persisted an authorization number that was
  never told to the caller — so the one outcome that most needs following up was
  un-inquirable. Approved, denied and pended responses now all carry the number
  that was persisted.
* **denial code and reason are persisted**, so an inquiry answers *why*, not
  just "denied".
* **the approved period is persisted**, so an inquiry can report `preAuthPeriod`.
* **requested services come from the submitted Claim** rather than a placeholder
  procedure code.
* **the authorization HTTP client propagates the tenant header.** Without it,
  authorization-service falls back to its default partition — reads and writes
  would have crossed tenants.

## Data retention

Prior-authorization records have an explicit, testable lifecycle. The rule lives
in one place — `IPriorAuthorizationRetentionPolicy` — and the sweeper only
discovers records it applies to, so a job that runs late, early, or twice reaches
the same answer.

### The rule

```
RetentionUntil = last status change + retention period
purgeable      = status is terminal  AND  now >= RetentionUntil
```

**Period.** CMS-0057-F states a **minimum** — retain prior-authorization data for
at least one year after the last status change — not a maximum. The one-year
figure is enforced as a **floor that configuration cannot go under**, and the
default is **six years**, matching the HIPAA posture already used for member
documents. Defaulting a destructive job to the bare regulatory minimum would
quietly make prior-auth the shortest-retained regulated data in the platform.

**Anchor.** The last change to the authorization's lifecycle:
`StatusHistory.ChangedAt` (max), falling back to `ReviewedDate`, then
`SubmittedDate`. Deliberately **not** `LastUpdatedDate` — every write touches it,
so an unrelated edit would silently move the boundary — and never a read. **An
inquiry cannot extend a record's regulatory retention**, which is asserted by
test.

Records with no establishable anchor are **kept**, not purged.

### Terminal versus open

| Status | | Purgeable |
| --- | --- | --- |
| Submitted, InReview, Pended | open | **never**, however old |
| Approved, Modified, Denied, Expired, Cancelled | terminal | once past the boundary |

Both conditions always apply. An ancient timestamp is not permission to delete an
authorization that is still operationally live — a pended decision may still be
waiting on information no matter how long it has waited. The open/terminal split
was previously spelled out separately in the SLA watchdog, the RFAI consumer and
both repositories; it is now defined once as `AuthorizationStatus.IsOpen()` /
`IsTerminal()` and is total over the enum.

### What is covered

The `Authorization` document is the authoritative prior-authorization record and
**embeds** its diagnosis codes, requested service lines, clinical-attachment
metadata and status history. Deleting the document deletes all of them — there
are no separate collections to orphan, and the PAS `ClaimResponse` is projected
on read rather than stored, so no FHIR artifact survives the purge.

Deletion is **hard**. Nothing is retained behind a soft-delete flag while
claiming to be purged.

Outside this boundary, and documented rather than deleted blindly:

* **blobs behind `ClinicalAttachment.FileUrl`** — the pointer is removed with the
  document; the blob's own lifecycle is storage-tier policy;
* **`rfai-service` `RfaiCase` records** keyed by authorization number —
  `IRfaiRepository` has no delete path at all, so they are explicitly out of
  scope rather than half-handled.

### The sweeper

`PriorAuthorizationRetentionWorker`, a `BackgroundService` modelled on
provider-service's `IntegrityProjectionWorker`:

* **disabled by default** — a destructive sweep opts in per deployment;
* a **scope per tenant** from `IServiceScopeFactory`, never a repository captured
  for the process lifetime;
* **tenant explicit on every call** — `ListTenantIdsAsync`,
  `FindRetentionCandidatesAsync` and `PurgeIfStillEligibleAsync` all take it as
  an argument, so the sweep never depends on an ambient `HttpContext`;
* **bounded** — `MaxRecordsPerTenantPerSweep` caps work; no unbounded load;
* **cancellation observed** between every tenant and every record;
* **dry-run mode**, following the convention the Cosmos claims migration uses.

Cadence (default daily) affects only *when* eligible records are discovered,
never *whether* a record is eligible.

### Concurrency and idempotency

Purge is a **conditional delete** predicated on the status the sweep decided
against:

* **Mongo** — one atomic `DeleteOne` whose filter includes id, tenant and
  expected status;
* **Cosmos** — a re-read inside the purge, carrying that read's ETag into
  `DeleteItemAsync` as `IfMatchEtag`.

So a record that reopens between being listed and being purged **survives**: the
predicate no longer matches. `Authorization` has no version field of its own; the
store's ETag is the concurrency token available.

Repeated sweeps are safe — an already-purged record returns false, not an error —
and a failed batch simply retries next interval, because each record is an
independent conditional delete rather than part of a transaction.

### Tenant isolation

Every query and every delete predicate carries the tenant. A purge naming the
wrong tenant refuses outright, asserted by test. Tenant iteration comes from
`ListTenantIdsAsync` on the authorization store itself, so no second service is
needed and no tenant's policy is resolved from another's configuration.

### Audit and metrics

Normal operation reports **aggregate counts** (`scanned`, `purged`, `skipped`,
`failed`) per sweep. Per-record purge lines carry PHI-free identifiers only:
tenant, opaque authorization id, policy version, retention boundary, status. Never
a member, a payload, a denial narrative, or a credential; CR/LF is stripped
(CWE-117).

`ChoMetrics.PriorAuthorizationRetentionOutcomes`
(`cho.authorization.retention.outcomes.total`) is dimensioned by `cho.outcome`
(`purged` | `would_purge` | `skipped` | `failed`), `cho.dry_run` and
`cho.tenant_id` — never by member, provider or authorization identity.

### Freshness — why there is no sync job

PAT-03's other half is *"update PA data within 1 business day"*. That obligation
bounds how stale a **copy** may be. Cloud Health Office keeps no copy: PA state is
projected from the authoritative record at **read** time, so the interval between
a status change and its visibility is **zero** and there is nothing for a
freshness job to synchronise.

What makes that structural rather than incidental is that the read seam cannot
hold state — `IPriorAuthorizationStore` exposes a single lookup and no write — so
no cached or replicated projection can exist behind it to drift. The absence of a
freshness job is the design, not a gap.

### `$inquire` before and after purge

A retained record is queryable through `Claim/$inquire` for its whole retention
period. Once purged, the lookup returns nothing and the inquiry collapses to the
same uniform `404` `OperationOutcome` as an unknown or inaccessible
authorization — a purged record is indistinguishable from one that never existed,
which is the correct anti-enumeration behaviour.

### Retention limitations

* Purging does not delete blobs referenced by `ClinicalAttachment.FileUrl`, nor
  `rfai-service` `RfaiCase` records (which have no delete path). Both are named
  above rather than silently left.
* There is no legal-hold flag: a record past its boundary in an enabled tenant is
  purged. Holds are a deployment concern today (leave the sweep disabled).
* The candidate query pre-filters on `SubmittedDate` as a coarse floor and the
  policy makes the real decision per record, so a sweep may examine more records
  than it purges. That is deliberate — the alternative is trusting a denormalised
  anchor column that nothing maintains.

## Limitations

* **PAS-07 (CDex) remains PARTIAL.** `$inquire` *reports* that a decision is
  pended awaiting information, which CHO already knows from the A4 review
  decision. It neither requests documentation nor accepts it; that round-trip is
  what CDex is, and it is not implemented.
* **An inquiry is bound to its authorization by the corroborating key, not by
  the caller's own identity.** PAS is system-to-system here: `$submit` does not
  check that the caller *is* the provider named in the Claim, and there is no
  mapping in this repository from a token subject to a provider NPI. So a caller
  who knows both the authorization number and a matching member or provider key
  can read it, within their own tenant. Inventing a subject-to-NPI mapping to
  close that would be security theatre — NPIs are public — so the caller is
  recorded in the audit trail instead, and tightening this waits on a real
  provider identity claim in the token.
* **No optimistic concurrency on authorization records.** There is no ETag or
  row version, so updates are last-write-wins and an inquiry reads committed
  state rather than a versioned snapshot. Partially persisted status changes are
  not exposed because a status write is a single document replace, but two
  concurrent updates can still overwrite one another — a pre-existing property of
  the store, not of the inquiry.
* **No X12 278 parser** exists in the .NET services. Authorization status derives
  from the adjudication record and the review-decision DTO, not from a parsed
  278; the 278 vocabulary is modelled but the transaction is not processed.
* Lookup is by authorization number plus a corroborating key. Searching by
  member or provider alone — enumerating a provider's open authorizations — is
  not offered through `$inquire`.
* Some fields the submit path persists are still placeholders (patient name,
  date of birth, line of business). They are deliberately **not** projected into
  the inquiry response, which carries no demographics at all.
* Zero GAPs in the acceptance suite is **not** complete CMS-0057-F compliance.
  This is implementation evidence, not certification.
