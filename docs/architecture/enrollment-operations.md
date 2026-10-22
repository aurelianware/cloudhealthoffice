# Enrollment operations: working 834 exceptions, reconciliation and retro changes

**Status:** Draft for review (2026-10-21).
**Scope:** How a plan's enrollment team works what the 834 could not apply:
- the exception queue;
- corrections sent back to sponsors;
- full-file reconciliation;
- retroactive changes and their effect on claims;
- the enrollment history on the member record.

**Related:**
- [edi-operations.md](edi-operations.md): file receipt, envelopes, acknowledgments and trading partners (the file layer this doc builds on).
- [work-management.md](work-management.md): the work engine every exception runs through.
- [member-foundation.md](member-foundation.md) and [temporal-eligibility.md](temporal-eligibility.md): members and dated coverage.
- [portal-navigation.md](portal-navigation.md): Enrollment Ops sits under Operations.

> **Not legal advice.** §6 summarizes the ACA rescission rule and state reconciliation practice as researched on 2026-10-21. Confirm with counsel before the commercial retro floor ships as a hard rule.

---

## 1. Who does what

Two teams touch an 834, and they do different jobs:

| Layer | Question | Owner | Where they work |
|---|---|---|---|
| File | Did it arrive, parse and get acknowledged? | EDI analyst | EDI Operations ([edi-operations.md](edi-operations.md)) |
| Record | Could each member change be applied? | **Enrollment specialist** | Enrollment Ops and the member record |

The EDI analyst never fixes members. Once a file is accepted, every member-level problem is the enrollment team's, worked as a work item with an SLA like any other queue. Supervisors see the same queue views they use for claims.

## 2. What exists today

**In place:**
- enrollment-import-service applies 834 adds, changes, terms and reinstatements through member-service and coverage-service.
- It records a run summary per file (`EnrollmentImportRun`) and one row per member transaction (`EnrollmentTransaction`).
- The member record's Enrollment tab shows the member's enrollment events and 834 transactions.
- `WorkType.Enrollment834` exists in work-management, with the `enrollment:process` permission.

**Missing:**
- ~~**Rejection reasons are not stored.**~~ Built (§9.1).
- **Nothing creates `Enrollment834` work items,** and there is no fix-and-reprocess action. Today the only remedy is re-sending the file or a manual enrollment.
- **The BGN segment is not read,** so CHO cannot tell a change file from a full or audit file, and there is no reconciliation.
- **Retro changes are flagged but not acted on.** Events dated more than 30 days back get `IsRetro`; nothing tells claims.
- **Files that arrive by SFTP skip the duplicate check and the transaction record.** The fix belongs to [edi-operations.md](edi-operations.md) §4.

## 3. The exception record

Every member transaction that cannot be applied, or that needs a person's decision, becomes an `EnrollmentException` and an `Enrollment834` work item:

| Field | Purpose |
|---|---|
| File, transaction set and INS position | Where it came from; links to the file in EDI Operations |
| Sponsor, subscriber ID, member ID (when matched) | Who it is about |
| Maintenance type (INS03), reason (INS04), effective dates | What the sponsor asked for |
| **Reason code** and message | Why CHO could not apply it (see the table below) |
| Proposed change | Before and after values, so the specialist sees exactly what would change |
| Status | Open → Resolved (applied / matched / rejected to sponsor / no action) or Held (waiting for the sponsor) |
| Resolution, resolver, approver | Who decided and why; second approver where §6 requires one |

**Reason codes** (from `EnrollmentValidator` plus the new checks):

| Code | Meaning | Usual fix |
|---|---|---|
| `SUBSCRIBER_NOT_FOUND` | A change or term names a subscriber CHO does not have | Match to an existing member, or send back to the sponsor |
| `DEPENDENT_BEFORE_SUBSCRIBER` | Dependent arrived without its subscriber | Hold until the subscriber arrives; auto-retry |
| `PLAN_CODE_UNMAPPED` | HD03/HD04 plan code has no mapping | Plan-side fix: add the mapping, then reprocess |
| `POSSIBLE_DUPLICATE` | An add looks like an existing person (name, DOB, SSN, MBI or Medicaid ID) | Match or confirm as new |
| `COVERAGE_OVERLAP` | New coverage overlaps an existing span | Choose which span wins |
| `RETRO_BEYOND_LIMIT` | Effective date is earlier than the retro rule allows (§6) | Approve with reason (two-person), apply prospectively, or send back |
| `RETRO_AFFECTS_PAID_CLAIMS` | A retro term or plan change would move paid claims | Approve; claims review is raised (§6) |
| `STATE_GATE` | Fails a state-specific enrollment rule | Per state rule |
| `RECON_DIFFERENCE` | Found by reconciliation (§5) | Per the sponsor's mode |

