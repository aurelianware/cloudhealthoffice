# Service-Category Mapping — System Defaults

This directory contains the curated CHO system-default service-category
mapping bundle consumed by `SystemDefaultMappingSeeder` in
`benefit-plan-service` (capability **BP 5.6 — Service Category Mapping**).

## What this bundle does

The benefit calculation engine resolves each adjudicated claim line to a
service category before looking up the operator-authored cost share. The
resolution order is:

1. **Plan-specific override** — operator-authored mapping scoped to a
   single benefit plan.
2. **Tenant-level default** — operator-authored mapping that applies to
   every plan in the tenant.
3. **System-level fallback** — the bundle in this directory, applied
   per-tenant by `SystemDefaultMappingSeeder` on first read or on admin
   trigger.
4. **System-level inference** — last-resort heuristic baked into
   `ServiceCategoryResolver`. Institutional claims: type of bill /
   facility type and revenue code (11x → Inpatient Hospital, 13x →
   Outpatient Hospital, REV 045x → Emergency Room). Professional claims:
   CMS place of service (POS 11 → Office Visit, 21 → Inpatient Hospital,
   19/22 → Outpatient Hospital, 23 → Emergency Room, 20 → Urgent Care,
   24 → Outpatient Surgery, 31 → Skilled Nursing, 34 → Hospice,
   51–58 (except 54) → Behavioral Health, 81 → Laboratory; 32, 33, 54
   and 62 among others are deliberately unmapped so the procedure code
   decides — the full table and the reasons are documented on
   `ServiceCategoryResolver.ProfessionalPlaceOfServiceMap`). Both emit the
   category names used in this bundle; every category the POS table emits
   is already here.

   When a plan has no category for what a line resolved to, the engine
   tries the category's X12 codes (the specific one the fallback used
   first, so POS 55 matches a plan's `AI` substance-abuse category ahead
   of its `A4` psychiatric one), then a one-step rollout fallback:
   Urgent Care → Office Visit, Outpatient Surgery → Outpatient Hospital,
   Laboratory → Outpatient Hospital, Hospice → Home Health (logged at
   Warning). Physical Therapy and Skilled Nursing have no fallback and
   deny with CARC 96. `scripts/migration/report-plans-missing-service-categories.js`
   lists active plans affected (read-only).

Without this bundle, the resolver depends solely on POS inference and
operator-authored mappings. New tenants that haven't authored mappings
will hit the inference fallback for most CPT codes — that's why the
bundle exists.

## Authoring conventions

Each mapping document carries:

| Field | Purpose |
|---|---|
| `serviceTypeCode` | Free-text **operator-friendly** category label. Joins to `Benefit.ServiceCategory` on the plan. **Not** an X12 5010 code (see "Known incoherence" below). |
| `serviceTypeDescription` | Human-readable description rendered in member-portal benefit summaries. |
| `since` | Optional. Bundle version the mapping was introduced in (absent = 1). A rule may carry its own `since` when it was added to an existing mapping later. Must be between 1 and `version`. |
| `rules` | Ordered list of `ProcedureCodeRule` entries; the resolver applies them by `priority` ascending and matches the first that fits the claim line. |

Rule fields mirror `ProcedureCodeRule` in `BenefitEngine.Domain`:

- `codeType` — `"CPT"`, `"HCPCS"`, `"REV"`, `"NDC"`, `"CDT"`, etc.
- `codePattern` — exact code, prefix wildcard ending in `*`, or range
  start (paired with `codeRangeEnd`).
- `codeRangeEnd` — inclusive range end when present; absent for exact /
  wildcard match.
- `placeOfServiceCode` — optional POS filter (e.g., `"11"` for office,
  `"21"` for inpatient).
- `requiredModifier` — optional CPT modifier filter (e.g., `"GC"`).
- `revenueCode` — optional UB-04 revenue-code filter.

## Known incoherence — `ServiceTypeCode` vs `Benefit.ServiceCategory`

`ServiceCategoryResolver` produces a `ServiceTypeCode`. The benefit
calculation engine uses that code to look up the matching `Benefit` on
the plan via `BenefitPlanConfig.GetCategory(serviceTypeCode)`. The plan
side of the join is `Benefit.ServiceCategory` — a free-text
plan-author label.

For the join to succeed, **the resolver's `ServiceTypeCode` must equal
the plan's `Benefit.ServiceCategory` string**. The two surfaces have
historically used different identifier conventions:

