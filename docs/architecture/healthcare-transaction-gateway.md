# Healthcare Transaction Gateway

The healthcare transaction gateway is Cloud Health Office's vendor-neutral
abstraction for external payer / clearinghouse connectivity. One capability
model, many interchangeable vendor implementations, living in
[`CloudHealthOffice.Infrastructure.Gateways`](../../src/services/shared/CloudHealthOffice.Infrastructure/Gateways/).

This is the **foundation** layer. The mock gateway, the Stedi eligibility
(270/271) adapter, outbound 837 submission, 277CA acknowledgment, 275 claim
attachments, 276/277 claim status inquiry, 835 remittance ingestion, and the
canonical payer reference service, and the claim intelligence read model
are implemented today. Payment posting from 835s is a later PR.

Payer-side inbound eligibility (CHO as the 271 information source) is a
**separate** capability: [`payer-eligibility-responder.md`](payer-eligibility-responder.md).
It uses `IEligibilityResponder`, not `IEligibilityGateway`. Stedi inbound 270
routing is adapter-ready / pending Stedi payer-side connectivity — it is not
implemented.

Payer-side inbound claim attachments (CHO as the 275 receiver) are likewise
separate: [`payer-claim-attachment-receiver.md`](payer-claim-attachment-receiver.md).
`IClaimAttachmentReceiver` is not `IClaimAttachmentGateway`. Stedi inbound
payer-side 275 is adapter-ready, not implemented.

## Where the boundary sits

```
                        CloudHealthOffice
                              |
                 Healthcare Transaction Gateway
                              |
              +---------------+---------------+
              |               |               |
            Stedi          Availity         Direct
                                        (payer / X12 / FHIR)
```

Outbound (client) mode — implemented, including Stedi:

```
CHO  →  Stedi  →  External Payer
```

Inbound (payer) mode — CHO responder implemented; Stedi routing pending:

```
Provider  →  Network / Clearinghouse  →  CHO
```

See [`payer-eligibility-responder.md`](payer-eligibility-responder.md) for the
inbound pipeline, the Stedi capability finding, and the planned adapter seam.

**Cloud Health Office owns the business.** Eligibility and benefit
interpretation, member coverage, provider / network logic, claims
adjudication, pricing, and accumulators all stay in CHO domain services. None
of that moves into a gateway.

**A gateway owns transport and translation only.** It carries a HIPAA/X12
transaction to an external system and translates the vendor's response back
into a CHO canonical model. It makes no coverage or adjudication decisions.

## Canonical models never leak vendor shapes

Request and response flow through CHO canonical models. A vendor DTO or raw
X12 payload never crosses the gateway boundary into a domain service.

```
CHO GatewayEligibilityRequest
        |
   IEligibilityGateway
        |
   Vendor adapter (translate)
        |
   Stedi / Availity / X12 / FHIR
```

```
   Vendor response
        |
   Vendor adapter (normalize)
        |
CHO GatewayEligibilityResponse
```

The canonical models live in
[`Gateways/Models`](../../src/services/shared/CloudHealthOffice.Infrastructure/Gateways/Models/):
`GatewayEligibilityRequest` and `GatewayEligibilityResponse`. They reference
only BCL and CHO types — a guard test
(`GatewayVendorNeutralityTests`) fails the build if any vendor name appears in
the abstraction.

## Capabilities are explicit, not faked

Not every gateway implements every transaction. Each gateway advertises the
subset it actually supports via `IHealthcareTransactionGateway.Capabilities`
and implements the matching capability-specific interface. Unsupported
transactions are **rejected explicitly** rather than returning a no-op result.

| Capability | Interface | Transaction | Status |
|------------|-----------|-------------|--------|
| `Eligibility` | `IEligibilityGateway` | 270/271 | **Implemented (Mock + Stedi)** |
| `ClaimSubmission` | `IClaimSubmissionGateway` | 837P/837I/837D | **Implemented (Mock + Stedi)** |
| `ClaimStatus` | `IClaimStatusGateway` | 276/277 | **Implemented (Mock + Stedi JSON)** |
| `ClaimAcknowledgment` | `IClaimAcknowledgmentGateway` | 277CA | **Implemented (Stedi retrieve + shared processor)** |
| `ClaimAttachment` | `IClaimAttachmentGateway` | 275 | **Implemented (Mock + Stedi JSON create/upload)** |
| `Remittance` | `IRemittanceGateway` | 835 | **Implemented (Stedi retrieve + shared processor)** |

### Per-gateway implementation status

This matrix is Cloud Health Office's **implementation** status — it is not
everything a vendor supports.

| Gateway | Eligibility (270/271) | 837 | 277CA | 275 | 276/277 | 835 |
|---------|:---:|:---:|:---:|:---:|:---:|:---:|
| Mock  | Yes | Yes | No* | Yes | Yes | No* |
| Stedi | Yes | Yes | Yes | Yes | Yes | Yes |

