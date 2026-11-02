# Texas MLR Report (UMCM 5.3.13.2)

HHSC requires Texas Medicaid and CHIP MCOs to file a Medical Loss Ratio report on its own Excel template
(Uniform Managed Care Manual, Chapter 5.3.13.2, version 2.4). CHO computes the figures and fills in the
state's workbook. CHO does not ship the workbook: finance downloads the current blank template from HHSC
and uploads it with each request.

## Endpoints (premium-billing-service)

Both endpoints need `finance:read`.

| Call | Returns |
| --- | --- |
| `POST api/v1/state-capitation/TX/mlr/texas` with a JSON `TexasMlrRequest` | Each line with its cell (`G6` to `G36`), the numerator, the denominator, the MLR and the credibility result |
| `POST api/v1/state-capitation/TX/mlr/texas/workbook` as multipart, with the blank workbook in `template` and the request JSON in `request` | The filled workbook, named `MLR-Report-SFY{year}.xlsx` |

## What CHO fills

* **Header:** MCO name (D2), SFY (D3), submission date (D4) and reporting period end (F5, August 31 of the
  SFY).
* **Line 1:** member months. By default this is the expected member-months loaded for the SFY.
* **Lines 2.1 to 2.4:** as entered. Enter the "Less:" lines (2.2 b, c, h, i, j) as positive amounts, because
  the template subtracts them. Line 2.2 k (solvency funds) and line 3.4 can be negative.
* **Line 2.4:** the counted amount, which is fraud prevention capped at 0.5% of the denominator
  (42 CFR 438.8(e)(4)).
* **Line 3.1:** gross revenue. By default this is the state capitation on recorded 820s for September
  through August, plus `otherPremiumRevenue`. HHSC asks for gross revenue as reported on the FSR, so pass
  `grossRevenue` when the two differ.
* **Line 5.1:** the partial credibility adjustment from `MedicaidMlrCalculator`. The Texas credibility
  table is still a starting value that nobody has checked against the contract. The response notes this.

The template's own formulas (G24, G30, G32 to G34 and G37) are left in place. The workbook is set to
recalculate when Excel opens it. Their results, and the Attestation sheet's copies of the header, are
also cached in the file, so previewers that don't recalculate show the right numbers.

## What the MCO still does

* The **Expense Methodology** narrative sheet.
* The **Attestation** signature, by the CEO or CFO.
* Checking line 2.2 e against the MCO's fraud reduction expenses. CHO does not check that limit.

## Layout check

Before writing anything, CHO checks the template's labels (A1, A6, B8, C10 to C21, B22, B23, B26 to B29,
B36) and its formulas (G24, G30, G32 to G34, G37). If HHSC changes the layout, CHO refuses the upload
with 422 and lists the cells that differ, instead of filling the wrong cells.

The workbook is read as a zip. Uploads are limited to 10 MB zipped and 100 MB unzipped, DTDs are
refused, and every part other than the sheets CHO edits is copied byte for byte.
