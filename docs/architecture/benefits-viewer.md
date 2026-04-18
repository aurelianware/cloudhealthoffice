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
models this by:

- assigning every pharmacy benefit to the single `Pharmacy` category, and
- carrying the raw tier string verbatim in `CategorizedBenefit.pharmacy.tierLabel`.

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
| `ContentHashSha256` | `content.attachment.hash` (base64 of sha256) |
| `DocType`           | `type.coding`                                |
| `Version`           | `version`                                    |
| `EffectiveDate`     | `date`                                       |

`Location` accepts both external HTTPS URLs and internal references of the
form `documentreference/{id}`. Consumers must accept both.

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

## Testing

- `tests/CloudHealthOffice.BenefitPlanService.Tests/BenefitViewServiceTests.cs`
  covers category mapping, pharmacy tier detection, out-of-network
  projection, document forward-compatibility fields, plan-version derivation,
  and the 404 path.
- `src/portal/CloudHealthOffice.Portal.Tests/Services/BenefitPlanServiceTests.cs`
  covers the portal HTTP client — happy path deserialization, 404 → null,
  and service-unavailable translation.
