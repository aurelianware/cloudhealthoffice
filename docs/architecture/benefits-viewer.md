# Benefits Viewer

The Benefits Viewer surfaces a member-facing, categorized rendering of a
benefit plan inside the **Member Details** dialog in CloudHealthOffice.Portal.
It answers, at a glance, "what is this member covered for today?"

## Endpoint

```
GET /api/v1/benefit-plans/{planId}/member-view?serviceDate=YYYY-MM-DD
```

Hosted by `benefit-plan-service` (`BenefitPlanMemberViewController`).
`serviceDate` is optional and defaults to today (UTC).

Response body is `MemberBenefitView` — see
`src/services/benefit-plan-service/Models/MemberBenefitView.cs`. Key fields:

| Field           | Meaning                                                   |
|-----------------|-----------------------------------------------------------|
| `planId`        | Plan identifier echoed from the request                   |
| `planVersion`   | Compact version stamp (plan UpdatedAt / ModifiedDate)     |
| `asOfDate`      | `serviceDate` used to resolve the view                    |
| `costSharing`   | Plan-level deductibles and OOP maxes                      |
| `categories[]`  | One entry per benefit, bucketed by canonical category key |
| `documents[]`   | SBC / EOC / Formulary / etc.                              |

> **Route note.** The existing `/api/v1/plans` route remains for backwards
> compatibility. The member-view endpoint lives under the hyphenated
> `/api/v1/benefit-plans` root. Consolidating the two is tracked by
> `TODO(deprecate-plans-route)` in `BenefitPlansController.cs`.

## Category mapping

Raw `Benefit.ServiceCategory` strings are translated to canonical category
keys by `Services/BenefitCategoryMap.cs`. Keys are stable identifiers the
portal owns display labels for. Current buckets:

- `PrimaryCare`, `Specialist`, `EmergencyRoom`, `UrgentCare`
- `Hospital`
- `Pharmacy` (with a `PharmacyDetail` sub-object carrying the raw tier label)
- `DurableMedicalEquipment`
- `MentalHealth`, `Maternity`, `Preventive`
- `Other` (fall-through)

When a raw value does not map, the service emits a structured
`Information`-level log line:

```
Unmapped benefit service category {ServiceCategory} on plan {PlanId} tenant {TenantId} — defaulting to Other
```

so gaps surface in telemetry rather than silently degrading the UI. Update
the dictionary in `BenefitCategoryMap` to close each gap. If the map grows
past ~100 entries, promote it to `config/benefit-category-map.json`.

### Pharmacy tiers

Plans vary: some expose `Tier 1/2/3/4`, some use
`Generic / Preferred Brand / Non-Preferred Brand / Specialty`. The response
assigns every pharmacy benefit to the single `Pharmacy` category and
carries three separate pieces of information on
`CategorizedBenefit.pharmacy`:

| Field           | Purpose                                       | Example input → value   |
|-----------------|-----------------------------------------------|--------------------------|
| `tierLabel`     | **Verbatim** from the plan; what the UI shows | `"Specialty Drug"` → `"Specialty Drug"` |
| `canonicalTier` | Normalized bucket for grouping / analytics    | `"Specialty Drug"` → `"Specialty"`       |
| `isSpecialty`   | Case-insensitive `contains("specialty")`      | `"Specialty Drug"` → `true`              |

> **Pharmacy tier semantics.** `tierLabel` is always the plan's original
> `ServiceCategory` string, trimmed only — never normalized, never
> collapsed. Display this. `canonicalTier` is lossy by design (for
> analytics); never render it in the UI. Splitting the two avoids the
> old trap where `"Specialty Drug"` silently became `"Specialty"` in
> downstream reports.

The `category` field is deliberately a string (not an enum) so new pharmacy
buckets can be introduced without a wire-format break.

## Documents

Plan-level documents are stored inline on `BenefitPlan.Documents` as
`PlanDocumentReference`. The field shape is chosen to be a clean superset of
the FHIR `DocumentReference.content.attachment`:

| Inline field        | FHIR equivalent                              |
|---------------------|----------------------------------------------|
| `Location`          | `content.attachment.url`                     |
| `ContentType`       | `content.attachment.contentType`             |
| `Size`              | `content.attachment.size`                    |
| `ContentHashSha256` | `content.attachment.hash` — **Base64-encoded SHA-256 digest (32 decoded bytes)** |
| `DocType`           | `type.coding`                                |
| `Version`           | `version`                                    |
| `EffectiveDate`     | `date`                                       |

`Location` accepts both external HTTPS URLs and internal references of the
form `documentreference/{id}`. Consumers must accept both.

> **Hash encoding.** `ContentHashSha256` is Base64-encoded — 32 decoded
> bytes, matching FHIR exactly. Validation runs at producer boundaries
> only (`BenefitPlansController` create/update) via
> `PlanDocumentValidation.ValidateHash`, which throws `ArgumentException`
> (the controller translates to a 400 with the field name). The model
> property itself is deliberately unvalidated so Mongo hydration and JSON
> deserialization of historical documents never throw from inside the
> pipeline — data that can't be read can't be corrected.

> **Phase 2.** `TODO(benefits-viewer-phase2)`: migrate documents into
> `member-document-service` (PR #650 sibling work) and retire the inline
> `Documents` list. Because the inline shape is already FHIR-aligned, the
> migration is a data copy — no model redesign required.

## Portal integration

`CloudHealthOffice.Portal/Pages/MemberDetailsDialog.razor` gains a new
`Benefits` `MudTabPanel`:

- **No active coverage** → friendly `MudAlert` prompting the reviewer to check
  coverage state.
- **One active coverage** → fetches the member view and renders a
  `MudSimpleTable` of categorized benefits plus document download buttons.
- **Multiple concurrent coverages** → a `MudSelect` lets the user pick which
  coverage to render.
- **Mid-year plan swap** → `serviceDate=today` always resolves to the plan
  version currently in effect; the `planVersion` stamp is shown in the header
  so users can tell when a plan has been revised.

The portal client method is `IBenefitPlanService.GetMemberViewAsync(planId, serviceDate)`.
On 404 it returns `null` (no configured view); on transport failure it
throws `ServiceUnavailableException`, matching the rest of the portal.

### Zero cost-share display

`$0` values are meaningful (ACA preventive benefits, tier-1 generics,
plan-level waivers) and must be shown — not hidden as if absent.
`Formatters/BenefitCostShareFormatter.Format` uses `HasValue`-based display:

| `copay`    | `coinsurance` | rendered             |
|------------|---------------|----------------------|
| `null`     | `null`        | `—`                  |
| `0`        | `null`        | `No copay`           |
| `null`     | `0`           | `No coinsurance`     |
| `0`        | `0`           | `No charge`          |
| `25`       | `null`        | `$25 copay`          |
| `null`     | `0.2`         | `20% coinsurance`    |
| `25`       | `0.2`         | `$25 copay · 20% coinsurance` |
| `0`        | `0.2`         | `No copay · 20% coinsurance`  |

## Testing

- `tests/CloudHealthOffice.BenefitPlanService.Tests/BenefitViewServiceTests.cs`
  covers category mapping, pharmacy tier detection, out-of-network
  projection, document forward-compatibility fields, plan-version derivation,
  and the 404 path.
- `src/portal/CloudHealthOffice.Portal.Tests/Services/BenefitPlanServiceTests.cs`
  covers the portal HTTP client — happy path deserialization, 404 → null,
  and service-unavailable translation.
