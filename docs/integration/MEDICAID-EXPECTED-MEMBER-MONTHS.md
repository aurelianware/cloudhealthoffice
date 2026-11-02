# Expected member-months from the 834

Finance loads the plan's expected member-months (member, month, rate cell)
into premium-billing-service today by CSV
(`POST /api/v1/state-capitation/{state}/expected`). They feed the 820
capitation reconciliation, the Texas claims lag and TPR reports and the MLR
member-month counts. This page describes building them from the state's 834
instead.

## Where the rate cell comes from

A state places each Medicaid member in a capitation rate cell from an
eligibility category (Medi-Cal aid code, Texas risk group or program), usually
crossed with an age band and a region. The state's 834 carries the
eligibility category, but where it puts it is the state's companion guide to
say. The common places are:

- a loop 2750 reporting category (`LX` / `N1*75*<name>` / `REF*<qualifier>*<value>` / `DTP*007`), or
- the HD line's plan coverage description (`HD04`).

CHO does not assume either. The plan's crosswalk names the field it reads.

## Steps

1. **Keep the inputs (done).** Every enrollment event written from an 834
   transaction carries `rateCellInputs` when the transaction had any:

   ```json
   {
     "reportingCategories": [ { "name": "RISK GROUP", "qualifier": "ZZ", "value": "STAR-ADULT", "date": "20260201" } ],
     "coverageLines": [ { "insuranceLineCode": "HLT", "planCoverageDescription": "STAR BEXAR",
                          "maintenanceType": "021", "benefitBeginDate": "20260201", "benefitEndDate": null } ],
     "dateOfBirth": "20150610", "countyCode": "029", "zip": "78205"
   }
   ```

   Date of birth (DMG02), county (N406 when N405 is `CY`, now parsed) and ZIP
   come from the same transaction, for age bands and regional rate cells.
   A dependent's own loop 2750 is kept on the dependent's event. Values are
   trimmed and empty categories are dropped. Events are append-only and
   dated, so a change of category mid-enrollment is a new event, not an
   overwrite. Files imported before this change have no inputs; reload them
   or send the next full file to fill the history.

   `GET /api/v1/enrollment/rate-cell-inputs?after={memberId}&limit=1000`
   (`enrollment:read`) lists members in member id order with every event that
   carried inputs, oldest first (version, occurredAt, eventDate, event type,
   maintenance type, inputs). Page on with `after={next}` until `next` is
   null; a member is never split across pages.
2. **Crosswalk (done).** Finance loads the state's crosswalk as CSV,
   replacing the whole crosswalk each time:

   `POST /api/v1/state-capitation/{state}/rate-cell-crosswalk` (`finance:write`, body `text/csv`)

   | Column | Required | Meaning |
   |---|---|---|
   | `rate_cell` | yes | The rate cell, as the state's rate sheet names it |
   | `plan_id` | yes | The CHO plan whose coverage this row places. The build lists coverage for every plan named |
   | `field` | no | `category:<name>`: the loop 2750 reporting category whose `N1*75` name is `<name>` (case-insensitive). `hd04`: an HD line's plan coverage description. Empty: no 834 condition |
   | `value` | with `field` | The value to match (case-insensitive) |
   | `age_min`, `age_max` | no | Age band in whole years, inclusive, on the first day covered in the month |
   | `county` | no | The county code the 834 sent (N406) |
   | `effective_from`, `effective_to` | no | Months (yyyy-MM) the row applies to |

   An empty column matches anything. Example (made-up codes):

   ```csv
   rate_cell,plan_id,field,value,age_min,age_max,county
   BEXAR-STAR-CHILD-1-5,STAR,category:RISK GROUP,TANF-CHILD,1,5,029
   BEXAR-STAR-ADULT,STAR,category:RISK GROUP,TANF-ADULT,,,029
   CHIP-ALL,CHIP,,,,,
   ```

   `GET .../rate-cell-crosswalk.csv` (`finance:read`) returns it as loaded.
3. **Build (done).** `POST /api/v1/state-capitation/{state}/expected/build?from=yyyy-MM&to=yyyy-MM`
   (`finance:write`) is a dry run: member-months by month and rate cell, and
   the exceptions (first 1,000 inline; `GET .../expected/build/exceptions.csv`
   has them all). Add `apply=true` to replace the expected member-months of
   every month in the window, as a CSV load does (`sourceFile` is `834-build`).

   - Coverage is every span in the crosswalk's plans, terminated ones
     included, from coverage-service. A member covered at any point in a month
     has one member-month, with the days covered (used when the state
     prorates).
   - The inputs used are those of the latest 834 transaction effective on or
     before the first day covered that month. Effective is the event's date
     (the 834's maintenance effective date), else the day it was processed.
   - Exceptions, never guessed: `NoInputs` (no 834 inputs in effect yet),
     `NoMatch`, `Ambiguous` (rows naming different rate cells match),
     `MultiplePlans` (covered by two of the plans in the month) and
     `NoDateOfBirth` (only age-banded rows match and the 834 sent no birth
     date).
   - premium-billing-service reads coverage-service and
     enrollment-import-service with its own service identity, so the finance
     user does not need `coverage:read` or `enrollment:read`.

## Not verified

Neither HHSC's nor DHCS's 834 companion guide is public. Which field carries
the Texas risk group or the Medi-Cal aid code, and whether region comes from
the 834 or from the member's county, must be checked against a pilot plan's
companion guide or a sample file.

The per-category `DTP*007` date inside loop 2750 is kept but not used yet: a
category change takes effect from the transaction's own effective date. Large
plans read coverage 100 records a page; a build for several hundred thousand
members takes minutes.
