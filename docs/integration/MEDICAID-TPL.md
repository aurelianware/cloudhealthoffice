# Medicaid third-party liability (TPL)

Medicaid is the payer of last resort (42 CFR 433.139). When a member has other
coverage that pays for a service, the plan either **cost-avoids** (returns the
claim to the provider to bill the other carrier first) or **pays and chases**
(pays, then recovers from the carrier). This page covers how CHO holds a
member's TPL and how to load the state's TPL file. Texas and California are
built in parallel.

Status by step:

| Step | What | State |
| --- | --- | --- |
| 1 | TPL store and state file loader (this page) | Built |
| 2 | Cost-avoid denial in adjudication when in-scope TPL is active | Built (off by default) |
| 3 | State exempt-service lists, TPL decision saved on the claim | Built |
| 4 | Recovery tracking (case per paid claim, 12-month CA clock, state reports) | Built |
| 5 | Report plan-discovered coverage to the state (CA: within 10 days) | Next |

## The TPL store

coverage-service keeps one `TplPolicy` per member, state, carrier, policy and
start date (collection `TplPolicies`). A member can hold several at once, each
with its own dates and **scope**: the service categories it pays for
(`Hospital`, `Professional`, `Pharmacy`, `Dental`, `Vision`, `BehavioralHealth`,
`LongTermCare`, `Dme`, `Casualty`, `MedicarePartA`–`D`; `Medical` and
`Comprehensive` are combinations). Step 2 cost-avoids a claim line only when an
active policy's scope covers that line.

This is separate from `Coverage.OtherInsurance`, which holds one commercial
policy from the 834 and drives commercial COB. Rows are never deleted: ended
and state-removed spans stay so adjudicated claims can be explained.

The member is matched by `Coverage.MemberId` (the 834 subscriber id, which for
Medicaid is the state id: Texas Medicaid number, Medi-Cal CIN). Rows for people
the plan has never enrolled are rejected and counted, since the state file
covers its whole population.

## Loading a file

```
POST /api/v1/coverage/tpl/import?layout=ca-ohc&fileDate=2026-11-01&mode=Full&dryRun=true
Content-Type: multipart/form-data
file=@ohc-202611.txt
layoutFile=@my-layout.json      (optional)
```

- **Dry run is the default.** The report shows the columns read, inserts,
  updates, ends, rejected rows with reasons, and codes whose scope still needs
  checking. Re-run with `dryRun=false` to write.
- **`mode=Incremental`** (default) applies the rows given. **`mode=Full`** treats
  the file as the state's whole picture for that layout: an active row it no
  longer carries is ended the day before `fileDate`. A full file that would end
  more than 25% of the active rows (and at least 100) is refused as a likely
  truncated file unless `allowLargeDrop=true`.
- An optional `action` column takes add/change (`A`, `C`), end (`T`, `D`) or
  remove-as-never-valid (`R`). A removed row never applies on any date.
- Needs `coverage:write`. `GET /api/v1/coverage/tpl/layouts` lists the layouts
  and code tables; `GET /api/v1/coverage/member/{id}/tpl?asOfDate=` reads a
  member's rows.

### Layouts

| Layout | State | Reads | Verified |
| --- | --- | --- | --- |
| `cho-csv` | any (pass `state`) | CHO's CSV, `scope` column of names | Yes |
| `tx-tpr` | TX | header aliases; coverage-type words | **No** |
| `ca-ohc` | CA | header aliases; Medi-Cal OHC codes | **No** |

Neither state publishes its MCO record layout openly, so the Texas and
California layouts find columns by header name and carry the code tables. If
the state file is fixed width, upload a `layoutFile` giving positions; the
built-in code table is kept for codes it does not name:

```json
{
  "format": "FixedWidth",
  "fields": {
    "MemberId":      { "start": 1,  "length": 9 },
    "CoverageCode":  { "start": 10, "length": 2 },
    "CarrierCode":   { "start": 12, "length": 4 },
    "CarrierName":   { "start": 16, "length": 30 },
    "PolicyNumber":  { "start": 46, "length": 20 },
    "EffectiveDate": { "start": 66, "length": 8 },
    "EndDate":       { "start": 74, "length": 8 }
  },
  "verified": true,
  "verificationNote": "Checked against <state layout doc, version, date>."
}
```

Every report from an unverified layout carries a warning. Do not rely on cost
avoidance from such a load until the layout has been checked.

### California (Medi-Cal OHC)

DHCS supplies known OHC to MCPs monthly from the Medi-Cal Eligibility Record
(APL 21-002, APL 23-002). Codes, from DHCS Information Notice 17-058:

| Code | Meaning | CHO scope |
| --- | --- | --- |
| A | Pay and chase, any carrier | Comprehensive, **pay-and-chase only** |
| C | Military comprehensive | Comprehensive |
| D | Medicare Part D | Medicare D + pharmacy |
| E | Vision plans | Vision |
| F | Medicare Part C | Medicare C + medical |
| G | Medical parolee | skipped (flag, not coverage) |
| H | Multiple plans comprehensive | Comprehensive |
| I | Institutionalized | skipped (flag, not coverage) |
| K | Kaiser | Comprehensive |
| L | Dental only | Dental |
| P | PPO/PHP/HMO/EPO n.o.s. | Medical *(verify)* |
| Q | Commercial pharmacy | Pharmacy |
| V / V2 | Any other carrier | Comprehensive *(verify)* |
| W | Multiple plans non-comprehensive | Hospital + professional *(verify)* |
| N | No OHC | ends the member's `ca-ohc` rows from that date |

APL 21-002 Appendix A maps codes to services but is not reproduced in the APL;
the *(verify)* scopes are CHO's reading until checked against it.

California rules that later steps implement: MCPs keep recoveries made within
12 months of payment (or under a repayment plan agreed before then); DHCS
recovers from month 13 and MCPs report those monthly by the 15th; MCPs report
new or changed OHC to DHCS within 10 calendar days of discovery; prenatal care
is cost-avoided (since 9 Feb 2018); 90 days with no carrier response counts as
proof the other payer is exhausted.

### Texas (third-party resources)

HHSC / OIG Third Party Recoveries supply TPR data to MCOs; the layout is in the
contract documents, not public. `tx-tpr` maps coverage-type words (`MAJOR
MEDICAL`, `HOSPITAL`, `PHYSICIAN`, `PHARMACY`, `DENTAL`, `VISION`, `TRICARE`,
`MEDICARE A`–`D`, `CASUALTY`, `LTC`…). Indemnity policies that pay cash
(`CANCER`) are skipped; TMPPM 8.2 makes them a TPR only when assignable.
Swap in the state's codes with a `layoutFile` once the layout is in hand.

Texas rules that later steps implement (TMPPM vol. 1 ch. 8, May 2026): STAR
Health is primary to other insurance unless a court orders otherwise (8.3.1);
THSteps medical and dental, SHARS, ECI and case management for children and
pregnant women need not bill other insurance first (8.3); providers wait 110
days for a TPR with no response (8.8.2.5) and file within 95 days of the TPR's
disposition (8.8.2.6).

### Federal floor (both states)

From the CMCS bulletin of 27 Aug 2021 on BBA 2018 and MSIAA 2019: prenatal,
labor and delivery and postpartum care are cost-avoided; pediatric preventive
services are paid without regard to TPL unless the state found 90-day cost
avoidance warranted; claims tied to medical child support may be paid without
regard to TPL for up to 100 days.

## Cost avoidance in adjudication

`ThirdPartyLiabilityStage` (claims-service, order 276, right after COB) reads
the member's active TPL rows for the earliest service date and decides, for
Medicaid claims only:

| Situation | Outcome |
| --- | --- |
| The 837 carries the other payer's adjudication (2320 paid amount or CAS, 2430 lines) or bills this plan as a later payer | Pass; COB pays the balance |
| Plan is Medicaid-primary (`MedicaidPrimaryPlanCodes`, e.g. Texas STAR Health) | Pass |
| Every line covered by an active resource that is not pay-and-chase-only and not exempt | **Deny CO-22 / MA04** on every line (`CostAvoid` mode) or pend `TPL` (`Pend` mode) |
| Some lines covered, others not | Pend `TPL` with the covered line numbers; the examiner denies those lines or approves |
| Covered only by pay-and-chase resources (Medi-Cal OHC `A`) or exempt services | Pass; lines recorded as pay-and-chase for recovery |
| TPL lookup failed | Pend (never a denial) |

An examiner who approves the exact `TPL` pend turns every covered line into a
pay-and-chase line. A scope matches a line by service category: inpatient or
outpatient hospital (type of bill), nursing facility (21x–23x, 65x, 66x),
professional, behavioral health (90785–90899, H-codes), DME (E, K, L, A4–A9),
vision (V0–V2, 92002–92499) and dental (837D). Medicare A covers inpatient and
SNF; B covers outpatient, professional and DME; C covers medical. Casualty
covers a claim only when it is marked accident-related (CLM11 AA/EM/OA or an
accident date).

