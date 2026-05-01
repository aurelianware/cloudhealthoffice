# NCCI / MUE Edits Enforcement Pipeline (Capability 5.7)

> **Status — Phase 1, May 2026.** Replaces `NcciEditsStubStage` at
> Order=400 with a real `CloudHealthOffice.NcciEngine`-backed
> implementation. NCCI Column 1/Column 2 (PTP) bundling edits and MUE
> (Medically Unlikely Edits) unit checks now run between
> `BenefitCalculationStage` (300) and the COB stub (500). Failed edits
> attach structured `NcciEditFailureSnapshot` records to the persisted
> claim version via the projection-bypass write — extending the 5.5
> bypass surface from `AdjudicationResult` only to
> `AdjudicationResult + PendDetails`.
>
> See [`claim-adjudication-pipeline.md`](./claim-adjudication-pipeline.md)
> for the orchestrator + stage-interface foundation.

## Why this exists

Pre-5.7, every adjudicated claim flowed through a no-op
`NcciEditsStubStage` that returned `Pass` regardless of the underlying
procedure codes. Bundled procedure pairs and units exceeding clinically
unlikely limits all paid the same as clean claims — every dollar of
NCCI policy enforcement was leaking through the stage replacement seam.

5.7 wires the engine that's been built and unit-tested as a class
library since Q1 2026. Engine code is unchanged; 5.7 is pure
consumer-side wiring + enforcement-mode policy + the persistence
extension that gets the snapshot to the head row.

## Pipeline placement

```
... 100 Scrubbing   (5.4)
    200 NetworkCredentialing (5.6)
    300 BenefitCalculation   (5.5)
    400 NcciEdits             ◄ 5.7 — this doc
    500 CoordinationOfBenefits (stub; 5.8)
    600 AiExamination          (stub; 5.9 — consumes EditFailures.ModifierOverridePresent)
    999 Persistence            (5.5)
```

NCCI runs after benefit calculation so post-edit adjustments to the
allowed amount are computable downstream by 5.10 (Remittance
Generation). 5.7 itself **does not** mutate `AllowedAmount` — it
records the failure; 5.10 reduces the payable amount and emits the
suggested CARC/RARC codes on the 835.

## Decisions

### D1 — In-process engine (no separate service)

The engine ships as `CloudHealthOffice.NcciEngine` class library with a
fluent registration extension:

```csharp
builder.Services.AddNcciEngine().UseRepositoryFromConfiguration(builder.Configuration);
```

Auto-detect: when `MongoDb:ConnectionString` is set the Mongo
repository binds; otherwise the Cosmos repository binds. Mirrors the
5.4 ClaimsScrubEngine wiring pattern. No microservice to decommission;
no external network call inside the stage.

### D2 — Stage replacement via direct DI swap

Same shape as 5.4 / 5.6: in `Program.cs`, the production stub
registration was replaced in place rather than through
`services.RemoveAll<>()`. The stub never shipped to a customer
environment, so removal isn't required.

```csharp
// before 5.7
builder.Services.AddScoped<IClaimAdjudicationStage, NcciEditsStubStage>();
// after 5.7
builder.Services.AddScoped<IClaimAdjudicationStage, NcciEditsStage>();
```

`NcciEditsStubStage.cs` was deleted in this PR (mirrors 5.4's
`ScrubbingStubStage.cs` deletion). Incidentally, the dead
`NetworkCredentialingStubStage.cs` (still on disk after the 5.6 PR
landed) was deleted alongside.

### D3 — `IsRequired = true`

NCCI is foundational claim integrity. Disabling the stage would let
bundled codes pay alongside the comprehensive codes that absorb them
— rule violations the engine was built to prevent. Tenants that don't
want hard NCCI enforcement set `NcciMode = SoftValidation` (D6 below)
rather than disabling the stage.

### D4 — `EditFailures` persisted on `Claim.PendDetails`

The on-disk model already carries the persistence surface:

```csharp
public class PendDetails
{
    public string PendCode { get; set; }     // "NCCI" / "MUE" / ...
    public string? PendReason { get; set; }
    public DateTime PendedAt { get; set; }
    public List<NcciEditFailureSnapshot> EditFailures { get; set; }
}

public class Claim
{
    public AdjudicationResult? AdjudicationResult { get; set; }
    public PendDetails? PendDetails { get; set; }   // load-bearing for 5.7
    public AiExamination? AiExamination { get; set; }
}
```

