# Medicaid APR-DRG pricing (Texas and Medi-Cal)

Code: `src/engines/CloudHealthOffice.FeeScheduleEngine/Medicaid/`.

Both states pay an inpatient stay as a base rate times the APR-DRG relative
weight for its severity of illness (the "DRG-SOI", e.g. `139-3`), adjusted
for transfers, plus an outlier. The details differ enough that each state has
its own pricer, `TexasMedicaidDrgPricer` and `MediCalDrgPricer`, which share
input shapes (`MedicaidDrgStay`, `MedicaidDrgWeight`, `MedicaidDrgHospital`).

Every number a state publishes is data, not code: weights, base rates, CCRs,
thresholds and adjustors. The pricers are pure functions. Each result lists
the steps of its arithmetic in plain words for the examiner and for appeals.
When an input is missing, the stay comes back unpriced with a reason, so
adjudication pends it. It is never paid at $0 or at charges.

## Status

These are the formulas only. Still to come:

- importers for the state weight tables and hospital files;
- wiring the formulas into `PricingStage`.

Until both land, no claim is priced by these formulas.

## Texas (1 TAC §355.8052(i))

| Step | Rule |
| --- | --- |
| DRG payment | Hospital final SDA (add-ons included) × relative weight |
| Transfer (transferring hospital) | Per diem (weight × SDA ÷ MLOS) × lesser of MLOS and allowed days; for patients 21 or older, also capped at 30 days |
| Outliers | Only for clients under 21. Both outliers are computed and the higher positive one is paid. Urban and rural hospitals get 90%, children's hospitals 100% |
| Day outlier | Applies when days exceed MLOS + 2 and exceed the day outlier threshold. Pays the lesser of (days over threshold × 60% × DRG per diem) and (charges × interim rate − DRG payment) |
| Cost outlier | Threshold = greater of (11.14 × lesser of universal mean and SDA) and (1.5 × DRG payment). Pays 60% of (charges × interim rate − threshold) |
| Charge cap | None. The prospective payment is full payment |

Inputs:

- **Per DRG-SOI:** relative weight, MLOS and day outlier threshold.
- **Per hospital:** final SDA, interim rate and class (urban, rural or children's).
- **State-wide:** the universal mean for the rate year.

To verify with a plan or HHSC:

- **Transfer discharge statuses.** The rule doesn't list codes, so the default is Medicare's acute-transfer list (02, 05, 66, 82, 85, 94).
- **Cost outlier wording.** The rule says the amount above the threshold is "multiplied by 60%". That is implemented as cost minus threshold.
- **Downgraded DRGs.** Where a DRG was downgraded for a preventable adverse event, the rule pays the lesser of the original and downgraded outliers. This is not implemented yet.
- **MCO contracts.** These set what an MCO actually pays. The rural hospital minimum fee schedule is one example.

## Medi-Cal (State Plan Attachment 4.19-A, DRG payment method)

| Step | Rule |
| --- | --- |
| Base price | Statewide or remote rural base price × (labor share × wage index + (1 − labor share)). A hospital-specific transitional base price is not wage adjusted |
| Payment weight | Relative weight × policy adjustor (by care category, SOI and NICU designation) × age adjustor (care categories under 21) |
| DRG payment | Adjusted base price × payment weight |
| Transfer (acute) | Per diem (DRG payment ÷ ALOS) × (days + 1), capped at the DRG payment. Statuses are Medicare's acute list plus 65 |
| Cost outlier | Estimated cost = charges × interim CCR. Loss = cost − DRG payment. Loss above each tier's threshold is paid at that tier's marginal percent |
| Low-side outlier | Optional and off by default |
| Charge cap | Payment cannot exceed charges |

SFY 2026-27 values from the DHCS provider training:

- APR-DRG V43;
- statewide base price $9,584 and remote rural $27,160, both subject to federal approval;
- outlier threshold $99,000;
- neonate adjustors 1.85 at designated NICU hospitals and 1.35 at others, and normal newborn 1.15.

The training says the marginal cost percentage is unchanged but doesn't give
its value. Load it from the DRG pricing calculator rather than assuming 60%.

To verify:

- **Adjustor table and wage indexes.** Load the full adjustor table and the wage index from the DRG pricing calculator and the Hospital Characteristics File.
- **5% cap on wage index reductions.** The State Plan caps wage index reductions at five percent, so the published index is expected to already include that cap.
- **Interim claims and administrative days.** Interim claims (stays over 29 days), administrative days and rehabilitation per diems are separate methods and are not covered here.

## Sources

- 1 TAC §355.8052, Inpatient Hospital Reimbursement.
- Medi-Cal State Plan Amendment CA-25-0022, Attachment 4.19-A.
- DHCS, *SFY 2026-27 DRG provider training*.
- DHCS, *California APR-DRG Payment Design Document* (2013). Its worked example (194-1, 0.4968 × $7,200 = $3,577) is a test case.