\*Mock does not retrieve 277CAs or 835s. Development injection
(`POST /api/dev/gateway/claims/{transmissionId}/277ca` and
`POST /api/dev/gateway/remittance`) feeds the same canonical processors
used by Stedi.

`IClaimAcknowledgmentGateway.RetrieveAcknowledgmentAsync` fetches and
normalizes a 277CA. Applying it to a transmission is
`IClaimAcknowledgmentProcessor` — transport (webhook vs poll) must not
duplicate that logic.

`IClaimAttachmentGateway.SubmitAttachmentAsync` submits supporting
documentation for an existing claim transmission.

`IClaimStatusGateway.CheckClaimStatusAsync` asks an external payer for the
current status of a previously submitted claim (276/277).

`IRemittanceGateway.RetrieveRemittanceAsync` fetches and normalizes an 835.
Applying it to claims is `IRemittanceProcessor` — it does not post payment.

`IClaimIntelligenceComposer` reads those stores and returns a unified
workflow view. See [`claim-intelligence.md`](claim-intelligence.md).

### Discovering and rejecting capabilities

```csharp
// Resolve the configured default gateway (or one by name).
var gateway = resolver.Resolve();               // e.g. the Mock gateway

// Discover a capability.
if (gateway.Supports(GatewayCapability.Eligibility)) { /* ... */ }

// Resolve typed to a capability — throws GatewayCapabilityNotSupportedException
// when the gateway does not support it.
var eligibility = resolver.ResolveCapability<IEligibilityGateway>();
var result = await eligibility.CheckEligibilityAsync(request, ct);
```

## Transaction metadata (non-PHI)

Every transaction returns a `GatewayResponse<T>` that pairs the canonical
result with `GatewayTransactionMetadata`: gateway name, transaction type,
submitted / completed timestamps, status, external transaction id, correlation
id, tenant id, latency, retry count, and error category.

This metadata is deliberately PHI-free so it can go straight into structured
logs, metrics, and audit records. **Raw request/response payloads are never
logged.** The mock gateway logs only metadata, and
`GatewayPhiLoggingTests` enforces that subscriber identifiers, names, and dates
of birth never reach the log sink.

## Configuration

Bound from the `HealthcareTransactions` section into
`HealthcareTransactionOptions`:

```yaml
HealthcareTransactions:
  DefaultGateway: Mock
  Gateways:
    Stedi:
      BaseUrl: https://healthcare.us.stedi.com/...
      ApiKey: ""          # supplied by the secret provider / Key Vault, never source control
      Environment: sandbox
```

Only `DefaultGateway` is required today. The per-gateway map is prepared for
future vendors; secrets (`ApiKey`) flow through the existing secret provider /
Azure Key Vault configuration layering — no credentials are committed.

## Dependency injection

Registration follows the existing `AddChoMessaging` convention. From a
service's `Program.cs`:

```csharp
builder.Services.AddChoHealthcareGateways(builder.Configuration);
```

This binds the options, registers the resolver, and registers the mock
gateway. Additional gateways register through the same extension without
touching the resolver:

```csharp
builder.Services.AddHealthcareGateway<StediHealthcareGateway>();
```

Callers depend on `IHealthcareGatewayResolver` (or a capability interface) via
constructor injection — there is no service-locator access to concrete
gateways.

## Relationship to the existing eligibility adapters

eligibility-service already has an internal `IEligibilityAdapter` factory
(CHO / Availity / Change Healthcare) for its own request path. The gateway
abstraction is the **shared, cross-service** transport layer that future
vendor connectivity (starting with Stedi) plugs into. This PR adds the
abstraction and wires the mock gateway into eligibility-service DI; the
existing adapter flow is unchanged.

## Stedi gateway

`StediHealthcareGateway` (`Gateways/Stedi/`) is the first real external gateway.
It implements `IEligibilityGateway` on top of Stedi's **real-time eligibility
(270/271) JSON API** — `POST /2024-04-01/change/medicalnetwork/eligibility/v3`
on `https://healthcare.us.stedi.com`. Stedi translates the JSON to X12 270,
sends it to the payer, and returns the 271 as JSON; Cloud Health Office never
generates raw X12.

### Transaction flow

```
CloudHealthOffice
        |
GatewayEligibilityRequest          (canonical, vendor-neutral)
        |
StediHealthcareGateway             (validate config, resolve payer, map)
        |
StediEligibilityRequestDto         (Stedi JSON)
        |
Stedi Healthcare API  ── 270 X12 ──►  payer network
        |                                    |
StediEligibilityResponseDto  ◄── 271 X12 ──  payer
        |
StediEligibilityMapper             (normalize)
        |
GatewayEligibilityResponse         (canonical, vendor-neutral)
        |
CloudHealthOffice
```

