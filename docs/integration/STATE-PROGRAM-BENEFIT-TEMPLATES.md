# State program benefit plan templates

Medicaid and CHIP benefits are set by the state, per program, not by the plan,
so one template per state program and population gives every MCO in that state
the same starting plan. Texas is the first state.

- Templates: `schemas/benefit-plan-templates/{state}/*.json`, copied into the
  benefit-plan-service image and loaded at startup by `BenefitPlanTemplateCatalog`
  (override the folder with `BenefitPlanTemplates:Directory`).
- Code: `src/services/benefit-plan-service/Templates/`,
  `Controllers/BenefitPlanTemplatesController.cs`.
- Tests: `tests/CloudHealthOffice.BenefitPlanService.Tests/Templates/`.

## API

| Call | Permission | What it does |
|---|---|---|
| `GET /api/v1/plans/templates?state=TX&program=CHIP` | `benefits:read` | Lists templates with their count of values still marked `verify`. |
| `GET /api/v1/plans/templates/{templateId}` | `benefits:read` | The template plus a preview of the plan it creates. |
| `POST /api/v1/plans/templates/{templateId}/drafts` | `settings:manage` | Creates a **Draft** plan. Body: `planId`, `payer`, `effectiveDate`, optional `planName`, `terminationDate`, `programId` (the tenant-service program, for example the tenant's "TX CHIP"). Returns 409 if the plan id exists. |

Nothing is published. The plan's benefits team checks each `verify` value
against its executed contract and the Uniform Managed Care Manual (UMCM) chapters
the contract incorporates. It adds its value-added services and loads its
prior-auth rules, then publishes. This follows the rule in
[TEXAS-MEDICAID-QUALIFICATION.md](../implementation/TEXAS-MEDICAID-QUALIFICATION.md):
every configured restriction needs a contract or policy citation, an effective
version and clinical approval.

## Template format

Each template has:

- **Identity:** `templateId`, `state`, `program`, `planName`, `population` and `ageRange`.
- **Plan fields:** `lineOfBusiness` and `planType`. Both are Medicaid; CHO has no separate CHIP line of business.
- **`benefits`:** one entry per adjudication category. `serviceCategory` must be a `serviceTypeCode` in `schemas/service-category-mappings/system-defaults.json`, and a test checks that all 20 are present. Each entry has the fields `covered`, `copay`, `coinsurance` (a fraction), `deductibleApplies`, `visitLimit`, `annualMaximum` and `limitations`, plus `source` and `status`.
- **`displayBenefits`:** pharmacy tier rows for the member benefits view.
- **`carveOuts`:** services the state contracts to another entity.
- **`notModeled`:** program rules CHO's plan model cannot hold yet, and why.
- **`sources`:** cited documents, with the date each was read.

`status` is `sourced` when the value was read from the cited source and
`verify` when it is a reasonable default that has not been confirmed. A limit
that would misfire if enforced as a CHO visit limit is kept in `limitations`
text, with a `notModeled` entry. CHO counts every claim in a category against a
visit limit, so "one eye exam and one pair of glasses" would deny the glasses.

## Texas

| Template | Program and population | Member cost sharing | Main differences |
|---|---|---|---|
| `tx-star-child` | STAR, under 21 | None | Texas Health Steps (EPSDT) covers anything medically necessary; children's dental carved out to a DMO |
| `tx-star-adult` | STAR, 21+ (pregnant women, parents and caretakers) | None | Adult limits: 30-day inpatient spell of illness (applicability to STAR adults marked verify) |
| `tx-starplus` | STAR+PLUS, 65+ and adults with disabilities, duals, nursing facility | None | Spell of illness; HCBS waiver LTSS, nursing facility and service coordination are not modeled |
| `tx-star-kids` | STAR Kids, under 21 with disabilities | None | MDCP waiver, private duty nursing, PPECC and Community First Choice are not modeled |
| `tx-star-health` | STAR Health, DFPS foster care and former foster youth | None | Adult limits for members who stay past 21 are not modeled |
| `tx-chip-151` | CHIP, at or below 151% FPL | Doctor visit $5, inpatient $35 per admission, generic $0, brand $5, non-emergency ER $5 | Enrollment fee $0 |
| `tx-chip-186` | CHIP, above 151% up to 186% FPL | Doctor visit $20, inpatient $75, generic $10, brand $35, non-emergency ER $75 | Enrollment fee $35 |
| `tx-chip-201` | CHIP, above 186% up to 201% FPL | Doctor visit $25, inpatient $125, generic $10, brand $35, non-emergency ER $75 | Enrollment fee $50 |
| `tx-chip-perinatal-above-threshold` | CHIP Perinatal (unborn child), income above the Medicaid threshold | None | Prenatal care, labor and delivery (facility and professional), pregnancy-related labs, imaging, drugs, emergency and transport only |
| `tx-chip-perinatal-at-or-below-threshold` | CHIP Perinatal, at or below the Medicaid threshold | None | Same coverage, except the labor and delivery facility charge goes to Emergency Medicaid |

All CHIP tiers share these rules:

- Preventive care and outpatient hospital have no copay.
- Durable medical equipment is capped at $20,000 per 12 months; diabetic supplies and implants don't count toward the cap.
- Hospice is covered for up to 120 days.
- Members get one eye exam and one pair of eyewear per 12 months.
- Dental is carved out to a CHIP dental maintenance organization (DMO).

What does not fit the CHO plan model yet:

- **CHIP cost-sharing cap.** The cap is 5% of household income per certification period. CHO's out-of-pocket maximum is a fixed dollar amount, so the enrollment broker keeps tracking the cap.
- **Non-emergency ER copay.** CHO cannot tell an emergent ER visit from a non-emergent one, so the ER copay is left at $0.
- **CHIP enrollment fees.** These belong to premium billing, not claims.
- **Member-level exemptions.** American Indian, Alaska Native and unaccompanied refugee minor members are exempt from cost sharing, which needs a member attribute.
- **Spell of illness.** This is a rolling per-spell day limit, not a yearly visit count.
- **LTSS and waiver services.** These are authorized through a service plan with hour and cost ceilings, and have no CHO category.
- **Diagnosis-conditioned coverage.** CHIP Perinatal emergency and ambulance coverage apply only when pregnancy-related, which needs benefit rule predicates authored per plan.
- **Value-added services and prior-auth lists.** Both are set by each MCO.
- **Formulary.** Drugs follow the Vendor Drug Program formulary and preferred drug list.

Sources are listed in each template. The CHIP cost-sharing figures come from the
HHSC Medicaid and CHIP Eligibility Handbook D-1800, with copay and fee sections
effective October 1, 2018 and read 2026-10-31. CHIP and CHIP Perinatal limits come
from Texas Children's Health Plan's published benefit tables. Confirm both against
the current UMCM and the plan's contract before going live.

## Adding a state

Copy a Texas template into `schemas/benefit-plan-templates/{state}/` and keep
the 20 categories. Cite every value and mark anything not confirmed `verify`.
The catalog test checks categories and source references for every file.
