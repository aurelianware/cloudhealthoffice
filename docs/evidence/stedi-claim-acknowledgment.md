# Stedi 277CA claim acknowledgment validation

Date: 2026-08-23 UTC

Branch: `feat/stedi-277ca`

Commit: `2d1990b5`

## Stedi delivery

| Step | Mechanism | API |
| --- | --- | --- |
| Discover | Webhook `transaction.processed.v2` and/or Poll Transactions | `GET https://core.us.stedi.com/2023-08-01/polling/transactions` |
| Retrieve JSON | 277CA Report | `GET https://healthcare.us.stedi.com/2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/277` |

Webhook authentication: Stedi credential-set API key header (no HMAC in the
public contract). CHO fail-closes unless `WebhookCredentialValue` is set.

## Live Stedi 277CA

**Not executed.** Stedi documents that sandbox accounts cannot submit test
claims; test 277CAs require a production-account test API key
([Test claim workflows](https://www.stedi.com/docs/healthcare/test-claims-workflow)).

```
Contract-tested against Stedi's documented 277CA format;
live acknowledgment pending production-account test access.
```

## Synthetic fixture (CLM-P-1001)

| Field | Value |
| --- | --- |
| Gateway | Stedi (stubbed HTTP) / Mock development injection |
| Claim type | 837P |
| Synthetic claim | CLM-P-1001 |
| Synthetic submission id | `synthetic-sub-001` |
| Synthetic acknowledgment id | `synthetic-ack-001` |
| 277CA status | Accepted |
| Payer claim control number | `synthetic-pcn-001` |
| Transmission | `SubmissionAcceptedByGateway` → `AcknowledgmentAccepted` |
| Claim | not adjudicated, not paid |
| Duplicate/replay | second identical 277CA is a no-op (no extra record, no extra event) |

Rejected counterpart (`CLM-P-1002` / invalid subscriber): transmission
`AcknowledgmentRejected`; claim still not adjudicated and not paid.

No API keys, raw 277CA JSON/X12, member names, member IDs, diagnoses, or
procedure payloads are recorded here.
