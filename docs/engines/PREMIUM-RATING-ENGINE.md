# Premium Rating Engine

The premium rating engine (`src/services/premium-billing-service/Rating/`) prices a
subscriber's coverage from a plan's rate table and turns a group's coverage into
an invoice: the charge for the billed month plus proration and retro adjustment
lines. It is pure code with no I/O and no clock, so every amount can be checked by hand.

## Table of contents

- [Rate tables](#rate-tables)
- [Rating methods](#rating-methods)
- [Tobacco surcharge](#tobacco-surcharge)
- [Invoice calculation](#invoice-calculation)
- [Rounding](#rounding)
- [Status and next steps](#status-and-next-steps)

---

## Rate tables

A `RateTable` holds the rates of one plan (`PlanId`) for one effective period
(`EffectiveFrom` to `EffectiveTo`, inclusive; `EffectiveTo` null means open-ended).
`RateTableCatalog` loads a tenant's tables, validates each one, rejects two tables
of one plan that cover the same day, and resolves `(planId, date)` to a table.

| Field | Used by | Meaning |
|---|---|---|
| `Method` | all | `Tier`, `AgeBand` or `Composite` |
| `TierRates` | Tier | Monthly rate per tier (EE, EE+Spouse, EE+Child(ren), Family) |
| `AgeBandBaseRate` | AgeBand | Monthly rate of a 21-year-old (factor 1.000) |
| `AgeCurve` | AgeBand | Age factors; null means the federal default curve |
| `AgeDeterminationDate` | AgeBand, Composite | Day ages are measured on; defaults to `EffectiveFrom` |
| `ChildCapAge`, `MaxRatedChildrenUnderCapAge` | AgeBand, Composite | 21 and 3: the three-oldest-children rule |
| `CompositeRate`, `CompositeBasis`, `CompositeTierFactors` | Composite | See below |
| `Tobacco` | all | Optional surcharge |

## Rating methods

Each household (`RatingEnrollment`: one subscriber, their dependents, plan, coverage
dates) is rated by `PremiumRatingEngine.Rate(table, enrollment)`. The tier is taken
from the enrollment, or derived: subscriber only is EE; with a spouse or domestic
partner it is EE+Spouse; with children it is EE+Child(ren); with both it is Family.

### Tier

The household pays the tier rate. The rate is carried on the subscriber's member row.

### ACA age band (45 CFR 147.102)

Each member pays `AgeBandBaseRate × factor(age)`, rounded to the cent, and the household
pays the sum. Family-tier rules:

- Only the **three oldest children under 21** are rated. Younger children under 21 are listed
  with `Rated = false` and a zero premium.
- Children aged 21 and over are always rated and do not use one of the three places.
- Ages are fixed at issue or renewal: the age on the plan-year start (`EffectiveFrom`
  or `AgeDeterminationDate`), or on the coverage start for someone who joins mid-year.

The age curve is data. The CMS federal default curve (plan years 2018 onward: 0–14 at
0.765, 21 at 1.000, 64 and older at 3.000) ships as
`Rating/AgeCurves/federal-default.json` and is embedded in the assembly. A state curve
is set on the table (`AgeCurve`) or loaded with `AgeCurve.FromJson`. Curves are validated:
bands must start at age 0, be contiguous, end in one open-ended band and have positive factors.

### Composite (45 CFR 147.102(c)(3))

A composite rate is derived once per plan year from the group's census with
`PremiumRatingEngine.DeriveCompositeRate(ageBandTable, census, basis)` and stored on a
composite table, where it stays fixed for the year:

- `PerMember`: total age-rated premium ÷ number of rated members. A household pays
  rate × rated members, with the three-child cap applied.
- `TierFactors`: total age-rated premium ÷ the sum of the households' tier factors. A household
  pays rate × its tier factor.

Tobacco is excluded when the rate is derived and added for each tobacco user.

## Tobacco surcharge

`TobaccoSurcharge.Factor` is between 1.0 and 1.5 (the ACA maximum). It applies only to
members at or above `MinimumAge`, which defaults to 21, the federal tobacco age.

- Age band: the member's premium × (Factor − 1).
- Tier and composite: `FlatMonthlyAmount` per tobacco user if it is set. Otherwise
  (Factor − 1) × the single-member rate (the EE tier rate, or the composite unit rate).

## Invoice calculation

`PremiumInvoiceCalculator.Calculate` takes the billing month, the billing date, the
group's coverage as recorded on the billing date (including coverage terminated since
the last invoice), and a `BilledLedger` built from the group's earlier invoices
(`BilledLedger.FromInvoices`; voided invoices count for nothing).

1. **Current month.** Each coverage active on any day of the month gets one line.
   A mid-month add or term is prorated by day: monthly premium × covered days ÷ days in month.
   A rate-table boundary inside the month splits the charge between the two tables.
2. **Retro.** Each earlier month within `MaxRetroMonths` (default 3) that has an invoice
   is reconciled one coverage at a time. If the correct charge differs from what was billed
   (line items plus earlier adjustments for that month), the difference becomes an
   `InvoiceAdjustment` with `CoverageId` and `ServicePeriodStart`/`End`, so a later invoice
   does not bill it again. The adjustment type is:
   - `RetroAdd` when nothing was billed for the month;
   - `RetroTerm` when nothing is due, or when coverage ended inside the month;
   - otherwise `RateChange`, for example after a tier change.
   Months that have no invoice are not reconciled; their own billing run bills them.

### Worked example (unit test `MarchInvoice_HandChecked_ProrationRetroAddsAndRetroTerm`)

Tier plan: EE 500, EE+Spouse 1,000, Family 1,400. January and February were invoiced
for A (EE, 500) and B (Family, 1,400). The March invoice is computed on 2026-02-20:

| Item | Calculation | Amount |
|---|---|---:|
| A, March, EE | full month | 500.00 |
| C, March, EE+Spouse | full month | 1,000.00 |
| D, March, EE from 3/10 | 500 × 22/31 | 354.84 |
| **Subtotal** | | **1,854.84** |
| C, January retro add (from 1/16) | 1,000 × 16/31 − 0 | +516.13 |
| B, February retro term (ended 2/14) | 1,400 × 14/28 − 1,400 | −700.00 |
| C, February retro add | 1,000 − 0 | +1,000.00 |
| **Adjustments** | | **+816.13** |
| **Invoice total** | | **2,670.97** |

## Rounding

Money is rounded to the cent half away from zero. Age-band premiums are rounded per
member, and the household premium is the sum of those rounded amounts. Prorated amounts
are rounded once, per coverage and month.

## Status and next steps

The engine, the federal age curve, the catalog and the invoice calculator are implemented
and unit-tested. Two pieces are not wired yet:

- **Billing-run integration.** `ExecuteBillingRunAsync` still bills coverage-service's
  `monthlyPremium`. Rating each household needs coverage-service to return each member's
  date of birth, relationship and tobacco status, and the tier, with the coverage, including
  coverage terminated since the last invoice.
- **Rate-table storage and API.** Tables are plain documents (`RateTable`), but there is no
  repository or controller yet. The catalog is built from whatever source holds them.
