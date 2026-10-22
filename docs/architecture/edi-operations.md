# EDI operations: files, acknowledgments and trading partners

**Status:** Draft for review (2026-10-21).
**Scope:** How a plan's EDI analysts see and run every X12 file in and out of CHO:
- one file record for every type;
- acknowledgments;
- control numbers;
- trading partners and expected-file schedules;
- the hand-off of record-level problems to the business teams that own them.

**Related:**
- [enrollment-operations.md](enrollment-operations.md): what happens to 834 members after the file is accepted.
- [healthcare-transaction-gateway.md](healthcare-transaction-gateway.md): CHO as submitter through a clearinghouse.
- [payer-claim-attachment-receiver.md](payer-claim-attachment-receiver.md) and [payer-eligibility-responder.md](payer-eligibility-responder.md): CHO as payer for 275 and 270.
- [SFTP-ARCHITECTURE.md](SFTP-ARCHITECTURE.md): the SFTP and Argo ingest paths.
- [claim-remittance-generation.md](claim-remittance-generation.md): 835 generation.
- [work-management.md](work-management.md): where record-level exceptions are worked.

---

## 1. The file layer and the record layer

Every X12 transaction has two layers of problems, owned by different people:

| Layer | Question | Owner |
|---|---|---|
| **File** | Did it arrive when expected, pass the envelope and syntax checks, and get acknowledged? Did what we sent get acknowledged? | **EDI analyst** |
| **Record** | Could each member, claim, attachment or payment in it be applied? | The business team for that transaction |

EDI Operations is the analyst's console for the file layer, across every transaction type. It counts and links record-level outcomes but never fixes them.

| Transaction | Direction | Record-level owner, and where they work |
|---|---|---|
| 834 enrollment | In | Enrollment specialists: Enrollment Ops ([enrollment-operations.md](enrollment-operations.md)) |
| 837P/I/D claims | In | Claims intake. Front-end rejections go back to the provider on the 277CA; accepted claims that pend go to the examiner queues that already exist. Provider relations sees repeat-rejecting submitters. |
| 275 attachments | In | Claims and UM: the unmatched-attachments queue |
| 820 premium payments | In | Finance: remittance exceptions (already built) |
| 835 remittance, 820 out | Out | Finance: confirms delivery and reconciles payments |
| 277CA, 999, TA1, 271 | Out | EDI analyst only |
| 270/271, 276/277, 278 (real time) | In / out | EDI analyst only: volume, latency and error rate |

## 2. What exists today

**In place:**
- A shared envelope check (ISA/IEA) with duplicate-ISA13 rejection, TA1 acknowledgments with sequenced control numbers, and a record of outbound interchanges. Each of claims, enrollment, eligibility and payments exposes `{prefix}/ta1` and `{prefix}/outbound`.
- One record per 837 claim (`ClaimImportTransaction`), with errors, ISA13/GS06/ST02, the submitter, the 999 and TA1 control numbers, and SNIP warnings. There is also one record per 834 member (`EnrollmentTransaction`) and a run summary per 834 file.
- The EDI Transactions page (`/edi-transactions`), which lists recent 834 runs and transactions and 837 claim transactions.
- 820 remittance exceptions with a resolve action, the only EDI rework path today.

**Missing:**
1. **No file record.** Nothing lists the files themselves: records are kept per claim or per member, and a clean file with ISA14=0 leaves only a duplicate-check key.
2. **SFTP bypasses the checks.** The Argo SFTP routes for 837 and 834 post parsed JSON straight to the services. They skip the envelope and duplicate checks, the SNIP edits and the transaction records.
3. **Outbound control numbers are not assigned properly.** ISA13 comes from the clock or a random number. GS06 and ST02 are hard-coded to `1` and `0001` for 277CA, 835 and 271. Only TA1 uses the sequence store.
4. **Trading partners are not linked to files.** No file record points at a partner, nothing looks a partner up by ISA06/GS02, and `LastSuccessfulTransmission` is never written. The Trading Partners page shows mock data.
5. **Inbound and outbound gaps:**
   - nothing reads a partner's 999;
   - outbound 835s are generated but never delivered;
   - the clearinghouse gateway's operator views are development-only.
6. **No expected-file monitoring.** A sponsor's Monday 834 that never arrives goes unnoticed.

## 3. The file record

An `EdiFile` is written for every interchange in or out, whichever path it took (upload, SFTP, gateway, real-time):

| Field | Purpose |
|---|---|
| Direction, transaction type(s), version | 834, 837P, 835 ... (one file can hold several groups) |
| Trading partner, sender/receiver IDs | Resolved from ISA06/ISA08 and GS02/GS03 against trading-partner-service |
| ISA13, each GS06 and ST02 | Searchable control numbers |
| Path and source | Upload, SFTP (with path), gateway, API; file name; size; SHA-256 |
| Raw file | Stored in the document store, never inline; opening it needs `edi:raw` and is audited |
| Lifecycle | Received → envelope checked → acknowledged → parsed → applied → closed, with a time for each |
| Acknowledgments | TA1 and 999 we sent (inbound) or received (outbound), each with its accept/reject code |
| Outcome counts | Accepted / pending / rejected records, linking to the record views (claims, members, attachments, payments) |
| Errors | Envelope and syntax errors at file level; record errors stay on the records |

