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
dates) is rated by `PremiumRatingEngine.Rate(table, enrollment)`, or by
`RateOn(table, enrollment, date)` to rate only the members covered on that date.
Each member can carry their own `EffectiveDate` and `TerminationDate` (a dependent
added or dropped later); when these are null, the coverage dates apply. The tier is
taken from the enrollment, or derived from the members covered: subscriber only is EE;
with a spouse or domestic partner it is EE+Spouse; with children it is EE+Child(ren);
with both it is Family. An explicit tier applies to the whole coverage, so leave it
null when dependents have their own dates.

### Tier

The household pays the tier rate. The rate is carried on the subscriber's member row.

### ACA age band (45 CFR 147.102)

Each member pays `AgeBandBaseRate × factor(age)`, rounded to the cent, and the household
pays the sum. Family-tier rules:

- Only the **three oldest children under 21** are rated. Younger children under 21 are listed
  with `Rated = false` and a zero premium.
- Children aged 21 and over are always rated and do not use one of the three places.
- Ages are fixed at issue or renewal: the age on the plan-year start (`EffectiveFrom`
  or `AgeDeterminationDate`), or on the member's own coverage start for someone who
  joins mid-year, including a dependent added to an existing coverage.

The age curve is data. The CMS federal default curve (plan years 2018 onward: 0–14 at
0.765, 21 at 1.000, 64 and older at 3.000) ships as
`Rating/AgeCurves/federal-default.json` and is embedded in the assembly. A state curve
is set on the table (`AgeCurve`) or loaded with `AgeCurve.FromJson`. Curves are validated:
bands must start at age 0, be contiguous, end in one open-ended band and have positive factors,
and the factors for ages 21 and over may range at most 3:1 (45 CFR 147.102(a)(1)(iii)).

### Composite (45 CFR 147.102(c)(3))

A composite rate is derived once per plan year from the group's census with
`PremiumRatingEngine.DeriveCompositeRate(ageBandTable, census, basis)` and stored on a
composite table, where it stays fixed for the year:

- `PerMember`: total age-rated premium ÷ number of rated members. A household pays
  rate × rated members, with the three-child cap applied.
- `TierFactors`: total age-rated premium ÷ the sum of the households' tier factors. A household
  pays rate × its tier factor.

Tobacco is excluded when the rate is derived and added for each tobacco user, on that
member's age-rated premium (see below).

## Tobacco surcharge

`TobaccoSurcharge.Factor` is between 1.0 and 1.5 (the ACA maximum). It applies only to
members at or above `MinimumAge`, which defaults to 21, the federal tobacco age.

- **Age band:** the member's premium × (Factor − 1).
- **Composite:** the tobacco user's age-rated premium (the table's `AgeBandBaseRate`
  × their age factor) × (Factor − 1), added to the composite premium. Composite
  premiums are built from per-member rating (45 CFR 147.102(c)(3)), and the tobacco
  factor applies to the individual's rate (147.102(a)(1)(iv)). A composite table with
  tobacco therefore needs `AgeBandBaseRate`.
- **Tier:** `FlatMonthlyAmount` per tobacco user, at most half the EE rate (the 1.5:1
  limit, checked when the table is validated and again at rating time). Without it,
  (Factor − 1) × the EE rate.
- A flat amount is refused on the ACA methods (age band, composite).

In the small-group market, a tobacco surcharge is allowed only if the employer offers
a wellness program that lets tobacco users avoid it (45 CFR 147.102(a)(1)(iv) and the wellness-program rules in 45 CFR 146.121). The
engine does not model that program or its waivers. Turn the surcharge on only where
that program exists, and handle waivers by marking the member as not rated for tobacco.

## Invoice calculation

`PremiumInvoiceCalculator.Calculate` takes the billing month, the billing date, the
group's coverage as recorded on the billing date (including coverage terminated since
the last invoice), and a `BilledLedger` built from the group's earlier invoices
(`BilledLedger.FromInvoices`; voided invoices count for nothing).

1. **Current month.** Each coverage active on any day of the month gets one line.
   The month is split wherever the household or the rate table changes: a coverage or
   dependent starting or ending mid-month, or a new rate period. Each piece is rated with
   only the members covered then and prorated by day (premium × days ÷ days in month).
2. **Retro.** Each earlier month within `MaxRetroMonths` (default 3) that has an invoice
   is reconciled one coverage at a time. If the correct charge differs from what was billed
   (line items plus the calculator's earlier retro adjustments for that month), the
   difference becomes an `InvoiceAdjustment` with `CoverageId`, `ServicePeriodStart`/`End`
   and `IsRatingRetro`, so a later invoice does not bill it again. Manual adjustments
   (no `IsRatingRetro`) are never folded in or reversed. Months are re-rated with the
   members covered in that month, so a dependent added later does not change them. The adjustment type is:
   - `RetroAdd` when nothing was billed for the month;
   - `RetroTerm` when nothing is due, or when coverage ended inside the month;
   - otherwise `RateChange`, for example after a tier change.
   Months that have no invoice are not reconciled; their own billing run bills them.
3. **Missing rate table.** A coverage-month with no rate table in force is left off the
   invoice and listed in `InvoiceCalculation.Issues`. The rest of the group's invoice
   is still produced.

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
