# Money rounding: what changes (Phase 6)

> **Owner sign-off required before merge.** This change moves paid amounts by
> a cent in some cases. It must not be merged until the owner has reviewed the
> list below and signed off on it.
>
> Sign-off: ______________________  Date: ____________

Commits `7b09910e` (`MoneyRounding.ToCents`) and `2b9c0d74` (every money
rounding calls it).

## The rule

`MoneyRounding.ToCents(x)` is `decimal.Round(x, 2, MidpointRounding.AwayFromZero)`.

Before, most of the sites listed below used `Math.Round(x, 2)`. Its default
is banker's rounding (`MidpointRounding.ToEven`). The two rules give different
answers only when the unrounded amount is **exactly** half a cent (`x.xx5`
with nothing after it) **and** the cent below is even. The difference is then
one cent, away from zero:

| Unrounded | Old (`Math.Round`, to even) | New (`ToCents`) |
|---|---|---|
| 10.125 | 10.12 | 10.13 |
| 10.135 | 10.14 | 10.14 |
| -0.125 | -0.12 | -0.13 |
| 10.1251 | 10.13 | 10.13 |

No calculation used to be unrounded and is now rounded. Every changed site
already rounded to two decimals; only the midpoint rule changed.

## Calculations whose results change

Each example is taken from a test added in `2b9c0d74` that fails under the old
rounding.

| Where | Calculation | Example: old → new |
|---|---|---|
| `BenefitCalculationEngine` (all three copay modes) | coinsurance = remaining allowed × coinsurance % | 15% of $100.30 = $15.045: $15.04 → $15.05 (plan paid $85.26 → $85.25) |
| `RateResolutionService` | final line allowed (rate × units after modifier adjustments) | $20.25 × 0.5 units = $10.125: $10.12 → $10.13 |
| `RateResolutionService` | DRG case rate = base rate × weight | $6,543.23 × 1.5 = $9,814.845: $9,814.84 → $9,814.85 |
| `RateResolutionService` | percent-of-Medicare line = Medicare rate × multiplier | same midpoint rule |
| `RateResolutionService` | RVU amount = total RVU × conversion factor | 1.00 × 10.125: $10.12 → $10.13 |
| `RateResolutionService` | Medicaid: inline RVU × percent, Medicare MPFS rate × percent, stored-rate fallback × percent | 70% of $50.35 = $35.245: $35.24 → $35.25 |
| `RateResolutionService` | multiple-procedure reduction, 50% of a rank-2+ line | 50% of $100.01 = $50.005: $50.00 → $50.01 |
| `RateResolutionService` | billed charges used for lesser-of and per-stay allocation | changes only for a billed amount with sub-cent precision |
| `FeeScheduleLoaderService` (Pricing API seed) | RBRVS facility / non-facility rate = total RVU × conversion factor; DRG rate = weight × base rate | (1.00 + 0.25) × 10.10 = $12.625: $12.62 → $12.63 |
| `CapitationRunService` | risk-adjusted PMPM = base PMPM × risk score; gross = adjusted PMPM × proration; withhold = gross × withhold % | $50.15 × 1.5 = $75.225: $75.22 → $75.23; 10% of $100.25: $10.02 → $10.03 (net $90.23 → $90.22) |
| `MpipAdjudicationEnhancer` | enhanced allowed = allowed × MPIP multiplier | $15.00 × 1.063 = $15.945: $15.94 → $15.95. **No runtime effect today**: claims-service registers the enhancer but nothing calls it. |
| `PremiumBillingService` (legacy line build) | subscriber premium, employer contribution (each × proration) and total | $100.125: $100.12 → $100.13; $50.005: $50.00 → $50.01 |
| `ExpectedMoney` (benchmark generator, MCC answer key) | expected line allowed, line and claim coinsurance, line paid, claim paid and member liability | same rule as the engines |

## Sites that call the helper with no change

These already rounded half away from zero, or only test whether an amount is
in whole cents (where the midpoint rule cannot matter):

- `AccumulatorCents` (benefit engine)
- `EditDispositionApplier` (examiner partial-pay and denied charge)
- `ExplanationOfBenefitProjector` (EOB unit price)
- `Edi820Parser`, `LockboxCsvParser`
- `PremiumRatingEngine.RoundMoney`
- `GlEntryBuilder`, `GlPostingService` (ar-service whole-cent checks)
- `RatedInvoiceGenerator` (whole-cent check)

