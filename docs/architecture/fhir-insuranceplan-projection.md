# FHIR InsurancePlan Projection (Capability BP 5.8)

> Projects CHO's `BenefitPlan` entity into a Plan-Net IG 1.1.0 +
> US Core 6.1.0 conformant `InsurancePlan` resource. Adds
> `/fhir/r4/InsurancePlan/*` to the public FHIR surface, completing
> the Plan-Net Provider Directory bundle (Practitioner +
> PractitionerRole + Organization + InsurancePlan + Coverage) for
> external consumers.

## Why

Provider Phase 1 (capabilities 5.7 / 5.8 / 5.9) shipped the FHIR
projections for the provider-side resources. CHO had no
`/fhir/r4/InsurancePlan/*` URL surface; consumers could read
Practitioner / PractitionerRole / Organization / Coverage but could
not read the insurance plan offering itself.

This capability adds it. `BenefitPlan` is the source entity in
benefit-plan-service; the projector is hand-built (no
`Hl7.Fhir.R4` dependency, mirroring the Provider 5.7 / 5.8 / 5.9
pattern) and the controller proxies through fhir-service so the
single FHIR façade convention is preserved.

## Architecture

```
external client
       │  GET /fhir/r4/InsurancePlan/{PlanId}
       ▼
┌──────────────────┐                  ┌─────────────────────────────┐
│ fhir-service     │   typed proxy    │ benefit-plan-service        │
│ InsurancePlan    │ ───────────────▶ │ FhirInsurancePlanController │
│ Controller       │   /fhir/Insurance│  ▶ FhirInsurancePlanProjector │
└──────────────────┘   Plan/{id}      └─────────────────────────────┘
                                                │
                                  IOrganizationLookupClient (BP 5.5)
                                  ▼
                                 provider-service
                                 (Organization name enrichment)
```

Two-hop pattern matches Provider 5.7 / 5.8 / 5.9 verbatim. The
proxy helper that translates upstream 5xx → 502 OperationOutcome
was extracted to `FhirControllerBase.ProxyUpstreamServiceAsync` in
this PR — both `ProviderDirectoryController` and the new
`InsurancePlanController` share one status-translation rule.

## Identifier shape

`InsurancePlan.id` carries `BenefitPlan.PlanId` — the operator-
authored human-meaningful identifier that consumers see on member ID
cards and SBC documents. Examples: `AUR-GOLD-PPO-2026`,
`HELIX-SILVER-EPO-2026`. Tenant scoping disambiguates the rare case
where two tenants happen to use the same value.

`InsurancePlan.identifier[0]` is `{ system: PlanIdSystem, value: PlanId }`
under the CHO canonical system
`http://fhir.cloudhealthoffice.com/plan-id`.

`meta.versionId` is left absent; FHIR-canonical resource versioning is
a separate concern from the BP 5.1 plan-version chain. A future
capability may surface `meta.versionId = BenefitPlan.VersionId` when
external consumers ask for it.

## Field-set classification

### In scope (BP 5.8)

| FHIR element | Source | Notes |
|---|---|---|
| `identifier` | `BenefitPlan.PlanId` | CHO canonical system |
| `status` | derived | `active` when in effective window; `retired` when terminated |
| `type` | `BenefitPlan.PlanType` | Two codings: HL7 `medical` + CHO product shape (HMO/PPO/etc.) |
| `name` | `BenefitPlan.PlanName` | |
| `period` | `EffectiveDate` / `TerminationDate` | |
| `ownedBy` | `BenefitPlan.Payer` | display-only Reference (Decision 12) |
| `network[]` | `NetworkTiers[].NetworkId` | All non-null tiers, ordered by `TierLevel` (Decision 10) |
| `coverage[].type` | `Benefit.BenefitType` | text-only (BP 5.6 incoherence) |
| `coverage[].network` | top-level `network[]` repeated | |
| `coverage[].benefit[]` | `BenefitPlan.Benefits` grouped by type | |
| `coverage[].benefit[].type` | `Benefit.ServiceCategory` | text-only (BP 5.6 incoherence) |
| `coverage[].benefit[].requirement` | `PriorAuthRequired` + `Limitations` | combined string |
| `coverage[].benefit[].limit[]` | `VisitLimit`, `AnnualMaximum`, `LifetimeMaximum` | |
| `plan[]` | one per `NetworkTier` with non-null `NetworkId` | |
| `plan[].identifier` | `TierName` under CHO `network-tier` system | |
| `plan[].type` | text-only `Tier {TierLevel}` | |
| `plan[].network` | tier-specific Organization reference | |
| `plan[].generalCost[]` | `CostSharing` deductible + OOP max + (Aggregate + post-cutoff) ACA cap | |
| `plan[].specificCost[]` | per-Benefit copay + coinsurance per network tier | |

