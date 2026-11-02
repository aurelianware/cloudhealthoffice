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
2. **Crosswalk (next).** Finance loads a CSV per state that maps a field and
   value, optionally with an age band, to a rate cell.
3. **Build (next).** For each month, every member with Medicaid coverage in
   the state is placed in a rate cell from the inputs in effect that month and
   the crosswalk. The result replaces the expected member-months for those
   months, after a dry run. A member whose inputs match no crosswalk row, or
   more than one, is listed as an exception and left out rather than guessed.

## Not verified

Neither HHSC's nor DHCS's 834 companion guide is public. Which field carries
the Texas risk group or the Medi-Cal aid code, and whether region comes from
the 834 or from the member's county, must be checked against a pilot plan's
companion guide or a sample file.
