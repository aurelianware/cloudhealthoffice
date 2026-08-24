# Payer-side inbound claim attachment receiver

Cloud Health Office can act as the **payer** that receives a 275-equivalent
attachment, matches it to an existing payer-side claim or service line, stores
the document securely, and makes it available to claim examination — without
adjudicating or paying the claim.

This is the opposite of the outbound healthcare transaction gateway:

```
IClaimAttachmentGateway
    CHO → external payer/network

IClaimAttachmentReceiver
    external provider/network → CHO
```

`IClaimAttachmentGateway` is not overloaded with inbound responsibility.

## Stedi capability finding

**Stedi does not currently expose a supported public API for a core-administration
platform to receive inbound 275 transactions as a payer.**

Reviewed 2026-08-24 against:

| Source | What it covers |
|--------|----------------|
| [Claim attachments](https://www.stedi.com/docs/healthcare/submit-claim-attachments) | Provider submits unsolicited 275s **to** a payer |
| [Create Claim Attachment JSON](https://www.stedi.com/docs/healthcare/api-reference/post-healthcare-create-claim-attachment) | `POST /claim-attachments/file` — provider upload URL |
| [Submit Claim Attachment JSON](https://www.stedi.com/docs/healthcare/api-reference/post-healthcare-submit-claim-attachment) | `POST /claim-attachments/submission` — Stedi **sends** 275 to the payer |
| [Submit Claim Attachment Raw X12](https://www.stedi.com/docs/healthcare/api-reference/post-healthcare-submit-claim-attachment-raw-x12) | Provider raw X12 **to** Stedi **to** payer |
| [Claim responses / webhooks](https://www.stedi.com/docs/healthcare/claim-responses-overview) | 277CA and 835 after **you** submitted an 837 |

There is no documented mechanism to:

- register Cloud Health Office as a payer that **receives** 275s
- receive inbound 275 JSON/metadata or binaries as the information source
- configure a custom payer endpoint / webhook for inbound attachments
- poll inbound 275s destined for a CHO payer application
- receive solicited 275 responses as the payer through Stedi's public APIs

Stedi inbound payer-side 275 is therefore:

```
Adapter-ready / pending Stedi payer connectivity
```

not

```
Implemented
```

Solicited 275 / 277 RFA responses are also unsupported on Stedi's public APIs
(documented as provider-side unsolicited only). The canonical model still
carries `Mode` and `PayerRequestControlNumber` for a later workflow.

## Architecture

Path B — vendor-neutral receiver with a planned Stedi adapter seam:

```
Inbound Transport Adapter
        /          |          \
  Stedi*        X12*       Canonical / dev
        |          |              |
        +----------+--------------+
                   |
                   v
        InboundClaimAttachment
                   |
                   v
        IClaimAttachmentReceiver
                   |
                   +--> trusted payer/tenant routing
                   +--> SHA-256 + MIME/size validation
                   +--> IClaimAttachmentContentStore
                   +--> deterministic claim matcher
                   +--> durable receipt + outbox
                   v
        CHO payer-side claim / service line
```

\*Stedi and raw X12 inbound adapters are **not implemented**. They exist as
named seams (`stedi-planned`, `x12-planned`) so a partnership contract or a
reusable 005010X210 parser can plug in without changing the receiver.

```
CloudDentalOffice
       |
       | 837
       v
     Stedi
       |
       v
CloudHealthOffice

CloudDentalOffice
       |
       | 275
       v
     Stedi   [inbound payer routing: adapter-ready / partnership required]
       |
       v
CloudHealthOffice
       |
       +--> claim
       +--> line
       +--> secure document
       +--> examiner workflow
```

## Matching

Identifiers, in order, **inside the routed tenant + canonical payer only**:

1. CHO claim id (from a trusted integration path such as the canonical URL)
2. Payer claim control number
3. Patient control number
4. Attachment control number on the claim

Zero matches → `UnableToMatch` (quarantined, persisted).  
More than one → `AmbiguousClaim` (quarantined).  
Service-line number/control that is missing → `ServiceLineNotFound` (not silently claim-level).

Fuzzy matching on member name, DOB, provider, dates, or amounts is prohibited.

`ClaimedTenantId` is ignored. Routing uses `PayerId` / `TradingPartnerId` /
`AuthenticatedEndpointId` through the same inbound routes as eligibility.

## Secure storage

Bytes live in `IClaimAttachmentContentStore` (SHA-256, MIME, length, tenant
path). Receipts store only metadata + content reference. Production fail-closes
if the content store or inbound receipt store is in-memory.

## Idempotency

Key: `adapter|externalTransactionId|attachmentControlNumber|checksum`.

`TryCreateAsync` is atomic (in-memory `TryAdd`, Mongo unique index). Sequential
duplicates skip a second store. Concurrent duplicates yield one receipt and one
outbox event set.

## Workflow

A matched attachment becomes `AvailableToClaim` and sets
`DocumentationReceived` on the payer-side claim projection. Claim status stays
`Pended` / `InAdjudication`. Events:

- `ClaimAttachmentReceived`
- `ClaimAttachmentMatched` or `ClaimAttachmentQuarantined`

Durable outbox; bus failure is retried. Receipt never approves, denies, or pays.

## Capability matrix

```
Outbound capability
-------------------
275 sender via Stedi                 Implemented

Inbound payer capability
------------------------
Canonical 275 receiver               Implemented
Direct/dev ingress                   Implemented
X12 275 ingress                      Deferred
Stedi inbound payer 275              Adapter-ready
```

Development: `POST /api/dev/payer/claims/{claimId}/attachments` (404 outside
Development). HTTP 202 is transport success; `result.status` is the receipt
lifecycle (including quarantine).