### Shipped in BP 5.9 (Plan Documents)

| FHIR element | Where it landed |
|---|---|
| `endpoint[]` | Populated by BP 5.9 — one `Reference(Endpoint/{id})` per projectable `PlanDocumentReference`, ordered by `(DocType, EffectiveDate desc, Id)` per Decision 8. Endpoint resources themselves are dereferenceable at `/fhir/r4/Endpoint/{id}`. See [`fhir-endpoint-projection.md`](fhir-endpoint-projection.md). |

### Deferred to Phase 2 / future capabilities

| FHIR element | Reason |
|---|---|
| `alias[]` | `BenefitPlan` has no DBA / alternative-name field today. |
| `contact[]` | `BenefitPlan` has no contact info field. |
| `coverageArea[]` | `BenefitPlan` has no geographic coverage area field. |
| `administeredBy` | `BenefitPlan` has no TPA reference field. |
| `description` | `BenefitPlan` has no plan-level description (only per-Benefit). |
| `plan[].coverageArea` | Same as above. |
| `ownedBy` Reference | `Payer` is a free-text string; a future "Payer Organization Linking" capability migrates this slot from `display`-only to a real `Reference`. |

## Cost-sharing emission

### `plan[].generalCost[]`

One entry per network tier × (Individual / Family) × (Deductible / OOP-max).
On the primary in-network tier, an additional ACA-cap entry projects the
per-member 45 CFR §156.130 cap **when the plan is Aggregate-mode AND
`AcaCapEnforcementPolicy.IsEnforced` returns true**.

The ACA-cap entry carries a CHO sub-extension
`insuranceplan-aca-cap-enforced=true` so standard Plan-Net consumers see
the cap as a real cost limit while CHO-aware consumers can disambiguate
it from a real plan-level individual cap (Decision 11 dual emission).

### `plan[].specificCost[]`

One `category` entry per benefit type group (medical / pharmacy / dental
/ vision / behavioralHealth / dme / maternity / preventive). Each
benefit emits up to 2 `cost` entries per network tier (copay,
coinsurance) with 2-element `qualifiers` arrays:

```json
"qualifiers": [
  { "coding": [{ "system": "http://fhir.cloudhealthoffice.com/CodeSystem/insuranceplan-cost-qualifier", "code": "in-network" }] },
  { "coding": [{ "system": "http://fhir.cloudhealthoffice.com/CodeSystem/insuranceplan-cost-qualifier", "code": "copay" }] }
]
```

If conformance testing rejects the 2-element-array shape, the projector
falls back to one cost entry per single qualifier (4 → 8 cost entries
per benefit). Currently 2-element per the Plan-Net IG examples we
checked during plan phase.

## CHO custom extensions

Two top-level extensions on `InsurancePlan`. Naming follows the
empirical Provider 5.7 / 5.8 / 5.9 convention
`{resource-lowercase}-{slug}` (no `cho-` prefix; that prefix is
appeals-domain-specific):

| URL | Source | Type |
|---|---|---|
| `http://fhir.cloudhealthoffice.com/StructureDefinition/insuranceplan-family-accumulator-model` | `BenefitPlan.FamilyAccumulatorModel` (BP 5.7) | `valueCode = Embedded \| Aggregate` |
| `http://fhir.cloudhealthoffice.com/StructureDefinition/insuranceplan-aca-cap-enforced` | `AcaCapEnforcementPolicy.IsEnforced(plan)` | `valueBoolean = true` (only emitted when true) |

