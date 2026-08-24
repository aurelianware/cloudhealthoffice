# Stedi 276/277 claim status validation

Date: 2026-08-24 UTC

Branch: `feat/stedi-claim-status`

Commit: `pending` (filled after commit)

## Stedi 276/277 API

| Item | Value |
| --- | --- |
| Endpoint | `POST https://healthcare.us.stedi.com/2024-04-01/change/medicalnetwork/claimstatus/v2` |
| Version | **2024-04-01** / `claimstatus/v2` |
| Request | JSON 276 |
| Response | Synchronous JSON 277 |
| Auth | Stedi API key in `Authorization` |

Documented base JSON: `tradingPartnerServiceId`, billing `providers[]`,
`subscriber`, `encounter` dates. Optional payer claim control number
(`tradingPartnerClaimNumber`), patient control number
(`patientAccountNumber`), dependent, institutional `billingType`, and
`serviceLinesInformation`.

## Supported claim types

| Type | Status |
| --- | --- |
| Professional | Supported (same JSON endpoint) |
| Institutional | Supported (same JSON endpoint; `billingType` when present) |
| Dental | Supported (same JSON endpoint; line qualifier `AD`) |
| Claim-level | Supported |
| Service-line-level | Supported (`serviceLinesInformation`; invalid line is rejected) |

## Live Stedi 276/277

**Not executed.** Stedi documents that test API keys are not supported for
Real-Time Claim Status, and that inquiries must target production claims
already accepted into the payer's system
([Check claim status](https://www.stedi.com/docs/healthcare/check-claim-status),
[Real-Time Claim Status JSON](https://www.stedi.com/docs/healthcare/api-reference/post-healthcare-claim-status)).

```
Contract-tested against Stedi's documented 276/277 API;
live status inquiry pending production/test capability.
```

Opt-in `CHO_STEDI_LIVE_CLAIM_STATUS_TESTS` does not run in CI.

## Synthetic fixture

| Field | Value |
| --- | --- |
| Gateway | Stedi (stubbed HTTP) / Mock |
| Inquiry type | Claim-level (and service-line 1 in a separate case) |
| Synthetic claim | CLM-P-1001 |
| Synthetic transmission | Generated at 837 submit (not a real payer claim) |
| Normalized status | InProcess (Mock default); Paid (stubbed F1/65) |
| External status code | P1/20 (Mock default); F1/65 (Stedi stub) |
| Response latency | ~4ms Mock demo; stubbed HTTP otherwise |
| Retry count | 0 on success; 1 on stubbed 429/5xx-then-success |
| 277CA | Unchanged (`Accepted` remains `Accepted` when 277 is InProcess or Paid) |
| 837 transmission | Unchanged (status dimension is separate) |
| 835 / payment | Not written |

No member IDs, names, dates of birth, raw 276/277 JSON, raw X12, or API keys
are recorded here.
