# Claim Intelligence

Cloud Health Office converts healthcare transactions into a unified,
tenant-scoped **claim intelligence** read model. Applications such as
CloudDentalOffice, a future provider portal, operations tools, and later AI
services consume this API instead of re-implementing 837 / 277CA / 276/277 /
275 / 835 logic.

```
837
277CA
276/277
275
835

        ↓

Claim Intelligence Model

        ↓

CDO / Portal / AI / Operations
```

```
                    CloudDentalOffice
                           |
                           v
                 Claim Intelligence API
                           |
                           v
                 CloudHealthOffice
                           |
        +------------------+------------------+
        |                  |                  |
       837                277CA              835
   Submission        Acknowledgment      Remittance
        |
       276/277 Status
        |
       275 Attachments
```

This layer **composes** existing durable stores. It is not the system of
record. It does not post payment, change 277CA, overwrite 276/277, or
adjudicate.

## API

```
GET /api/claims/{claimId}/intelligence
Header: X-Tenant-ID
```

Missing tenant → HTTP 400. Unknown claim or a different tenant's claim →
HTTP 404 (no cross-tenant leak).

## Lifecycle mapping

Business `lifecycleStatus` is derived, not stored:

| Inputs | Lifecycle |
| --- | --- |
| 837 ready / queued | `Draft` |
| 837 accepted by gateway, no 277CA | `AcceptedByClearinghouse` |
| 277CA accepted, no 276/835 | `AcceptedByPayer` |
| 277CA accepted + 276 in process, no 835 | `Processing` |
| 276 pending / additional information | `PendingInformation` |
| 277CA rejected, 276 denied, or 835 denied | `Denied` |
| 835 matched, primary paid | `Paid` |
| 835 matched, secondary/tertiary (CLP02 2/3) | `PartiallyPaid` |

Rules that must not be violated:

- `277CA Accepted` is not `Paid`.
- `276/277 Paid` does not create an 835 and does not set lifecycle `Paid`.
- Duplicate 277CA / 835 / attachment deliveries do not duplicate timeline
  events (stable event ids from source record ids).

Source transaction states remain on `transactions`:

```json
{
  "lifecycleStatus": "Processing",
  "transactions": {
    "837": { "status": "AcknowledgmentAccepted" },
    "277CA": { "status": "Accepted" },
    "276277": { "status": "InProcess" },
    "835": null
  }
}
```

## Financial and attachments

Financial summary is informational from a matched 835. It is not posting.

Attachment summary reports outbound 275 plus inbound payer-side 275 type
names and counts. It does not return bytes, storage URLs, or document
payloads.

## Persistence

Transaction stores remain the system of record. Each GET rebuilds the view
from those stores, so the projection is always refreshable.

## PHI

The JSON response may include patient/provider identity for an authorized
tenant caller. Logs and metrics use tenant, claim id, lifecycle status, and
next action only — never names, member ids, DOB, attachment content, or ERA
payloads.

## Out of scope

Provider portal UI, CloudDentalOffice changes, payment posting, accounting,
AI assistants, denial prediction, and automated follow-up.
