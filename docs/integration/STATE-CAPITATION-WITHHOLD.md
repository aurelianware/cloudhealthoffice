# State capitation withholds

Some state Medicaid contracts hold back part of each monthly capitation payment and release it later
against performance or deliverables. The 2025 Medi-Cal Dental Managed Care boilerplate contract
(Exhibit B, Payment Withhold), for example, withholds 6% of the monthly capitation: 3% for performance
measures and 3% for timely deliverables.

Without the withhold, CHO's expected-versus-received capitation reconciliation would show every
member-month as underpaid by the withheld share.

## Loading it

Add a `withhold_percent` column to the state rates CSV (`POST api/v1/state-capitation/{state}/rates`).
It is the percent of the PMPM withheld, from 0 up to (not including) 100. Leave it blank, or leave the
column out, for no withhold.

```csv
rate_cell,effective_from,effective_to,pmpm,withhold_percent
CHILD-SIS,2025-07-01,2025-12-31,20.00,6
```

## What the reconciliation does with it

* **Expected** for a member-month is the rate (prorated if the profile says so) less the withhold:
  what the state should pay now.
* **Withheld** is the held-back amount, per member-month, per rate cell and month in the summary, and
  in total (`totalWithheld`). Finance books it as a receivable until the state releases it.
* `reconciliation.csv` has a `withheld` column.

This assumes the state pays each member line net of the withhold. If a state instead pays the full
rate per member and takes the withhold as a separate adjustment, leave `withhold_percent` at 0. The
adjustment then shows on the 820, not on the member lines.