- The **resolver fallback** (POS inference) emits X12 5010 codes
  (`"98"` Professional Visit, `"48"` Inpatient).
- **Plan authors** type free-text categories like `"Office Visit"`,
  `"Inpatient Hospital"`.

These did not match, so adjudication via the POS fallback denied any
plan whose `Benefit.ServiceCategory` values aren't X12 codes. **Resolved
for the fallback:** both resolver fallbacks now emit the category names
in this bundle (via `ServiceCategoryNames`, a single X12 code → name map),
and `BenefitPlanConfig.GetCategories` falls back to the X12 code of a
name it cannot match exactly, so plans still keyed by `"98"` / `"48"`
keep matching.

This bundle takes a deliberate position: **operator-friendly text labels
that match the plan-author convention.** A plan with
`Benefit.ServiceCategory = "Office Visit"` will adjudicate correctly
against the seeded `Office Visit` mapping. The X12 5010 alignment is
deferred to a future translation-layer capability that introduces a
`ServiceTypeCodeAlias` table joining canonical X12 codes to operator
text labels — that work falls under **BP 5.10 (Adjudication API
Stabilization, Phase 1 closer)** or a follow-up.

The architecture document at
`docs/architecture/service-category-mapping.md` carries the canonical
decision record.

## Bundle versioning

The top-level `version` field carries an integer. The seeder records the
last applied version per tenant in a `SystemDefaultsApplied` document
and skips reruns at the same version.

To ship bundle changes: **bump `version`, set `since` to the new version
on every mapping (or rule) you add, and trigger the seeder admin
endpoint** for affected tenants. A tenant's first seed writes the whole
bundle. A version bump writes **only the delta**:

- only mappings / rules whose `since` is later than the tenant's
  recorded version — earlier ones are never written again, so defaults a
  tenant deleted are not restored and categories are not duplicated;
- and of those, only rules whose codes no existing tenant-default row
  already covers (same code type, overlapping code, range or wildcard).
  Reads sort newest-first and the first match wins, so a re-inserted
  default would otherwise outrank the tenant's own mappings.

Re-running at the same version is a no-op. Changing an existing rule's
codes is not a delta the seeder can apply safely; ship it as a new rule
with a new `since`, and tell operators to delete the old row if needed.

## Operating the seeder

The seeder is a hosted service registered in `Program.cs`. At startup
it loads and validates this bundle. It does **not** enumerate tenants
on its own — applying the bundle to a specific tenant is operator-
triggered via the admin write controller (config-gated by
`ServiceCategoryMapping:AdminWriteEnabled=true`).

For pilot onboarding the operator runs:

```
POST /api/v1/service-category-mappings/seed-system-defaults
X-Tenant-ID: <tenant>
```

The tenant is resolved from the `X-Tenant-ID` header by the standard
benefit-plan-service tenant middleware (no `tenantId` query parameter,
no `/admin` path prefix). The call is idempotent — repeated calls at
the same bundle version are no-ops.

## Bundle source

CHO-curated. Version 2 added **Outpatient Hospital** (HCPCS G0463,
REV 0510–0519 clinic, REV 0760–0769 treatment / observation room), the
category the institutional fallback assigns to 13x bills. Like every
entry, it carries no cost share: cost share is authored on the plan.
The bundle covers ~20 categories across professional
E&M, inpatient, outpatient hospital, outpatient surgery, emergency, urgent care, pharmacy,
behavioral health, preventive, maternity, imaging, laboratory, DME,
vision, home health, hospice, physical therapy, ambulance, and skilled nursing.

Version 3 added **Hospice** (REV 0650–0659 hospice services, `since: 3`).
The institutional fallback already assigned Hospice to 81x/82x bills,
and the corrected professional place-of-service fallback now assigns it
to POS 34 (it previously assigned Home Health), so plans need the
category in the default vocabulary. There is no HCPCS rule: Q5001–Q5009
read "hospice *or home health* care provided in <setting>", so the code
alone does not mean hospice; the hospice-only revenue codes and the
81x/82x type-of-bill fallback carry it. Apply it per tenant with the
seeder admin endpoint
(`POST /api/v1/service-category-mappings/seed-system-defaults`); for a
tenant already at version 2 the call adds only the Hospice row (and only
where the tenant does not already map REV 065x).

Future bundles may incorporate authoritative third-party sources
(X12 5010 service-type code list, CMS HCPCS service-type mapping)
once the X12 ↔ free-text translation layer lands. Bundles that cite a
third-party source must record the source in the top-level `source`
field for audit traceability.
