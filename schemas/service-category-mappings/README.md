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
4. **POS-code inference** — last-resort heuristic baked into
   `ServiceCategoryResolver` (POS 11 → Professional Visit, etc.).

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

These do not match. As a result, current adjudication via the POS
fallback produces denial code `18 — No benefit category mapping` for any
plan whose `Benefit.ServiceCategory` values aren't X12 codes.

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
and skips reruns at the same version. To re-apply with bundle changes,
**bump `version` and trigger the seeder admin endpoint** for affected
tenants. Re-application **inserts new mapping rows alongside existing
seeded rows**; it does **not** replace prior mappings — that is a
deliberate choice so operator-authored overrides aren't lost on
re-apply. Operators clean up superseded seed rows manually via the
`DELETE` admin endpoint when needed.

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

CHO-curated for v1. The bundle covers ~18 categories across professional
E&M, inpatient, outpatient surgery, emergency, urgent care, pharmacy,
behavioral health, preventive, maternity, imaging, laboratory, DME,
vision, home health, physical therapy, ambulance, and skilled nursing.

Future bundles may incorporate authoritative third-party sources
(X12 5010 service-type code list, CMS HCPCS service-type mapping)
once the X12 ↔ free-text translation layer lands. Bundles that cite a
third-party source must record the source in the top-level `source`
field for audit traceability.
