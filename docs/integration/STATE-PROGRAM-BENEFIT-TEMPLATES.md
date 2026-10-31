# State program benefit plan templates

Medicaid and CHIP benefits are set by the state, per program, not by the plan,
so one template per state program and population gives every MCO in that state
the same starting plan. Texas, California, Florida, Arizona and New York are done.

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

## California

| Template | Program and population | Member cost sharing | Main differences |
|---|---|---|---|
| `ca-medi-cal-child` | Medi-Cal managed care, under 21 (California runs CHIP inside Medi-Cal) | None; under 18 exempt by statute | EPSDT; CCS conditions carved out to the county except in Whole Child Model counties |
| `ca-medi-cal-adult` | Medi-Cal managed care, 21+ (expansion adults, seniors and persons with disabilities, long-term care) | $0 in the template; statute allows $1 per visit, $1 per prescription and $5 for a non-emergency ER visit, with wide exemptions | Long-term care carved in statewide; nominal copays left for the plan's contract to decide |

Both templates share these carve-outs:

- Pharmacy-claim drugs go to Medi-Cal Rx; drugs billed on medical claims stay with the plan.
- Specialty mental health goes to the county mental health plan.
- Substance use treatment goes to the county DMC or DMC-ODS program.
- Dental goes to Medi-Cal Dental.
- Developmental services go to regional centers.

The county plan model (COHS, two-plan, single plan, GMC, regional) decides which
plan a member is in, not what the plan covers, so it belongs on the tenant-service
program rather than in a template. CalAIM Enhanced Care Management and Community
Supports are listed as not modeled. The covered-services wording in the DHCS
boilerplate contract has not been reviewed line by line, so most values are `verify`.

## Florida

| Template | Program and population | Member cost sharing | Main differences |
|---|---|---|---|
| `fl-mma-child` | SMMC Managed Medical Assistance, under 21 (also MediKids, ages 1 to 4) | None | EPSDT; MediKids premiums are premium billing |
| `fl-mma-adult` | SMMC MMA, 21+ | $0 in the template; the fee-for-service schedule is $1 to $3 per provider per day, plus 5% of the first $300 (max $15) for non-emergency ER use | Copays and their exemptions are left for the plan contract |
| `fl-ltc` | SMMC Long-Term Care, nursing facility level of care | None (patient responsibility is set at eligibility) | Covers only nursing facility, home health, hospice and equipment; HCBS waiver services not modeled; medical stays with MMA |
| `fl-healthy-kids` | Florida Healthy Kids (CHIP, ages 5 to 18), subsidized and full-pay | Office and specialist $5, ER and ambulance $10, prescriptions $5, therapy, home health, hospice and refraction $5; hospital, labs, behavioral health $0 | Premiums $15 or $20 (subsidized); ER copay waiver, visit limits and eyewear copay not modeled |

Dental is a separate SMMC dental plan, and Healthy Kids has its own dental plan.
Urgent care and outpatient surgery copays are not on the Healthy Kids benefits
page, so they are marked `verify`.

## Arizona

| Template | Program and population | Member cost sharing | Main differences |
|---|---|---|---|
| `az-ahcccs-child` | AHCCCS Complete Care (ACC), under 21 | None; under 19 exempt | EPSDT, including dental, eyeglasses and hearing aids |
| `az-ahcccs-adult` | ACC, 21+ (Adult Group, parents, SSI without long-term care) | $0 in the template; optional copays ($3.40 office, $2.30 Rx and therapy) or required copays for Transitional Medical Assistance ($4.00 office, $2.30 Rx, $3.00 therapy and non-emergency outpatient surgery), capped at 5% of income per quarter | PT and OT 15 visits per contract year to restore plus 15 to maintain (kept as text); emergency dental $1,000; no adult hearing aids or cochlear implants |
| `az-altcs-epd` | ALTCS Elderly and Physically Disabled, nursing facility level of care | None (ALTCS is exempt) | Fully integrated: acute, behavioral and long-term care; HCBS and share of cost not modeled |
| `az-kidscare` | KidsCare (CHIP), under 19 | None | Premiums are suspended until further notice; same benefits as AHCCCS children (marked verify) |

Physical and behavioral health are integrated in ACC plans, and members with a
serious mental illness go to the regional ACC-RBHA plan. Copays depend on the
member's eligibility group, which a plan cannot hold, so they are left at $0 and
described in `notModeled`. American Indian and Alaska Native members can choose
the AIHP fee-for-service program, and members with developmental disabilities
are served by DES/DDD. The AHCCCS contract year runs October 1 to September 30.

## New York

| Template | Program and population | Member cost sharing | Main differences |
|---|---|---|---|
| `ny-mmc-child` | Mainstream Medicaid managed care, under 21 | None | EPSDT; pharmacy carved out to NYRx |
| `ny-mmc-adult` | Mainstream Medicaid managed care, 21+ | None from the plan; NYRx charges $1 generic and $3 brand, up to $50 a quarter | Pharmacy carved out to NYRx since April 1, 2023 |
| `ny-harp` | Health and Recovery Plan, 21+ meeting behavioral health high-risk criteria | Same as adult | Adds BH HCBS and CORE services (not modeled) with Health Home care management |
| `ny-mltc` | Partial capitation Managed Long Term Care, 21+ needing more than 120 days of community care | None (spenddown is set by the county) | Covers only nursing home, home care, therapies, equipment, optometry and non-emergency transport; medical care is Medicare or fee-for-service |
| `ny-child-health-plus` | Child Health Plus (CHIP), under 19 | None; premiums $0 to $60 per child per month | Pharmacy stays with the plan; dental and vision included |

The Essential Plan (New York's Basic Health Program) is not included. Its
eligibility and cost-sharing tiers are changing in 2026, and published plan
pages do not yet show a full tier table. MAP and PACE products are also not
included. The model contract has not been reviewed line by line, so most
mainstream covered-service values are `verify`.

## Adding a state

Copy a Texas template into `schemas/benefit-plan-templates/{state}/` and keep
the 20 categories. Cite every value and mark anything not confirmed `verify`.
The catalog test checks categories and source references for every file.
