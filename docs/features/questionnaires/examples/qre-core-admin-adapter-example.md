# Core Administration Adapter QRE: Worked Example (Fictional)

**This is a fictional example. Example Regional MCO is not a real plan, and every value below is invented.**
Blank questionnaire: [qre-core-admin-adapter-questions.md](../qre-core-admin-adapter-questions.md)

> **Adapter status.** The QNXT adapters this example configures are interface stubs today (`QnxtClaimAdapter`, `QnxtProviderAdapter`, `QnxtBenefitPlanAdapter`, `QnxtIdCardAdapter` throw `NotImplementedException`). The tenant configuration at the end shows what this plan's configuration would look like once its adapters are built from these answers. Applying it before then would make the `qnxt` adapter calls fail.

## Summary of Answers

| Question area | Example answer |
|---|---|
| Plan | Example Regional MCO (fictional), Texas Medicaid STAR and CHIP |
| Membership | About 150,000 members |
| Core platform | TriZetto QNXT, vendor-hosted (BPaaS) |
| Database | SQL Server, read-only reporting replica refreshed nightly |
| Vendor APIs | QXI enabled for member, provider and claim inquiry; no write services licensed |
| Network path | Site-to-site VPN to the hosting provider |
| Authentication | OAuth 2.0 client credentials for QXI; SQL login for the replica, rotated every 90 days |
| Initial load | Three years of members and claims from the replica |
| Ongoing changes | QXI inquiry for real-time reads; nightly deltas from the replica |
| Prior authorization | UM module in QNXT is the system of record. CHO provides CRD, DTR and PAS for the first EHR partner; X12 278 and the portal stay in place |
| Vendor contract | Replica access allowed; direct production database access not allowed |
| State requirements | Texas HHSC notice required before any change to the claims system; none for read-only APIs |

## Processing Engines

| Processing engine | Engine today | Starting mode | Target mode | Cutover order and dependencies |
|---|---|---|---|---|
| Claims adjudication: professional Medicaid | QNXT | Legacy only | Augment, then Replace | After appeals; needs accumulator ownership agreed |
| Claims adjudication: institutional Medicaid | QNXT | Legacy only | Legacy only | Not in scope |
| Claims adjudication: professional CHIP | QNXT | Legacy only | Legacy only | Not in scope |
| Benefit and cost-sharing calculation | QNXT | Legacy only | Follows professional Medicaid | Same cutover as adjudication |
| Pricing and fee schedule resolution | QNXT | Legacy only | Follows professional Medicaid | Texas Medicaid fee schedules loaded first |
| Claims edits | Commercial edit vendor | Legacy only | Legacy only | Not in scope |
| Prior authorization rules | QNXT UM | Legacy only | Legacy only | CHO serves CRD, DTR and PAS but does not decide |

## Data Domains

| Data domain | Source of truth today | Source of truth at target | Copy direction | Access method | Sync frequency |
|---|---|---|---|---|---|
| Members and eligibility | QNXT | QNXT | Core to CHO | QXI and replica | Real time (QXI); nightly (replica) |
| Providers and networks | QNXT | QNXT | Core to CHO | QXI and replica | Nightly |
| Claims and claim history | QNXT | QNXT, then shared for professional Medicaid | Core to CHO, both ways after cutover | Replica | Nightly, then daily both ways |
| Authorizations | QNXT UM | QNXT UM | Core to CHO | Replica | Nightly |
| Appeals and grievances | QNXT | CHO | CHO to core for reprocessing overturned appeals | Replica (read); vendor file feed (overturns) | Daily |

## Transition Ownership

| Item | Owner during transition |
|---|---|
| Accumulators | QNXT. CHO receives daily accumulator updates and sends professional Medicaid accumulator changes back daily after cutover |
| Claim history for edits | Shared through the nightly claim history sync |
| Payments and 835 | QNXT runs the single payment cycle and produces one 835 per provider |
| Member and provider letters | QNXT's correspondence vendor |

## Resulting Tenant Configuration

```json
{
  "tenantId": "example-regional-mco",
  "configuration": {
    "claimsPlatform": {
      "platform": "qnxt",
      "apiEndpoint": "https://qxi.example.invalid",
      "keyVaultSecretName": "example-mco-qxi-client",
      "timeoutMs": 10000,
      "retryCount": 2,
      "platformSettings": { "historySource": "replica", "deltaSchedule": "nightly" }
    },
    "providerPlatform": {
      "platform": "qnxt",
      "apiEndpoint": "https://qxi.example.invalid",
      "keyVaultSecretName": "example-mco-qxi-client",
      "platformSettings": { "deltaSchedule": "nightly" }
    },
    "benefitPlanPlatform": { "platform": "qnxt" },
    "idCardPlatform": { "platform": "qnxt" }
  },
  "operatingMode": {
    "engines": {
      "professional-medicaid": "legacy",
      "institutional-medicaid": "legacy",
      "professional-chip": "legacy",
      "benefitCalculation": "augment",
      "rateResolution": "augment"
    }
  }
}
```

When professional Medicaid enters Augment, `professional-medicaid` changes to `augment`. At cutover it changes to `replace`. Prior authorization stays in Augment (QNXT authoritative) through the authorization-service setting `Cms0057:Authorization:OperatingMode` = `Augment`, with `AugmentBackend` = `qnxt`.

**Why `benefitCalculation` is set to `augment`:** a claim type with no matching key falls back to the `benefitCalculation` engine mode, and an unconfigured engine defaults to `replace` (see `ClaimTypeRouter`). The fallback can be `augment` or `replace` but never `legacy`, so every claim type the plan sends must be listed explicitly. Setting the fallback to `augment` keeps an unlisted claim type from becoming CHO-authoritative by accident.
