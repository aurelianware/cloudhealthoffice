# Payer-side eligibility responder validation

Date: 2026-08-23 UTC

Branch: `feat/payer-eligibility-responder`

Commit: `20bbc77f`

Stedi inbound test: **not executed**. Stedi does not currently document a
public inbound 270 payer-hosting API. This record covers the vendor-neutral
CHO responder and the canonical development ingress only.

## Synthetic fixture

| Field | Value |
| --- | --- |
| Payer | CHO Demo Health Plan (`CHO-DEMO-HEALTH` / `CHODEMO` / trading partner `19999`) |
| Tenant | `cho-demo` (resolved from payer id; inbound `claimedTenantId` ignored) |
| Subscriber | MEMBER-10001 / John Doe / DOB 1980-01-15 |
| Dependent | DEP-10001 / Jane Doe / DOB 2012-05-20 |
| Plan | Demo PPO |
| Coverage | Active 2020-01-01 – 2099-12-31 |
| Provider | NPI 1999999984 (in-network) |
| Service type | 30 (Health Benefit Plan Coverage) |

## Result

| Field | Value |
| --- | --- |
| Transport | Success (`GatewayResponse.IsSuccess = true`, HTTP 200 on `POST /api/dev/payer/eligibility`) |
| Business status | Success |
| Coverage | Active |
| Network | In-network |
| Deductible | $1,500 individual / $800 remaining |
| Copay | $25 in-network |
| Coinsurance | 20% in-network |
| OOP max | $5,000 individual / $3,200 remaining |
| Latency | ~3 ms in-process / ~80 ms through the Development API host |
| Accumulators | Unchanged (`MutationProbe.IsUnchanged`, remaining deductible/OOP identical before and after) |
| Claims / auths / payments / enrollment | Not created |

No API keys, auth headers, raw 270/271 payloads, member identifiers, names, or
dates of birth are recorded here. The fixture is invented synthetic data.

## Tests

- Infrastructure `PayerEligibility*` : 41 passed
- Eligibility-service (including canonical ingress + X12 mapper): 52 passed
- Infrastructure suite excluding live Stedi: 394 passed, 15 skipped
