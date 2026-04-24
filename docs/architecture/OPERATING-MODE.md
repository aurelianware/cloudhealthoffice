# Operating Mode (Augment / Replace)

The OperatingMode pattern enables progressive migration from a legacy core admin system (e.g., QNXT) to CHO. Each engine can run independently in Augment (shadow) or Replace (authoritative) mode, per tenant, per claim type.

## Table of contents

- [Design](#design)
- [Configuration](#configuration)
- [Claim type routing](#claim-type-routing)
- [AugmentResult wrapper](#augmentresult-wrapper)
- [Discrepancy comparison](#discrepancy-comparison)
- [Migration workflow](#migration-workflow)

---

## Design

```
┌──────────────────────────────────────────────────────────┐
│                  Tenant Configuration                     │
│                                                           │
│  "professional-medicaid": "replace"   ← CHO authoritative│
│  "institutional-medicaid": "augment"  ← shadow mode      │
│  "professional-commercial": "legacy"  ← QNXT only        │
└────────────────────────┬─────────────────────────────────┘
                         │
                         ▼
                  ClaimTypeRouter.Route()
                    ┌────┼────────┐
                    │    │        │
                    ▼    ▼        ▼
               Replace  Augment  LegacyOnly
                 │        │         │
                 │        │         └─→ Return immediately
                 │        │
                 ▼        ▼
          CalculateWithModeAsync()
                 │        │
                 │        ├─→ Run CHO calculation
                 │        ├─→ Compare with legacy result
                 │        └─→ Return AugmentResult (CHO + legacy + discrepancies)
                 │
                 └─→ Run CHO calculation
                 └─→ Return AugmentResult (CHO only, authoritative=true)
```

**Key principles:**

- **No big-bang migration.** Each claim type/LOB combination can be flipped independently.
- **Shadow mode is safe.** In Augment mode, the legacy system's result is authoritative. CHO's result is logged and compared but never applied.
- **Discrepancies are actionable.** The comparison captures specific differences (plan paid, deductible, copay, per-line amounts) with human-readable descriptions.
- **Default is Replace.** Unconfigured engines default to CHO authoritative, so new tenants get full CHO without configuration.

---

## Configuration

Operating mode is stored on the tenant document in tenant-service:

```json
{
  "tenantId": "txmco01",
  "operatingMode": {
    "engines": {
      "professional-medicaid": "replace",
      "institutional-medicaid": "augment",
      "benefitCalculation": "replace",
      "rateResolution": "replace"
    },
    "updatedAt": "2026-04-12T14:30:00Z",
    "updatedBy": "admin@example.com"
  }
}
```

**API:** `PUT /api/tenants/{tenantId}/operating-mode` on tenant-service.

**Engine names** (`OperatingModeConfiguration.EngineNames`):

| Constant | Key | Description |
|----------|-----|-------------|
| `BenefitCalculation` | `benefitCalculation` | Cost-sharing waterfall |
| `RateResolution` | `rateResolution` | Fee schedule pricing |
| `NcciEdits` | `ncciEdits` | NCCI/MUE edits |
| `ClaimsScrubbing` | `claimsScrubbing` | Pre-payment validation |
| `CobCalculation` | `cobCalculation` | Coordination of benefits |
| `RiskAdjustment` | `riskAdjustment` | HCC scoring |
| `PriorAuthRules` | `priorAuthRules` | Prior auth evaluation |
| `ProviderVerification` | `providerVerification` | Exclusion screening |
| `TerminologyCrosswalk` | `terminologyCrosswalk` | Code crosswalk |

---

## Claim type routing

The `ClaimTypeRouter` resolves routing decisions using a compound key hierarchy:

1. **Compound key:** `{claimType}-{lineOfBusiness}` (e.g., `professional-medicaid`)
2. **Type key:** `{claimType}` (e.g., `professional`)
3. **Engine default:** `benefitCalculation` engine mode

LOB mapping: 1=commercial, 2=medicare, 3=medicaid, 4=chip, 5=exchange.

**Route outcomes:**

| Route | CHO processes? | CHO authoritative? | Legacy consulted? |
|-------|---------------|--------------------|--------------------|
| `ChoReplace` | Yes | Yes | No |
| `ChoAugment` | Yes | No | Yes (result compared) |
| `LegacyOnly` | No | — | Yes |

---

## AugmentResult wrapper

`AugmentResult<T>` wraps every engine output with mode context:

```csharp
public class AugmentResult<T>
{
    public T ChoResult { get; init; }           // CHO's computed result
    public T? LegacyResult { get; init; }       // Legacy result (Augment only)
    public EngineOperatingMode Mode { get; init; }
    public bool Authoritative { get; init; }    // True in Replace
    public string[] Discrepancies { get; init; }
    public DateTime? ComparedAt { get; init; }
}
```

**Factory methods:**

- `AugmentResult.ForReplace(choResult)` — CHO authoritative, no comparison
- `AugmentResult.ForAugment(choResult, legacyResult, discrepancies)` — Shadow mode with comparison

---

## Discrepancy comparison

When `CalculateWithModeAsync` runs in Augment mode, `CompareBenefitResults()` captures:

- Overall outcome (approved vs. denied)
- Denial reason codes
- Total plan paid, deductible, copay, coinsurance, allowed amount
- Total member responsibility
- Line count differences
- Per-line plan paid and coverage status
- Lines present in one result but missing from the other

Discrepancies are logged at Warning level and included in the `AdjudicationResponse.Discrepancies` array for downstream consumption (dashboard, alerting, audit).

---

## Migration workflow

### Phase 1: Shadow mode (Augment)

1. Set target claim type to `augment` in tenant config
2. Both CHO and QNXT process claims; QNXT result is authoritative
3. Monitor discrepancy rate on the Blazor portal dashboard
4. Investigate and resolve systematic differences
5. Target: <1% discrepancy rate sustained over 30 days

### Phase 2: Flip to Replace

1. Set target claim type to `replace`
2. CHO result becomes authoritative
3. QNXT no longer consulted for this claim type
4. Continue monitoring for regressions

### Phase 3: Expand

Repeat for each claim type / LOB combination:

1. TX Medicaid professional (837P)
2. TX Medicaid institutional (837I)
3. TX CHIP professional
4. TX STAR+PLUS professional
5. Commercial professional

### Phase 4: Decommission

When all claim types route through CHO in Replace mode:

1. Remove `ICoreAdminAdapter` for the tenant
2. CHO becomes system of record
3. Enable 835 ERA generation via payment-service
4. Enable encounter submission via EncounterEngine