The Stedi request/response DTOs and the mapper are **internal** to the
infrastructure assembly — an architecture test fails the build if any of them
becomes public. Only `StediHealthcareGateway`, `StediGatewayOptions`, and the DI
extension are public.

### Subscriber vs patient

Canonical eligibility distinguishes the **subscriber** (policyholder / insured)
from the **patient** (person receiving services). They are the same person for
self coverage and different people for a dependent inquiry.

```
Subscriber = insured / policyholder
Patient    = person receiving services
```

Existing flat fields (`SubscriberId`, `SubscriberFirstName`, …) remain valid.
New callers should set `Subscriber` and, when needed, `Patient` as
`GatewayEligibilityPerson`. `MemberId` on the request is **not** a dependent
inquiry by itself — populate `Patient` for that.

#### Subscriber inquiry

```
Provider
   ↓
CHO
   ↓
Subscriber eligibility request
   ↓
Stedi subscriber object (no dependents[])
   ↓
271
```

#### Dependent inquiry

```
Provider
   ↓
CHO
   ↓
Subscriber + Patient
   ↓
Stedi subscriber + dependents[]
   ↓
271
```

CHO `Patient` maps to Stedi `dependents[]` (at most one). Stedi's request
schema does not require a relationship on `dependents[]`; CHO only sends
first name, last name, DOB, and an optional dependent member id. Payer-returned
`relationToSubscriber` is preserved on the canonical response `Patient`.
Date of birth is `YYYYMMDD`. The subscriber member id stays on Stedi
`subscriber.memberId`.

Verified sandbox evidence (Stedi `applicationMode = test`):

```
Trading partner 87726 (UnitedHealthcare)
Subscriber John Doe / UHC202649
Dependent  Jane Doe / DOB 1952-11-21
Service type 30
→ HTTP 200, Active Coverage, benefits present
```

### Outbound 837 claim submission

```
CHO Claim
   ↓
GatewayClaimSubmissionRequest
   ↓
IClaimSubmissionGateway
   ↓
StediHealthcareGateway
   ↓
Stedi JSON 837P / 837I / 837D
   ↓
Payer
```

Stedi endpoints (API version `2024-04-01` on `https://healthcare.us.stedi.com`):

| Claim type | Path |
|------------|------|
| 837P | `POST /2024-04-01/change/medicalnetwork/professionalclaims/v3/submission` |
| 837I | `POST /2024-04-01/change/medicalnetwork/institutionalclaims/v1/submission` |
| 837D | `POST /2024-04-01/dental-claims/submission` |

Synchronous HTTP 200/`status=SUCCESS` means **the clearinghouse accepted the
submission for processing**. It is not a 277CA, payer acceptance,
adjudication, or payment.

### 277CA claim acknowledgment lifecycle

```
CHO Claim
   ↓
837
   ↓
Stedi
   ↓
Submission accepted by gateway
   ↓
277CA acknowledgment received
   ↓
CHO matches acknowledgment to transmission
   ↓
Claim acknowledgment status updated
```

These states remain distinct:

```
837 submitted
    ↓
Gateway accepted          (GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway)
    ↓
Awaiting 277CA
    ↓
277CA accepted / rejected / partial
    ↓
later adjudication        (not this PR)
    ↓
later 835 / payment       (not this PR)
```

A 277CA is an acknowledgment of claim acceptance or rejection **into
downstream processing**. It is not adjudication and it is not payment.
`ClaimAcknowledgmentStatus.Accepted` must never be treated as paid,
adjudicated, approved, or denied.

#### Production durability (hardening)

```
At-least-once Stedi delivery
        ↓
durable discovery (webhook pointer or poll item)
        ↓
atomic idempotency claim (unique gateway + acknowledgmentId)
        ↓
277 retrieval + structural mapping
        ↓
deterministic transmission match
        ↓
atomic state + outbox persistence
        ↓
event publication retry
```

`HealthcareTransactions:ClaimLifecycle:Store` is `InMemory` in Development and
**Mongo** in non-Development when `IMongoClient` is registered. A production
host with ephemeral storage **fails startup** (no silent in-memory fallback).
Mongo unique indexes make `TryCreateAsync` atomic across replicas. Outbox
entries (`Received` / `Accepted` / `Rejected`) are stored on the
acknowledgment record; a hosted dispatcher retries unpublished events.
Malformed / unmatched 277CAs are quarantined (`UnableToMatch` / `Malformed`)
without a tenant guess and without mutating an existing acknowledgment
outcome. Poll cursors persist `pageToken`, `windowStartUtc`, and
`lastPolledThroughUtc`. When Stedi returns no next page token the window
advances to `now - PollOverlapHours` (default 24h). Unprocessed/non-quarantined
items do not advance the cursor.

Post-277CA `SubmitClaimAsync` with the same #1111 idempotency key is a replay
and does not resend the 837. A new claim version / frequency remains a new
transmission.

