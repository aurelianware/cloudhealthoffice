# Adjudication API Stabilization (Capability BP 5.10)

**Status:** Implemented in BP 5.10. Closes Benefit Plan Phase 1.

This capability wires two existing-but-deferred seams into the
adjudication hot path so the engine actually consumes the model the
plan author wrote down:

1. **Effective-date filtering** on `ServiceCategoryResolver`
   (BP 5.6 captured `EffectiveStart` / `EffectiveEnd` / `IsActive` —
   nothing read them).
2. **`BenefitRulePredicate` evaluation** during benefit lookup
   (BP 5.4 shipped the model + evaluator + unit tests — nothing read
   them at adjudication time).

The X12-alias deferral (operator text labels vs X12 5010 codes) stays
as a Phase 2 capability with its posture recorded explicitly here so
the next person to read the code knows why the surface is shaped the
way it is.

## Effective-date filtering on ServiceCategoryResolver

`IServiceCategoryResolver.ResolveAsync` gains a non-optional
`DateOnly serviceDate` parameter, positioned right after
`benefitPlanId`. The breaking signature change is mechanical at the
call sites — both engine callers (`BenefitCalculationEngine`'s DRG
path and per-line path) already have `request.ServiceDate` in scope.

`ServiceCategoryResolver.FindMatch` filters mappings before iterating
rules:

```text
mapping is in effect for serviceDate iff
  mapping.IsActive == true
  AND (mapping.EffectiveStart is null OR mapping.EffectiveStart <= serviceDate)
  AND (mapping.EffectiveEnd   is null OR mapping.EffectiveEnd   >= serviceDate)
```

Both bounds are **inclusive**. `null` bound = open. `IsActive == false`
filters the row regardless of date — operators use `IsActive` as an
emergency kill switch independent of the date window.

**Worked example.** A mapping authored with
`EffectiveStart = 2026-01-01, EffectiveEnd = 2026-12-31` does **not**
match a 2025-12-15 service date even when it's the only mapping the
tenant has authored — the resolver falls through to tenant-default
mappings, then to the POS-based inference fallback.

**Service date semantics.** "Service date" is the **claim line's**
service date, not the adjudication date. A claim adjudicated in 2027
for service performed on 2026-08-15 hits 2026 mappings. This matters
for retroactive adjudication and for delayed claims processing.

**Filtering is in-memory.** The repo seam (`GetMappingsAsync`) is
unchanged — filtering runs over the cached mapping list. No backend
round-trip per service date.

**Producer-boundary validation.** The
`ServiceCategoryMappingsController` rejects requests where
`EffectiveEnd < EffectiveStart` with a 400 (`effective_window_invalid`,
field-level message `"effectiveEnd must be on or after
effectiveStart"`). Backfill of historical malformed rows is
operator-driven; the validation gate prevents new bad rows from being
authored.

**Telemetry.** Counter
`cho.benefit_plan.scm_filtered_by_effective_window.total` (dimensions:
`cho.tenant_id`, `cho.scope = plan|tenant`) increments per call when
filtering removes ≥1 row from a non-empty mapping set. Gives operators
a signal that effective-date authoring is doing real work.

## BenefitRulePredicate evaluation in adjudication

`BenefitRulePredicate` (model + evaluator) was introduced in BP 5.4
and lived as dead state on the wire until BP 5.10 wired it into the
engine.

### Request-shape extension

A new `MemberContext` record on `BenefitResolutionRequest` carries
member demographics + diagnosis context fed to predicate evaluation:

```csharp
public record MemberContext
{
    public int? AgeYears { get; init; }
    public BenefitMemberGender? Gender { get; init; }
    public IReadOnlyCollection<string>? DiagnosisCodes { get; init; }
}

public MemberContext? Member { get; init; }
```

`Member` is **optional**. When the caller doesn't supply demographic
context, the engine falls back to the per-line `DiagnosisCodes` for
the diagnosis facet (so a single-dx claim still gates correctly even
when the controller hasn't plumbed member-level dx into
`MemberContext`).

### Evaluation point

After `ServiceCategoryResolver` returns a non-null
`ServiceCategoryMatch`, the engine calls into `IBenefitRuleGate` to
pick the applicable benefit:

1. Look up every `BenefitCategoryConfig` whose `ServiceTypeCode`
   matches via `BenefitPlanConfig.GetCategories(code)`.
2. Iterate in declaration order (matches `BenefitPlan.Benefits`
   order at the projection seam).
3. Return the first benefit whose
   `BenefitCategoryConfig.Predicate` is satisfied by the member
   encounter, or `null` if every candidate's predicate rejects.

### Worked example — pediatric vs adult office visit

A plan authors two benefits with `ServiceCategory = "98"`:

| Benefit                       | Predicate                  |
|-------------------------------|----------------------------|
| Pediatric Office Visit        | `MemberAgeMin = 0, MemberAgeMax = 17` |
| Adult Office Visit            | `MemberAgeMin = 18`        |

