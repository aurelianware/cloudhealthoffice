# Stedi outbound 837 claim submission validation

Date: 2026-08-23 UTC

Branch: `feat/stedi-claim-submission`

Commit: `a96503d1`

## Stedi API

| Claim type | Endpoint |
| --- | --- |
| 837P | `POST https://healthcare.us.stedi.com/2024-04-01/change/medicalnetwork/professionalclaims/v3/submission` |
| 837I | `POST https://healthcare.us.stedi.com/2024-04-01/change/medicalnetwork/institutionalclaims/v1/submission` |
| 837D | `POST https://healthcare.us.stedi.com/2024-04-01/dental-claims/submission` |

Idempotency: HTTP header `Idempotency-Key` (Stedi, 24h) plus CHO transmission store.

## Live Stedi 837

**Not executed.** Stedi documents that sandbox accounts cannot submit test
claims; test 837s require a production-account test API key
([Test claim workflows](https://www.stedi.com/docs/healthcare/test-claims-workflow)).

```
Contract-tested against documented Stedi API;
live 837 submission pending production/test-mode access.
```

## Contract fixture (synthetic)

| Field | Value |
| --- | --- |
| Gateway | Stedi (stubbed HTTP) |
| Claim type | 837P |
| Synthetic claim | CLM-P-1001 / CPT 90837 / $109.20 / NPI 1999999984 |
| HTTP | 200 |
| Gateway status | `SUCCESS` → `SubmissionAcceptedByGateway` |
| Submission id | `01CLAIMCORR` (synthetic) |
| Retry count | 0 |
| Idempotency | second identical call returns the same transmission; no second HTTP |

This is **not** payer 277CA accepted, adjudicated, or paid.

No API keys, raw 837 JSON, member names, diagnoses, or procedure payloads
are recorded here.