**Specialist actions:**
- **Correct and reprocess:** edit the proposed values (not the original transaction), then apply.
- **Match to member:** link to an existing member and apply.
- **Approve retro:** with a reason, and a second approver where §6 requires one.
- **Send back to sponsor:** with a reason (see §4).
- **Hold:** waiting for the sponsor; it comes back on a date.
- **No action:** with a reason.

Every action is recorded on the exception and on the member's enrollment history.

## 4. Telling the sponsor

Who owns the data decides what "send back" means, so each sponsor has a **correction channel**:

| Channel | Sponsors | What CHO produces |
|---|---|---|
| **Error report** | Employer groups (commercial) | **Per file:** a readable report modeled on carrier summary reports. It gives counts by severity, then each rejected member with the reason and the fix needed. Stored in the document store, with a notice to the sponsor's contact. The sponsor corrects the data in its next file. |
| **Discrepancy report** | State Medicaid | **Per reconciliation cycle:** the state's discrepancy form, a template per state, plus a "no discrepancies" certification when there are none. The deadline is tracked (Oregon allows 14 days after the monthly file) and the signer recorded (Oregon requires the CEO, CFO or a delegate). The plan never rejects a state's member: the state is the source of truth and corrects confirmed errors in its next file. |
| **Reconciliation file** | Marketplace (FFM) | **Later:** the monthly reconciliation response (RCNI) against CMS's pre-audit file. Until then, Marketplace sponsors use report-only reconciliation. |

File-level problems (bad envelope, syntax) go back as the 999/TA1, plus the same readable summary. That belongs to EDI Operations.

## 5. Reconciliation

The BGN segment says what a file is: BGN08 `2` change, `4` verify (audit), `RX` replace. Full and audit files are run through a reconciliation that compares the file with what CHO holds for that sponsor on the file's date. It finds:
- members on the file but not in CHO;
- members in CHO but not on the file;
- differences in dates, plan or demographics.

What happens to each difference is set per sponsor:

| Mode | Typical sponsor | Behavior |
|---|---|---|
| **Authoritative** | State Medicaid | CHO aligns to the file. Two kinds of difference become exceptions instead: anything that hits paid claims, and anything the plan believes the state got wrong. Those feed the state's discrepancy report. |
| **Report-only** | Marketplace until RCNI exists; any sponsor being onboarded | Every difference is an exception; nothing changes automatically. |
| **Full replace** | Small employer groups that send a complete file every time | Members missing from the file are termed (subject to §6). **Guard:** if the file would term more than 10% of the sponsor's members, or more than 5 members for a sponsor with fewer than 50, whichever is smaller, the whole file is held for review. One truncated file must not wipe out a group. Both numbers are settings at the same three levels as §6. |

Each reconciliation run is a record of its own:
- the file and the date it was compared on;
- counts per difference type;
- exceptions created;
- for authoritative sponsors, the report sent, its due date, and its signer.

The Enrollment Ops page shows the current cycle per sponsor.

## 6. Retroactive changes

**How limits are set.** Line-of-business defaults, overridden per benefit plan, then per sponsor; the most specific wins. Each level sets:
- how far back an add may go;
- how far back a term may go, and to what date;
- whether a term beyond the limit is applied at the limit, held, or applied with approval.

**Waiting periods are a separate setting** at the same three levels: how long a new hire waits before coverage starts, which decides the effective date an add should carry. A sponsor with a 90-day waiting period does not thereby get a 90-day retro window.

**Proposed defaults:**

| Line of business | Adds | Terms |
|---|---|---|
| Commercial group | Up to 60 days back (decided) | Back to the **premium paid-through date** only. Beyond that, apply at the end of the current month unless a reason is recorded and a second person approves. |
| Individual / ACA | The exchange's dates | The exchange's dates |
| Medicaid | The state's dates, no limit | The state's dates, no limit |

