# Stedi 835 ERA remittance validation

Date: 2026-08-24 UTC

Branch: `feat/stedi-835`

Commit: `pending`

## Stedi 835 API

| Item | Value |
| --- | --- |
| Discovery | Webhook `transaction.processed.v2` or `GET https://core.us.stedi.com/2023-08-01/polling/transactions` |
| Retrieve | `GET https://healthcare.us.stedi.com/2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/835` |
| Version | **2024-04-01** / reports v2 `/835` |
| Shape | JSON ERA (CHC-compatible) |
| Auth | Stedi API key in `Authorization` |
| Enrollment | Required with the payer for ERAs |

## Supported data

| Area | Status |
| --- | --- |
| Claim-level remittance | Supported |
| Service-line remittance | Supported |
| Adjustments (CO/PR/OA) | Supported; kinds classified from group+reason |
| Patient responsibility | Supported (CLP05 + PR adjustments) |
| Dental remittance | Supported (qualifier AD, tooth code when present) |

## Live Stedi 835

**Not executed.** Stedi sandbox accounts do not produce test ERAs for this
workflow. Retrieve needs a production-account ERA `transactionId`.

```
Contract-tested against Stedi documented 835 API;
live ERA validation pending production/test capability.
```

## Synthetic fixture

| Field | Value |
| --- | --- |
| Gateway | Stedi (stubbed HTTP) / processor (Mock inject) |
| Synthetic claim | CLM-P-1001 / CLM-ERA-1001 |
| Inquiry type | Inbound 835 retrieve + match |
| Matching method | Payer claim control number, then patient control number |
| Charged / allowed / paid | $500 / $400 / $320 |
| Patient responsibility | $80 (deductible $50 + coinsurance $30) |
| Adjustments | CO-45 contractual $100; PR-1 $50; PR-2 $30 |
| Lifecycle | AvailableForPosting |
| 277CA | Unchanged (`Accepted`) |
| 837 transmission | Unchanged (`AcknowledgmentAccepted`) |
| Payment posting | Not executed |

No raw 835, PHI, member identifiers, or banking/trace numbers are recorded here.
