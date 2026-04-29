# Declarative Benefit Model (5.4)

> **Status:** Phase 1, capability 5.4. Establishes the discriminated-union
> shape and the predicate evaluator. Type-aware engine paths arrive in
> capability 5.7+ (preventive zero-cost-share, embedded vs non-embedded
> OOP) and 5.14 (formulary resolution).
>
> **See also:**
> [`service-category-mapping.md`](service-category-mapping.md) (BP 5.6) —
> resolves CPT/HCPCS to the `ServiceCategory` label used by the typed
> benefits below; documents the X12 ↔ free-text incoherence that
> currently links `Benefit.ServiceCategory` to the resolver's
> `ServiceTypeCode`.

## Why

The original `Benefit` class was a flat bag of fields covering every kind
of benefit a plan can carry — medical, dental, pharmacy, behavioral,
vision, DME, maternity, preventive. Type-specific facets (formulary tier,
ACA preventive grade, MHPAEA parity flag, orthodontic lifetime maximum)
either lived nowhere or were inferred from `ServiceCategory` substring
matches at the call site (see the pharmacy branch in
`Services/BenefitViewService.cs:102-117` before this change).

That shape made every new capability harder:

- 5.7 (embedded / non-embedded OOP) needs to know whether a benefit is
  preventive without re-parsing the service category string.
- 5.14 (formulary service) needs structured tier / specialty / step-therapy
  metadata, not "Tier 1 (Specialty)" as a category label.
- 5.17 (MHPAEA attestation) needs an explicit parity flag per benefit, not
  a `ServiceCategory.Contains("Mental Health")` heuristic.
- External adapters (QNXT, Facets, HealthEdge — capability 5.2 stubs)
  need to know which fields a vendor API maps to, and that's much easier
  with a typed model than a flat bag.

5.4 introduces a discriminated-union `Benefit` hierarchy so each of those
capabilities can extend the right subclass without touching unrelated code,
while preserving full backward compatibility with rows persisted before
the discriminator existed.

## Shape

The base `Benefit` class continues to carry every facet that applies to
every benefit (cost-sharing, prior-auth, visit limits, deductibles, OOP
behavior, annual / lifetime maximums, the new optional `Rules` predicate
list). Type-specific facets live on subclasses under
`Models/Benefits/`:

| Subclass                   | Adds                                                         |
| -------------------------- | ------------------------------------------------------------ |
| `MedicalBenefit`           | nothing — catch-all default                                  |
| `DentalBenefit`            | `IsOrthodontic`, `IsImplant`, `LifetimeBenefitMaximum`       |
| `PharmacyBenefit`          | `FormularyTier`, `IsSpecialtyDrug`, `RequiresStepTherapy`, `QuantityLimit`, `DaysSupply` |
| `BehavioralHealthBenefit`  | `IsParityProtected` (default `true`), `ParityCategory`       |
| `VisionBenefit`            | `IsRoutineExam`, `FrameAllowance`, `LensCoverageType`        |
| `DMEBenefit`               | `RequiresFitting`, `FittingPeriodDays`, `IsRental`, `MaxRentalMonths` |
| `MaternityBenefit`         | `CoversPrenatal`, `CoversDelivery`, `CoversPostpartum`, `CoversNICU` |
| `PreventiveBenefit`        | `IsAcaPreventive`, `UspstfRecommendationGrade`               |

Each subclass overrides a `BenefitType` virtual property whose value is
written to the wire as the `"benefitType"` discriminator. Discriminator
constants live in `BenefitTypeDiscriminators` so call sites never spell a
discriminator string by hand.

## Wire format and the catch-all-as-Medical hydration rule

The `Benefit` base class is decorated with
`[JsonConverter(typeof(BenefitJsonConverter))]`. The custom converter
peeks at the JSON object's `"benefitType"` property and dispatches:

- `"dental"`, `"pharmacy"`, `"behavioralHealth"`, `"vision"`, `"dme"`,
  `"maternity"`, `"preventive"` (case-insensitive) → matching subclass.