Federal exemption: pediatric preventive services for members under 21
(preventive visits 99381–99384 and 99391–99394 at any age; 99385/99395,
immunization administration, developmental, vision, hearing and lead
screening, fluoride varnish, and well-child or Z23 encounters under 21). Turn
off with `PayPediatricPreventive=false` only if the state has found 90-day
cost avoidance warranted.

### State pay-and-chase lists

Built in, keyed by the TPL row's state; configured codes are added unless
`ReplaceBuiltIn` is set:

| State | Pays and chases | Authority |
| --- | --- | --- |
| TX | `G9012` case management for children and pregnant women; `T1017`, `H2014` ECI; THSteps dental checkup and preventive codes (`D0120`, `D0145`, `D0150`, `D1110`, `D1120`, `D1206`, `D1208`, `D1351`, `D1354`) under 21 | TMPPM 8.3 (codes are CHO's mapping; check against the THSteps and ECI handbooks) |
| CA | nothing beyond federal by default | APL 21-002 refers to a DHCS direct-bill CPT/HCPCS list it does not reproduce; configure it |

SHARS is carved out of Texas managed care, so it is not listed. Prenatal care
stays cost-avoided in both states (federal law since 9 Feb 2018).

```json
"States": {
  "CA": { "PayAndChaseProcedureCodes": [ "..." ], "Authority": "DHCS direct-bill list, <date>" }
}
```

### On the claim

Every decision is saved on the claim as `adjudicationResult.thirdPartyLiability`:
decision, reason code, the TPL rows that applied (with their coverage-service
ids), cost-avoided line numbers and pay-and-chase lines with the reason.
Recovery tracking reads the pay-and-chase lines.

```json
"Adjudication": {
  "ThirdPartyLiability": {
    "Mode": "SoftValidation",
    "MedicaidPrimaryPlanCodes": [ "STAR-HEALTH" ],
    "PayPediatricPreventive": true,
    "States": { "CA": { "PayAndChaseProcedureCodes": [] } }
  }
}
```

Modes: `Off` (default), `SoftValidation` (records the decision, never changes
the claim; use it first, and in shadow runs), `Pend`, `CostAvoid`. The stage
is shadow-safe, so a shadow adjudication run shows what cost avoidance would
have done to a plan's history.

## Recovery cases

claims-service keeps a `TplRecoveryCase` (collection `TplRecoveryCases`) per
claim and origin, under `api/v1/claims/tpl-recoveries` (`claims:read` to read,
`claims:work` to change):

| Call | What it does | When |
| --- | --- | --- |
| `POST sync?from=&to=` | Opens a case for each Medicaid claim adjudicated in the window whose pay-and-chase lines were paid. Idempotent. | Daily, or after each payment cycle |
| `POST retroactive` `{ "memberIds": [...] }` | Re-checks those members' paid Medicaid claims (last 12 months, `RetroactiveLookbackMonths`) against their TPL now and opens a case for lines other coverage turns out to have covered. Claims already cost-avoided, paid-and-chased or billed to the other payer are skipped. | After a TPL file load, for the members it added coverage for |
| `POST {id}/events` | `Billed`, `PaymentReceived` (amount), `RepaymentPlanAgreed`, `CarrierDenied`, `WrittenOff` (amount), `Note` | As the recovery team works the case |
| `POST apply-clocks` | Hands open cases past the state's window to the state | Daily |
| `GET report?state=CA&month=2027-02-01` | Month's CSV: cases handed to the state, recoveries received | Monthly |
| `GET ?state=&status=&openOnly=&deadlineBefore=&memberId=` | Work list | Any time |

Each case holds the lines, what Medicaid paid on them, the TPL rows to
recover from, amounts recovered and written off, and every event with who
recorded it.

**Retention window.** `RecoveryRetentionMonths` is per state; California is
12 (APL 21-002): the plan keeps recoveries made within 12 months of payment,
or later under a repayment plan agreed by then; from month 13 DHCS's TPLRD
recovers and keeps the money. `apply-clocks` moves such cases to
`TransferredToState` the day after the deadline unless a repayment plan was
agreed on or before it. Texas has no window by default; set one if the
contract gives HHSC-OIG the recovery after a period.

**Monthly report.** DHCS asks MCPs for a monthly report by the 15th over SFTP.
The CSV here is CHO's layout; map it to the DHCS spec once the plan has it.

## References

- TMPPM Vol. 1 ch. 8, Third Party Liability (May 2026), tmhp.com
- DHCS APL 21-002 and APL 23-002, Cost Avoidance and Post-Payment Recovery for Other Health Coverage
- DHCS MHSUDS Information Notice 17-058, Other Health Coverage Codes
- CMCS Informational Bulletin, 27 Aug 2021, third-party liability changes (BBA 2018, MSIAA 2019)
