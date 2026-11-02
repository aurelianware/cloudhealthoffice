# Texas HHSC appeals and complaints text files

CHO builds the two monthly fixed-width files Texas Medicaid/CHIP plans upload to TexConnect:

| File | HHSC layout | Endpoint |
|---|---|---|
| Member appeals | UMCM 5.24.6 Member Appeals Report Text File Layout, v2.7 (effective 2024-09-01) | `GET /api/appeals/reports/texas/member-appeals` |
| Member and provider complaints | UMCM 5.24.5 Member and Provider Complaints Text File Layout | `GET /api/appeals/reports/texas/complaints` |

One file per program per month, due 45 days after the month ends.

## Building a file

```
GET /api/appeals/reports/texas/member-appeals?month=2026-09&program=01&mcoCode=SUP&planCode=4F&format=txt
```

- `month`: the reporting month, `yyyy-MM` (Texas calendar month; the state fiscal year is derived, September to August).
- `program`: HHSC program code (01 STAR, 02 STAR+PLUS, 03 STAR Health, 04 STAR Kids, 05 CHIP, 06 CMDS, 07 CHIP Dental, 10 MMP).
- `mcoCode`, `planCode`: the plan's HHSC MCO/DMO name code and 2-character plan code.
- `format=txt` returns the file. Without it, JSON lists the cases in the file and the **problems**: cases that belong in the file but are missing a required HHSC code. Those are left out of the file (a malformed row would fail the upload), and `X-Report-Problems` gives their count on the txt response.

Needs `reports:um` or `reports:compliance`. Every case and member in a file is written to the record access log.

## What goes in a file

Every case received by the end of the month and not resolved before it began: cases resolved in the month carry their resolution date, the rest go as pending (no date, `PN` / `PND`) with the same tracking ID every month until resolved. Grievances go in the complaints file; every other case type in the appeals file. No cases gives a blank file, as HHSC asks.

## Per-case HHSC codes

What CHO already records is mapped: received and resolution dates (Central time), urgency (72-hour expedited or 30-day standard), the 14-day extension and who asked for it, and the outcome of a decided case (Approved OV, partial PU, Denied UP, Withdrawn WD; complaints withdrawn WTD). The rest are HHSC's own codes, set per case:

```
PUT /api/appeals/{id}/texas-codes      (appeals:write)
GET /api/appeals/{id}/texas-codes      (appeals:read)
```

Codes are checked against the HHSC value lists in the layouts; free text against its field width. Each change keeps the previous version in the record's history.

Required for an appeal: program, the member's 9-digit state Medicaid/CHIP number, method received, received by, benefit type, reason for denial, adverse benefit determination, and (when overturned) the overturn reason. A case closed as expired, administrative error or other needs its HHSC outcome recorded.

Required for a complaint: program, complainant, method received, received by, benefit type, category and detail; for a member complaint the state member number and MDCP flag; for a resolved one the disposition and (unless withdrawn) the outcome. A provider complaint reports the case's NPI, or 9999999999 when none.

## Limitations

- The tracking ID defaults to the appeal number compacted to 15 characters (`APL-20260915-ABCD1234` → `260915ABCD1234`); set `trackingId` to use another.
- "Initial contact complaint" counts weekends but not Texas state holidays.
- The state member number is recorded per case; it is not yet read from the member's enrollment.
- Generated files are not yet kept in CHO's document catalog; a follow-up stores each one there.
- Sequence is zero-padded to 6 digits; every other field is left-justified and space-filled, per the layouts' instructions. Confirm against TexConnect's first validation report.
