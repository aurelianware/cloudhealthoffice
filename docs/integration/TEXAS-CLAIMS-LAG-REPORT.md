# Texas STAR Claims Lag Report (UMCM 5.6.2.11)

HHSC asks STAR MCOs for a quarterly Claims Lag Report on its Excel template (UMCM 5.6.2.11). The
instructions are in 5.6.2.4. Each STAR risk group has its own section, with months of service down
and months of payment across. A later adjustment or recoupment goes in the column for the month it
happened, on the row for the claim's original month of service.

## Endpoints (claims-service)

Both endpoints need `finance:read`.

| Call | Returns |
| --- | --- |
| `POST api/claims/finance/texas/claims-lag` (JSON) | Payments by risk group, month of service and month of payment, plus what was left out and why |
| `POST api/claims/finance/texas/claims-lag/workbook` (multipart: `template` = HHSC's blank workbook, `request` = the JSON) | The filled workbook, named `STAR-Claims-Lag-SFY{year}-Q{n}.xlsx` |

The request fields:

```json
{
  "stateFiscalYear": 2027,
  "quarter": 1,
  "incurredFrom": "2026-09",
  "contractor": "Lone Star Health Plan",
  "serviceArea": "Bexar",
  "rateCellRiskGroups": {
    "STAR-BEXAR-TANF-ADULT": "TANF-ADULT",
    "STAR-BEXAR-PREG": "PREGNANT-WOMAN"
  }
}
```

* **`stateFiscalYear` and `quarter`:** payments made through the last month of the quarter are
  counted. Q1 ends in November, Q2 in February, Q3 in May and Q4 in August.
* **`incurredFrom`:** the first month of service included. It defaults to the start of the SFY and can
  reach back at most 48 months.
* **`contractor` and `serviceArea`:** required for the workbook only.
* **`rateCellRiskGroups`:** maps each rate cell to one of the eight groups. Use the code or the
  template's section title: `UNDER-1`, `AGE-1-5`, `AGE-6-14`, `AGE-15-18`, `AGE-19-20`, `AA-PCA`,
  `TANF-ADULT`, `PREGNANT-WOMAN`.

## Where the numbers come from

* **Payments:** plan-paid amounts on Medicaid claim versions. These are the same increments the IBNR
  lag triangle uses. An adjustment counts only the difference, and a void takes back everything paid
  so far. Both land in the month they were paid.
* **Risk group:** the member's rate cell for the month of service, taken from the expected
  member-months loaded for capitation (premium-billing `GET state-capitation/TX/expected.csv`), then
  mapped through `rateCellRiskGroups`.
* **One service area per report:** map only the rate cells for that service area. Claims for members
  in other rate cells, such as other programs or other service areas, are left out.
* **Left out and reported:** claims with no member, claims whose member has no expected member-month
  for the month of service, and claims whose rate cell is unmapped. They are listed under `excluded`,
  or in the `X-Report-Excluded` header for the workbook.

## The workbook

CHO does not hard-code row or column positions. It finds each section and month from the template's
own labels ("Month Incurred Sep 2026", "Month Paid Oct 2026"), because HHSC adds months every year.

The upload is refused (422) in these cases:

* A section or a header label is missing.
* The report needs a month the template doesn't have, for example after HHSC's last listed month.

CHO writes the header (B2 to B6) and the payment cells. The subtotals and the Total section stay as
HHSC's formulas. Their stale cached zeros are dropped, and the workbook recalculates when it opens.

## Open questions

* 5.6.2.4 calls the report "contract year-to-date". CHO defaults to months of service from the start
  of the SFY, so set `incurredFrom` if HHSC expects earlier months of service on the report.
* Payments are plan-paid amounts per claim. The instructions mention categories of service, but the
  5.6.2.11 template has none, so nothing is split by category.