Both project to one engine config with two `BenefitCategoryConfig`
entries sharing `ServiceTypeCode = "98"`. The rule gate picks the
right one per encounter; the rest of the cost-sharing waterfall runs
against the gate's choice.

### Decisions

#### Decision 1 — multiple benefits with the same ServiceCategory

The pre-BP-5.10 projection (`ChoBenefitPlanProvider.MapToConfig`)
projected every `Benefit` to its own `BenefitCategoryConfig` but the
lookup helper `GetCategory` used `FirstOrDefault` and effectively
deduplicated by service type code. The second benefit was unreachable.

BP 5.10 splits the lookup:

- `GetFirstCategory(code)` — legacy any-match. Kept for callers that
  don't need predicate evaluation (limit checks, audit lookups).
- `GetCategories(code)` — full ordered set. Used by the rule gate.
- `GetCategory(code)` — `[Obsolete]` shim that delegates to
  `GetFirstCategory`. Removed in a follow-up PR after the deprecation
  cycle.

The first-match result is unchanged when no `Benefit` carries
predicates, so existing pinned tests (DRG path, line-level path) keep
passing without rewriting.

#### Decision 2 — effective-date semantics

`EffectiveStart <= serviceDate <= EffectiveEnd` AND
`IsActive == true`. Both bounds inclusive. `null` bound = open.
`IsActive == false` filters regardless of window. Service date is the
claim line's service date, not the adjudication date.

#### Decision 3 — null-MemberContext posture

The default `BenefitRulePredicate.Evaluate(null context) => false`
is the right posture *for callers that own the context*. The engine
hot path doesn't always own it (today, never; tomorrow, usually). Two
postures available:

- **A — strict:** always require `MemberContext`. The engine fails
  any request without one. Forces the controller to source DOB /
  gender / diagnoses on every call.
- **B — best-effort:** when `MemberContext` is null, skip predicate
  evaluation entirely. Every benefit is considered applicable.
  Predicate-rejected denials only happen when the caller chose to
  supply context.

**Decision: B (best-effort).** Phase 1 plans authored today don't use
`Rules`. The Argo workflow doesn't supply demographics today. Going
strict would fail every adjudication on day one. Going best-effort
lets the feature roll out per-tenant as plan authors start using
`Rules` and as the controller starts plumbing `MemberContext` from
coverage fetches.

When `MemberContext` is non-null, predicate evaluation is **strict**
— context-required facets that the context can't satisfy fail closed
(unchanged from the existing `BenefitRulePredicate.Evaluate`
semantics).

Counter
`cho.benefit_plan.predicate_skipped_no_member_context.total`
(dimensions: `cho.tenant_id`, `cho.service_type_code`) fires when the
gate encounters a benefit with a non-null predicate AND null
`MemberContext`. Operators see "this plan has rules but my caller
isn't supplying context" without reading engine logs.

#### Decision 4 — Benefit.Rules as List<BenefitRulePredicate>?

The model is a list. BP 5.10 projects only the first non-null
predicate. Multi-predicate AND semantics is Phase 2 — it warrants its
own design pass (rare in authored plans today; want operator-visible
signal first).

Counter `cho.benefit_plan.predicate_multi_rule_truncated.total`
(dimension: `cho.tenant_id`) fires when projection sees a
`Benefit.Rules.Count > 1`, plus a structured warning log naming the
plan + benefit so operators see if multi-predicate rules are being
authored in the wild.

#### Decision 5 — resolver signature breaking change

`IServiceCategoryResolver.ResolveAsync` gains `DateOnly serviceDate`
as a non-optional parameter. Breaking. The two engine callers
(`BenefitCalculationEngine`) already have `request.ServiceDate` in
scope; the change is mechanical at the call sites and the test
fixtures. The interface is internal-to-CHO and not consumed by any
external artifact (no client SDK, no FHIR contract). The breaking
change costs nothing externally.

#### Decision 6 — denial code for predicate-rejected benefit

`96` (Non-covered charge[s]). Reusing the existing code is correct:
from the X12 835 perspective, the service is genuinely not covered
for this member-and-encounter combination. The narrative description
distinguishes it from a missing-mapping `96`:

| Path | Code | Description |
|------|------|-------------|
| Mapping resolved, category matched, predicate rejected | `96` | `"Benefit category {ServiceTypeCode} matched but no rule predicate is satisfied for this member encounter"` |
| Mapping resolved, category not configured | `96` | `"No benefit configured for service type {ServiceTypeCode}"` |
| Mapping resolved, category not covered | `96` | `"{description} is not covered under this plan"` |
| Mapping not resolved | `18` | `"No benefit category mapping for procedure code"` |

The narratives are operator-facing — they're also what surfaces in
the explanation-of-benefits tooling.

## Phase 2 deferrals

Recorded explicitly so the next person reading the code knows the
shape of the surface and why these aren't BP 5.10:

