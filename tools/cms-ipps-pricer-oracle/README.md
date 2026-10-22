# CMS IPPS payment parity oracle

Runs CMS's own IPPS pricer over a reference corpus and writes down what it paid,
so CHO's payment calculator can be held to the same amounts.

This is the parity run ADR 024 step 5 was shipped without. The calculator was
written from 42 CFR with the paragraph cited at each line, and that produced
something careful — but reading a formula correctly and implementing it the way
CMS's program does turn out to be different things, and only the second gets a
payment right. **The first run of this oracle reversed one of the three findings
the calculator shipped with.** See "What the first run corrected" below.

## There is a downloadable pricer, despite appearances

CMS retired the downloadable PC Pricer in favour of the browser-based Web
Pricer, which has no bulk interface and would make a poor oracle. It still
publishes the pricer itself. From
<https://www.cms.gov/pricersourcecodesoftware>:

| File | What it is |
| --- | --- |
| `ipps-pricer-2027-0-v2-14-1-executable-jar.zip` | the pricer, runnable |
| `ipps-pricer-2027-0-v2-14-1-java-source-code.zip` | its source, **with the original COBOL quoted as comments above each rule** |

CMS says it posts these "to fulfill Freedom of Information Act (FOIA) requests,
and facilitate Medicare pricing methodology and rate transparency", attaches no
licence, and states that it "does not provide technical support to incorporate
Pricer source code and software into new or existing systems outside of its
Original Medicare claims processing system". So this directory holds only the
questions and the answers; nothing from the CMS package is committed, and
nothing here works without it.

The source archive earns its place separately. When the oracle and the
calculator disagree, the COBOL comment above the rule says what CMS's program
does, which is a better answer than re-reading the regulation and guessing
again.

## Running it

The pricer needs a Java 17 or later runtime. It is a local HTTP service and
nothing leaves the machine.

```bash
CMS_IPPS_PRICER_JAR=/path/to/ipps-pricer-application-2.14.1.jar ./run.sh
```

That starts the pricer, waits for it, posts every stay in
`corpus/reference-claims.json`, writes `corpus/expected-fy2026.psv`, and stops
it again. `CMS_IPPS_PRICER_PORT` moves it off 8089.

`corpus/expected-fy2026.psv` is committed, and
`CloudHealthOffice.GrouperEngine.Tests/IppsPricerParityTests` reads it and
compares CHO's calculator against it — which is how the comparison runs on every
build with no CMS artifact present.

The pricer's API is worth knowing beyond this tool. `GET /v1/drg-entries/{year}`
gives every DRG's weight, geometric and arithmetic mean length of stay and
post-acute transfer flags; `GET /v1/cbsa-wage-indexes/{year}` gives the
geographic, reclassified and rural floor wage indexes; and there is a
`/v1/apc-rates/{year}` that will matter for ADR 024 step 6.

## No rate is in the code, including the test's

The output's columns include the rate figures: the standardized amount's labor
and non-labor split, the capital federal rate, the fixed loss amount, each DRG's
weight and geometric mean. That is deliberate. ADR 024 holds that no rate
belongs in CHO's code, and it would be a poor answer to that principle to
hard-code a rate into the test that checks it. So the parity test builds its
`IppsRateSet` out of what CMS reported for these stays, and a new fiscal year is
a re-run of this script and nothing else.

## Getting a claim to price at all

Five things stop a well-formed request before any arithmetic happens, none of
them documented, all of them found by trial. They are encoded in
`corpus/reference-claims.json` under `providerDefaults`, but they are worth
knowing when adding a row:

1. **An empty array is rejected.** `diagnosisCodes`, `procedureCodes`,
   `conditionCodes`, `demoCodes` and `nationalDrugCodes` must be *absent*
   rather than `[]`. `countyCode` must be five digits.
2. **The provider's `effectiveDate` picks the wage-index table vintage**, not
   the discharge date. The lookup is `floorEntry(effectiveDate)` over the
   fiscal year's table, so a provider effective in 2020 priced for a 2026
   discharge gets return code 52, "invalid wage index", because the only row in
   the table is dated 1 October 2025.
3. **`capitalPpsPaymentCode` must be `B` or `C`** — or `newHospital` must be
   `"Y"` — notwithstanding that the return code for getting it wrong is named
   "PAY CODE NOT ABC". `A` is rejected.
4. **`hrrParticipantIndicator` must be set.** Blank gives the same return code
   65. `"0"` with a zero `hrrAdjustment` is the no-readmissions-programme case.
5. **`vbpParticipantIndicator` must be `"N"`**, or `"Y"` with
   `hospitalQualityIndicator` of `"1"`. Anything else is return code 68.

A minimally valid stay then prices and returns code 14, "paid DRG with
perdiem".

## The corpus

Nineteen invented stays. There is no member, no claim number and no real
provider: the CMS certification numbers are fabricated, chosen only so their
first two digits are a plausible state code, because the pricer reads the state
from them. The wage-index localities are real published CBSA codes, which is
public reference data and the only way to exercise the real wage adjustment.

Each row carries a `why` saying what it pins. A row that pins nothing does not
belong: this is for distinguishing a correct calculator from a plausible wrong
one, not for covering lines.

The output goes well beyond the total on purpose. CMS returns every
intermediate — which labor split it chose, the base DRG payment, each add-on,
the discharge fraction, both sides of the capital calculation, the cost
threshold — and a harness comparing only totals would call agreement on a
calculator that was wrong in two directions at once.