Not converted, and still `Math.Round(x, 2)`: synthetic seed values in the
benchmark generator (`SyntheticAccumulatorGenerator` random accumulator
balances, `SyntheticFeeScheduleGenerator` 150% commercial rates) and the
work-routing score in work-management-service. None is a computed payment.

## What happens to stored amounts

Nothing is recalculated. Claims, capitation runs, premium lines and seeded
fee schedule entries already stored keep their old cents. A claim
re-adjudicated after deploy can differ from its first adjudication by a cent
on the lines above. Seeded Pricing API rates change only when they are seeded
again.

## Million Claim Challenge figures

### Answer key (computed)

Generated the full default corpus (`DefaultCorpusProfile`, 1,000,000 claims,
seed 42, reference date 2026-10-01) twice: once with the generator source at
`2b9c0d74^` (old rounding) and once at this branch (`ExpectedMoney`). The
generator code differs between the two only in the rounding calls. Every
claim's expected allowed, paid, member liability, copay, coinsurance and
deductible, and every line's expected allowed and paid (2,649,903 lines),
were compared.

**Result: no figure changes.** 0 of 1,000,000 claims and 0 of 2,649,903 lines
differ. Seeds 1 and 7 (reference date 2025-06-15) also produce identical
answer keys.

| Answer-key total (seed 42) | Old | New |
|---|---|---|
| Expected allowed | $6,265,317,030.90 | $6,265,317,030.90 |
| Expected paid | $4,900,061,157.24 | $4,900,061,157.24 |
| Expected member liability | $1,355,913,087.54 | $1,355,913,087.54 |
| Expected coinsurance | $1,253,970,662.54 | $1,253,970,662.54 |
| Expected copay | $67,109,175.00 | $67,109,175.00 |
| Expected deductible | $34,833,250.00 | $34,833,250.00 |

Why nothing moves: every generated charge and rate is a whole-dollar amount,
the generator's allowed ratios are 0.50 / 0.60 / 0.65 / 0.70, and its
coinsurance rates are applied to whole-cent amounts. None of these products
lands on an exact half cent.

### Engine side (derived, not run)

The platform's payments on the MCC corpus were **not** re-run end to end.
What was checked:

- The plan-default fee schedule (`GeneratePlanDefaultSchedule`, 128 lines) and
  the revenue-code rates are all whole dollars (computed). Rate × whole units
  cannot be a half cent; neither can the 50% multiple-procedure reduction of
  a whole-dollar rate (16 lines carry indicator 2, 0 midpoints, computed).
- The validator's plan uses 20% in-network coinsurance. 20% of a whole-cent
  amount is a multiple of $0.002, never an exact half cent, so the benefit
  engine's coinsurance cannot change on it (arithmetic).
- MPIP enhancement is not called (see above).

So the MCC payment scores should be unchanged. This is an inference from the
seeded data, not a measured run. A provider contract, fee schedule or plan
with cents in its rates, a percentage that is not a multiple of 5%, or a
fractional unit count would produce half cents, and those claims can move by
one cent per rounded amount. The MCC payment gate's tolerance is $0.01
(`MccRunSummaryBuilder.PaymentTolerance`). A claim with several changed lines
could move by more than that.

### How this was computed

The comparison harness lives outside the repository. It compiles the
generator source from each commit (`git archive 2b9c0d74^` and the branch
head) with a writer that dumps every claim's expected outcome, sorted by
claim id, and diffs the two dumps. Running the same commit twice gives
byte-identical output.

## Merging this branch

This branch must be **squash-merged**.

The portal project does not compile from `5f1f8f40` ("refactor(portal): one
file per service type") through `2b9c0d74`, 18 commits. `5f1f8f40` added one
file per service type (299 files) but kept `Services/IServices.cs` and
`Services/ServiceImplementations.cs`, so every type is defined twice. Building
only the portal project gives 872 errors at `5f1f8f40` and 871 at `2b9c0d74`
(the commits in between were not built one by one; none of them touches the
two combined files)
(CS0101 duplicate type, CS0111 duplicate member, CS0579 duplicate attribute,
CS8863). `f4c52cf8` deletes the two combined files, carries main's Insights and
access-log additions into the split files, and builds with 0 errors.

History is not rewritten. Merged commit by commit, those 18 commits would land
on `main` unbuildable, which breaks bisecting.

Folding `f4c52cf8` into `5f1f8f40` would also fix this: no commit in between
touches the two combined files or any file `f4c52cf8` changes, and there are
no merges in the range, so the fixup should apply without conflicts. That has
not been tried; it would rewrite the branch and every branch stacked on it.
