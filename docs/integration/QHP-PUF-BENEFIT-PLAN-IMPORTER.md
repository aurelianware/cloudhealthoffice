# QHP PUF benefit plan importer

Seeds CHO benefit plans from the CMS Exchange Public Use Files (PUFs), so a
tenant can start with the real cost sharing of every ACA marketplace plan in a
state or for one issuer, instead of typing plans in from SBCs.

- Source: [Health Insurance Exchange PUFs](https://www.cms.gov/marketplace/resources/data/public-use-files),
  plan years 2014 to 2027. Two files are used: the **Plan Attributes PUF** and
  the **Benefits and Cost Sharing PUF** (data dictionaries are linked from the
  same page).
- Coverage: plans on the federal platform (FFE states, states doing plan
  management, SBEs on the federal platform), multi-state plans and
  off-exchange stand-alone dental plans. States running their own exchange
  platform are generally not in these files.
- License: US government public data.
- Code: `src/services/benefit-plan-service/Import/QhpPuf/`,
  `Controllers/QhpPufImportController.cs`.
- Samples: `docs/integration/samples/qhp-puf/` (synthetic, issuer ids 999xx,
  state `ZZ`, built from the PY2026/PY2027 data dictionaries).

## API

```
POST /api/v1/plans/import/qhp-puf?state=VA&dryRun=true
Content-Type: multipart/form-data
  planAttributes       = plan-attributes-puf.zip   (or the CSV inside it)
  benefitsCostSharing  = benefits-and-cost-sharing-puf.zip
```

Permission: `settings:manage` (the service's default write permission).
Upload limit: 1 GB per request; the national Benefits and Cost Sharing file is
several hundred MB, so a state extract uploads faster.

| Query | Meaning |
|---|---|
| `state` | Two-letter state code. |
| `issuerId` | Five-digit HIOS issuer id. |
| `planIds` | Comma list of 17-character plan ids or 14-character standard component ids. |
| `csrVariants` | Comma list of CSR suffixes to keep: `00` off exchange, `01` on exchange, `02`/`03` AI/AN, `04`/`05`/`06` silver 73/87/94% AV. Empty keeps all. |
| `businessYear` | Plan year filter. |
| `maxPlans` | Default 200, capped at 2000. `truncated: true` in the report means more matched. |
| `dryRun` | Default `true`: map and report, write nothing. |
| `includePlans` | Return the mapped plan documents on a real run too (always on for a dry run). |

At least one of `state`, `issuerId` or `planIds` is required.

A real run creates each plan as a **Draft** version (`POST /api/v1/plans/drafts`
semantics, including the ACA limit validator). Nothing is published; a plan
administrator reviews the warnings, edits, and publishes. A plan id that
already exists for the tenant is reported as `Exists` and left alone, so
re-running the same files is safe. To apply a later PUF refresh, amend the plan.

## What maps where

**Plan Attributes PUF → `BenefitPlan`**

| PUF | CHO |
|---|---|
| `PlanId` (17 chars, with CSR suffix) | `PlanId` |
| `PlanVariantMarketingName`, else `PlanMarketingName` | `PlanName` |
| `IssuerMarketplaceMarketingName`, else `IssuerId` | `Payer` |
| `PlanType` HMO/PPO/EPO/POS | `PlanType`; Indemnity → PPO (warning); `IsHSAEligible = Yes` → HDHP (warning) |
| `MetalLevel` | `MetalLevel` (dental High/Low left null) |
| `MarketCoverage` Individual / SHOP | `LineOfBusiness` Exchange / Commercial |
| `PlanEffectiveDate`, `PlanExpirationDate` | `EffectiveDate`, `TerminationDate` (default: plan year) |
| `TEHBDed*` when `MedicalDrugDeductiblesIntegrated = Yes`, else `MEHBDed*` | `CostSharing` deductibles: in-network tier 1 individual and family per group, out of network |
| `TEHB*MOOP` / `MEHB*MOOP` likewise | `CostSharing` out-of-pocket maximums |
| `*DedInnTier1Coinsurance` | `CostSharing.Coinsurance` |
| `NetworkId`, `ServiceAreaId`, `FormularyId`, `HIOSProductId`, SBC/brochure/formulary URLs | Report `references` only (for the network, service area and formulary loaders that come next) |

**Benefits and Cost Sharing PUF → `Benefit`**

Each CHO adjudication category takes the first QHP benefit row the plan has
from its list (`QhpBenefitCategoryMap`). The category label is the
`serviceTypeCode` from `schemas/service-category-mappings/system-defaults.json`,
because that is what claim lines resolve to; a test keeps the two in step.

| CHO category | QHP benefit rows, in order of preference |
|---|---|
| Office Visit | Primary Care Visit to Treat an Injury or Illness |
| Specialist Visit | Specialist Visit |
| Preventive Care | Preventive Care/Screening/Immunization (typed `PreventiveBenefit`; ACA preventive when no in-network cost sharing) |
| Urgent Care | Urgent Care Centers or Facilities |
| Emergency Room | Emergency Room Services |
| Ambulance | Emergency Transportation/Ambulance |
| Inpatient Hospital | Inpatient Hospital Services (e.g., Hospital Stay) |
| Outpatient Surgery | Outpatient Facility Fee (e.g., Ambulatory Surgery Center); Outpatient Surgery Physician/Surgical Services |
| Outpatient Hospital | Outpatient Facility Fee (e.g., Ambulatory Surgery Center) |
| Laboratory | Laboratory Outpatient and Professional Services |
| Imaging | X-rays and Diagnostic Imaging; Imaging (CT/PET Scans, MRIs) |
| Behavioral Health | Mental/Behavioral Health Outpatient Services; Substance Abuse Disorder Outpatient Services |
| Maternity | Delivery and All Inpatient Services for Maternity Care; Prenatal and Postnatal Care |
| Skilled Nursing | Skilled Nursing Facility |
| Home Health | Home Health Care Services |
| Hospice | Hospice Services |
| Physical Therapy | Outpatient Rehabilitation Services |
| DME | Durable Medical Equipment |
| Vision | Routine Eye Exam for Children; Routine Eye Exam (Adult) |
| Pharmacy | Generic Drugs |
| Generic, Preferred Brand, Non-Preferred Brand, Specialty Drug | The four drug tiers, as `PharmacyBenefit` rows for the member benefits view |

Cell parsing (`QhpCostShareParser`): `$X Copay` → copay; `X% Coinsurance` →
coinsurance as a fraction; `No Charge` → 0% coinsurance; "after deductible" or
"with deductible" on either in-network cell → `DeductibleApplies`;
`Not Applicable` → unset. `QuantLimitOnSvc` with visits per year →
`VisitLimit`; dollars per year / lifetime → `AnnualMaximum` / `LifetimeMaximum`;
anything else and `Explanation` / `Exclusions` → `Limitations` text.

## The report: read it before publishing

Each plan in the response carries:

- `warnings`: where CHO's plan model is coarser than the QHP template. CHO has
  one deductible flag per benefit (in-network rule used for both networks), one
  deductible and one OOP maximum (a separate drug deductible counts against the
  medical one), one in-network tier (tier 2 dropped), and applies a per-day or
  per-stay copay once per claim. HMO/EPO benefits with nothing stated out of
  network are set to 100% member coinsurance out of network, because CHO has
  no per-network coverage flag; emergencies are priced in network by the engine.
- `categoriesFilled`: CHO category → the QHP row used.
- `categoriesMissing`: CHO categories with no QHP row. Physical Therapy and
  Skilled Nursing have no fallback category in the engine and deny with CARC 96.
- `foldedBenefits`: QHP rows that matched an already-filled category (for
  example an MRI copay that differs from the X-ray copay used for Imaging).
- `unmappedBenefits`: every other QHP row (chiropractic, bariatric surgery,
  infertility, dental, ...), with whether it is covered. The run-level
  `unmappedBenefitCounts` shows which ones are worth a category next.

## Not done yet

- Stand-alone dental plans are skipped (`DentalOnlyPlan = Yes`).
- Second in-network tier, separate drug deductible, per-day copays, CSR
  variant relationships (each variant is its own plan).
- Network (Network PUF), service area and formulary references are reported,
  not loaded.
- Plan-code crosswalk: a plan sending 834s or CPCDS coverage with its own plan
  codes still needs a `plan-code-mappings` entry to the HIOS plan id.
- Not yet run against a full PY2027 national file in CI; the tests use the
  synthetic samples.

## Other public sources considered

| Source | What it gives CHO | Fit |
|---|---|---|
| Medicare Advantage / Part D PBP benefits files (CMS, quarterly zip of tab-delimited tables) | Every MA and PDP plan's benefits by Medicare service category, coded | Best next importer for Medicare lines; coded and multi-table, so roughly two to three times this one |
| MA Landscape files | Premium, deductible, MOOP per MA/PDP plan | Plan-level summary only |
| Plan-Net `InsurancePlan` endpoints (public under CMS-0057-F) | Plan names, types, networks, coverage areas; cost sharing optional and rarely filled | Network and plan crosswalk, not benefit design |
| SBC PDFs | Every commercial plan, including employer plans, in a standard template | Per-plan document extraction; the right path for self-funded and fully insured group plans |
| Medicaid state plans, MCO model contracts, member handbooks | Covered services and limits per state; cost sharing is nominal | No national structured file; one hand-built template per state program |
| Transparency in Coverage machine-readable files | Negotiated rates by plan, provider and code | Provider contracts and fee schedules, not benefits |
| Clearinghouse 270/271 (Stedi and others) | One member's benefit summary on one date | Needs a real member, a provider and consent; a validation check, not a bulk source |