`AcaCapEnforcementPolicy` is the single source of truth for the
"is the cap enforced" decision — both this projector AND
`ChoBenefitPlanProvider.ResolveIsAcaCapEnforced` (engine-config
projection) call into it. Cutoff is pinned at
`2026-04-28 00:00:00 UTC` per the BP 5.7 G8 rollout.

## End-to-end Plan-Net navigation

After this PR ships, the chain
`Practitioner → PractitionerRole.organization → Organization →
InsurancePlan.network → Organization` resolves end-to-end through the
fhir-service public surface. `InsurancePlan.network[]` references emit
as `Organization/{networkId}`; consumers dereference via
`/fhir/r4/Organization/{networkId}` which fhir-service proxies to
provider-service per Provider 5.9.

## Known gaps

### Coverage.class slice → InsurancePlan reference (deferred)

Plan-Net IG 1.1.0 joins `Coverage` to `InsurancePlan` via
`Coverage.class[type=plan].value = PlanId`. fhir-service's
`CoverageController` (mock-adapter-backed today) does **not** emit a
`class` slice. After this PR, `InsurancePlan` resources are
dereferenceable directly via `/fhir/r4/InsurancePlan/{PlanId}` and via
the `PractitionerRole → Organization → InsurancePlan.network`
traversal, but **NOT** via `Coverage.class`.

A future capability ("Coverage.class slice for InsurancePlan
reference") updates the Coverage projector to emit:

```json
"class": [{
  "type": { "coding": [{
    "system": "http://terminology.hl7.org/CodeSystem/coverage-class",
    "code": "plan"
  }] },
  "value": "{PlanId}"
}]
```

This PR does NOT modify `CoverageController`; the gap is documented
and tracked.

### `InsurancePlan.type` standard-coding discrimination

For Phase 1 every projected plan emits `medical` as the standard
coding. Authoring a dental-only / vision-only / drug-only plan today
would project an inaccurate standard coding. A future capability
introduces `BenefitPlan.CoverageScope` (or equivalent) and the
projector switches on it. The CHO product-shape coding (HMO / PPO /
HDHP / etc.) is always accurate because plan authors set it directly.

## Testing

| Test file | What it pins |
|---|---|
| `BenefitPlanService.Tests/Services/AcaCapEnforcementPolicyTests.cs` | Policy delegation + cutoff pinning + parity with BP 5.7 inline rule |
| `BenefitPlanService.Tests/Services/FhirInsurancePlanProjectorTests.cs` | US Core / Plan-Net structure, network projection, coverage grouping, cost-sharing, ACA-cap emission, deterministic output |
| `BenefitPlanService.Tests/Controllers/FhirInsurancePlanControllerTests.cs` | Read 200/400/404, search bundle shape, identifier token parsing, name + status filtering, tenant scoping |
| `tests/CloudHealthOffice.FhirService.Tests/Controllers/InsurancePlanControllerProxyTests.cs` | URL forwarding, query-string passthrough, 4xx verbatim, 5xx → 502 OperationOutcome without body leak, transport faults |

## References

- [Plan-Net IG 1.1.0 InsurancePlan profile](http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-InsurancePlan)
- [US Core 6.1.0 InsurancePlan profile](http://hl7.org/fhir/us/core/StructureDefinition/us-core-insuranceplan)
- [`family-accumulator-models.md`](./family-accumulator-models.md) — BP 5.7 (FamilyAccumulatorModel + ACA cap rollout)
- [`network-tier-organization-reference.md`](./network-tier-organization-reference.md) — BP 5.5 (NetworkTier as Organization Reference)
- [`fhir-organization-projection.md`](./fhir-organization-projection.md) — Provider 5.9 (Organization projection that this PR's `network[]` references resolve to)
- [`fhir-conformance.md`](./fhir-conformance.md) — running ledger of CHO FHIR conformance posture