| Item | Rationale |
|------|-----------|
| `ServiceTypeCodeAlias` table for X12 5010 ↔ free-text translation | Real translation surface that needs its own bundle authoring story, alias-precedence rules, and a real-world data audit. The SCM seed bundle ships operator-friendly text labels and works today; the alias is an enhancement, not a closer. |
| Multi-predicate `Benefit.Rules` AND semantics | The model carries `List<BenefitRulePredicate>?` but BP 5.10 projects only the first. Most authored plans use one predicate per benefit; multi-predicate is rare and warrants its own design pass. |
| Member DOB → `AgeYears` plumbing in `AdjudicationController` | The controller wires up `MemberContext` on the request when caller-supplied; AgeYears computation from a Coverage-fetched DOB at the controller seam is a follow-up. |
| `UpdatedBy` / `UpdatedAt` audit fields on `ServiceCategoryMapping` | Service-wide audit-pattern initiative tracked separately. |
| Telemetry-driven hard-validation flip on legacy `EffectiveEnd < EffectiveStart` | Producer-boundary 400 ships in BP 5.10. Backfill of any historical malformed rows is operator-driven. |
| `BenefitRuleEvaluationContext.HasRelatedEncounter` wiring at the controller | Encounter-history lookup belongs in claims-service / encounter-service. The model + evaluator path supports it; nothing in BP 5.10 supplies it. Predicates that require it fail closed when the supplier function is null — same posture as the existing evaluator. |

## What this closes

- **Benefit Plan Phase 1** (capabilities BP 5.1 → BP 5.10) ships.
- The two BP-internal "deferred to BP 5.10" markers in
  `service-category-mapping.md` and `declarative-benefit-model.md`
  flip to shipped.
- The engine adjudicates against the model the plan author actually
  wrote down — age- and gender-restricted benefits are honoured;
  time-bounded mappings are honoured; the "silent no-op" hazard on
  authoring is removed.

## Benefit Plan Phase 1 — capability ledger

| Capability | Title                                                  | Status | Reference doc                                           |
|------------|--------------------------------------------------------|--------|---------------------------------------------------------|
| BP 5.1     | Plan Identity & Versioning                             | Shipped | `plan-versioning.md`                                    |
| BP 5.2     | Benefit Plan Adapter Pattern                           | Shipped | `benefit-plan-adapter-pattern.md`                       |
| BP 5.3     | Plan-Year Definition Foundation                        | Shipped | `plan-year-definition.md`                               |
| BP 5.4     | Declarative Benefit Model + BenefitRulePredicate model | Shipped | `declarative-benefit-model.md`                          |
| BP 5.5     | NetworkTier ↔ Organization reference                   | Shipped | `network-tier-organization-reference.md`                |
| BP 5.6     | Service Category Mapping                               | Shipped | `service-category-mapping.md`                           |
| BP 5.7     | Family Accumulator Models + ACA cap                    | Shipped | `family-accumulator-models.md`                          |
| BP 5.8     | FHIR InsurancePlan projection                          | Shipped | `fhir-insuranceplan-projection.md`                      |
| BP 5.9     | Benefits Viewer                                        | Shipped | `benefits-viewer.md`                                    |
| BP 5.10    | Adjudication API Stabilization (Phase 1 closer)        | **Shipped (this doc)** | `adjudication-api-stabilization.md`         |

Phase 2 picks up: X12 alias table, multi-predicate AND semantics,
member-DOB plumbing into the adjudication request, service-wide audit
fields on `ServiceCategoryMapping`, and encounter-history wiring for
related-encounter predicates.

## File map

| File | Role |
|------|------|
| `src/engines/CloudHealthOffice.BenefitEngine/Domain/BenefitRulePredicate.cs` | Predicate model + evaluator + context, moved into the engine domain so the engine can reference it without a circular dependency on benefit-plan-service. |
| `src/engines/CloudHealthOffice.BenefitEngine/Services/ServiceCategoryResolver.cs` | Effective-date filtering + `serviceDate` parameter on `ResolveAsync`. |
| `src/engines/CloudHealthOffice.BenefitEngine/Services/BenefitRuleGate.cs` | New `IBenefitRuleGate` + default implementation; the predicate-aware benefit selector. |
| `src/engines/CloudHealthOffice.BenefitEngine/Services/BenefitCalculationEngine.cs` | Routes through the rule gate after category resolution; threads `request.ServiceDate` into the resolver. |
| `src/engines/CloudHealthOffice.BenefitEngine/Services/Providers.cs` | `BenefitCategoryConfig.Predicate` field, `GetFirstCategory` / `GetCategories` lookup pair. |
| `src/engines/CloudHealthOffice.BenefitEngine/Services/BenefitEngineMetrics.cs` | Engine-side counters for the SCM-window-filter and skipped-no-context paths. |
| `src/engines/CloudHealthOffice.BenefitEngine/Models/BenefitModels.cs` | `MemberContext` record + `BenefitResolutionRequest.Member` field. |
| `src/services/benefit-plan-service/Services/ChoBenefitPlanProvider.cs` | Projects `Benefit.Rules[0]` onto `BenefitCategoryConfig.Predicate`; emits the multi-rule truncated counter. |
| `src/services/benefit-plan-service/Controllers/ServiceCategoryMappingsController.cs` | 400 on `EffectiveEnd < EffectiveStart`. |
