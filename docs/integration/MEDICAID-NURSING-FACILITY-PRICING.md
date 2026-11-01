# Nursing facility per diem net of patient liability (Medicaid managed care)

Code: `src/engines/CloudHealthOffice.FeeScheduleEngine/Medicaid/NursingFacilityPricer.cs`.

A Medicaid nursing facility stay pays a daily rate. The resident pays part
of their own monthly income toward it first. Texas calls this **applied
income** and Medi-Cal calls it **share of cost**. The facility collects it
from the resident, so the plan pays the month's per diem total less any
liability still unmet.

## How a claim is priced

1. **Gross.** Room and board days (revenue codes 0100–0179 and 0190–0199)
   × the facility's daily rate in force for the line's dates.
2. **Leave days.** Revenue codes 0180–0189 are paid only under a configured
   `NursingFacilityLeaveRule`, which sets the share of the rate and the
   maximum days per line. A leave line with no matching rule pends.
3. **Liability.** It is met once per month for each resident, not once per
   claim. The pricer takes:
   - the monthly liability;
   - the amount already applied that month by earlier claims.

   It applies what remains to the lines in date order and returns the amount
   it applied, so the next claim's running total stays right. Liability never
   makes a line negative.
4. **What pends.** All of these pend:
   - a month with no liability on file (enter 0 when the resident owes none);
   - a line that spans two months;
   - no rate covering a line's dates;
   - a non-accommodation line;
   - duplicate line numbers.

## Texas

STAR+PLUS MCOs pay the HHSC daily rate for the resident's classification,
less applied income. The classification is billed as a HIPPS code on revenue
code 0022. It was RUG-III, and Texas MCOs announced a move to PDPM-LTC in
2025. `RateKeyedByClassification` is on, so the claim must carry exactly one
classification. No leave rule is configured by default, so take one from the
plan's HHSC contract.

## Medi-Cal

Since the CalAIM LTC carve-in, plans pay SNFs. APL 26-010 extends the SNF
reimbursement provisions through 31 December 2026. The pricer pays the
facility's per diem less share of cost.

A bed hold for an acute hospital stay of up to 7 days (22 CCR §51535.1) is
configured on revenue code 0185 at the full per diem. Confirm that code and
rate against the plan's DHCS contract.

## Status

These are the pricing rules only. Still to come:

- a rate importer (TX HHSC rate tables, the Medi-Cal LTC rate file);
- a monthly liability feed (TX service authorization or eligibility, the
  Medi-Cal SOC on the eligibility record);
- wiring into `PricingStage`.

Until then, no claim is priced by this code.