**The legal floor for commercial plans.** Under the ACA rescission rule (45 CFR 147.128), a retroactive termination is allowed only for:
- non-payment of premium, back to the date premiums were paid through;
- fraud or intentional misrepresentation, with 30 days' written notice.

An employer telling the plan late that an employee left does not qualify; the practice is a prospective term with COBRA offered from that date. Overrides may tighten the defaults but may not loosen past this floor without a recorded reason in the allowed categories.

**Effect on claims.** A retro term, retro plan change or retro add that touches dates with paid or pended claims publishes an event. Claims raises a review work item listing the affected claims; it does not silently re-adjudicate. Recoupment is a claims decision, made with the provider notice rules of that line of business. North Carolina's 2025 recoupment problems after retro managed-care enrollment show what goes wrong without this step.

**Medicare Advantage** enrollment does not arrive by 834. It is reconciled against CMS's weekly transaction replies and monthly membership report, and is out of scope here (an existing gap in MODULE-STATUS).

## 7. Enrollment history on the member record

The member's Enrollment tab answers "why is this person termed?" without leaving the record. It shows every 834 transaction that touched the member:
- file, date and maintenance type;
- before and after values;
- the outcome, with the stored reason;
- any exception and who resolved it.

Manual changes and reconciliation actions appear on the same timeline. Today's tab has the right shape; it needs the stored reasons (§2) and the exception links.

## 8. The Enrollment Ops page

For enrollment specialists and their supervisors (`enrollment:process`; supervisors also `workqueue:admin`):

1. **Exceptions:** the `Enrollment834` queue by reason, sponsor and age, with Get Next. Opening one shows the exception, the proposed change, the member (if matched) and the actions in §3.
2. **Reconciliation:** the current cycle per sponsor, with differences, exceptions, the report due date and its status.
3. **Sponsors:** files expected and received, last file, counts, the error rate and open exceptions. Missing-file alerts come from EDI Operations' expected-file schedule.
4. **Retro:** retro changes awaiting approval, and those applied this month with claims impact.

The page reads enrollment and work-management data only. Member detail needs `members:read`, as on the member record.

## 9. Build order

1. **Store the reason** on every rejected transaction, with the INS position and file link. Show it on the member's Enrollment tab. *Built 2026-10-22 (§9.1), including the link to the EDI file record.*
2. **Exceptions and work items:** the `EnrollmentException` record, `Enrollment834` items with SLAs, and the specialist actions (reprocess, match, hold, no action, send back). *Built 2026-10-22 (§9.2), API only; the screen is step 3.*
3. **Enrollment Ops page:** the exceptions view, then sponsors. Ahead of it: the automatic orphan-dependent retry (§10, decision 5).
4. **Retro rules:** line-of-business, plan and sponsor settings; the commercial floor; two-person approval; the claims-impact event and claims review items.
5. **Sponsor error reports:** generated per file into the document store, plus the sponsor-contact notice.
6. **Reconciliation:**
   - read BGN08;
   - the comparison engine and the three modes;
   - the full-replace guard;
   - reconciliation runs.
7. **State discrepancy reports:** a template per state, deadline tracking and signer, starting with the pilot plan's state.
8. **Marketplace reconciliation file (RCNI),** when a Marketplace plan needs it.

SFTP files reaching the same import path with the duplicate check is a prerequisite of steps 2 and 6, owned by [edi-operations.md](edi-operations.md) §9 step 1.

### 9.1 What step 1 built

- `EnrollmentTransaction` keeps, beside the status:
  - the sponsor's maintenance reason (INS04) and the coverage dates sent;
  - the position in the file ("INS 3", "INS 3, dependent 1", "dependent 2 without its subscriber");
  - a **reason code**;
  - the individual errors (code, field, message).
- **Reason codes:**
  - `VALIDATION_FAILED`, with each failed rule;
  - `SUBSCRIBER_NOT_FOUND`;
  - `DEPENDENT_NOT_IDENTIFIED`;
  - `INVALID_TRANSACTION`;
  - `DOWNSTREAM_REJECTED` or `DOWNSTREAM_UNAVAILABLE`: a 5xx or 429 from member-, coverage- or sponsor-service, safe to reprocess;
  - `CONCURRENT_UPDATE`;
  - `UNEXPECTED_ERROR`;
  - `TERM_SKIPPED_NOT_ON_FILE`: a note on an accepted termination that found no one to terminate.
