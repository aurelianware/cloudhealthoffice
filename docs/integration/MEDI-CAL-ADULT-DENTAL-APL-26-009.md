# Medi-Cal adult dental for state-only members (DHCS APL 26-009)

From **July 1, 2027**, Medi-Cal members **19 and older** who are **not eligible for federally funded
full-scope Medi-Cal** lose non-emergency dental benefits (SB 164, which moved the date from July 1, 2026;
APL 26-009 supersedes APL 26-005). The APL makes these exceptions:

- Members **under 19** keep full dental and stay in their Dental Managed Care Plan.
- Members in a county-designated **pregnancy** aid code (including one year postpartum) keep full dental, through Dental FFS.
- **Foster youth and former foster youth** under 26 in a foster aid code keep full dental, through Dental FFS.

The affected members are **carved out of the Dental MCP contract to Medi-Cal Dental fee-for-service**. A member who
turns 19 after July 1, 2027 loses dental on the first of their birth month when the birthday is the 1st, and
otherwise on the first of the following month.

## What CHO does

| Piece | Where |
|---|---|
| Aid codes on the member, each with its dates | member-service `aidCodes`; `PUT /api/v1/members/{memberId}/aid-codes` replaces them (`members:write`). Create accepts them too. |
| The rule: dates, age, aging-out date, outcome | `StateOnlyAdultDentalRule` (CloudHealthOffice.Infrastructure, `Programs/MediCal`) |
| Which aid codes are state-only, which are exceptions, which CDT codes are emergencies | Plan setting `eligibility.mediCal.stateOnlyAdultDental` (JSON), at the tenant, line of business or benefit plan |
| Claims | `StateOnlyAdultDentalStage`, Order 160, on CDT lines (D0000–D9999) |

The setting is `"off"` by default:

```json
{
  "mode": "CarveOut",
  "stateOnlyAidCodes": ["<state-only full-scope aid codes from the DHCS aid code master chart>"],
  "exceptionAidCodes": ["<pregnancy and foster youth aid codes>"],
  "emergencyProcedureCodes": ["<CDT codes the plan treats as emergency services>"]
}
```

- **CarveOut** is for a Dental Managed Care Plan. When every CDT line on a claim is for an affected member on or
  after their loss date, the claim is denied CO-109 (not covered by this payer), with a reason telling the provider to
  bill Medi-Cal Dental FFS. Emergencies and the exceptions are included, because they go to FFS as well. A claim
  with lines on both sides of the loss date pends `STATEDENTAL`.
- **EmergencyOnly** is for a payer that keeps covering these members. A member in an exception aid code keeps full
  dental. Otherwise, CDT codes not on the emergency list pend `STATEDENTAL`, naming the lines. An examiner then decides
  whether each service treated an emergency (severe pain, infection, injury).
- When there is no date of birth, or no aid code on file for the date of service, the claim pends `AIDCODE`.
  An unknown is never treated as covered.
- If tenant-service cannot be reached for the setting, the claim pends `CONFIGUNAVAIL` as a check that could not run.
  This happens only for claims with a CDT line on or after July 1, 2027.

An examiner approval passes a reviewed `STATEDENTAL` or `AIDCODE` pend.

## Not done yet

- **Loading aid codes from the 834.** Where the DHCS 834 carries the aid code needs the DHCS 834 companion guide.
  Until then, the eligibility feed or an operator calls the aid-codes endpoint.
- **Prior authorization.** Dental PA requests for these members are not checked yet.
- **The DHCS aid code lists.** No aid codes are in the repository. The plan sets them from DHCS's aid code master chart.
- **Member outreach.** The APL's 30-day notice, three outbound call attempts by June 1, 2027, and the 60/30-day
  aging-out notices from the August 2027 month of eligibility are not built.