## What the first run corrected

Three things, all in code that had already been reviewed and merged.

### The labor share is a threshold after all, not a comparison

42 CFR 412.64(h)(3) sets the labor-related share at 62 percent "unless employing
that percentage would result in lower payments for the hospital" than the share
in the annual rule. That is written as a comparison, and CHO implemented it as
one. The two readings agree wherever the cost-of-living adjustment is 1.0 and
come apart in Alaska and Hawaii, where 412.64(m)(1)(ii) lifts the non-labor
share.

**CMS's program tests the wage index against 1.0 and nothing else.** Its
`CalculateNationalPercentages` rule takes the larger share and then replaces it
with the statutory one `IF (H-WAGE-INDEX < 01.0000 OR H-WAGE-INDEX = 01.0000)`.
On this corpus's Alaska stay — wage index 1.1438, cost-of-living adjustment
1.25 — CMS pays 15,368.43 on the larger share where the comparison reading would
have paid 15,424 on the smaller one. CHO was overpaying every hospital in those
two states, on the strength of a correct reading of the regulation.

### The fixed loss amount is apportioned per side, not applied whole or combined

This took three attempts. The first draft tested operating and capital each
against the whole fixed loss amount, which counts it twice and pays outliers on
ordinary cases. The correction added the two costs and tested them against one
threshold. CMS does neither: it apportions the single amount between the two
sides **in proportion to the hospital's two cost-to-charge ratios**, so the
shares sum to one, and then adjusts each side by its own geographic factor — the
labor split and wage index for operating, the capital geographic adjustment
factor for capital. Those factors differ for almost every hospital, so the
apportionment is not cosmetic.

CHO's capital outlier now reproduces CMS's exactly on the corpus's outlier stay,
and its operating outlier to within rounding.

### A same-day transfer is paid two per diems, not one

412.4(f)(1) pays twice the per diem for the first day and one for each later
day, which is the length of stay plus one. CMS floors the length of stay at one
day **before** adding it, so a zero-day transfer is paid two per diems. CHO paid
one, which was half the right amount.

The same rule has a consequence worth stating: because every stay is paid at
least two per diems, **no transfer reduction is possible at all in a DRG whose
geometric mean is under two days**. The corpus's transfer rows were originally
written against DRG 470, whose geometric mean is 1.9 days, and every one of them
came back paying in full and pinning nothing.

### And it settled the line that was openly ambiguous

412.4(f)(2) pays "50 percent of the amount calculated under (f)(1) for each day
of the stay", which admits a reading where the halving is per day. CMS computes
`0.5 + ((0.5 × per diem days) / mean)`, which is the whole-amount reading CHO
had implemented. That one was right, and is no longer a guess.

## What is still not compared

`IppsPricerParityTests` names these rather than skipping them quietly, and
asserts that each exclusion is still a real corpus row:

- **`disproportionate-share`** — CMS derives the adjustment from the hospital's
  SSI and Medicaid day ratios; `IppsProvider` takes the resulting factor as an
  input, so there is nothing to compare.
- **`transfer-fifty-fifty-special-payment-drg`** — CMS refused it with return
  code 12, and which post-acute rule a DRG carries is a column of CMS's DRG
  table that nothing here imports yet.

And the pricer pays several things the calculator still has no notion of: the
value-based purchasing and readmissions-reduction adjustments, the low-volume
adjustment, the hospital-acquired-condition penalty, the hospital-specific
portion, pass-through amounts and new-technology add-ons. It also works in
covered days and lifetime reserve days, where `IppsStay` has no beneficiary-day
concept. Those are recorded in ADR 024 as the remaining work, and they are why
this calculator is still not wired into adjudication.

## What the second run added: the rate table

The first run found the hospital quality indicator moving the standardized
amount itself, which `IppsRateSet` could not express with one amount. Reading
CMS's source for how showed the shape was bigger than a flag.

**CMS holds eight operating rate tables and picks one on three facts:** whether
the hospital met the inpatient quality reporting requirement, whether it is
subject to the electronic-health-record reduction, and whether its wage index
is above 1.0. `2050-RATES-TB` in the COBOL is a straight eight-way branch on
exactly those. The eight rows are four standardized amounts — one per
combination of the two reductions — each split two ways by the wage-index rule.

Two consequences that a reduction factor would have got wrong:

- **The reductions are independent and a hospital can carry both**, and the
  both-reductions amount is published rather than being the two reductions
  applied in turn.
- **The rows hold dollars, not a percentage of one amount.** The
  record-reduced row above a wage index of 1.0 is 4,349.21 labor and 2,240.51
  non-labor. They sum to 6,589.72 exactly, but 6,589.72 × 0.66 is 4,349.2152
  and × 0.34 is 2,240.5048, so neither amount is the sum times a share. CMS
  rounds them independently and deriving either moves the answer by a cent on
  every claim in that row.

So the labor percentages stayed, with one job: CMS's outlier threshold is the
cost threshold times the percentages, and the standardized amount plays no part
in it. A reader who finds the percentages multiplying a rate should suspect the
rate.

The pricer has no endpoint for its rate table, so the only way to learn a row
is to price a claim that selects it. Five corpus rows were added for that, and
the parity test now builds the whole eight-row table out of CMS's answers and
asserts CHO paid from the same row — which also exercises CMS's own selection
branch, where reading the file would not have.
