# Texas Claims Summary Report (UMCM 5.24.1)

HHSC asks each MCO and DMO for a monthly Claims Summary Report, one fixed-width text file per program,
uploaded to TexConnect by the last day of the month after the claims month (UMCM 5.0.1). The layout
is UMCM 5.24.1, "Claims Summary Report Text File Layout", v2.5 (effective 2024-09-01). Each line is
1,603 characters:

* General information (positions 1 to 20): sequence, MCO/DMO code, claims month, state fiscal year,
  program, claim type and claim form.
* One of these sections, chosen by the claim form, with every other section left blank:
  * B: UB04 and CMS 1500 (21 to 285)
  * C: NF 837i (286 to 550)
  * D: ADA (551 to 815)
  * E: pharmacy (816 to 953)
  * F: a recoupment (954 to 1278)
  * G: an advanced payment (1279 to 1603)

## Endpoints (claims-service)

Both endpoints need `finance:read`.

| Call | Returns |
| --- | --- |
| `POST api/claims/finance/texas/claims-summary` (JSON) | Every program's lines, plus the claims left out and why |
| `POST api/claims/finance/texas/claims-summary/text?program=01` (JSON) | One program's text file, named `ClaimsSummary-{mco}-{program}-SFY{year}-{MM}.txt`. Claims left out are listed in the `X-Report-Excluded` header. |

The text endpoint refuses a program with nothing to report (422). It also refuses a figure too large
for its field (422), because CHO never cuts a figure to fit.

Example request:

```json
{
  "claimsMonth": "2026-10",
  "mcoCode": "SUP",
  "rateCellPrograms": { "STAR-BEXAR-TANF-ADULT": "01", "SPLUS-BEXAR-OCC": "02" },
  "claimTypeRules": [
    { "claimType": "BH", "procedureCodePrefixes": ["H00", "H20", "908"] },
    { "claimType": "BH", "claimForm": "Institutional", "revenueCodePrefixes": ["090", "091", "011", "0124"] },
    { "claimType": "UR", "claimForm": "Institutional", "revenueCodePrefixes": ["0101"] },
    { "claimType": "MT", "procedureCodePrefixes": ["A01", "A04", "T20"] },
    { "claimType": "VS", "procedureCodePrefixes": ["V2", "9200", "9201"] }
  ],
  "deficientPendCodes": ["MEDREVIEW"],
  "appealedClaimNumbers": ["CLM-000123"],
  "capitatedBillingNpis": ["1234567893"],
  "rejectedClaims": [{ "program": "01", "claimType": "AC", "claimForm": "C", "claims": 42 }],
  "interestPenalties": [{ "program": "01", "claimType": "AC", "claimForm": "C", "claims": 3, "amount": 18.40 }],
  "pharmacy": [{ "program": "01", "electronicPaidWithin18Claims": 51200, "electronicPaidWithin18Amount": 2401733.12 }],
  "recoupments": [{ "program": "01", "facilityName": "Bexar General", "reason": "01", "amount": 75000, "date": "2026-10-09" }],
  "advancedPayments": []
}
```

The rules above are only an example. Each plan sets its own rules for its contracts.

## Where the numbers come from

**Which claims.** The report reads Medicaid claim versions adjudicated in the claims month, in Texas
(Central) time.

**Program.** A claim's program is its member's rate cell for the month of service, taken from the
expected member-months loaded for capitation and mapped through `rateCellPrograms`.

**Claim type.** The first `claimTypeRules` entry whose conditions all match sets the claim type. A rule
can test the 837 kind, the billing NPI, procedure code prefixes, revenue code prefixes and place of
service. A claim no rule matches is DT on an 837D and AC otherwise.

**Claim form.** The claim form follows the "Claim Types Crosswalk" tab:

| Claim | Claim form |
| --- | --- |
| Dental (DT) | ADA (A) |
| Nursing facility unit rate (UR) or Medicare coinsurance (MC) | NF 837i (N) |
| Any other institutional claim | UB04 (U) |
| Any other professional claim | CMS 1500 (C) |

**What each claim counts as.** Each claim is placed by the first of these that applies:

1. **Capitated:** the billing NPI is in `capitatedBillingNpis`. These claims count only in Total
   Capitated Claims.
2. **Appealed:** an adjustment (version 2 or later) of a claim in `appealedClaimNumbers`.
3. **Adjusted:** any other adjustment, or a void. Adjusted $ Paid is what the adjusted versions paid.
4. **Duplicate:** a denial with an exact duplicate finding or a code in `duplicateDenialCodes`
   (default CARC 18).
5. **Deficient-denied:** a denial with a code in `deficientDenialCodes` (default CARC 16).
6. **Clean:** everything else, as paid (Pay or Paid) or denied (Deny or Denied).

**Days from receipt.** Days count from the version's receipt date to its adjudication date, as Texas
calendar dates. The buckets follow the layout:

| Claims | Buckets (days) |
| --- | --- |
| Clean claims, UB04, CMS 1500 and ADA | 1 to 30, 31 to 90, over 90 |
| Clean claims, NF 837i | 1 to 10, 11 to 90, over 90 |
| Appealed claims, all forms | 1 to 30, 31 to 90, over 90 |

Average Processing Time is the rounded mean of those days over every claim processed in the month for
that line.

**Claims open at the end of the month.** These are claims received by the month's end and not
adjudicated by then. A claim pended now with a code in `deficientPendCodes` is Deficient-Pended. Any
other open claim is Other Unprocessed. Because pend status is read when the report runs, run it
shortly after the month ends.

**Entered by finance.** CHO does not hold these, so finance enters them in the request:

* rejected claims (front-end 999 and 277CA rejections never reach the claim store)
* prompt-pay interest penalties (CHO does not compute interest)
* pharmacy figures from the PBM
* recoupments
* advanced payments

HHSC asks for recoupments of $50,000 or more and excludes COB and TPL recoupments. A smaller
recoupment is still written, with a note.

**Never guessed.** CHO doesn't guess a program or a claim type the program doesn't report. Such a
claim is left out and counted under `excluded`. The reasons are:

* no member on the claim
* no rate cell for the month of service
* the rate cell has no program mapped
* the claim type isn't on the Claim Types Crosswalk for that program

## Open questions

* **Appealed claims.** CHO's claim appeals don't yet link to the claim version they produced, so
  appealed claims come from `appealedClaimNumbers`. Reading them from appeals-service is the next
  step.
* **Clean claim.** CHO has no clean-claim flag. Every original claim that isn't a duplicate or
  deficient denial is counted as clean. Check this against the plan's contract definition.
* **Adjusted $ Paid.** The layout says "the amount paid for Claims adjusted". CHO reports the
  adjusted versions' paid amounts, not the net change from the original.
* **Capitated claims.** These are counted only as capitated, not also in the clean buckets.
* **Field typo in the layout.** Section B's "Appealed Claims Adjudicated-Paid: $ Paid >30=<90 days"
  shows `NNNNNNNNNNNNNNN` with no decimal. CHO writes two decimals there, like every other amount.
