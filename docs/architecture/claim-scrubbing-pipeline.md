# Pre-Adjudication Claim Scrubbing (Capability 5.4)

> **Status — shipped, April 2026.** Replaces `ScrubbingStubStage` with
> a real `ClaimsScrubEngine`-backed `ScrubbingStage` at Order=100 in the
> adjudication pipeline. Decommissions the parallel
> `claims-scrubbing-service` microservice shell.

## Why this exists

Before 5.4, every claim that reached the adjudication pipeline ran the
`ScrubbingStubStage` — a no-op `Pass`-returning placeholder. The 5.5
foundation guaranteed the pipeline ran end-to-end but did no structural
validation: a claim missing a billing NPI, missing a subscriber DOB, or
carrying a malformed CPT code was happily handed to
`BenefitCalculationStage`, which then produced unreliable outputs from
incomplete inputs.

The `CloudHealthOffice.ClaimsScrubEngine` class library has been
sitting in `src/engines/` since the platform's early days, well-tested
and ready to wire. 5.4 is the wiring PR.

A separate `claims-scrubbing-service` microservice shell was built at
the same time as the engine but was never integrated — no production
caller ever made an HTTP request into it. Pass 2 §5.4 ratified that
scrubbing belongs inside the synchronous adjudication pipeline (no HTTP
roundtrip), so the parallel service is decommissioned in this PR.

## Pipeline shape

```
ClaimAdjudicationOrchestrator (capability 5.5)
       │
       └─ iterate stages by Order ascending:
              100  ScrubbingStage             ★ real (5.4)   ← this doc
              200  NetworkCredentialingStage  ★ real (5.6)
              300  BenefitCalculationStage    ★ real (5.5)
              400  NcciEditsStubStage         (5.7 replaces)
              500  CoordinationOfBenefitsStubStage (5.8 replaces)
              600  AiExaminationStubStage     (5.9 replaces)
              999  PersistenceStage           ★ real (5.5)
```

## Stage shape

`ScrubbingStage` (`src/services/claims-service/Services/Adjudication/Stages/ScrubbingStage.cs`):

| Property | Value |
|---|---|
| `Name` | `"Scrubbing"` |
| `Order` | `100` |
| `IsRequired` | `true` |
| Constructor deps | `IClaimRoutingService` (engine), `ILogger<ScrubbingStage>` |

`IsRequired = true` (Decision 4) because a structurally invalid claim
corrupts every downstream stage. The orchestrator's
[`IsEnabled`](../../src/services/claims-service/Services/Adjudication/ClaimAdjudicationOrchestrator.cs)
check treats `IsRequired=true` as non-overridable, so per-tenant
disablement of scrubbing via `AdjudicationPipelineOptions.EnabledStages`
is intentionally rejected.

## Mapping layer

The engine consumes `X12837Claim` (`src/engines/CloudHealthOffice.ClaimsScrubEngine/Models/ScrubModels.cs`),
which is shaped to match an X12 837 EDI transaction. The pipeline
operates on `AdapterClaim`. `ClaimToX12837Mapper`
(`src/services/claims-service/Services/Adjudication/Mapping/ClaimToX12837Mapper.cs`)
bridges the two (Decision 5 — mapping lives consumer-side; the engine
stays domain-agnostic so it can also serve the future state-Medicaid
EDI ingest path).

The mapper populates only the fields the **default rule set** actually
inspects (Decision 10). Fields the rule set doesn't read (X12 envelope,
provider address, raw EDI) get well-formed sentinel values. Audited
load-bearing fields:

| X12837Claim field | AdapterClaim source | Engine rule(s) |
|---|---|---|
| `Subscriber.MemberId` | `SubscriberId ?? MemberId` | DC001 |
| `Subscriber.DateOfBirth` | `ResolvedMember.DateOfBirth` (yyyyMMdd) | DC002 |
| `BillingProvider.Npi` | `BillingProviderNPI` | DC003, PV001 |
| `ClaimHeader.DiagnosisCodes` | `DiagnosisCodes` | DC004, CV001 |
| `ServiceLines[].ProcedureCode` | `ClaimLines[].ProcedureCode` | DC005, CV002, CV003 |
| `ServiceLines[].ServiceDate` | `ClaimLines[].ServiceDateFrom` (yyyyMMdd) | DC006, DL001-004 |
| `ServiceLines[].ChargeAmount` | `ClaimLines[].ChargeAmount` | AL001 |
| `ServiceLines[].Units` | `ClaimLines[].Units` | AL003 |
| `TotalClaimedAmount` | `TotalChargeAmount` | AL002 (Warning) |

### Subscriber DOB sourcing

`AdapterClaim` does not carry a subscriber date of birth, but
`ResolvedMember.DateOfBirth` does (resolved by the orchestrator before
stages run). The mapper takes the context's resolved-member as a second
argument and pulls DOB from there. When member resolution fails (null
`ResolvedMember`), the mapper passes an empty string and engine rule
DC002 (Subscriber DOB Required, Error) honestly rejects the claim
rather than silently skipping a load-bearing rule.

### `ClaimType` enum bridge