#### Stedi delivery mechanism

Stedi delivers asynchronous 277CAs as **pointers**, then JSON reports:

1. **Discover** (either or both; processor is shared):
   - Webhook: `transaction.processed.v2` (`source: stedi.core`) to
     `POST /api/integrations/stedi/claim-responses`
   - Poll: `GET https://core.us.stedi.com/2023-08-01/polling/transactions`
2. **Retrieve JSON**: `GET https://healthcare.us.stedi.com/2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/277`
   (API version **2024-04-01**). Auth: raw API key in `Authorization`.

The webhook body does **not** contain 277CA content. Filter
`direction=INBOUND` and `x12.metadata.transaction.transactionSetIdentifier=277`
(docs shorthand `x12.transactionSetIdentifier`). 835/999 events are ignored.

Stedi authenticates **to** CHO using a configured credential set (API key
header, Basic, or none). **Stedi does not HMAC-sign claim-response webhooks.**
CHO fail-closes: `HealthcareTransactions:Gateways:Stedi:WebhookCredentialValue`
must match the header Stedi is configured to send. Unverifiable requests
return 401. Payload size is limited. Duplicate deliveries are expected
(5s timeout, retries); processing is idempotent on `gateway + acknowledgmentId`
and webhook `event id`.

Polling is opt-in (`ClaimAcknowledgmentPollingEnabled`, default `false`) using
the existing hosted-service pattern. It overlaps a one-day window and stores
Stedi's `nextPageToken`.

```
Stedi 277CA (webhook pointer or poll item)
    ↓
Stedi transport adapter (Report API 2024-04-01)
    ↓
GatewayClaimAcknowledgment
    ↓
ClaimAcknowledgmentProcessor
    ↓
IClaimTransmissionStore / IClaimAcknowledgmentStore
```

#### Matching

Deterministic only — never fuzzy, never guessed:

1. Explicit `TransmissionId` (development injection)
2. Unique `SubmissionId` / original correlation id
   (`claimTransactionBatchNumber` / `clearinghouseTraceNumber`)
3. Unique patient control number (`referencedTransactionTraceNumber`,
   case-insensitive; Stedi-documented 30-character truncation only)

Zero matches or more than one match → `UnableToMatch`, persisted for
operators, **not** attached to a claim. Tenant is taken from the matched
transmission, never from inbound payload text. Payer identity is preserved
from the original transmission.

#### Live validation