Inbound duplicates are rejected as today (TA1 note 025) and also recorded as files with status `Duplicate`, so the analyst can see that one was sent twice.

Outbound files get the same record when generated, plus delivery attempts and status (queued, sent, acknowledged, rejected), whichever channel delivers them.

## 4. One intake path

SFTP, upload and gateway files all go through the same intake:
1. write the file record;
2. run the envelope and duplicate check;
3. send the TA1/999;
4. hand off to the transaction's service, which writes its per-record rows linked to the file.

The Argo workflows stop posting parsed JSON to the services. They hand the raw file to the same endpoint the upload uses, so SFTP files get the same checks and history.

## 5. Control numbers

Every outbound interchange takes ISA13 from the sequence store already used for TA1: per tenant and sender/receiver pair, nine digits, never reused within the duplicate window. GS06 and ST02 are assigned per interchange.

The 277CA, 835, 271, 999, 824, encounter 837 and FMMIS builders all take numbers from the sequence store instead of clocks, randoms or constants. This closes the audit's outbound control number finding. Regenerating a file for download reuses its recorded numbers; re-sending as a new interchange takes new ones and records the link to the original.

## 6. Trading partners

trading-partner-service stays the source of partner settings. It gains:
- **sender/receiver ID lookup**, so every file is attributed to a partner, and an unknown sender is held as `UnknownPartner` rather than processed;
- **an expected-file schedule** per partner and transaction type, for example "834 change file every weekday by 06:00 MT" or "834 audit file monthly by the 5th";
- **last received / last sent** times, written by the intake.

The Trading Partners page shows real data: each partner's settings, schedule, recent files and error rate.

## 7. The EDI Operations page

For EDI analysts (`edi:read`; actions need `edi:operate`):

1. **Files.** One list across every type and direction. It filters by partner, type, direction, status, date and control number, and a search box takes any ISA13/GS06/ST02 or file name.
2. **File detail.** It shows:
   - the envelope and control numbers;
   - the lifecycle timeline and acknowledgments;
   - errors and outcome counts, each count linking to the business view: rejected 834 members to Enrollment Ops exceptions, rejected 837 claims to the claim list filtered by file, pended claims to examiner queues.

   Actions:
   - reprocess, after a configuration fix such as a partner setting or plan-code mapping;
   - mark a file as a known duplicate;
   - re-send an outbound file;
   - download the raw file (`edi:raw`, audited).
3. **Expected files.** Today's schedule: received, late or missing. A missing file raises an `EdiMissingFile` work item for the EDI queue, and the sponsor's Enrollment Ops view shows it too.
4. **Acknowledgments.** TA1 and 999 rejections we sent (the partner must fix) and received (we must fix), plus unmatched acknowledgments.
5. **Real time.** 270/271, 276/277 and 278 volume, latency and error rate by partner.

The Dashboard's EDI-volume tile returns as a summary of this page.

**PHI.** File metadata and counts carry no PHI and need only `edi:read`. Drilling into members, claims or payments needs that domain's read permission, so an analyst can see "14 members rejected" without seeing who. The raw file is PHI and needs `edi:raw`.

## 8. How 837s line up with 834s

The two share the same file layer; only the record layer differs:

| | 834 | 837 |
|---|---|---|
| File record, TA1/999, duplicate check | EDI Operations | EDI Operations |
| Record rejected at the front end | Enrollment exception: the plan fixes it or sends it back to the sponsor | 277CA rejection: the provider fixes it and resubmits; the plan does not edit claims |
| Record accepted, needs work | Retro approval, reconciliation difference | Pend, worked in the examiner queues |
| History on the record | Member → Enrollment tab: every 834 that touched the member | Claim → source file, control numbers, 999 and 277CA |

## 9. Build order

1. **File record and one intake path** for 834 and 837 (upload and SFTP), with the raw file in the document store.
2. **Control numbers from the sequence store** for every outbound builder; outbound files recorded.
3. **Trading partner lookup** on intake; last received/sent; the Trading Partners page on real data.
4. **EDI Operations page:** files, file detail, acknowledgments; links into the business views.
5. **Expected-file schedules** and `EdiMissingFile` work items.
6. **Reprocess and re-send** actions.
7. **Other types on the file record:** 275, 820, 835 out (with delivery), 271 and 277CA out.
8. **Partner 999 intake;** real-time monitoring tiles; the dashboard tile.

## 10. Open questions

1. **Raw file retention:** how long to keep raw inbound files (the 7–10 year record rules, or shorter with the parsed records kept)?
2. **Unknown sender:** hold every file from an unknown sender, or allow a per-tenant setting to accept and attribute later?
3. **835 delivery:** SFTP per partner first, or through the clearinghouse gateway?
4. **Permissions:** add `edi:read` / `edi:operate` / `edi:raw` as new permissions, granting `edi:read` to ClaimsSupervisor and EnrollmentSpecialist?