Platform `ClaimType` is 1-based (`Professional=1`, `Institutional=2`,
`Dental=3`); engine `ClaimType` is 0-based (`Professional=0`, ...). The
mapper switches by name — never raw-casts — so the values never silently
shift.

## Routing decision

The engine returns a `ClaimsScrubResponse` carrying counts
(`ErrorCount`, `WarningCount`, `InfoCount`) and a string-typed
`Routing.Destination` (`"adjudication"`, `"work-queue"`, or
`"reject"`). 5.4 drives stage outcome off the **counts**, not the
destination string (Decision 7):

| Engine output | Stage outcome | Pipeline behavior |
|---|---|---|
| `ErrorCount > 0` | `Reject` (`Continue=false`) | Short-circuits to PersistenceStage; downstream stages skipped. Claim is rejected back to submitter via `ClaimAcknowledgmentService.Generate277CA`. |
| `ErrorCount == 0`, `WarningCount > 0` | `Pass` with warnings on `context.ScrubbingResult` | Pipeline continues; warnings persist on the audit trail. |
| `ErrorCount == 0`, `WarningCount == 0` | `Pass` clean | Normal flow. |
| Engine throws | `Reject` with `ENGINE_EXCEPTION` violation | Caught in stage (Decision 12) so the audit trail captures structured error rather than the orchestrator's generic safety-net Reject. |

Engine rule severity classification: `Error`, `Warning`, `Info`. Default
rules emit only Error and Warning today; Info is bucketed into Warnings
for forward-compat (no contract-breaking surprise if a future rule emits
Info severity).

## Outcome on context

`context.ScrubbingResult` (`Models/Adjudication/ScrubbingOutcome.cs`)
carries:

```csharp
public sealed class ScrubbingOutcome
{
    public ScrubbingDecision Decision;            // Approve | RejectStructural
    public IReadOnlyList<RuleViolation> Errors;
    public IReadOnlyList<RuleViolation> Warnings;
    public string? RoutingNote;                   // Engine routing reason
    public int RulesExecuted;
    public string EngineStatus;                   // "clean" | "flagged" | "rejected"
}

public sealed record RuleViolation(
    string RuleId, string RuleName, string Message,
    string? Field, string? EditCode, IReadOnlyList<int>? ServiceLines);
```

The `EditCode` field carries the engine's X12 277CA edit code so that
277CA generation can produce structurally faithful rejections.

`ScrubbingDecision` has only two values today (`Approve` /
`RejectStructural`). `PendForReview` is intentionally absent — no
default rule produces a pend semantic, so the value would be dead. Add
the value when a rule that produces it ships.

## Persistence (deferred)

PersistenceStage (5.5) currently projects only `AdjudicationResult` and
`LineAdjudicationResults` to the persisted claim version. It does not
yet persist `ScrubbingResult` (or 5.6's `EnforcementOutcomes`) — the
audit-trail-on-version backfill is a focused follow-up PR that will
land both at once. In the meantime the outcomes flow through stage
results (Reject reason on the emitted Service Bus
`ClaimVersionAdjudicatedMessage`).

## DI wiring

Two changes in `Program.cs`:

```csharp
// 5.4 — Claims Scrub Engine (class library). Default standard rule set.
builder.Services.AddClaimsScrubEngine();

// Stage registration — replaces ScrubbingStubStage in place.
builder.Services.AddScoped<IClaimAdjudicationStage, ScrubbingStage>();
```

The engine's `AddClaimsScrubEngine()` extension registers
`StandardRuleSet` as Singleton and `IValidationRuleEngine` +
`IClaimRoutingService` as Scoped. `ScrubbingStage` is Scoped (created
once per orchestrator run); the mapper is a static type with no state.

## Decommission of `claims-scrubbing-service`

The parallel `src/services/claims-scrubbing-service/` shell is removed
in this PR. No production code path called it, and its
`ValidationRuleEngine` was a stale parallel copy of the engine class
library's. Consolidating eliminates drift risk and one fewer service to
deploy / monitor.

Removed:

- `src/services/claims-scrubbing-service/` (entire tree)
- `tests/CloudHealthOffice.ClaimsScrubbingService.Tests/` (entire tree)
- Solution-file entry for the test project
- GitHub Actions matrix entries (`deploy-azure-aks.yml`,
  `pr-validation.yml`, `security-scan.yml`)
- `.github/dependabot.yml` npm stanza + ignore-list entry
- `.github/paths-filter.yml` entry
- `docker-compose.yml` and `docker-compose.development.yml` service
  definitions
- README and 10 documentation references
- Comment-only reference in the claims-examiner-service ConfigMap

The named-similar Argo workflow
`infrastructure/argo-workflows/x12-837-claims-scrubbing.yaml` is a
distinct Node.js validator pipeline — it does not call the .NET
service and is not affected.

## Cross-references

- [`claim-adjudication-pipeline.md`](./claim-adjudication-pipeline.md) —
  full pipeline architecture (capability 5.5)
- [`claim-adapter-pattern.md`](./claim-adapter-pattern.md) — the
  AdapterClaim shape the mapper consumes
- [`claim-versioning.md`](./claim-versioning.md) — version chain that
  records the rejection / pass outcome
