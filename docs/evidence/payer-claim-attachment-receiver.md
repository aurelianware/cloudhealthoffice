# Payer-side inbound 275 receiver validation

Date: 2026-08-24 UTC

Branch: `feat/payer-claim-attachment-receiver`

Commit: *(filled at commit time)*

## Stedi inbound payer-side 275

**Not implemented.** Public Stedi APIs submit 275s *to* payers; they do not
deliver inbound 275s *to* a custom payer application.

```
Inbound payer-side Stedi 275:
Adapter-ready / pending Stedi payer connectivity
```

## Synthetic scenario

| Field | Value |
| --- | --- |
| CHO claim | `CLM-DEMO-275-001` |
| Claim status before/after | `Pended` / still `Pended` (not adjudicated, not paid) |
| Attachment type | DentalImage |
| Content | synthetic JPEG (6 bytes) |
| Checksum | `fc16d7dcee9cae83ef3923222a81ccd8fe96c9d25fdb7f504d66f1011e0cd870` |
| Source adapter | canonical |
| Matching method | Deterministic `ClaimId` |
| Storage | metadata + content reference, no raw bytes on the receipt |
| Duplicate | second identical call is replay; one receipt; one matched event |
| DocumentationReceived | true after first match |

No raw file bytes, base64, names, or member IDs are recorded here.
