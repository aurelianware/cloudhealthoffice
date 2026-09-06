# Additional information on a pended prior authorization (Da Vinci CDex)

How Cloud Health Office asks a provider for documentation when a prior
authorization is pended, and how the provider sends it back.

Acceptance scenario: **PAS-07**. Behaviour is proven end to end in
`tests/Cms0057Acceptance.Tests/Scenarios/CdexAdditionalInformationTests.cs`
against the real services; the aggregate's own rules are covered in
`src/services/rfai-service/RfaiService.Tests`.

---

## Which CDex interaction, and why

Cloud Health Office is the payer, so the exchange it needs is the **solicited**
one: the payer asks for documentation on a prior authorization it has pended, and
the provider sends it. In the Da Vinci Clinical Data Exchange IG that is:

| Half | Standard shape | Served at |
| --- | --- | --- |
| Request | `Task` on the **CDex Task Attachment Request** profile (`http://hl7.org/fhir/us/davinci-cdex/StructureDefinition/cdex-task-attachment-request`) | `GET fhir/r4/Task/{id}`, `GET fhir/r4/Task?…` |
| Response | **`$submit-attachment`** (`http://hl7.org/fhir/us/davinci-cdex/OperationDefinition/submit-attachment`) | `POST fhir/r4/$submit-attachment` |

CDex's *Task Data Request* profile — a payer querying a provider's clinical
record — is a different transaction and is deliberately **not** what a pended
prior authorization uses.

