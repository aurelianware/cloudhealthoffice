# Fee Schedule Engine

The Fee Schedule Engine resolves the contractually allowed rate for each line of a claim. It is the authoritative source for provider payment rates and is consumed by the adjudication workflow's `calculate-cost-sharing` and `get-rates` steps.

## Table of contents

- [Overview](#overview)
- [Rate resolution chain](#rate-resolution-chain)
- [Fee schedule types](#fee-schedule-types)
- [MPFS RVU calculation](#mpfs-rvu-calculation)
- [Payment modifier rules](#payment-modifier-rules)
- [Provider contracts](#provider-contracts)
- [Pricing result and audit trail](#pricing-result-and-audit-trail)
- [Persistence](#persistence)
- [DI registration](#di-registration)
- [Configuration reference](#configuration-reference)
- [QNXT equivalence](#qnxt-equivalence)

---

## Overview

A fee schedule maps procedure codes (CPT/HCPCS) to allowed payment rates. Every provider is linked to one or more fee schedules through a `ProviderContract`. When a claim is adjudicated, the engine:

1. Finds the provider's active contract for the plan on the service date
2. Selects the applicable fee schedule (with per-service-category overrides)
3. Looks up the rate for the procedure code and modifiers
4. Calculates the base allowed amount (flat rate, RVU, percent-of-billed, etc.)
5. Applies CMS payment modifier adjustments (bilateral, multiple procedure, co-surgery, etc.)
6. Returns a `PricingResult` with the final allowed amount and a full adjustment audit trail

The engine supports both individual line pricing (`ResolveAsync`) and batch claim pricing (`ResolveBatchAsync`).

---

## Rate resolution chain

For each claim line:

```
1. Provider contract lookup
   ProviderNpi → active ProviderContract for PlanId on ServiceDate
   If NPI not found → retry with GroupTin
   If no contract → NetworkStatus = Unknown (out-of-network)

2. Fee schedule selection
   a. Check ProviderContract.ContractLines for a procedure code range override
   b. Fall back to ProviderContract.FeeScheduleId (contract default)
   c. If no contract → IFeeScheduleRepository.GetDefaultForPlanAsync()
   d. If no schedule at all → UCR fallback (billed charges)

3. Rate line lookup (within the schedule)
   a. Exact match: ProcedureCode + Modifier
   b. Base rate:   ProcedureCode + null modifier
   c. Not found:   UCR fallback (billed charges)

4. Base amount calculation
   Depends on FeeScheduleType (see table below)

5. Payment modifier adjustments
   Applied sequentially in CMS-defined priority order

6. Units multiplication
   FinalAllowed = AdjustedRate × Units
```

---

## Fee schedule types

| `FeeScheduleType` | Rate calculation |
|---|---|
| `MedicareMpfs` | RVU-based: `(WorkRVU × WorkGPCI + PeRVU × PeGPCI + MpRVU × MpGPCI) × ConversionFactor` |
| `MedicareOpps` | Pre-calculated APC rate stored in `FeeScheduleLine.Rate` |
| `Medicaid` | Either RVU-based × `PercentOfMedicare`, or flat rate stored on the line |
| `Commercial` | Flat rate per procedure (negotiated per provider/group) |
| `Custom` | Flat rate, percent-of-billed, or percent-of-Medicare per line's `RateType` |
| `PerDiem` | `PerDiemRate × LengthOfStay` (inpatient daily rate) |
| `Drg` | Fixed case rate regardless of services rendered |
| `Capitation` | `AllowedAmount = $0` (provider is paid PMPM; claim is tracking-only) |
| `Ucr` | Fallback: billed charges (no contracted rate found) |

**`FeeScheduleRateType`** (per-line interpretation of `Rate`):

| Value | Meaning |
|---|---|
| `FlatRate` | Dollar amount per unit |
| `Rvu` | RVU-based; `Rate` is unused; `WorkRvu`/`PeRvu`/`MpRvu` fields apply |
| `PercentOfBilled` | `Rate` is a multiplier, e.g. `0.80` = 80% of billed charges |
| `PercentOfMedicare` | `Rate` is a multiplier against the Medicare MPFS rate |

---

## MPFS RVU calculation

For `FeeScheduleType.MedicareMpfs` lines with `RateType.Rvu`:

```
AllowedAmount = (WorkRVU × WorkGPCI + PeRVU × PeGPCI + MpRVU × MpGPCI) × ConversionFactor
```

**Place of service** determines which PE RVU to use:
- Non-facility POS codes (`11` Office, `12` Home, `02`/`10` Telehealth) → `FeeScheduleLine.PeRvu`
- All other POS codes → `FeeScheduleLine.PeRvuFacility`

**GPCI values** are stored on the `FeeSchedule` document (per locality):
- `WorkGpci` — physician work geographic adjustment
- `PeGpci` — practice expense geographic adjustment
- `MpGpci` — malpractice geographic adjustment

Default GPCI is `1.0` for non-locality-adjusted schedules. The CMS locality code is stored in `FeeSchedule.Locality` (e.g., `"01"` = Alabama).

**2026 example** — CPT 99213, Office, Locality 01:
```
WorkRVU = 0.97, WorkGPCI = 1.000
PeRVU   = 0.85, PeGPCI   = 0.873   (non-facility)
MpRVU   = 0.07, MpGPCI   = 0.570
ConversionFactor = 33.8872

AllowedAmount = (0.97 × 1.000 + 0.85 × 0.873 + 0.07 × 0.570) × 33.8872
              = (0.97 + 0.742 + 0.040) × 33.8872
              = 1.752 × 33.8872
              ≈ $59.38
```

---

## Payment modifier rules

Modifiers are applied sequentially in this order. Each step records a `RateAdjustment` in the `PricingResult.Adjustments` list for the 835 ERA audit trail.

| Modifier | Code | Rule |
|---|---|---|
| Professional component | `26` | Rate line is already the PC-only rate; recorded for audit |
| Technical component | `TC` | Rate line is already the TC-only rate; recorded for audit |
| Bilateral procedure | `50` | `AllowedAmount × 1.50` (applies only if `FeeScheduleLine.BilateralAdjustmentApplies = true`) |
| Increased complexity | `22` | `AllowedAmount × 1.25` |
| Reduced services | `52` | `AllowedAmount × 0.50` |
| Discontinued procedure | `53` | `AllowedAmount × 0.50` |
| Co-surgery | `62` | `AllowedAmount × 0.625` (each surgeon) |
| Assistant surgeon | `80` | `AllowedAmount × 0.16` (applies only if `AssistantAtSurgeryAllowed = true`) |
| Assistant-at-surgery (PA/NP/CRNA) | `AS` | `AssistantRate × 0.85` = 13.6% of primary rate |
| Multiple procedures | `51` | `AllowedAmount × 0.50` for secondary lines (line 2+) |

The multiple procedure reduction (`51`) is also applied automatically when `LineNumber > 1` and `TotalLineCount > 1`, without requiring the modifier to be present on the claim.

`AllowedAmount` is floored at `$0.00` — modifiers cannot produce a negative payment.

---

## Provider contracts

`ProviderContract` links a provider to a fee schedule for a specific plan:

```
ProviderContract
  ├── ProviderNpi          (primary lookup key)
  ├── GroupTin             (fallback if NPI lookup fails)
  ├── PlanId
  ├── EffectiveDate / TermDate
  ├── NetworkStatus        (InNetwork | OutOfNetwork | Participating | NonParticipating)
  ├── FeeScheduleId        (default schedule for all services)
  └── ContractLines[]      (per-service-category overrides)
        ├── ProcedureCodeFrom / ProcedureCodeTo  (CPT/HCPCS range, inclusive)
        └── FeeScheduleId                         (override schedule for this range)
```

**Example:** A provider might have a commercial fee schedule for most services (`FeeScheduleId = "commercial-2026"`) but a separate mental health schedule for procedure codes `90785–90899` via a `ContractLine`.

Contract lookup:
1. Filter by `TenantId`, `PlanId`, `EffectiveDate ≤ ServiceDate`, `TermDate > ServiceDate`
2. Try `ProviderNpi` first; if not found, retry with `GroupTin`
3. If no contract found → `NetworkStatus = Unknown`, fall back to plan default schedule

---

## Pricing result and audit trail

`PricingResult` captures the complete resolution decision for one claim line:

```csharp
public record PricingResult
{
    int LineNumber          // 1-based claim line number
    string ProcedureCode
    decimal AllowedAmount   // Final allowed after all adjustments
    decimal BilledAmount
    decimal ContractualAdjustment  // BilledAmount − AllowedAmount (CO-45 on 835)
    FeeScheduleType FeeScheduleType
    RateSource RateSource
    NetworkStatus NetworkStatus
    string? FeeScheduleId   // For audit
    string? FeeScheduleName // For portal display
    IReadOnlyList<RateAdjustment> Adjustments  // Each modifier applied
}
```

`RateAdjustment` maps directly to a CAS segment on the 835 ERA:

```csharp
public record RateAdjustment
{
    string Modifier          // e.g. "50", "51", "26"
    string Description       // Human-readable for portal/audit
    decimal AdjustmentFactor // e.g. 1.50 for bilateral
    decimal AdjustmentAmount // Dollar delta (negative = reduction)
}
```

`PricingResultSet` wraps batch results with computed totals:
- `TotalAllowedAmount`
- `TotalBilledAmount`
- `TotalContractualAdjustment`

---

## Persistence

Two Cosmos DB containers (or MongoDB collections) back the engine. Lines are embedded in the parent `FeeSchedule` document — no join required at adjudication time.

### Fee schedules

**Partition key:** `/tenantId`

| Field | Description |
|---|---|
| `id` | `"{tenantId}:{name}:{effectiveDate:yyyyMMdd}"` |
| `tenantId` | Multi-tenant isolation key |
| `name` | Human-readable, e.g. `"Medicare MPFS 2026 Locality 01"` |
| `type` | `FeeScheduleType` enum |
| `effectiveDate` / `termDate` | Date range the schedule is active |
| `locality` | CMS locality code for MPFS schedules |
| `conversionFactor` | CMS annual CF (MPFS only) |
| `workGpci` / `peGpci` / `mpGpci` | Geographic adjustment factors |
| `percentOfMedicare` | Medicaid multiplier (Medicaid schedules only) |
| `perDiemRate` | Daily rate (PerDiem schedules only) |
| `lines[]` | Embedded `FeeScheduleLine` documents |
| `defaultForPlanId` | Populated for plan-default schedules (enables `GetDefaultForPlanAsync`) |

**Recommended indexes (Cosmos):** `effectiveDate`, `defaultForPlanId`

**Recommended indexes (MongoDB):**
```
{ tenantId: 1, defaultForPlanId: 1, effectiveDate: -1 }
```

### Provider contracts

**Partition key:** `/tenantId`

| Field | Description |
|---|---|
| `id` | `"{tenantId}:{providerNpi}:{planId}"` |
| `providerNpi` | Rendering or billing NPI (primary lookup) |
| `groupTin` | Group/organization TIN (fallback lookup) |
| `planId` | Benefit plan this contract applies to |
| `effectiveDate` / `termDate` | Active date range |
| `networkStatus` | `InNetwork`, `OutOfNetwork`, etc. |
| `feeScheduleId` | Default fee schedule for all services |
| `contractLines[]` | Per-procedure-code-range overrides |

**Recommended indexes (MongoDB):**
```
{ tenantId: 1, providerNpi: 1, planId: 1, effectiveDate: -1 }
{ tenantId: 1, groupTin: 1, planId: 1 }
```

---

## DI registration

In the host service's `Program.cs`:

```csharp
// Auto-detect Mongo vs Cosmos from configuration
builder.Services.AddFeeScheduleEngine()
    .UseRepositoriesFromConfiguration(builder.Configuration);

// Or explicitly:
builder.Services.AddFeeScheduleEngine()
    .UseCosmosRepositories();   // requires CosmosClient to be registered

builder.Services.AddFeeScheduleEngine()
    .UseMongoRepositories();    // requires IMongoDatabase to be registered
```

This registers:
- `IRateResolutionService` → `RateResolutionService`
- `IFeeScheduleRepository` → `FeeScheduleRepositoryCosmos` or `FeeScheduleRepositoryMongo`
- `IProviderContractRepository` → same implementation (implements both interfaces)

The fee schedule engine is intended to be consumed by the **adjudication service** or the **Argo workflow steps** for `calculate-cost-sharing` and `get-rates`. It has no dependency on HTTP context and can be used in any host.

---

## Configuration reference

| Key | Required | Description |
|---|---|---|
| `CosmosDb:DatabaseName` | Cosmos only | Database name (default: `CloudHealthOffice`) |
| `CosmosDb:Endpoint` / `CosmosDb:Key` | Cosmos only | Cosmos DB credentials |
| `MongoDb:ConnectionString` | Mongo only | MongoDB connection string |
| `MongoDb:DatabaseName` | Mongo only | MongoDB database name |
| `FeeScheduleEngine:FeeScheduleContainer` | Cosmos only | Container name (default: `FeeSchedules`) |
| `FeeScheduleEngine:ProviderContractContainer` | Cosmos only | Container name (default: `ProviderContracts`) |
| `FeeScheduleEngine:FeeScheduleCollection` | Mongo only | Collection name (default: `FeeSchedules`) |
| `FeeScheduleEngine:ProviderContractCollection` | Mongo only | Collection name (default: `ProviderContracts`) |

---

## QNXT equivalence

| CHO component | QNXT equivalent |
|---|---|
| `FeeSchedule` | `FS_FEE_SCHEDULE` |
| `FeeScheduleLine` | `FS_FEE_SCHEDULE_LINE` |
| `ProviderContract` | `CONTRACT` + `CONTRACT_LINE` + `PROV_PLAN` |
| `ProviderContractLine` | `CONTRACT_LINE` (service category override) |
| `NetworkStatus` | `PROV_PLAN.IN_NETWORK_IND` |
| `IFeeScheduleRepository.GetDefaultForPlanAsync` | `FS_FEE_SCHEDULE.PLAN_ID` + date range join |
| `IProviderContractRepository.GetContractAsync` | `CONTRACT` lookup by NPI then TIN with date range |
| `PaymentModifiers.Bilateral` (`50`) | CMS bilateral surgery rule |
| `PaymentModifiers.MultipleProcedures` (`51`) | CMS multiple procedure reduction |
| `FeeScheduleType.Capitation` | `CONTRACT.CONTRACT_TYPE = 'CAP'` |
| `RateSource.BilledCharges` (UCR fallback) | QNXT `PROC_PRICE.PRICE_TYPE = 'UCR'` |
