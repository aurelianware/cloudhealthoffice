# ACA Out-of-Pocket Limits

This directory holds the file-backed seed values consumed by
`IAcaLimitsProvider` in `benefit-plan-service`. The provider validates
plan-author-supplied `IndividualOutOfPocketMax` and
`FamilyOutOfPocketMax` against the values here at write time and
projects the per-member ACA individual cap onto Aggregate-mode plans
for runtime enforcement in the BenefitEngine.

## Regulatory anchor

ACA 45 CFR §156.130 sets annual maximum out-of-pocket cost-sharing
amounts that apply to all non-grandfathered group and individual market
plans. The ceiling is published by HHS / CMS each spring in the Notice
of Benefit and Payment Parameters (NBPP) for the following plan year.

The cap exists for **every** plan, including Aggregate-family plans
that pool all members under a single family OOP. Without per-member
runtime enforcement on Aggregate plans, a single member could absorb
the entire family pool — which violates §156.130. See
`docs/architecture/family-accumulator-models.md`.

## Source attribution per plan year

| Plan year | Individual | Family   | Source                                            |
|-----------|-----------:|---------:|---------------------------------------------------|
| 2024      | $9,450     | $18,900  | CMS-9911-F (NBPP 2024)                            |
| 2025      | $9,200     | $18,400  | CMS-9895-F (NBPP 2025)                            |
| 2026      | $10,600    | $21,200  | CMS-9888-F (NBPP 2026) — revised methodology      |
| 2027      | $12,000    | $24,000  | Projected from revised methodology — verify!      |

### 2026 methodology change

The original NBPP 2026 finalization (released April 2024) set the caps
at **$10,150 individual / $20,300 family**. A revised premium
adjustment percentage methodology, finalized in 2025, recalculated the
2026 ceiling upward to **$10,600 / $21,200**. The values in
`limits.json` reflect the revised, currently-effective figures.

If a stakeholder asks "where do these numbers come from?", point them
at the 2026 final rule and the revised methodology rule both. Earlier
documentation that cites 10,150 / 20,300 is referencing the superseded
finalization.

### 2027 caveat

The 2027 row in `limits.json` is a **projection** off the revised
methodology, NOT a published final rule. CMS typically publishes the
final 2027 NBPP in mid-2026. When that rule lands:

1. Update `limits.json` with the published values.
2. Remove the `note` field on the 2027 row.
3. Update the table above and the `lastReviewed` date.
4. Bump `version` if the schema changed; otherwise leave it.

Plans authored against plan year 2027 today will validate against the
projected values. If the published values are lower than projected,
some plans authored under the projection will need to be re-validated
when their plan-year resolution falls in 2027.

## Update cadence

- Watch the CMS NBPP final-rule release (typically January–April of
  each year, for the following plan year).
- File a PR updating `limits.json` plus a one-line addition to the
  table in this README.
- Bump `lastReviewed`. Leave `version` unless the schema changes.

## Important: ACA cost-sharing max ≠ HSA-qualified HDHP max

The IRS publishes a separate set of out-of-pocket maximums for
HSA-qualified High Deductible Health Plans (Internal Revenue Code
§223). The HDHP-OOP-max is generally **lower** than the ACA-OOP-max
and updates on a different cadence (Revenue Procedure each May for
the following calendar year).

This file is for the **ACA** ceiling only. Plans that are HSA-qualified
HDHPs must satisfy BOTH ceilings; the lower of the two governs. HDHP
caps are not enforced from this file — they are validated separately
against IRS Publication 969 / Rev. Proc. parameters.

If you are looking for the HDHP max, you want the IRS source, not this
one.

## File shape

```jsonc
{
  "version": "1.0",                      // schema version, bump on shape change
  "source": "...",                       // free-form provenance
  "lastReviewed": "YYYY-MM-DD",          // when these values were last verified
  "limits": [
    {
      "planYear": 2025,                  // calendar year of the plan year
      "individualCap": 9200,             // §156.130 self-only OOP max
      "familyCap": 18400,                // §156.130 other-than-self-only OOP max
      "rule": "CMS-9895-F (NBPP 2025)",  // citation for the value
      "note": "..."                      // optional caveats
    }
  ]
}
```

The provider treats unknown fields tolerantly so future additions
(e.g. catastrophic-plan caps, marketplace-specific subsidies) don't
require lockstep code changes.

## Loading behaviour

- The provider loads `limits.json` once at service startup.
- Plans with a plan year NOT present in `limits` cause the validator
  to **fail-closed** — write rejected with a structured 400. Better
  to force operators to publish a fresh `limits.json` than silently
  accept a plan against stale or missing caps.
- The lookup is keyed strictly on integer `planYear`; no fallback to
  "nearest" or "latest" by design. Regulatory caps are point-in-time
  values; using last year's number for next year is a compliance bug.
