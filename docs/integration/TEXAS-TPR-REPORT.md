# Texas TPL/TPR Report (UMCM 5.3.4.1)

HHSC requires Texas Medicaid and CHIP MCOs to file a quarterly Third Party Liability and Recoveries
report on its own Excel template (Uniform Managed Care Manual, Chapter 5.3.4.1, with instructions in
5.3.4.2). CHO computes the figures and fills in the
state's workbook. CHO does not ship the workbook: finance downloads the current blank template from HHSC
and uploads it with each request.

## Endpoints (claims-service)

Both endpoints need `finance:read`.

| Call | Returns |
| --- | --- |
| `POST api/claims/finance/texas/tpr` with a JSON `TexasTprRequest` | Each program, plan code, month and line with its amount, plus what was left out and why |
| `POST api/claims/finance/texas/tpr/workbook` as multipart, with the blank workbook in `template` and the request JSON in `request` | The filled workbook, named the way HHSC asks (`{MCO code}TPRQ{quarter}{MMYY}.xlsx`, for example `LS1TPRQ020227.xlsx` for Q2 of SFY 2027) |

```json
{
  "stateFiscalYear": 2027,
  "quarter": 2,
  "contractor": "Lone Star Health Plan",
  "mcoCode": "LS1",
  "rateCellPlanCodes": {
    "STAR-BEXAR": { "program": "STAR", "planCode": "4F" },
    "SP-BEXAR": { "program": "STAR+PLUS", "planCode": "45" }
  },
  "tortRecoveries": [
    { "program": "STAR", "planCode": "4F", "month": "2027-02", "amount": 1000.00 }
  ]
}
```

`program` is the worksheet: `CHIP`, `Dental-CHIP`, `Dental-Medicaid`, `STAR`, `STAR Health`, `STAR Kids` or
`STAR+PLUS`. `submissionDate` defaults to today in Texas.

## Where each line comes from

Each amount goes to the member's plan code in the month of service: the member's rate cell in that month
(from the expected member-months loaded for capitation), mapped by `rateCellPlanCodes`. Months are Texas
(Central) months.

| Line (5.3.4.2) | CHO source | Month |
| --- | --- | --- |
| 1. Cost Avoidance, Denied Claims | Medicaid claims denied by the TPL stage as cost avoided. The amount is the Medicaid allowable CHO priced for the cost-avoided lines, not billed charges. | Adjudicated |
| 2. Other Insurance Credits | Other payers' paid amounts on the claim (COB) | Adjudicated |
| 3. Other Insurance Recoveries | Recovery case payments with `source: Carrier` | Received |
| 4. Provider Refunds & Recoupments | Recovery case payments with `source: Provider` | Received |
| 5. Tort Recoveries | `tortRecoveries` in the request, net of fees and costs. CHO does not track settlements. | As entered |

A claim is counted once per line, from its first version that qualifies, so a resubmission or adjustment
of the same claim is not counted again.

Recovery payments recorded before `source` existed are counted by the case's origin: pay-and-chase cases
as insurance recoveries, retroactive ones as provider refunds. The response notes how many were inferred.
Record `source` on new payments (see [MEDICAID-TPL.md](MEDICAID-TPL.md)).

## What is left out

The response's `excluded` list, and the workbook's `X-Report-Excluded` header, count what could not be
placed:

* Amounts for a member with no expected member-month in the month of service, or whose rate cell has no
  plan code in `rateCellPlanCodes`.
* Cost-avoided claims with no priced Medicaid allowable. Claims cost avoided before CHO started saving the
  allowable on the TPL decision are in this group.

## What CHO fills

For each program sheet that has plan codes:

* **Header:** "Contractor:", "SFY:", "Quarter:" and "Date Submitted:" in A2 to A5. HHSC merges and unlocks
  only those label cells, so the value goes in the label cell itself.
* **Plan codes:** in the September block's plan code row. The later months' plan code cells are HHSC's
  formulas that copy it.
* **The five lines:** for every month from September through the quarter's last month, with zero where
  there is nothing, as 5.3.4.2 asks.
* **Certification sheet:** MCO name, plan codes, document name and submission date. The signature is left
  for the MCO.

Totals, quarterly cumulative blocks and the "OIG TPR Use" summary stay HHSC's formulas. The workbook is
set to recalculate when Excel opens it, and the stale cached totals are removed.

## Layout check

Before writing anything, CHO reads each program sheet it needs: the header labels in A2 to A5, all 12 month
blocks (the month name in column B, the quarter in column A), the five line labels under each, and the
"Plan Code" columns. If a sheet is missing, a label differs, or a program has more plan codes than the
sheet has columns, CHO refuses the upload with 422 and lists the problems, instead of filling the wrong
cells. This was checked against the HHSC workbook as published in 2026.

The workbook is read as a zip. Uploads are limited to 10 MB zipped and 100 MB unzipped, DTDs are refused,
and every part other than the sheets CHO edits is copied byte for byte.