`PendDetails` is intentionally kept distinct from `AdjudicationResult`
so the deterministic edit-failure reason cannot be silently
overwritten by a downstream consumer (notably the AI examiner, which
writes its output to the separate `AiExamination` field — never to
`PendDetails`).

The 5.7 plan-phase audit found that the prompt's premise
("`AdjudicationResult.EditFailures` already exists, no PersistenceStage
modification needed") was incorrect — `EditFailures` lives on
`PendDetails`, not `AdjudicationResult`. Two persistence options
surfaced:

- **A.** Defer persistence (mirror 5.4's α — keep failures on context,
  let 5.13 wire a separate write path).
- **B.** Extend the 5.5 projection bypass with an optional
  `PendDetails?` parameter so PersistenceStage forwards
  `context.PendDetails` to the head row in the same write that already
  patches `AdjudicationResult`.

5.7 ships **Option B**. The bypass is the architecturally coherent
surface for "operationally distinct from claim identity" projection
state (which is exactly what `PendDetails` is per its model
docstring). The signature change is non-breaking: the new parameter is
optional with `null` default.

```csharp
Task<bool> UpdateAdjudicationProjectionAsync(
    string tenantId,
    string claimVersionId,
    AdjudicationResult adjudicationResult,
    IReadOnlyList<LineAdjudicationResult> lineResults,
    CancellationToken ct = default,
    PendDetails? pendDetails = null);   // ← 5.7 addition
```

Both Cosmos and Mongo implementations forward the field when non-null;
null leaves the head row's existing `PendDetails` untouched (so a
clean re-adjudication doesn't accidentally drop a prior pend reason).

### D5 — `NcciEnforcementMode` extends `TenantEnforcementPolicyOptions`

5.7 extends the 5.6 options class rather than introducing a parallel
config surface:

```csharp
public class TenantEnforcementPolicyOptions
{
    public NetworkEnforcementMode NetworkMode { get; set; }
        = NetworkEnforcementMode.FailClosed;
    public CredentialingEnforcementMode CredentialingMode { get; set; }
        = CredentialingEnforcementMode.FailClosed;
    public NcciEnforcementMode NcciMode { get; set; }
        = NcciEnforcementMode.PendForReview;     // ← 5.7 default
}

public enum NcciEnforcementMode { PendForReview, Deny, SoftValidation }
```

The default **diverges** from the FailClosed default of the other
enforcement modes. Reasoning:

- NCCI failures often have a legitimate -59 / X{EPSU} modifier
  override path. Auto-denial without human review is operationally
  harsh.
- The work queue is the right channel for "this might be a bundling
  violation, but might be a legitimate distinct-procedure case" — and
  the AI examiner (5.9) consumes `NcciEditFailureSnapshot` entries
  where `IsModifierAddressable()` is true exactly to assist that
  review.

Mode behaviour:

| Mode | Stage outcome | Failures recorded? | When to use |
|---|---|---|---|
| `PendForReview` (default) | `Pend` (continue=true) | yes | production default — failures route to the work queue |
| `Deny` | `Deny` (continue=false) | yes | tenants confident NCCI tables are tuned for hard denial |
| `SoftValidation` | `Pass` (continue=true) | yes | rollout / observability — telemetry without payment effect |

`Deny` is a `Deny` factory, **not** `Reject`. `Reject` is reserved for
structural pre-adjudication failures (5.4 scrubbing); `Deny` is the
terminal benefit-side denial — see
[`IClaimAdjudicationStage.cs`](../../src/services/claims-service/Services/Adjudication/IClaimAdjudicationStage.cs)
docstring.

### D6 — Mapping layer in claims-service

[`ClaimToNcciScrubRequestMapper`](../../src/services/claims-service/Services/Adjudication/Mapping/ClaimToNcciScrubRequestMapper.cs)
mirrors 5.4's `ClaimToX12837Mapper` — static class with a single
`Map(AdapterClaim)` entry point. The engine consumes its own
`NcciScrubRequest` shape with engine-local `ClaimServiceLine`; the
mapper translates from `AdapterClaim`. Engine stays domain-agnostic so
state-Medicaid EDI ingest can use it directly in Phase 2.

### D7 — Earliest service date is the effective date

`NcciScrubRequest.EffectiveDate` resolves which CMS quarter's NCCI /
MUE table applies. Mapper sets it to the earliest line-level
`ServiceDateFrom` (mirroring 5.6's credentialing-as-of-date semantic
— most-restrictive interpretation).

### D8 — Engine exception → mode-driven outcome

Try/catch around `INcciEditService.ScrubAsync`. On non-cancellation
exception:

- a synthetic snapshot with `RuleId = "ENGINE_EXCEPTION"`,
  `EditType = "EngineError"` and `Message = "NCCI engine threw:
  {TypeName}"` is appended to `context.PendDetails.EditFailures`
  (PHI-safe — no `ex.Message` interpolation, full exception detail to
  ILogger only)
- the stage returns the mode-driven outcome (Pend / Deny / Pass) so
  the orchestrator's safety-net catch is a fallback, not the primary
  path

### D9 — `ClaimType` enum → engine string

| Platform `ClaimType` | Engine `ClaimType` |
|---|---|
| `Professional` (1) | `"837P"` |
| `Institutional` (2) | `"837I"` |
| `Dental` (3) | `"837D"` |
| (other) | `"837P"` (default) |

The engine's MUE branch hard-checks `ClaimType == "837P"` for the
professional-vs-facility selection; 837I and 837D both land in the
facility branch. NCCI pair edits are claim-type-agnostic. Dental
behaviour is acceptable today; per-rule-set claim-type handling is
Phase 2 territory.

### D10 — Modifier override semantic is engine-side, not stage-side

The original plan-phase Decision 15 contemplated stage-side modifier
inspection on each affected line to set the snapshot's
`ModifierOverridePresent`. Plan audit found this is unnecessary — the
engine pre-filters override-present cases before emitting failures (see
[`NcciEditService.cs`](../../src/engines/CloudHealthOffice.NcciEngine/Services/NcciEditService.cs):
the `if (overridePresent) return;` short-circuit at the top of
`EvaluatePairEdit`). For any failure in the result list, the engine
has already determined that no override modifier applied — so the
field is structurally `false` on engine output and the stage simply
copies it through.

The snapshot's `IsModifierAddressable()` predicate consumed by the AI
examiner (5.9) gates on `RuleId == "NE001"` and `EditType == "NcciPair"`
(case-insensitive) — independent of `ModifierOverridePresent`. So the
copy-through is sufficient.

### D11 — Missing-table tenants get a soft-pass naturally

Phase 1 doesn't auto-load NCCI seed data at startup
(`SeedNcciDataAsync` is operator-controlled — Phase 2 will ship the
quarterly import workflow). For a tenant with no NCCI / MUE data
loaded, the engine's repository lookups return `null` for every pair
and MUE, the loops emit no failures, and `NcciScrubResult.Passed` is
`true`. The stage requires no pre-flight `GetTableVersionAsync`
guard — missing data is the engine's natural soft-pass path.

The stage **does** explicitly soft-pass when the mapper produces zero
engine-valid lines (engine has `[Required] [MinLength(1)]` on
`ServiceLines` — calling with zero lines would throw at the boundary).
This catches malformed claims that survived 5.4 scrubbing somehow
without stalling the pipeline.

## Field-by-field mapping

Engine `NcciEditFailure` → claims-service `NcciEditFailureSnapshot`:

| Snapshot field | Source | Notes |
|---|---|---|
| `EditType` | `MapEditType(failure.EditType)` | `NcciPair`/`Mue`/`Unknown` (PascalCase, matches `IsModifierAddressable()` comparison) |
| `RuleId` | direct copy | `"NE001"` for NCCI pair, `"NE002"` for MUE |
| `Message` | direct copy | engine-generated, PHI-safe (procedure codes + integer MAI) |
| `Column1Code` | direct copy | populated for NCCI pair edits only |
| `Column2Code` | direct copy | populated for both NCCI pair AND MUE (engine sets to procedure code) |
| `AffectedLineNumbers` | direct copy | 1-based per engine model docstring |
| `ModifierOverridePresent` | direct copy | always `false` on engine output (D10) |
| `UnitsBilled` | direct copy | populated for MUE only |
| `MueMaxUnits` | direct copy | populated for MUE only |
| `SuggestedCarc` | direct copy | engine emits `"97"` (MI=0), `"B20"` (MI=1, no override), `"151"` (MUE) |
| `SuggestedRarc` | direct copy | engine emits `"N519"` (NCCI) or `"N115"` (MUE) |

The mapping is a trivial 1:1 — there are no computed or
claims-service-derived fields. The implementation lives in
[`NcciEditsStage.MapFailure`](../../src/services/claims-service/Services/Adjudication/Stages/NcciEditsStage.cs).

## Telemetry

`ActivitySource = "ClaimsService.Adjudication"`, span name
`Adjudication.NcciEdits`. Tags:

```
ncci.mode             = PendForReview | Deny | SoftValidation
ncci.engine_status    = success | exception | mapper_invalid_lines
ncci.outcome          = approve | pend | deny | softpass | softvalidation
ncci.pairs_checked    = <int>          (success only)
ncci.mues_checked     = <int>          (success only)
ncci.failures         = <int>          (success only)
claim.versionId       = <ClaimVersionId>
tenant.id             = <TenantId>
```

`engine_status` values:

- **`success`** — engine call returned. Note that a tenant with no
  NCCI / MUE table loaded surfaces here as `success` with
  `pairs_checked` / `mues_checked` populated but `failures = 0` — the
  engine's repository lookups return null on every key and the loops
  emit no failures (Decision 11). 5.7 doesn't positively detect
  missing-table to keep the stage simple; if production needs that
  signal, add a pre-flight `INcciEditService.GetTableVersionAsync`
  guard in a follow-up.
- **`exception`** — engine threw; the synthetic snapshot path applied
  (Decision 8).
- **`mapper_invalid_lines`** — every claim line failed
  `IsLineEngineValid` (procedure code not 5-char CPT/HCPCS, units
  outside [0.01, 9999], or missing service date). The stage falls back
  to a soft-pass without calling the engine, since the engine's
  `[Required] [MinLength(1)]` validation on `ServiceLines` would throw
  at the boundary. This is a data-quality signal — 5.4 scrubbing
  should have rejected the claim upstream, so non-zero counts here
  indicate a scrubbing-rule gap.

These hang off the orchestrator's parent span so traces show the full
adjudication run.

## Decommission scope

None. NCCI was always an in-process engine target; there's no
parallel microservice to retire (unlike 5.4's
`claims-scrubbing-service` decommission).

## Recovery posture

Worst-case rollback: revert this PR. Stub stage restored; engine
project reference removed; `NcciMode` field removed from
`TenantEnforcementPolicyOptions`; appsettings reverted; test files
removed; `IClaimRepository.UpdateAdjudicationProjectionAsync` reverts
to the 5-parameter signature. No data changes; no migration to undo.
The optional `PendDetails?` field on the projection bypass is the
only forward-incompatible surface — Cosmos / Mongo head rows that
captured a `PendDetails` projection during 5.7 would still
deserialize correctly into the pre-5.7 `Claim` model (the field
already existed) and would simply not be re-projected on
re-adjudication.

## After 5.7

Open Claims Phase 1 capabilities:

- **5.8 Coordination of Benefits** — replaces `CoordinationOfBenefitsStubStage` at Order=500.
- **5.9 AI-Backed Examination** — replaces `AiExaminationStubStage` at Order=600; consumes `NcciEditFailureSnapshot.IsModifierAddressable()`.
- **5.10 Remittance Generation** — emits the `SuggestedCarc` / `SuggestedRarc` codes on the 835; reduces `AllowedAmount` based on bundling edits.
- **5.11 FHIR Projection.**
- **5.12 Adjustment Workflow.**
- **5.13 Adjudication API Stabilization** — Phase 1 closer.

After 5.7 ships:

- 4 of 7 adjudication stages now real (Scrubbing, NetworkCredentialing, BenefitCalculation, NcciEdits) — 3 stubs remain
- 6th instance of pipeline-stage DI replacement
- 6th instance of `IsRequired=true` semantics
- 5th engine class library wired into a service
- `TenantEnforcementPolicyOptions` extends to 3 modes (Network, Credentialing, Ncci)
- `Claim.PendDetails` populated for the first time in production flow
- Projection bypass extended from `AdjudicationResult` only to `AdjudicationResult + PendDetails`
