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
1. ~~**No file record.**~~ *Built for 834 and 837 (§9.1).* Other transaction types still keep records per transaction only.
2. ~~**SFTP bypasses the checks.**~~ *Fixed for 834 and 837 (§9.1):* the Argo SFTP workflows now send the raw file through the upload path.
3. ~~**Outbound control numbers are not assigned properly.**~~ *Fixed (§9.2):* every outbound builder takes ISA13, GS06 and ST02 from the sequence store, and outbound files are recorded.
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

Every outbound interchange takes its numbers from the sequence store already used for TA1, in one atomic increment per number, scoped to the tenant:
- **ISA13 / IEA02:** per tenant and ISA sender → receiver pair (the TA1's own sequence), nine digits, wrapping from 999999999 to 1, so it is never reused within the duplicate window;
- **GS06 / GE02:** per tenant and application sender → receiver (GS02 → GS03);
- **ST02 / SE02:** 0001, 0002, … within the interchange.

The 277CA, 835, 271, 999, 824, encounter 837 and FMMIS builders all take numbers from the sequence store instead of clocks, randoms or constants. This closes the audit's outbound control number finding. Regenerating a file for download reuses its recorded numbers; re-sending as a new interchange takes new ones and records the link to the original (the re-send action is step 6).

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

1. **File record and one intake path** for 834 and 837 (upload and SFTP), with the raw file in the document store. *Built 2026-10 (§9.1), except the raw file in the document store, which moves to step 4.*
2. **Control numbers from the sequence store** for every outbound builder; outbound files recorded. *Built 2026-10 (§9.2).*
   - **Next, ahead of step 3: large-file intake (§11)**, so state audit 834s arriving over SFTP are not refused.
3. **Trading partner lookup** on intake; last received/sent; the Trading Partners page on real data.
4. **EDI Operations page:** files, file detail, acknowledgments; links into the business views.
5. **Expected-file schedules** and `EdiMissingFile` work items.
6. **Reprocess and re-send** actions.
7. **Other types on the file record:** 275, 820, 835 out (with delivery), 271 and 277CA out.
8. **Partner 999 intake;** real-time monitoring tiles; the dashboard tile.

### 9.1 What step 1 built

- **The `EdiFile` record** (shared, `CloudHealthOffice.Infrastructure.Edi.Files`; collection `edi-files` in each service's database), written for every raw 834 and 837 the services receive. It holds:
  - the source (Upload, Sftp, Gateway, Api), file name, SFTP path and who handed it in;
  - the size and SHA-256;
  - sender and receiver, version and usage indicator;
  - every ISA13, GS06 and ST02;
  - the lifecycle with a time for each step (Received → EnvelopeChecked → Acknowledged → Parsed → Applied, or Rejected / Duplicate);
  - the TA1 and 999 codes and control numbers;
  - accepted and rejected record counts, and file-level errors.

  Recording is best-effort: a store failure is logged and never fails the intake.
- **Every outcome is recorded:** accepted files, envelope rejections, duplicate ISA13s (status `Duplicate`, a file of their own), unreadable files and files whose contents could not be parsed.
- **Links from the records:** `ClaimImportTransaction.EdiFileId` and `EnrollmentImportRun.EdiFileId`, and `ediFileId` in the raw 837 and 834 responses.
- **Read API:** `GET api/v1/claims/edi/files` and `GET api/v1/enrollment/edi/files` (filters: direction, type, status, from, to, controlNumber matching any ISA13/GS06/ST02, limit), and `.../edi/files/{id}`. Every query is tenant-scoped, and the service's read permission applies until `edi:read` exists.
- **One intake path:** the Argo workflows `x12-837-ingest` and `x12-834-enrollment-import` no longer parse files or post JSON. They post each raw file to `import/raw837` and `import/raw834` with `source=Sftp` and the SFTP path, so SFTP files get:
  - the envelope and duplicate check, and the TA1;
  - for 837s, SNIP and the 999;
  - the file record.

  The 837 workflow no longer publishes to the retired claims-adjudication Kafka topic.
- **Deferred:**
  - **The raw file in the document store.** The catalog needs an EDI-file subject and `edi:raw` first (step 4), so `RawDocumentId` is empty for now and the SHA-256 identifies the file.
  - **The other transaction types** (step 7).

### 9.2 What step 2 built

- **One allocator** (`IX12ControlNumberAllocator`, shared, `CloudHealthOffice.Infrastructure.Edi.Interchange`). Builders keep writing placeholder numbers; the allocator reads the finished interchange and stamps ISA13/IEA02, every GS06/GE02 and every ST02/SE02 with numbers from the sequence store (collection `x12-control-sequences`, `findOneAndUpdate` with `$inc`, so concurrent sends never share a number). It is registered by `AddChoX12ControlNumbers()` (included in `AddChoX12Interchange`).
  - A sequence store failure fails the send: nothing goes out with a number that may repeat.
  - Content with no ISA that can carry the numbers is sent as built and takes no numbers.
- **Every builder uses it:**

  | Outbound | Where | Re-download reuses its numbers | `EdiFile` recorded |
  |---|---|---|---|
  | 277CA | claims `GET api/claims/{id}/277ca` | Yes (per claim and status) | Yes |
  | 999 for a raw 837 | claims `import/raw837` (the validate-only call is numbered, not recorded) | n/a, sent once | Yes, linked to the inbound file |
  | 835, batch (ERA envelopes) | payment `TrackingEraEnvelopeRepository` | n/a, one per envelope | Yes |
  | 835, per payment | payment `GET api/payments/{id}/835` | Yes | Yes |
  | 271 | eligibility real-time `270` and `GET {id}/271` | Yes: the download carries the real-time reply's numbers | Yes |
  | 835, capitation statement | capitation `statements/{id}/era` | Yes (per statement and payer) | Deferred |
  | Encounter 837 | encounter `GET {id}/837` | Yes (per encounter and control number) | Deferred |
  | 999 / 824 for a 275 | attachment `{id}/acknowledgment` | n/a, sent once | Deferred |
  | FMMIS | `FmmisFileBuilder.BuildAsync` | n/a | Deferred (the builder has no production caller yet) |

  Every row writes an outbound interchange record (for TA1 matching) that now also keeps the GS06 and ST02 lists, which is what lets a re-download reuse all of them.
- **Outbound files:** an `EdiFile` with direction `Outbound`, source `Generated`, status `Sent`, the source reference (claim, payment, response, file) and the ids of its outbound interchange records; each record links back by `EdiFileId`. Recording is best-effort like step 1.
- **Deferred:**
  - `EdiFile` recording in capitation, encounter and attachment, which register no file store yet (step 7). Their outbound interchange records are written.
  - A re-download whose ISA sender or receiver differs from the first issue (the 271 download names the payer and provider ids where the real-time reply swapped the 270's) keeps the first numbers; the step 3 partner lookup will make both use the same ids.

## 10. Decisions (2026-10-22)

1. **Raw file retention:** 10 years by default, per tenant and transaction type; a tenant may lengthen it but not go below a platform floor. Files move to archive storage after about 90 days. Deletion at the end of retention is a logged job. Opening a raw file needs `edi:raw` and is audited.
2. **Unknown sender:** hold the file as `UnknownPartner` and raise an EDI work item. When the SFTP folder belongs to a known partner, the item says so ("sender ID 123 in Sponsor X's folder") and one action adds the ID to the partner and releases the file. Nothing is accepted automatically, including a known folder with an unknown ID. The TA1/999 is sent only after release.
3. **835 delivery:** through a clearinghouse first, configured per tenant (Stedi first, then the pilot plan's own clearinghouse, likely Availity); direct SFTP for large health systems later. A record of which providers are enrolled for ERA comes with it, and every 835 sent is on the file record with its delivery status.
4. **Permissions:**

   | Permission | Allows | Roles |
   |---|---|---|
   | `edi:read` | Files, file detail, acknowledgments, counts; no PHI | New **EdiAnalyst**, ClaimsSupervisor, EnrollmentSpecialist, ComplianceOfficer |
   | `edi:operate` | Reprocess, mark duplicate, re-send, release held files, partner IDs and schedules | EdiAnalyst |
   | `edi:raw` | Download a raw file (audited) | Nobody by default; granted by name |

   `edi:raw` is exact-only: no wildcard role and no service token satisfies it. Drilling from a file into members or claims still needs that domain's read permission.
5. **Large files:** see §11.

## 11. Large-file intake

State audit 834s run to hundreds of megabytes (about 0.5–1 GB for a million members). Today the raw intake endpoints cap uploads at 20 MB, and the whole path is in memory: the request is read into one string, the envelope check copies it, the parser builds every member before the first is applied, and the import runs inside the HTTP request. enrollment-import-service runs with a 512 MiB memory limit, so raising the cap alone would move the failure from a 413 to an out-of-memory restart.

The design (decided 2026-10-22):

1. **Stage, then reference.** The SFTP workflow writes the file to object storage (the raw-file archive of decision 1) and posts a reference, not the bytes. Portal uploads stream to the same storage. Nothing large travels through HTTP or sits in one string.
2. **Envelope check by streaming.** ISA, GS/GE and IEA are read as the file streams; the duplicate ISA13 check and TA1 work as today.
3. **Streaming 834 parser.** Members are produced one INS loop at a time. Dependents are matched to their subscriber within the transaction set as today; a dependent whose subscriber has not been seen is held until the set ends.
4. **Import as a run, not a request.** The intake answers `202 Accepted` with the import run and file ids; the run applies members in chunks, records progress on the import run and the file record, and can resume after a restart from the last committed chunk.
5. **837s** follow the same path once 834 is done; their files are smaller, so they come second.
