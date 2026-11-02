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
| Prior authorization | `StateOnlyAdultDentalPaCheck` in authorization-service, run by the CHO backend on REST and PAS requests with a CDT code |

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

## Prior authorization

authorization-service applies the same rule when a dental request arrives, for requests with a CDT code and a
requested start date on or after July 1, 2027. It reads the setting at the request's line of business and the
member's benefit plan: the plan of the coverage the request names when that coverage is active on the date,
otherwise the member's only active plan. With several active plans and none named, only the tenant and line of
business levels apply. It then reads the member's aid codes and date of birth from member-service.

| Outcome | PA decision |
|---|---|
| `CarveOut`, every requested code is CDT | Denied (A3), reason code `STATEDENTAL-FFS`: request it from Medi-Cal Dental FFS |
| `CarveOut`, dental and other codes on one request | Pended (A4) for a reviewer |
| `EmergencyOnly`, procedures not on the emergency list | Pended, naming them; approve only if the service treats an emergency |
| No aid code or date of birth on file | Pended |
| Coverage-service, member-service or tenant-service unreachable, or the setting unreadable | Pended |
| Under 19, federally funded aid code, exception aid code, emergency procedures, or setting off | No change |

On a PAS request with an automated decision, the carve-out denial outranks an automated approval, and a pend
replaces an automated approval. An automated denial or pend stands. The decision's history entry has the source
`state-program:medi-cal-apl-26-009`.

These reads use authorization-service's own identity, so the provider submitting the request needs no access to
plan configuration, coverage or member records. authorization-service needs `Services:TenantService`,
`Services:CoverageService` and `Services:MemberService`.

## Not done yet

- **Loading aid codes from the 834.** Where the DHCS 834 carries the aid code needs the DHCS 834 companion guide.
  Until then, the eligibility feed or an operator calls the aid-codes endpoint.
- **The DHCS aid code lists.** No aid codes are in the repository. The plan sets them from DHCS's aid code master chart.
- **Member outreach.** The APL's 30-day notice, three outbound call attempts by June 1, 2027, and the 60/30-day
  aging-out notices from the August 2027 month of eligibility are not built.
