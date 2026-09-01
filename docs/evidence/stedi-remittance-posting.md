# Inbound 835 payment posting validation

Date: 2026-08-25 UTC

Branch: `feat/stedi-835-posting`

This PR posts what [stedi-remittance.md](stedi-remittance.md) received and
stored. It does not invent 835s, change 277CA or 276/277, or reconcile EFT.

## Poster

| Item | Value |
| --- | --- |
| Contract | `IRemittancePoster.PostAsync` |
| Source | Stored `RemittanceReceipt` only |
| Tenant | Matched transmission (fail closed) |
| Eligible status | `AvailableForPosting` |
| Success status | `Posted` |
| Claim sink | `IClaimRemittancePostingSink` (in-memory default) |
| Accumulator sink | `IRemittanceAccumulatorSink` (in-memory default) |
| Accumulator id | `835|{remittanceId}|{claimId}` |
| Deltas | 835 PR deductible / copay / coinsurance |
| Metric | `cho.remittance.posted.total` (gateway, status) |
| Outbox | `RemittancePosted` |

## Boundaries

| Must not | How |
| --- | --- |
| Invent an 835 | Missing receipt → not found; processor still owns ingest |
| Change 277CA | Poster does not load or save acknowledgments |
| Change 276/277 | Poster does not touch status inquiries |
| Generate outbound 835 | Claims use `POST /api/claims/{id}/inbound-remittance`, not PaymentRun `/remittance` |
| Double-count accumulators | Does not emit `claims.finalized.v1` |
| Reconcile EFT | `Posted` is claim+accumulator effect, not bank match |
| Log PHI / bank data | Logs gateway, remittance id, tenant, counts |

## Synthetic fixture

| Field | Value |
| --- | --- |
| Tenant | tenant-alpha |
| Claim | CLM-P-1001 |
| Member | from transmission InquirySource (not logged) |
| Paid | $320 |
| Patient responsibility | $80 (deductible $50 + coinsurance $30) |
| 277CA after post | `Accepted` |
| 837 after post | `AcknowledgmentAccepted` |
| Remittance after post | `Posted` |
| Duplicate post | Replay, no second sink write |

Gateway-only claims (claim sink `NotFound`) still mark the receipt `Posted`
and apply accumulators when a member id is present. Rejected/failed claim
or failed accumulator writes leave the receipt `AvailableForPosting`.

## Live Stedi

Not executed. Posting runs against stored receipts. Live ERA retrieve is
still pending payer enrollment as documented in `stedi-remittance.md`.

```
Contract-tested against stored GatewayRemittance receipts;
does not call Stedi and does not generate outbound 835s.
```

Development: `POST /api/dev/gateway/remittance/{receiptId}/post`
(404 outside Development). Tenant from `X-Tenant-ID`.

No raw 835, PHI, member identifiers, or banking/trace numbers are recorded here.