**Pull, not push.** CHO makes the request available for retrieval rather than
POSTing a Task into the provider's FHIR server. There is no provider FHIR
endpoint registry in this repository to push to; see
[Limitations](#limitations).

---

## Topology

```
278 A4 review decision  (POST api/authorizations/{id}/response, or a status update)
  └─ AuthorizationsController
       └─ PendedAuthorizationRfaiCoordinator      (authorization-service)
            └─ IRfaiRequestGateway  ── HTTP ──►  rfai-service
                                                   └─ RfaiCaseService
                                                        └─ RfaiCaseLifecycle
                                                             └─ RfaiCase  ◄── THE record
                                                                  ▲   ▲
   GET fhir/r4/Task/{id}                                          │   │
     └─ TaskController ─► ICdexAdditionalInformationStore ────────┘   │
          └─ CdexTaskMapper  (CDex Task Attachment Request)           │
                                                                      │
   POST fhir/r4/$submit-attachment                                    │
     └─ CdexController                                                │
          └─ CdexAttachmentSubmissionService ──────────────────────────┘
               ├─ CdexSubmitAttachmentParameters   (read the Parameters)
               ├─ CdexAttachmentPolicy             (what CHO will accept)
               ├─ IAttachmentContentScanner        (seam)
               └─ IClaimAttachmentContentStore     (the bytes)
                        │
                        └─ rfai-docs-received ─► RfaiDocsReceivedConsumer
                                                   └─ authorization → InReview
```

There is exactly **one** additional-information record — rfai-service's
`RfaiCase` — and everything above reads or writes that. The FHIR `Task` is a
projection of it, computed on read; authorization-service keeps only the
**handle** (`Authorization.RFAIReference`), never a copy.

---

## 1. What creates a request

`PendedAuthorizationRfaiCoordinator.EnsureRequestForDecisionAsync`, called from
`AuthorizationsController` when a decision is recorded. It raises a request only
when **both** hold:

1. the authorization is `Pended` with review decision **`A4`** — pended,
   additional information required; **and**
2. the decision **names what documentation is wanted**
   (`AuthorizationResponse.RequestedInformation`, or
   `AuthorizationStatusUpdate.RequestedInformation`).

A pended state alone is not enough. A decision that asks the provider for
nothing has not asked them anything, and manufacturing a documentation request
from it would put a question to the provider that no reviewer posed. That case is
recorded in the audit trail and the authorization stays pended exactly as before.
`A1`, `A2`, `A3` and an ordinary `InReview` state raise nothing.

The `PUT api/authorizations/{id}/status` path calls the same coordinator, which
makes it the **repair path**: an authorization left `Pended`/`A4` with
`RFAIIssued = false` (because rfai-service was unreachable at decision time) gets
its request there, under the same correlation key.

---

## 2. Lifecycle

`RfaiCase` has four states. They are CHO's own, unchanged from before this work;
the conceptual RFAI lifecycle maps onto them exactly:

| Conceptual | `RfaiStatus` | `Task.status` | `Task.businessStatus` |
| --- | --- | --- | --- |
| Requested / AwaitingResponse | `Open` | `requested` | `Open` |
| ResponseReceived / AcceptedForReview | `DocsReceived` | `completed` | `DocsReceived` |
| Closed, answered | `Closed` | `completed` | `Closed` |
| Closed, unanswered | `Closed` | `failed` | `Closed` |
| Cancelled | `Cancelled` | `cancelled` | `Cancelled` |

FHIR's `Task.status` vocabulary is narrower than CHO's states, so the CHO state
is carried on `Task.businessStatus` as well — nothing is lost in translation. A
cycle closed *without* the information reads as `failed`, not `completed`:
reporting both the same way would tell a provider their unanswered request had
been satisfied.

Two states that deliberately do **not** exist:

* **`InvalidResponse`** is not a state. An invalid submission is refused at
  intake and the request stays `Open`, so a rejected attempt can never consume
  the provider's one chance to answer.
* **`Expired`** is not stored. Expiry is *derived* from `DueDate`
  (`RfaiCase.IsOverdue`), because nothing in this repository sweeps due dates and
  a stored state nothing ever sets would be a lie in the data.

`DocsReceived` is not an ending: a request that has been answered still accepts
supplementary documents, and a retry has to reach the duplicate check to be
recognised as a replay. Only `Closed` and `Cancelled` end a request.

The **prior-authorization** lifecycle is not duplicated. The two coordinate and
stay distinct:

```
PA = Pended     RFAI = Open           payer waiting on the provider
documentation received
PA = InReview   RFAI = DocsReceived   reviewer can look again
```

---

## 3. Correlation

```
Tenant
  └─ Authorization (AuthNumber, AuthorizationId)
       └─ RfaiCase (Id, TrackingId, Sequence)
            └─ ReceivedAttachment (SubmissionId)
                 └─ stored artifact (Container + StorageKey + SHA-256)
```

* **`AuthNumber`** — the 278 TRN02 / PAS `preAuthRef` the submitter already
  holds. Also on `Task.focus` (reference *and* identifier).
* **`TrackingId`** — the provider-facing handle; the CDex `TrackingId` and the
  X12 275 attachment control number. **Random**, not derived: it is one of the
  keys an intake must match, so deriving it from facts a caller already knows
  would hand it to anyone who knows them.
* **`Sequence`** — 1-based cycle number. A later cycle is a new record.
* **`SubmissionId`** — identity of one submission; see
  [Idempotency](#7-idempotency).

`Authorization.RFAIReference` holds the tracking id, `RFAIIssued`/`RFAIIssuedDate`
record that the request went out, and `RFAIResponseDate` records the first
arrival.

---

## 4. Request content

Structured first. Each `RequestedItem` carries:

| Field | Meaning |
| --- | --- |
| `Code` | X12 PWK attachment-type code — for the 277/275 wire |
| `LoincCode` | LOINC document-type code — for the FHIR/CDex wire |
| `Description` | human-readable; **supplements** the codes, never replaces them |
| `Required` | whether the item is mandatory |
| `ServiceLineProcedureCode` | which requested line the question is about |
| `DiagnosisCode` | diagnosis context (ICD-10) |

Plus, on the case: `DueDate`, `ReasonCode`/`ReasonDescription`, `ReviewDecision`
(`A4`), `Notes` (free text supplement), and the provenance fields in §10.

A request with no items is refused at creation — the same rule that stops a
generic pended state becoming a documentation request.

---

## 5. Standards-facing retrieval

```
GET  /fhir/r4/Task/{id}                                  one request
GET  /fhir/r4/Task?identifier={trackingId}               by tracking id
GET  /fhir/r4/Task?code=attachment-request-code&focus=Claim/{authNumber}
                                                         the cycles on one PA
```

Both a bare code and the `system|code` token form are accepted for `code`.
`Task/{id}` dispatches to the CDex store when the id carries the reserved
`rfai-` prefix — the case's own document-id prefix — so no lookup is attempted
against both the appeal store and this one. Everything else on `Task` remains the
appeal projection, unchanged.

The projected Task carries:

| Element | Value |
| --- | --- |
| `meta.profile` | `cdex-task-attachment-request` |
| `code` | `cdex-temp#attachment-request-code` |
| `identifier` | tracking id (official), authorization number (secondary) |
| `focus` | `Claim/{authNumber}` + identifier — the prior authorization |
| `for` | `Patient/{memberId}` |
| `owner` | Organization, identified by the requesting provider's NPI |
| `requester` | the payer |
| `reasonCode` | X12 306 `A4`, plus the reviewer's coded reason |
| `restriction.period.end` | the due date |
| `input` | one `attachment-code` per requested item (LOINC + PWK), `line-item`, `diagnosis-context` (ICD-10, on CHO's own input code system — CDex has none, and typing a diagnosis as `attachment-code` would read as a document being requested), `purpose-of-use` = `COVAUTH`, `signature-flag` = false |
| `output` | one entry per accepted artifact — see below |
| `businessStatus` | CHO's own RFAI state |
| `note` | free-text supplement |

A bare `code=` search with neither `identifier` nor `focus` returns nothing
rather than every outstanding request in the tenant: a documentation request is
addressed to *one* provider, and this endpoint has no provider identity to filter
by (see [Limitations](#limitations)).

Reading a request stamps delivery provenance (`FirstDeliveredAt`,
`LastDeliveredAt`, `DeliveryCount`) via an explicit action, best-effort — failing
to record that a request was delivered must not stop a provider learning what the
payer needs.

---

## 6. Response submission

```
POST /fhir/r4/$submit-attachment
Content-Type: application/fhir+json
```

Body: the CDex `Parameters` resource.

| Parameter | Required | Notes |
| --- | --- | --- |
| `TrackingId` | yes | names the request being answered |
| `AttachTo` | yes | Identifier (a plain string is also read) naming the prior authorization |
| `Provider` | when the request records a provider | Reference carrying the NPI |
| `Organization` | — | the payer; read but not used as authority |
| `Attachment` (0..*) | yes, ≥ 1 | `Code` part (CodeableConcept) + `Content` part (`Attachment`); a supplied `DocumentReference` resource is also read |

**Correlation.** A submission is bound to its request by four things that must
all agree: the tenant (from the authenticated context, never the payload), the
tracking id, the authorization named in `AttachTo`, and — where the request
records one — the submitting provider's NPI. **Knowing an authorization number
attaches nothing**; that is what the tracking id is for.

**Accepted artifact types.** `application/pdf`, `image/jpeg`, `image/png`,
`image/tiff`, `text/plain`, `text/rtf`, `application/rtf`, `text/xml`,
`application/xml`, `application/hl7-cda+xml`, `application/fhir+json`,
`application/json`. Anything else is refused rather than stored and hoped for.

**Limits**, enforced before anything is stored, all-or-nothing per call:

| Limit | Value |
| --- | --- |
| Per attachment | 20 MB decoded |
| Per call | 50 MB decoded, 10 attachments |
| Per request (all calls) | 25 artifacts, enforced by the aggregate itself |

**Never fetched.** `Attachment.url` is captured only so it can be refused
explicitly. CHO does not dereference a caller-supplied URL — that would make the
payer's server fetch whatever the submitter points it at.

**Storage keys are server-derived.** The bytes go to the platform's existing
`IClaimAttachmentContentStore`, which builds the key from tenant, request,
submission id, checksum and validated content type. No part of a caller's
filename, title or path reaches it; the title is kept as sanitised metadata only.

**Scanning** goes through `IAttachmentContentScanner` before the bytes are
written. The default implementation, `UnscannedAttachmentContentScanner`, scans
nothing, says so at startup, and records scan status `Unknown` rather than
`Safe` — nothing downstream may read the absence of a scanner as a clean verdict.

---

## 7. Idempotency

Two independent keys, at the two points where duplication would hurt.

**Request creation** — the document id *is* the idempotency key:

```
id = "rfai-" + sha256(tenant + authNumber + correlationKey)[..32]
correlationKey = sha256(tenant | authNumber | "A4" | 278-response-control-number)
                 (falling back to a digest of the decision's content)
```

Two workers handling one A4 event derive the same id, both attempt the insert,
and the conditional create (Cosmos 409 / Mongo duplicate key) lets exactly one
through; the loser reads back the winner's case. A redelivered event replays onto
the request the first delivery created, **whatever status it has since reached** —
otherwise a redelivery after the cycle closed would open a second one. A request
created without a correlation key gets an `rfai-adhoc-` id, so the absence of
replay protection is visible rather than hidden.

At most **one cycle is open per authorization**: two concurrent requests would
leave the provider guessing which one their documents answer.

**Response intake** — the submission id is content-derived:

```
submissionId = sha256(tenant | requestId | trackingId | sha256(bytes))[..32]
```

A retry of the same document lands on the same id and records nothing. A
materially **different** document under the same request gets a different id and
is **appended as an additional response**, never an overwrite. The resume-review
announcement is published only on the transition into `DocsReceived`, so a replay
cannot restart the review clock a second time.

---

## 8. What happens to the authorization

`rfai-service` publishes `rfai-docs-received`; `RfaiDocsReceivedConsumer` in
authorization-service consumes it and moves `Pended → InReview`, sets
`SlaResumedAt`, stamps `RFAIResponseDate`, and appends a status-history entry
recording *why*.

**Receiving documents never approves anything.** The most this path does is
return the authorization to review; whether the documents actually answer the
clinical question is the reviewer's call. An acceptance test asserts this
explicitly. A partial delivery records the arrival but leaves the status pended
and the decision clock stopped. A decided authorization (Approved / Modified /
Denied / Expired / Cancelled) is not reopened by documents arriving late.

---

## 9. `Claim/$inquire`

The inquiry follows the lifecycle rather than freezing at A4:

| Authorization state | `outcome` | `disposition` | reviewAction |
| --- | --- | --- | --- |
| `Pended` (waiting on the provider) | `queued` | `pended-additional-information` | `A4` |
| `InReview` (documents received) | `queued` | `pending` | — |

So a submitter polling `$inquire` sees the pend clear once their documentation is
in. `$inquire` is unchanged by this work — it already projected the one
authorization record — but its A4 disposition is now backed by a real request the
provider can retrieve and answer.

---

## 10. Security, tenancy and provider isolation

**Authentication and scopes.** Every route under `/fhir/r4` requires a validated
token. `Task` reads need a `Task` **read** scope. `$submit-attachment` needs a
`Task` **write** scope in a `user/` or `system/` context — a read scope is not
enough to put documents into a payer's record, and a patient-context token is not
an acceptable caller for a provider/payer transaction however it is scoped.

`SmartScopeEnforcementMiddleware` was reworked for this, and the rework fixed a
wider hole. It previously derived the scope from the resource-type path segment
and always demanded `.read`, so a path naming no resource type
(`/fhir/r4/$submit-attachment`) fell through its "unknown path" branch
**unenforced**, and every write under `/fhir/r4` — `POST Claim/$submit` included —
was authorized by a read scope. A request is now resolved into the interaction it
actually is:

| Interaction | Access |
| --- | --- |
| A classified operation (`Claim/$submit`, `$submit-attachment`, …) | as declared |
| An unclassified operation | from the HTTP method |
| Plain REST | GET/HEAD read; POST/PUT/PATCH/DELETE write |

Operations are classified explicitly because their HTTP method says nothing about
their effect — `$inquire` and `$member-match` are POSTs that read. Nothing falls
through to unenforced. smart-auth-service issues the corresponding write scopes
and `.well-known/smart-configuration` advertises them; there is deliberately no
patient-context write scope.

**Tenant** comes from the authenticated context on every path. It is never read
from a `Task`, a `Parameters` payload, an identifier system, an `Organization`
reference, or a route segment. The store seam has no lookup that omits a tenant,
asserted structurally, and rfai-service's own `by-auth/{tenantId}/…` legacy route
now honours a path tenant only when it *matches* the authenticated one.

**Provider isolation.** A submission must name the provider the request was
addressed to. A request that records no provider says so in the audit trail
rather than pretending the check was made.

**Not Provider Access consent.** This exchange is deliberately outside the
Provider Access consent gate. That gate governs a provider *reading a member's
clinical record*; this is a payer/provider transaction about the submitter's own
prior-authorization request, governed by the PAS/CDex authorization model. The
separation introduced with the shared consent registry (CONSENT-01) is preserved,
not borrowed from.

**Anti-enumeration.** Every refusal about a *record* — unknown tracking id, other
tenant, other authorization, other provider — returns **one identical 404**. The
distinguishing category survives only in a PHI-free audit line. Defects in the
*request itself* (no tracking id, no `AttachTo`) are described plainly as 400
because they say nothing about what exists; payload defects are 422; and a fully
correlated but closed request is told so with a 409, because the caller has
already proven it is theirs.

---

## 11. Audit and provenance

Structured, PHI-free events with safe identifiers only.

| Event | Where |
| --- | --- |
| Request raised / already existed | `PendedAuthorizationRfaiCoordinator`, `RfaiCaseService` |
| Request delivered to the requester | `RfaiCase.FirstDeliveredAt` / `LastDeliveredAt` / `DeliveryCount` |
| Response accepted, duplicate replayed, refused | `CdexController.Audit` |
| Authorization returned to review | `Authorization.StatusHistory` + `RfaiDocsReceivedConsumer` |
| Request closed / cancelled | `RfaiCase.ClosedBy` / `ClosedAt` / `ClosureReason` |

Also on the record: who created the request (`RequestedBy`), **why**
(`RequestSource` = `review-decision-a4`, plus the `ReviewDecision` itself), who
submitted each artifact (`SubmittedBy`), through which channel (`Channel`), and
its integrity hash.

Never logged: attachment content, document titles, diagnoses, notes, member
demographics, raw FHIR payloads, tokens or credentials. The resume-review
announcement carries control numbers and submission ids only. Audit lines are
CR/LF-scrubbed.

---

## Limitations

* **Caller binding.** The submitter is bound by the tracking id and the
  corroborating provider NPI, **not** by the caller's own identity: this
  repository has no mapping from a token subject to a provider NPI, and NPIs are
  public, so inventing one would be security theatre. The caller is recorded in
  the audit trail instead. This is the same documented limitation as
  `Claim/$inquire`.
* **Pull, not push.** CHO makes the request available for retrieval. Pushing a
  Task to the provider's FHIR server needs a provider endpoint registry that does
  not exist here.
* **Attachment durability is a deployment step.** fhir-service registers the
  in-process `IClaimAttachmentContentStore` by default and says so at startup. A
  deployment **must** bind a durable (blob-backed) implementation before submitted
  documentation survives a restart. The abstraction is shaped for `IDocumentStore`
  / Azure Blob, which attachment-service already uses.
* **No malware scanner — only the seam.** `IAttachmentContentScanner` is the call
  site on the path every submission takes. Registering a real implementation
  (ICAP, Defender for Storage on-upload, a sidecar) is deployment integration.
  Until then, artifacts are recorded with scan status `Unknown`.
* **No outbox, and no distributed transaction.** authorization-service and
  rfai-service cannot participate in one transaction, and this does not pretend
  otherwise. The failure modes, in order of where they can break:
  * *Request persists but the handle write fails* — the request exists and is
    durable; a later delivery of the same decision replays onto it and stamps the
    handle again.
  * *Decision recorded but the request is not raised* — the authorization stays
    `Pended` with `RFAIIssued = false`. That is the recoverable state: a replay of
    the decision, or a status update landing on the same condition, retries with
    the same correlation key and cannot duplicate.
  * *Bytes stored but the record write fails* — the submission simply did not
    happen; the caller gets a 503 telling them a retry is safe, and the retry
    recomputes the same submission id, writes the same key and records once.
    Storage happens first on purpose: the reverse order would leave a recorded
    response whose content is missing, which is the failure that actually loses
    information.
  * *Response recorded but the announcement fails* — the documents are durable;
    the authorization returns to review late rather than never, and re-announcing
    is safe because the consumer's update is idempotent.
* **No due-date sweeper.** Overdue is derived and reported, never recorded.
* **No X12 275/277 generation** from this path. The request models the PWK codes
  a 277RFAI would carry, and `ReceivedAttachment.SourceTransaction` is where a
  correlated 275 lands, but this work does not emit or parse those transactions.
* **`AllRequestedItemsReceived` is a count, not a judgement.** Deciding whether a
  document satisfies a clinical question is the reviewer's job — which is exactly
  why the round trip returns an authorization to review rather than approving it.
* Zero GAPs in the acceptance suite is **not** complete CMS-0057-F compliance.
  This is implementation evidence, not certification.