Stedi sandbox accounts cannot submit test claims; test 277CAs require a
production-account test API key
([Test claim workflows](https://www.stedi.com/docs/healthcare/test-claims-workflow)).

```
Contract-tested against Stedi's documented 277CA format;
live acknowledgment pending production-account test access.
```

Payer readiness uses `IPayerReferenceService` for the matching 837
transaction type (external Stedi id, payer support, enrollment). Arbitrary
payer ids are never passed through.

Idempotency key: `tenant|claimId|claimVersion|claimType|frequency`. Repeat
calls return the existing accepted transmission. Frequency `7`/`8` and a new
`ClaimVersion` are intentional resubmissions. The same key is sent as Stedi's
`Idempotency-Key` header.

Retries: 429 / 5xx / network / timeout only. 400 / 401 / 403 / validation
are not retried.

Institutional claims without type of bill or revenue codes fail
`ClaimTypeNotReady` rather than inventing 837I fields. Dental tooth/surface
are mapped when present on the canonical line.

Development: `POST /api/dev/gateway/claims` and
`POST /api/dev/gateway/claims/{transmissionId}/277ca` (404 outside Development).
The 277CA injection uses the same canonical processor as the Stedi adapter.

Live 837 validation: Stedi sandbox accounts cannot submit test claims.
Contract tests cover the documented JSON/HTTP. Opt-in
`CHO_STEDI_LIVE_CLAIM_TESTS` requires a production-account test API key.

A subscriber-only request for that same UHC fixture returns AAA 73
(Invalid/Missing Subscriber/Insured Name) — a payer business rejection, not a
transport failure (`GatewayTransactionStatus.Rejected` /
`GatewayErrorCategory.PayerRejected`).

### 275 claim attachments

A 275 is supporting documentation for a claim. It is not a claim, a 277CA,
adjudication, or payment. Attachment lifecycle is stored separately from 837
and 277CA state.

```
CHO Claim / Transmission
        ↓
ClaimAttachmentSubmissionRequest
        ↓
IClaimAttachmentGateway
        ↓
StediHealthcareGateway
        ↓
POST https://claims.us.stedi.com/2025-03-07/claim-attachments/file
        ↓
PUT pre-signed upload URL
        ↓
Stedi 275 (unsolicited)
        ↓
Payer
```

#### Stedi JSON contract (API version 2025-03-07)

Documented JSON workflow
([Create Claim Attachment (275) JSON](https://www.stedi.com/docs/healthcare/api-reference/post-healthcare-create-claim-attachment),
[Claim attachments](https://www.stedi.com/docs/healthcare/submit-claim-attachments)):

1. `POST /claim-attachments/file` with `{ "contentType": "application/pdf" }`
   → `{ attachmentId, uploadUrl }`
2. `PUT` the file to `uploadUrl` with a matching `Content-Type`. The PUT is
   **not** authenticated with the Stedi API key.
3. Stedi generates the unsolicited 275. The JSON API is designed to pair the
   `attachmentId` with a later 837 `reportInformations` reference. This PR
   submits the file through the documented JSON create+upload path for an
   **existing** CHO transmission. It does **not** resubmit the 837 and does
   **not** synthesize raw X12 275.

Supported MIME types (Stedi allow-list): `application/pdf`, `image/tiff`,
`image/jpeg`, `image/jpg`, `image/png`. Stedi recommends a 64MB per-file
limit for JSON/S3 uploads. CHO enforces `min(CHO max, Stedi max)` before
transport and returns `AttachmentTooLarge` rather than relying on HTTP 413.

#### Supported attachment modes

| Mode | Status |
|------|--------|
| Professional | Implemented |
| Institutional | Implemented |
| Dental | Implemented (claim-level; dental-specific types such as radiograph / periodontal chart / narrative map vendor-neutrally) |
| Unsolicited | Implemented (Stedi APIs/SFTP only support unsolicited 275) |
| Solicited | Unsupported (Stedi documents that solicited 275 / 277 RFA responses cannot be submitted through Stedi APIs) |
| Claim-level | Implemented |
| Service-line-level | Implemented for professional and institutional; rejected for dental (do not silently fall back) |

#### Secure storage

Attachment **bytes** live in `IClaimAttachmentContentStore` (container +
storage key, the same shape as CHO `IDocumentStore`). Development/tests use
the in-memory implementation. Production hosts that already register
`IDocumentStore` (Azure Blob, encryption at rest, private containers — see
`attachment-service`) should register a durable `IClaimAttachmentContentStore`
that delegates to that store. Infrastructure does not take a project reference
on `CloudHealthOffice.DocumentStore` in this PR because that package currently
pulls System.Text.Json 10 / Logging 10 and fails restore against this net8
assembly (NU1605).

Domain objects hold only:

```
ClaimAttachment metadata + ContentReference (container, storage key, MIME, length, SHA-256)
```

Storage keys are `tenantId/transmissionId/attachmentId/{checksum}.{ext}`.
Caller file names are untrusted display metadata and are never used in paths,
URLs, or logs.

There is no malware scanner in this PR. `ScanStatus` of `Quarantined`,
`Unsafe`, or `ScanFailed` is rejected. Scanning is an operational
responsibility of the upload path that writes to the content store.

#### Claim association

`ClaimId` and `TransmissionId` are required. The transmission must exist.
Tenant, claim id, and payer must match the original transmission (no fuzzy
match). Service-line numbers must exist on the original submitted claim
(`ClaimTransmissionRecord.ServiceLineNumbers`). Tenant is taken from the
transmission, not from an untrusted body field.

Payer readiness reuses `IPayerReferenceService` for
`HealthcareTransactionType.ClaimAttachment275`: gateway capability, payer
support, and tenant enrollment are distinct. Enrollment is surfaced as
`EnrollmentRequired`; this PR does not submit enrollments.

#### Idempotency

Key: `tenant|transmissionId|attachmentId|checksum|attachmentType|serviceLine|version`.

Identical retries replay the accepted record and do not resend. Changed
content with the same attachment id requires a new `AttachmentVersion`
because submitted content is immutable. SHA-256 is persisted for integrity,
duplicate detection, and audit (checksum prefix only in logs).

#### Lifecycle

```
Stored → Validated → ReadyForSubmission → Transmitting → GatewayAccepted
                                                      ↘ GatewayRejected / Failed
```

Independent of 837 transmission status and 277CA acknowledgment status. A
claim may be `AcknowledgmentAccepted` while a later attachment is
`GatewayRejected`.

Synchronous Stedi success means **the gateway accepted the file for
processing**. It does not mean the payer reviewed the document, accepted the
claim, adjudicated, or paid.

No separate 275 acknowledgment framework is built in this PR. Stedi's
documented payer responses for attachments remain the claim's 277CA / 835.

#### PHI / logging / retention

Logs include only attachment id, transmission id, content type, content
length, checksum prefix, status, latency, retry count, and error category.
Never file bytes, base64, member names/ids, raw Stedi JSON, upload URLs, API
keys, or caller file names.

Retention is not invented here:

- Attachment **metadata** follows CHO operational data retention.
- **Binary content** follows the blob/document store lifecycle (Stedi stores
  uploaded files for 45 days; CHO's copy is independent).
- **Gateway transmission evidence** follows claim-lifecycle store retention.

CHO sales/security materials mention a 7-year HIPAA retention pattern for
clinical records; this PR does not encode a legal hold period.

#### Live validation

```
Contract-tested against Stedi's documented 275 JSON API;
live 275 pending production/test account capability.
```

Stedi test claims (and therefore test 275s tied to those claims) require a
production-account test API key. Opt-in `CHO_STEDI_LIVE_CLAIM_TESTS` does
not run in CI.

Development: `POST /api/dev/gateway/claims/{transmissionId}/attachments`
(multipart, 404 outside Development). Tenant comes from `X-Tenant-ID` and
the original transmission.

### 276/277 claim status inquiry

A 276/277 asks what happened to a claim **after** it entered the payer's
system. That is a different lifecycle dimension from:

```
277CA  = claim acknowledgment (accepted/rejected into processing)
277    = claim status response (in process / finalized / paid / denied / …)
835    = remittance / payment detail
```

These must not overwrite each other. A transmission can be
`AcknowledgmentAccepted` while 276/277 status is `InProcess`, and a 277 of
`Paid` does not post payment or change 835 state.

```
CHO Claim / Transmission
       ↓
ClaimStatusRequest
       ↓
IClaimStatusGateway
       ↓
Stedi Real-Time Claim Status JSON
       ↓
276 → External Payer → 277
       ↓
ClaimStatusResponse
```

Callers supply `ClaimId` or `TransmissionId`. The coordinator derives payer,
billing provider, subscriber/patient, dates, and control numbers from the
original 837 snapshot and from a matched 277CA when present.

Identifier preference:

1. Payer claim control number from 277CA (Stedi `tradingPartnerClaimNumber`)
2. Patient control number from the original 837 (Stedi `patientAccountNumber`)
3. Stedi transaction / control number as correlation on the response
4. Original CHO claim id

Stedi's documented JSON is claim-type-agnostic: professional, institutional,
and dental use the same endpoint. Service-line inquiry uses
`serviceLinesInformation` (the older `serviceLineInformation` object is
deprecated). An unknown line number is rejected; it is never silently
widened to claim-level status.

#### Stedi API

| Item | Value |
| --- | --- |
| Host | `https://healthcare.us.stedi.com` |
| Path | `POST /2024-04-01/change/medicalnetwork/claimstatus/v2` |
| Version | **2024-04-01** / `claimstatus/v2` |
| Shape | JSON 276 request, synchronous JSON 277 response |
| Auth | Production Stedi API key in `Authorization` |
| Concurrency | Shared with eligibility (50 requests) |

Documented JSON (base request): `tradingPartnerServiceId`, billing
`providers[]` (`npi`, `organizationName`, `providerType=BillingProvider`),
`subscriber` (`firstName`, `lastName`, `dateOfBirth`, `gender`, `memberId`),
`encounter` dates. Optional `dependent`, `tradingPartnerClaimNumber`,
`patientAccountNumber`, `billingType`, `serviceLinesInformation`.

HTTP 200 with no matching claims is a **business** `NoRecordFound`, not a
transport failure. Invalid subscriber / unable-to-respond messages on HTTP
200 are likewise business outcomes (`PayerRejected` /
`ClaimStatusUnavailable`).

Payer readiness reuses `IPayerReferenceService` for
`HealthcareTransactionType.ClaimStatus276277`. Distinct:

```
gateway supports 276/277
payer supports 276/277
tenant is enrolled/configured
```

Enrollment is separate from 837 enrollment. Arbitrary payer IDs are never
passed through.

Inquiry snapshots persist on `IClaimStatusInquiryStore` (Mongo
`claim_status_inquiries` in production). They append; they do not rewrite
277CA records or 837 transmission status. Duplicate persistence is keyed by
gateway + Stedi transaction id. Recurring polling is **not** registered —
`ClaimStatusRules.IsFollowUpCandidate` and `ListByTransmissionIdAsync` are
the later monitoring seam.

#### Live validation

```
Contract-tested against Stedi's documented 276/277 API;
live status inquiry pending production/test capability.
```

Stedi documents that **test keys are not supported** for Real-Time Claim
Status, and that requests must target production claims already accepted
into the payer's system. Opt-in `CHO_STEDI_LIVE_CLAIM_STATUS_TESTS` does
not run in CI.

Development: `POST /api/dev/gateway/claims/{transmissionId}/status` and
`GET /api/dev/gateway/claims/{transmissionId}/status` (404 outside
Development). Tenant comes from `X-Tenant-ID` and the original
transmission.

### 835 remittance ingestion

An 835 is the payer's **financial** outcome for claims it accepted. It is
not a 277CA, not a 276/277 status check, and not payment posting.

```
837 submitted
        ↓
277CA accepted/rejected into processing
        ↓
276/277 current claim status
        ↓
835 ERA (this PR)
        ↓
Future payment posting
```

```
Stedi
 ↓
835 ERA
 ↓
Stedi Adapter
 ↓
Canonical Remittance
 ↓
CHO Remittance Store
 ↓
Future Payment Posting
```

Stedi delivers 835s asynchronously:

1. Discover via webhook `transaction.processed.v2` (`x12.transactionSetIdentifier = 835`) or Poll Transactions.
2. Retrieve JSON: `GET https://healthcare.us.stedi.com/2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/835`
3. Map to `GatewayRemittance`.
4. `IRemittanceProcessor` matches claims and persists a receipt.

Matching is deterministic (never name, DOB, provider name, or amount):

1. Payer claim control number (from 277CA / ERA `payerClaimControlNumber`)
2. Patient control number
3. Explicit transmission id (development)

Unmatched ERAs are stored for reconciliation. Mixed-tenant matches fail closed
and do not assign a tenant. The processor does **not** change 837 transmission
status, 277CA records, 276/277 snapshots, accumulators, or payment-service
posting.

Lifecycle: `Received` → `Validated` / `Matched` / `AvailableForPosting` /
`Unmatched` / `Failed`. `AvailableForPosting` means the ERA is stored and
matched — posting is out of scope.

#### Live validation

```
Contract-tested against Stedi documented 835 API;
live ERA validation pending production/test capability.
```

835 enrollment is required with the payer (separate from 837). Opt-in
`CHO_STEDI_LIVE_CLAIM_TESTS` + `CHO_STEDI_835_TRANSACTION_ID` does not run
in CI.

Development: `POST /api/dev/gateway/remittance` and
`GET /api/dev/gateway/claims/{transmissionId}/remittance` (404 outside
Development).

### Architectural boundary

Stedi = network / transport / transaction translation.
Cloud Health Office = healthcare business logic.

A 271 response is an **external payer eligibility statement**, not a Cloud Health
Office calculation. The gateway surfaces it as a normalized eligibility context;
prospective benefits, cost estimates, and adjudication remain separate Cloud
Health Office steps downstream. The gateway never applies benefits, computes
accumulators, or adjudicates.

### Payer identifier mapping

Payer identity is a first-class Cloud Health Office platform concept, not a
Stedi configuration map. Canonical payer records live in
[`ReferenceData/Payers`](../../src/services/shared/CloudHealthOffice.Infrastructure/ReferenceData/Payers/)
and are reused across eligibility, and later claims, attachments, claim status,
and remittance.

```
                  CloudHealthOffice
                         |
                  Canonical Payer
                         |
                 Payer Reference Data
                    /           \
                   /             \
             Stedi ID         Other IDs
                |                |
                v                v
              Stedi          Availity/etc.
```

Three distinct concepts:

| Concept | What it answers |
|---------|-----------------|
| **Gateway capability** | Does this CHO gateway implementation send 270/271 (or 837, …)? |
| **Payer capability** | Does *this payer/network* support that transaction? |
| **Tenant enrollment/configuration** | Has *this tenant* completed any required enrollment, and do they override routing? |

A transaction is attempted only when the gateway implements it **and** the
payer supports it **and** tenant enrollment (when required) is complete.

```
GatewayEligibilityRequest.PayerId
        |
        v
IPayerReferenceService
        |
        v
Canonical payer
        |
        v
External identifier
  System = "stedi"
  Type   = "tradingPartnerServiceId"
        |
        v
StediHealthcareGateway
```

Resolution is exact-match only (canonical id, alias, or external identifier
value). Multiple matches return `AmbiguousPayer`; zero matches return
`PayerNotFound`. Arbitrary payer ids are **never** passed through to Stedi.

Hand-maintained `PayerMap` / `TenantPayerMap` remain as a **deprecated
fallback** for environments that have not yet synchronized a directory. They
are not the primary architecture. `TenantPayerMap` is still consulted only for
the requesting tenant.

### Payer directory synchronization

Stedi's List Payers JSON API is the source of directory data:

```
GET https://payers.us.stedi.com/2024-04-01/payers?pageSize=100
Authorization: <api-key>
```

API version: **2024-04-01**. Pagination request query parameters are `pageSize`
(minimum 10) and `pageToken`. The response field `nextPageToken` is supplied as
the next request's `pageToken`. The mapper copies only fields Stedi documents (stediId,
displayName, primaryPayerId, aliases, names, transactionSupport, enrollment
process metadata, coverage types, etc.). Stedi transport DTOs stay internal.

Synchronization is opt-in (`PayerReference:Sync:Enabled`, default `false`) so
hosts start without live Stedi credentials. When enabled, a hosted service
refreshes on a configurable interval (default 24h) and optionally on startup.
An on-demand `POST /api/payer-references/sync` is available in Development
(or when `AllowOnDemandSync` is true).

Records are stored in the configured `PayerReference:Store` (`InMemory` by
default; `Mongo` when an `IMongoClient` is registered). CI uses deterministic
synthetic seed payers — no live Stedi credentials required.

Payers present in a previous Stedi sync but missing from the latest run are
**disabled**, not deleted. Seed records (`Source = seed`) are left untouched.

### Tenant overlays

Global payer records are shared. A `PayerTenantOverride` keyed by
`(tenantId, payerId)` may:

- enable/disable the payer for that tenant
- supply a preferred alias
- replace the clearinghouse identifier
- record which transactions the tenant has enrolled

One tenant cannot read or modify another tenant's overlays.

### Configuration (no secrets in source control)

```yaml
HealthcareTransactions:
  DefaultGateway: Stedi          # or Mock
  Gateways:
    Stedi:
      BaseUrl: https://healthcare.us.stedi.com
      ApiKey: ""                 # supply via env var or secret provider / Key Vault
      Environment: sandbox       # sandbox | test | production
      PayerDirectoryBaseUrl: https://payers.us.stedi.com
      PayerDirectoryPath: /2024-04-01/payers
      ClaimAcknowledgmentReportPath: /2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/277
      CoreBaseUrl: https://core.us.stedi.com
      PollTransactionsPath: /2023-08-01/polling/transactions
      ClaimAcknowledgmentPollingEnabled: false
      WebhookCredentialHeaderName: Authorization
      WebhookCredentialValue: ""   # CHO secret Stedi is configured to send; fail-closed when empty
      PayerMap:                  # deprecated fallback only
        AETNA: "60054"
      TenantPayerMap:            # deprecated fallback; tenant-scoped
        tenant-alpha:
          AETNA: "60055"

PayerReference:
  Store: InMemory                # InMemory | Mongo
  SeedSyntheticPayers: true      # CI/dev seed; no live Stedi required
  Sync:
    Enabled: false               # opt-in; application starts without Stedi
    OnStartup: false
    IntervalHours: 24
```

Supply the API key out of band, e.g.:

```
export HealthcareTransactions__Gateways__Stedi__ApiKey="<your-stedi-key>"
```

or through the existing Azure Key Vault / secret-provider layering. The key is
sent in the `Authorization` header per request and never appears in logs,
exceptions, telemetry, or checked-in configuration. If Stedi is selected but its
configuration is invalid, the Stedi gateway returns a `Configuration` error —
it never silently falls back to Mock.

### Resilience & error handling

The Stedi API client runs an explicit, configurable retry loop (default 2
retries) so the retry count can be recorded on `GatewayTransactionMetadata` and
so behaviour is deterministically testable. Transient failures (HTTP 429, 5xx,
network errors, timeouts) are retried with exponential backoff and honour
`Retry-After`; validation (400/422), authentication (401), authorization (403),
and payer business rejections are never retried. All failures map to the
vendor-neutral `GatewayErrorCategory`; no Stedi exception type escapes the
gateway.

### Choosing a gateway (Mock vs Stedi)

Selection is configuration-only — no code change and no caller awareness of
`StediHealthcareGateway`:

```yaml
HealthcareTransactions:
  DefaultGateway: Mock    # deterministic, offline
# DefaultGateway: Stedi   # real payer transaction (requires ApiKey)
```

In Development, eligibility-service exposes a dev-only demo endpoint
(`POST /api/gateway-demo/eligibility`) that runs a request through the
configured gateway, so the same request can be pointed at Mock or Stedi.

## Relationship to the existing eligibility adapters (consolidation path)

eligibility-service still has its internal `EligibilityAdapterFactory`
(`IEligibilityAdapter`: CHO / Availity / Change Healthcare) for its own request
path. That system and `IHealthcareGatewayResolver` currently **overlap** for
eligibility: the adapter factory routes per-tenant platform choices inside the
service, while the gateway resolver is the shared, cross-service transport layer.
This PR does not consolidate them (a broad refactor is out of scope and higher
risk). The intended future path is to have the CHO/Availity/Change Healthcare
eligibility adapters delegate to (or be replaced by) capability gateways behind
`IHealthcareGatewayResolver`, leaving one transport abstraction. That
consolidation should be its own PR.

### Claim intelligence read model

The transaction layer stays the system of record. Claim intelligence is a
rebuildable projection:

```
Transaction Layer

837
 |
277CA
 |
276/277
 |
275
 |
835

        ↓

Claim Intelligence Layer

        ↓

Applications

CDO
Provider Portal
AI Services
Operations
```

`GET /api/claims/{claimId}/intelligence` is tenant-scoped. It answers where
the claim is, what happened, whether action is required, what was paid, and
whether the payer needs information — without exposing raw HIPAA payloads.

### Next application integration

Recommended next step: **CloudDentalOffice integration** — a provider claim
intelligence dashboard that consumes this API rather than duplicating payer
transaction logic. Payment posting from stored 835s remains a separate
follow-up.