- `"medical"`, missing, empty, or any unrecognized value → `MedicalBenefit`.

The "missing or unknown ⇒ `MedicalBenefit`" rule is the backward
compatibility seam. Every benefit row that was persisted before 5.4 has
the legacy flat shape — no `benefitType` property at all. After this
change, those rows hydrate as `MedicalBenefit` with every common field
populated from the JSON. There is no migration; legacy data continues to
work indefinitely. Unknown discriminators (e.g. a future
`"telehealth"` value emitted by a payer ahead of our schema) also fall
back to `MedicalBenefit` so deserialization never throws on read.

On write, the converter delegates to `JsonSerializer` with the runtime
type, so each subclass emits its `BenefitType` override and its
type-specific facets. A round trip through `JsonSerializer.Serialize` /
`JsonSerializer.Deserialize` preserves the concrete type — verified by
`TypedBenefitSerializationTests` and `LegacyBenefitHydrationTests`.

### Serializer-config scope

This change relies on `System.Text.Json` polymorphism, which is the
serializer used by:

- ASP.NET Core's HTTP MVC pipeline (configured in `Program.cs` via
  `AddCloudHealthOfficeJsonOptions`).
- The in-memory test fake `InMemoryBenefitPlanRepository`, which clones
  every store / fetch with `JsonSerializerOptions(JsonSerializerDefaults.Web)`.

Real Cosmos (uses Newtonsoft.Json by default) and real Mongo (uses BSON,
not System.Text.Json) **do not** pick up `[JsonConverter]` attributes on
their own. The test scaffolding exercises the polymorphic round-trip via
the in-memory fake, which uses the same wire format the API surface and
the future System.Text.Json-on-Cosmos path will use; the real-infrastructure
serializer registration (`CosmosSystemTextJsonSerializer` and
`BsonClassMap.RegisterClassMap`) is a follow-up before the typed model
is exercised in production. Until that follow-up lands, real Cosmos /
Mongo continue to round-trip the **flat** shape — which then hydrates
through this converter as `MedicalBenefit`, the same way any pre-5.4 row
does. No data loss; the typed-facet write path simply isn't exercised in
prod yet.

## Predicate evaluation strategy

`BenefitRulePredicate` is the declarative gate that restricts when a
benefit applies to a member encounter. Facets:

- `MemberAgeMin` / `MemberAgeMax` — inclusive age range.
- `MemberGender` — `Female` / `Male` / `NonBinary` / `Any`. `Any` skips
  the check.
- `RequiredDiagnosisCodes` — list of ICD-10 codes; the encounter must
  carry at least one (case-insensitive OR semantics).
- `RequiresRelatedEncounter` + `RelatedEncounterLookbackDays` — gate the
  benefit on a qualifying earlier encounter, evaluated through a caller-
  supplied `Func<int, bool>` so the predicate stays in-process.

Evaluation rules:

- An unset facet is "no opinion" and never blocks the benefit.
- Every set facet must match for the predicate to evaluate `true`.
- A null `BenefitRuleEvaluationContext` evaluates `false` — predicates
  refuse to gate without information.
- A `RequiresRelatedEncounter` predicate without a supplied
  `HasRelatedEncounter` source fails closed: we'd rather decline a
  benefit than admit one we can't verify.

5.4 established the predicate type and its in-process evaluator;
**BP 5.10** wires it into the adjudication hot path through
`IBenefitRuleGate` so age-, gender-, and diagnosis-restricted
benefits are evaluated correctly. See
[`adjudication-api-stabilization.md`](adjudication-api-stabilization.md)
for the rule-gate placement, the null-`MemberContext` posture
(Decision 3), and the projection-shape change that lets a plan author
multiple benefits with the same `ServiceCategory` (Decision 1).

## Engine integration: Strategy A (deferred type-awareness)