- Stored messages follow the import's existing rule: an unexpected exception's text stays in the log. A downstream refusal is recorded by service and HTTP status, not by its body.
- Each transaction from an uploaded or SFTP file carries `EdiFileId`, the EDI file record it came in ([edi-operations.md](edi-operations.md) §3).
- member-service passes the fields through on `GET /api/v1/members/{id}/834-transactions`. The member record's Enrollment tab shows:
  - the file and position;
  - the sponsor's reason (INS04) in its own column;
  - a "Why" column with the reason in words and each error.
  
  EDI Transactions shows the same under each 834 transaction's status.

### 9.2 What step 2 built

- **The `EnrollmentException` record** (collection `enrollment-exceptions`), one per rejected 834 row, opened by the import as it records the rejection. Its id is the rejected transaction's id and its number is `EX-` plus eight characters. It keeps:
  - the reason code and errors, the file, file position and EDI file link;
  - the change as received: the subscriber loop, or the dependent. A dependent that failed inside its subscriber's loop is kept as a standalone dependent with what it inherited from that loop (subscriber id, group, maintenance type and reason, dates), so it can be reprocessed alone;
  - status (Open, Held, Resolved), resolution (Applied, SentBackToSponsor, NoAction), the hold date, attempts, every action with who did it and its outcome, and every transaction that touched it.

  Accepted rows open nothing. Recording is best-effort: a store failure is logged and never fails the import.
- **Specialist actions** (`api/v1/enrollment/exceptions/{id}/…`, `enrollment:process`):
  - `reprocess`, optionally with a corrected subscriber or dependent (it must be the same kind);
  - `match`, to set the subscriber id and reprocess;
  - `hold` until a date (future, at most 90 days), with a note;
  - `send-back` to the sponsor and `no-action`, each with a required note.

  A reprocess runs the change through the normal import as batch `{batch}-RP{n}`. If it is accepted the exception resolves as Applied; if it fails again the exception stays open with the new reason and errors, and no second exception is opened. Changes are compare-and-swap on a version (409 on a conflict); a resolved exception cannot change (422).
- **Reads** (`enrollment:read`): `GET api/v1/enrollment/exceptions` (status, reason, group, member, EDI file) and `GET …/{id}`.
- **Work items.** `GET …/exceptions/work-snapshots` pages the workable exceptions (open, or held past their date) with no PHI: id, number, reason, group, member id, maintenance type, created date. work-management-service reconciles against it every 2 minutes (`WorkManagement:EnrollmentReconcile`; also `POST api/v1/work/admin/reconcile/enrollment`): each becomes an `Enrollment834` item, routed by reason code, and items whose exception is resolved or held are closed. Default queues: Retry (downstream unavailable, concurrent update; 2-day SLA), Matching (subscriber or dependent not found, possible duplicate; 5 days), Retro (3 days), Data (validation, invalid, downstream rejected, plan code, state gate; 5 days) and Other. An item opens `/enrollment-ops/exceptions/{id}`.
- **Deferred to step 3:** the exception screen. Until it exists the demo seed gives no one membership in the enrollment queues, so Get Next never opens a page that is not there; supervisors see the items in the work list.

## 10. Decisions (2026-10-22)

