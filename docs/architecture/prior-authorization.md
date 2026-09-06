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

## Limitations

* **PAS-07 (CDex) remains PARTIAL.** `$inquire` *reports* that a decision is
  pended awaiting information, which CHO already knows from the A4 review
  decision. It neither requests documentation nor accepts it; that round-trip is
  what CDex is, and it is not implemented.
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
