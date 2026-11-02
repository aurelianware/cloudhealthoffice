# Psychiatric hospital per diem (Texas Medicaid managed care)

Texas pays freestanding psychiatric hospitals a daily rate per hospital. HHSC Provider Finance publishes the rates each state fiscal year as a PDF, `{yyyy}-ffy-psychiatric-hosp-per-diem-rates.pdf` ("SFY 2027 Psychiatric Hospital Rates"). The file has two sections: private freestanding facilities and the HHSC state hospitals.

## Status

| Piece | Where | State |
|---|---|---|
| Pricer | `PsychiatricPerDiemPricer` (FeeScheduleEngine, `Medicaid/`) | Done |
| Rate store | `IMedicaidPsychiatricPerDiemRepository`, collection `MedicaidPsychiatricPerDiems` | Done |
| HHSC PDF import | `POST /api/v1/medicaid-drg/import/texas/psychiatric-per-diems` | Done |
| Claim pricing | `MedicaidDrgRateResolutionService` (the Texas Medicaid inpatient decorator) | Done |

## How a claim is priced

A claim reaches this pricer through the Texas inpatient path: the plan sets `pricing.inpatient.medicaidDrgMethod` to `texas`, and the stay is grouped by APR-DRG as usual (see [MEDICAID-DRG-PRICING.md](MEDICAID-DRG-PRICING.md)). Before pricing by DRG, the decorator looks up the facility NPI in the psychiatric per diem store on the admission date. If the hospital has a per diem, the stay is priced by it instead of the DRG; otherwise it is priced by DRG as before. Load the per diems before turning on the Texas method for a plan that uses psychiatric hospitals, or their stays will be priced by DRG.

- Each line's days are its units, starting on the line's service date.
- A line that crosses a new fiscal year's rate pays each day at the rate in force that day.
- Cost sharing applies once to the stay, as for any inpatient per diem.
- A stay that can't be priced pends the whole claim (`Unresolved`), with the reason.


- **Days:** each accommodation line (revenue codes 0100-0219) pays days × the hospital's per diem in force on the line's dates.
- **Ancillaries:** lines such as pharmacy or lab are allowed at $0, because the per diem covers the hospital's services for the day (`AncillariesIncludedInPerDiem`). Turn this off and an ancillary line leaves the claim unpriced.
- **Leave days** (0180-0189) pend: no leave rule is configured.
- **No rate** for the hospital and dates: the claim pends.

### The adult IMD limit

These hospitals are institutions for mental diseases (IMDs). Medicaid covers an IMD stay for clients under 21 and 65 or older. For adults 21 to 64, a managed care plan may cover it "in lieu of" other services for at most 15 days in a calendar month (42 CFR 438.6(e)). A longer stay costs the state federal match on that month's capitation.

- The limit applies by age at admission, default 21 to 64 (`ImdLimitMinimumAge`, `ImdLimitMaximumAge`).
- Days are counted per calendar month. Days already paid on the member's earlier claims are passed in (`ImdDaysAlreadyPaid`), and each priced claim returns the days it adds (`ImdDaysByMonth`).
- Over the limit, the claim pends by default (`OverLimitAction = Pend`). Whether the plan still pays the hospital is the plan's decision, so the plan can set `Pay` to pay with a step noting the limit.
- An unknown age pends, because the limit can't be checked.
- The limit is set by `ImdMaxDaysPerMonth` (default 15). Null turns it off.

## Importing HHSC's file

```
POST /api/v1/medicaid-drg/import/texas/psychiatric-per-diems?dryRun=true&replace=false
Content-Type: multipart/form-data   file=<HHSC psychiatric per diem PDF>
```

- One row per TPI: TPI, NPI, provider, address, city, ZIP, county and per diem.
- Each rate loads effective on its section's date: "Effective on or before 09/01/2026" for private facilities and "Effective 09/01/2026" for state hospitals.
- One NPI can bill under several TPIs. If they carry the same per diem, one rate is stored. If they differ, the NPI is held back and listed in `heldBack`, because claims find the hospital by NPI.
- A row that can't be read refuses the whole file. Dry run by default. Writing needs `settings:manage`.

Results on the files Mark uploaded:

| File | Rows | Rates | Held back |
|---|---|---|---|
| SFY 2026 (effective 09/01/2025) | 78 | 77 (66 private, 11 state) | 0 |
| SFY 2027 (effective 09/01/2026) | 74 | 74 (63 private, 11 state) | 0 |

## Not covered yet

- **Psychiatric units of general hospitals.** These are paid by APR-DRG like other acute stays (see [MEDICAID-DRG-PRICING.md](MEDICAID-DRG-PRICING.md)), not by this per diem.
- **IMD days on the member's other claims** are not yet passed in from claims history, so the monthly limit counts only this claim's days. The pricer accepts them (`ImdDaysAlreadyPaid`); feeding them from paid claims is a follow-up.
- **The pay-over-the-limit choice** (`OverLimitAction = Pay`) is not yet a plan setting; claims use the default (pend).
- **The under-22 continuation.** A client admitted before 21 keeps the under-21 benefit until 22. Using the age at admission covers this for a single stay; a readmission after 21 is checked as an adult.
- **Medi-Cal.** Psychiatric inpatient services are carved out to county mental health plans, so a Medi-Cal managed care plan does not price them.