1. **Full-replace guard:** hold the file when it would term more than 10% of the sponsor's members, or more than 5 for sponsors under 50 members, whichever is smaller (§5).
2. **Commercial retro adds:** 60 days as the commercial line-of-business default, overridable per benefit plan and then per sponsor. Waiting periods are a separate setting at the same levels (§6).
3. **Pilot states:** the pilot search targets California local Medi-Cal plans (public RFPs; D-SNP expansion to 41 counties in January 2026 means local plans need Medicare reconciliation) and Oregon CCOs (CCO 3.0 procurement with the 2027 waiver; the strictest published reconciliation rules). Texas stays warm through existing relationships. Discrepancy reports are a template per state: **Oregon first**, as the reference implementation (public, strictest specification), then California and Medicare TRR/MMR reconciliation when a California plan engages.
4. **Sponsor self-service:** sponsors correct errors in their next 834. A sponsor portal where employer groups fix errors themselves is on the roadmap, after the error reports (step 5).
5. **Orphan dependents across files:** a dependent rejected as `SUBSCRIBER_NOT_FOUND` starts as a held exception for a grace period instead of entering the Matching queue. When any import or manual change adds that subscriber, CHO reprocesses the tenant's orphans that name the subscriber id, and a periodic sweep retries held orphans as a backstop. A successful retry resolves the exception as Applied ("automatic retry, subscriber arrived in file X"). An orphan still unmatched when the grace period ends opens in the Matching queue with its history. The grace period is a setting at line of business, overridable per benefit plan and then per sponsor (the most specific wins). There is no published industry standard; companion guides require the subscriber in the same or an earlier transmission ([SC PEBA 834 guide](https://www.procurement.sc.gov/files/Attachment_8_--_834_Dental_Companion_Guide.pdf)), and the Marketplace sends each enrollment group as one transaction ([CMS 834 companion guide](https://ichraconnect.hioscar.com/CMS_X12_834_Companion_Guide_v7.2.pdf)), so an orphan is either a late subscriber in the sponsor's next file or an error. The window therefore covers one file cycle. Line-of-business defaults: **commercial group 7 days** (weekly files are the common employer cadence; a sponsor sending daily can set 2); **individual / Marketplace 0**; **Medicaid 0** (the state's file is complete, and a real orphan is a discrepancy to report). Once expected-file schedules exist (EDI step 5), a sponsor's window can be set as "one expected file cycle" instead of a day count. Within one file, the parser already holds a dependent until its transaction set ends. Built after the large-file intake (edi-operations.md §11), since both change the import.

Sources for 3: [PointClickCare on California's D-SNP expansion](https://pointclickcare.com/blog/our-markets/health-plans/californias-dsnp-expansion-changed-the-stakes-for-medical-plans/), [OHA CCO 3.0 procurement](https://www.oregon.gov/oha/OHPB/MtgDocs/6.%20CCO%203.0%20Procurement.pdf), [OHA CCO Enrollment Reconciliation and Discrepancy Reporting Guide 2025](https://www.oregon.gov/oha/HSD/OHP/CCO/CCO%20Enrollment%20Reconciliation%20and%20Discrepancy%20Reporting%20Guide%202025.pdf), [Fierce Healthcare on the Texas STAR/CHIP injunction](https://www.fiercehealthcare.com/payers/texas-pauses-116b-medicaid-contract-awards-state-health-plans-relieved).

## Sources

- [Oregon OHA, Enrollment Discrepancy Reporting Guide](https://www.oregon.gov/oha/HSD/OHP/CCO/Enrollment-Discrepancy-Reporting-Guide.pdf)
- [Oregon OHA, 2025 changes to enrollment reconciliation certification](https://oregon.gov/oha/HSD/OHP/Announcements/2025%20Changes%20to%20Enrollment%20Reconciliation%20Certification%20documents.pdf)
- [CMS, Enrollment Reconciliation and IC834 workgroup](https://www.hhs.gov/guidance/sites/default/files/hhs-guidance-documents/CMS/enrollment-reconciliation-&-ic834-workgroup.pdf)
- [GuideWell, 834 Summary Error Report](https://files.guidewell.com/m/3a13abea01829941/original/834-summary-error-report.pdf)
- [Washington HCA, PEBB error correction guidelines](https://www.hca.wa.gov/assets/perspay/pebb-error-correction-guidelines.pdf)
- [45 CFR 147.128, Rescissions](https://www.law.cornell.edu/cfr/text/45/147.128)
- [McGriff, Retroactive termination of individuals' benefits coverage](https://mcgriff.com/resources/articles/retroactive-termination-individual-benefits.html)
- [NC Medicaid, Retro-enrollment changes and impact on claims (2025)](https://medicaid.ncdhhs.gov/blog/2025/02/11/important-update-retro-enrollment-changes-and-impact-claims)
- [CMS, Medicare Advantage enrollment reconciliation process](https://www.cms.gov/research-statistics-data-and-systems/cms-information-technology/mapdhelpdesk/downloads/enrollment_recon_process_final.pdf)