`BenefitCalculationEngine` and the prior-auth rule engine continue to
read the base `Benefit` class verbatim. Every field they need —
`ServiceCategory`, `CptCodes`, `InNetworkCopay`, `CoinsurancePercentage`,
`DeductibleApplies`, `OopApplies`, `PriorAuthRequired`,
`RequiresPriorAuth`, `VisitLimit`, `AnnualMaximum`, `LifetimeMaximum` —
remains on the base class. The new typed facets are silently ignored by
the engine in this PR.

Type-aware engine paths arrive in subsequent capabilities:

- **5.7** — embedded vs non-embedded OOP rules. The engine becomes
  preventive-aware (`benefit is PreventiveBenefit { IsAcaPreventive: true }`
  + grade A/B ⇒ zero member liability).
- **5.14** — formulary service. The engine resolves
  `PharmacyBenefit.FormularyTier` against the formulary doc to land
  benefits on the correct tier.
- **5.17** — MHPAEA attestation. The engine cross-checks
  `BehavioralHealthBenefit.IsParityProtected` and `ParityCategory`
  against the medical/surgical analog when running parity analysis.

Strategy A keeps this PR additive and reversible: any regression on the
adjudication hot path is, by construction, a serialization issue
(verified by the round-trip tests), not a calculation issue.

## Adapter integration

`AdapterBenefit` mirrors the discriminated-union shape of `Benefit` —
one `Adapter…Benefit` subclass per concrete `Benefit` subclass. The DTO
hierarchy uses `[JsonPolymorphic]` + `[JsonDerivedType]` directly because
adapter response payloads are constructed only by `AdapterBenefit.From`
(the runtime-type-dispatching factory) and never by deserializing legacy
data — there is no flat-shape legacy at the adapter layer.

`AdapterBenefit.From(typed)` ⇒ `AdapterBenefit.ToBenefit()` round-trips
without field loss. External adapters (today CHO; 5.2 stubs for QNXT,
Facets, HealthEdge) populate the matching subclass; legacy / unknown
benefits map to `AdapterMedicalBenefit`.

## What this PR doesn't touch

- Versioning (5.1) — `BenefitPlan` identity, `VersionId`, `VersionState`,
  `PublishAndSupersedeAsync`. Verified by the round-trip tests under
  `Repositories/`.
- Adapter factory routing (5.2) — `BenefitPlanAdapterFactory`, tenant
  config cache, the QNXT/Facets/HealthEdge stubs. Stubs continue to
  throw `NotImplementedException`.
- Plan-year (5.3) — `PlanYearDefinition`, `PlanYearScheduler`,
  `PlanYearTransitionPublisher`. No coupling.
- `AdjudicationController` and the six adjudication endpoints — no
  contract change.
- `formulary-service` (5.14) and ACA preventive zero-cost-share (5.7 /
  5.16) — typed-shape work only.

## Testing

Five new test files cover the contract:

- `Models/Benefits/TypedBenefitSerializationTests.cs` — round-trip every
  typed benefit through `JsonSerializer`.
- `Models/Benefits/LegacyBenefitHydrationTests.cs` — legacy flat-shape
  JSON deserializes as `MedicalBenefit`; unknown discriminators fall back.
- `Models/Benefits/BenefitRulePredicateTests.cs` — predicate evaluation
  per facet.
- `Repositories/TypedBenefitRoundTripTests.cs` — typed benefits survive
  the publish / amend / publish v2 lifecycle through the in-memory fake.
- `Adapters/AdapterTypedBenefitRoundTripTests.cs` — `AdapterBenefit.From`
  + `ToBenefit` round-trip every typed shape.

Existing test suites (`BenefitPlanServiceVersionTests`,
`BenefitPlanRepositoryVersionChainTests`,
`PlanYearTransitionPublisherTests`, `ChoBenefitPlanAdapterTests`,
`BenefitViewServiceTests`, the `PriorAuthRuleEngine.Tests` suite) pass
unchanged.
