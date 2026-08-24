# Claim intelligence validation

Date: 2026-08-24 UTC

Branch: `feat/claim-intelligence`

Commit: `7ec9a739`

## Synthetic claim

| Field | Value |
| --- | --- |
| Tenant | tenant-alpha |
| Claim | CLM-INT-1001 / CLM-INTEL-PAID |
| Payer | 60054 |
| Procedure | D2740 |
| Submitted | $500 |

## Timeline (paid scenario)

| Event | Source |
| --- | --- |
| 837 submitted / gateway accepted | 837 |
| 277CA accepted | 277CA |
| 835 ready for posting | 835 |

Duplicate 277CA and 835 deliveries produce a single timeline event each.

## Transaction sources

| Transaction | Status preserved |
| --- | --- |
| 837 | `AcknowledgmentAccepted` |
| 277CA | `Accepted` |
| 276/277 | `InProcess` when present; `Paid` on 276 does **not** create an 835 |
| 275 | inbound `DentalImage` sets `attachmentAvailable` |
| 835 | `AvailableForPosting` |

## Normalized status

| Scenario | Lifecycle |
| --- | --- |
| 837 + 277CA accepted + 276 in process, no 835 | `Processing` |
| 837 + 277CA + 835 paid (CLP02 1, $320 of $500) | `Paid` |
| 277CA accepted, no 835 | `AcceptedByPayer` (not `Paid`) |

## Financial summary

| Field | Value |
| --- | --- |
| Submitted | $500 |
| Paid | $320 |
| Patient responsibility | $80 |
| Posting | Not executed |

## Attachment summary

Inbound 275 `DentalImage` → `attachmentAvailable = true`. No bytes or storage
URLs in the intelligence response.

## Tests

Infrastructure composer / PHI / DI tests plus eligibility API tenant
isolation. See the PR test plan. No live Stedi dependency.

No PHI, member identifiers, attachment content, or remittance payloads are
recorded here.
