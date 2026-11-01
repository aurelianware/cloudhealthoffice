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

The formulas are in place, and the reader for the Texas SDA files is in place (see below). Still to come:

- a store and import endpoint for hospital rates, and the importer for the state weight tables;
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

- **Rural labor and delivery.** A rural hospital is paid its OB-GYN delivery SDA instead of its final SDA for labor and delivery stays (§355.8052(e)(2)). The rule names services, not DRGs. The default list is APR-DRGs 540, 541, 542 and 560 (`TexasDrgPolicy.DeliveryBaseDrgs`), which is a guess to confirm.

### Texas SDA files

HHSC Provider Finance publishes each fiscal year's SDAs as PDFs, one per hospital class (for example `2027-fy-hosp-urban-sda.pdf` and `2027-fy-hosp-rural-sda.pdf`). `TexasSdaFileReader` (benefit-plan-service, `Import/MedicaidDrg`) reads them as published:

- **Urban:** base SDA, supplemental funds base adjustment, then the wage index, IME, trauma and safety-net add-ons, giving the SDA with applicable add-ons.
- **Rural:** SDA, add-on, SDA with add-on, and the OB-GYN delivery SDA.
- **Title block:** the class, the state fiscal year, and the universal mean with its effective date. The universal mean feeds `TexasDrgPolicy.UniversalMean`.

Every row's arithmetic is checked: the parts must add up to the final SDA, and the delivery SDA can't be below it.

- A row that reads cleanly but contradicts itself is HHSC's error, not a misread. It is held back as a discrepancy, so that hospital's stays pend, and the rest of the file loads.
- A row that can't be read at all, or a layout with an unexpected number of dollar columns, refuses the whole file.
- Children's hospital files are refused until their layout has been seen.

The interim rate (needed only for outliers) is not in these files.

Results on the SFY 2027 files (effective September 1, 2026, universal mean $10,161.08):

- **Urban:** 298 hospitals, none held back.
- **Rural:** 138 hospitals loaded, 1 held back. HHSC prints Memorial Hospital of Polk County (TPI 112697106) with a $576.53 add-on, but its SDA with add-on ($12,435.40) equals the SDA alone. Its delivery SDA ($17,750.68) is consistent with no add-on. The plan should confirm the right SDA with HHSC.

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

## Outpatient hospital

### Texas (1 TAC §355.8061)

`TexasOutpatientHospitalPricer` implements both of Texas's methods. The rule
switches between them on an event, not a date: everything moves to EAPG
"once the modernized MMIS is operational". Confirm which method is in force
before choosing one. The method is chosen per plan.

- **`PercentOfChargesInterim`** is the rule's current text, §355.8061(b).
  - Payment is allowable charges × class percentage × the hospital's interim
    rate (its outpatient cost-to-charge ratio).
  - The class percentages are:

    | Hospital class | High volume | Other |
    | --- | --- | --- |
    | Children's and state-owned | 76.03% | 72.27% |
    | Rural | 100% | 100% |
    | All other | 72.00% | 68.44% |

  - A rural interim rate is capped at 100%.
  - Imaging, non-emergent ER and outpatient surgery (ASC method) are paid by
    fee schedule. The existing fee schedule engine handles them.
- **`Eapg`**: each payable or discounted line is paid EAPG weight × conversion
  factor × discount factor. Packaged, consolidated and not-payable lines are
  paid $0. The EAPG comes from the plan's licensed grouper (#1718).
- Since 1 September 2020, MCOs must pay rural hospitals at least the
  rural-specific rates (§355.8061(f)).

### Medi-Cal

Medi-Cal pays outpatient hospital services from the Medi-Cal fee schedule by
HCPCS/CPT, not EAPG or APC. That is already loadable with the Medi-Cal rates
importer (#1711), so nothing new is needed here.
