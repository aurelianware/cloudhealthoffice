# Document store: one home for every document plan operations touch

**Status:** Draft for review (2026-10-15). Steps 1 (storage providers) and 2 (the catalog, document-service) are built (2026-10-20); see §11 for what step 2 includes and leaves for later. Decided: the catalog is its own service (§10, question 1).
**Scope:** Where documents live, how they get in, how they link to the work they support, and who may see them:
- claim attachments and claim RFAI responses;
- authorization clinicals and CMS-0057-F prior-auth documents (CRD/DTR/PAS);
- appeal records and letters;
- member documents and correspondence;
- call recordings and transcripts (see [cho-service.md](cho-service.md));
- paper and fax claim images (see [claim-review-workbench.md](claim-review-workbench.md) §5);
- letters CHO generates (see [correspondence.md](correspondence.md)).

---

## 1. Principles

1. **Blob storage holds the bytes; a catalog holds the meaning.**
   - Files live in object storage (Azure Blob, Google Cloud Storage or S3), whichever cloud the plan runs on.
   - Everything about a file lives in a document record: what it is, where it came from, what it supports, and who may see it.
2. **Users never touch storage.** Every read goes through the document service. It decides, from the screen the user is on and their permissions, whether they may see the file, then serves it through a short-lived link or a stream. Every read is audited.
3. **Store once, link many times.** One set of medical records can support a claim, its appeal, and the authorization behind both. It's stored once and linked three times.
4. **Write once.** A file is never overwritten. A correction is a new version that points back to the one it replaces. Retention and legal hold apply to the record and its versions together.
5. **Everything that asked for a document gets an answer.** A document that responds to a request (claim RFAI, authorization CDex task, appeal records request) is matched to that request. The matching reopens the waiting work and records the clean-date event (see [claim-review-workbench.md](claim-review-workbench.md) §3).

## 2. What exists today

CHO already has four document paths, each built for one use:

| Piece | What it does | Gaps |
|---|---|---|
| `CloudHealthOffice.DocumentStore` (`IDocumentStore`) | Upload (with tags) / download / exists / delete / tags / properties / pre-signed upload links against a container and blob name. Azure Blob, Google Cloud Storage, S3 and in-memory providers, chosen by `DocumentStore:Provider` (`AddChoDocumentStore`); one contract test suite runs every provider against its emulator. | No catalog, versioning or access control. |
| **attachment-service** (`Attachment`) | X12 275 and portal uploads. One optional `ClaimId`, `AuthorizationId` and `AppealId` each, plus an `RFAIReference`. Generates 999/824 acknowledgments. Stores through `IDocumentStore`. | One link per kind, so a document can't support two claims. No retention, legal hold, sensitivity or access audit. Download streams to any caller in the tenant. |
| **member-document-service** (`MemberDocument`) | Member documents. Has the most mature model: retention policy (`DEFAULT-10Y`), legal hold with history, SHA-256 hash, content sniffing, two-phase upload (pending → finalize), and a FHIR `DocumentReference` endpoint. | Member-scoped only. (Files through the catalog since step 3; see §13.) |
| **rfai-service** (`RfaiCase`) | Authorization RFAIs (CDex Task, PAS-07). Received attachments carry an attachment control number, storage key and hash. | **Authorization only:** there's no claim RFAI. Attachment metadata lives inside the case rather than in a shared catalog. |
| Claims gateway (`IClaimAttachmentContentStore`) | Attachments that arrive through the clearinghouse gateway, with a scan status. | A separate store, not linked to the others. |

Also:
- The 837 **PWK** segment (paperwork attachment indicator) is kept on the claim since step 3 (§14), so a 275 is matched to the claim that announced its control number.
- In the portal, only the member details dialog shows documents. Claim, authorization and appeal screens have none.

The design below **consolidates these rather than adding a fifth path.** member-document-service's model is the starting point, because it already has retention, legal hold, hashing and FHIR.

## 3. The document record

```
Document
  id, tenantId, version, supersedesId?
  type          one of the taxonomy below
  title, contentType, sizeBytes, sha256, pageCount?
  source        channel + external ids (see §4)
  links[]       { kind: Member|Claim|ClaimLine|Authorization|Appeal|Grievance|Call|Provider|RfaiRequest|PaymentRun, id }
  sensitivity[] PART2_SUD | BEHAVIORAL_HEALTH | HIV | REPRODUCTIVE | GENETIC | MINOR_CONFIDENTIAL
  retention     policyId, retainUntil, legalHold { set, by, at, reason, history[] }
  scan          { status: Pending|Clean|Infected|Failed, engine, at }
  extracted     { text?, fields?, ocrConfidence? }      // for search and paper-claim keying
  createdBy, createdAt, receivedAt                       // receivedAt drives clean-date and timeliness
```

**Type taxonomy** (a tenant can extend it):
- **Clinical:** medical record, operative report, lab/imaging result, discharge summary.
- **Billing:** itemized bill, invoice, prior-payer EOB/835, paper claim image (CMS-1500 / UB-04).
- **Authorization:** clinical justification, DTR questionnaire response, PAS supporting bundle.
- **Appeals and grievances:** request, appointment of representative, case file, decision letter.
- **Correspondence:** inbound letter, outbound letter (RFAI, denial, acknowledgment, EOB), email.
- **Member:** ID card, enrollment form, consent and authorization forms, coverage notices.
- **Service:** call recording, call transcript, call summary.

## 4. Getting documents in

| Channel | How it arrives | Matching |
|---|---|---|
| **X12 275** (CMS-0053-F: 006020X314, with the 277 RFAI 006020X313) | EDI intake, then attachment service | Attachment control number → the claim's PWK, or the open RFAI request. |
| **FHIR**: CDex `$submit-attachment`, PAS bundle, DTR `QuestionnaireResponse` | fhir-service | CDex Task id → RFAI request; PAS claim → authorization. |
| **Provider portal / Availity upload** | Upload API (two-phase: pending → scan → finalize) | Chosen claim, authorization or appeal, or an RFAI tracking number. |
| **Fax and mail scan** | Intake folder or fax gateway, then OCR | OCR finds a claim number, member id or RFAI tracking number. Anything unmatched goes to a person through an "Unmatched documents" work queue. |
| **Call recording / transcript** | Phone platform webhook | Call record → member, plus anything discussed on the call. |
| **Generated by CHO** | The correspondence service ([correspondence.md](correspondence.md)) stores each rendered letter write-once | Linked to every subject the letter concerns (case, claim, authorization, member, provider). |

On every path:
- **Hashing:** the SHA-256 hash is recorded at upload. A duplicate upload links to the existing file instead of storing it again.
- **Scanning:** nothing opens before the virus scan says *Clean*. Infected files are quarantined.
- **Received date:** `receivedAt` is the front-door time (clearinghouse or portal receipt, or fax arrival), never the time it was processed. That's what the clean date and appeal clocks use.

## 5. Who may see what

Access is decided by **permission plus context**, not by tenant alone:

1. The caller needs `documents:read`, plus the read permission for the subject they're working: a claim examiner opening documents on a claim needs claim access.
2. The document must be **linked to the subject on screen**. The claim screen asks for "documents for claim X", never "document Y".
3. **Sensitivity tags** need an extra permission, and for 42 CFR Part 2 records a consent check through consent-service. A call rep sees a member's correspondence but not Part 2 records.
4. **Files are served safely:** content comes as a stream or a signed link valid for a few minutes. Storage paths never reach the browser.
5. **Every view and download is recorded** in an append-only access audit: who, which document, when, from which screen and subject, and whether it was allowed. Denied attempts are recorded too. This answers "who looked at this member's records" for HIPAA accounting and investigations.

Writing (upload, link, unlink, new version) needs `documents:write`. Retention changes and legal hold need `records:legal-hold`, the permission that already exists.

## 6. Retention and legal hold

- **Retention policies are named and dated.** The default is 10 years, matching member-document-service's `DEFAULT-10Y` and the Medicare Advantage record-retention period. A tenant can set a policy per document type and state.
- **Legal hold** blocks deletion of a document and all its versions until released; set and release are both audited.
- **Expiry:** a nightly job deletes expired documents that aren't on hold and records each deletion. The catalog record keeps a tombstone (id, type, hash, deleted when and why).

## 7. In the portal

One **Documents panel** component appears on claim, authorization, appeal, member, provider and call screens. It shows:
- type, source, received date, size and scan status, plus "also linked to" (e.g., this record also supports appeal A-123);
- a viewer for PDF and images, and download where the user is allowed;
- an upload button that opens with the current subject already linked;
- an "Open requests" strip for RFAIs: what was asked, when it's due, and what has arrived.

*Built 2026-10-23 (§15): the panel, read-only, on claim, authorization, appeal and provider. The viewer, upload and the RFAI strip are not built.*

## 8. Storage providers

Extend `IDocumentStore` with **Google Cloud Storage** and **S3** providers alongside Azure Blob, chosen by configuration. The GKE demo then has durable storage, and AWS deployments work.

*Built in step 1:*
- **Configuration:** `DocumentStore:Provider` is `Azure`, `Gcs`, `S3` or `InMemory` (Development only). With no provider set, a deployment that has `BlobStorage:ConnectionString` stays on Azure; otherwise the service refuses to start.
- **Layout:** Azure keeps one container per CHO container. Google Cloud Storage and S3 use one bucket per deployment, with each CHO container as a prefix, because bucket names are global.
- **Tags** (retention policy, retention date, legal hold) are Azure blob index tags, GCS object metadata and S3 object tags (10 per object). Azure and S3 lifecycle rules can filter on them; GCS lifecycle rules cannot match custom metadata, so on GCS the tags are a record only. Storage-level enforcement of holds (GCS object holds, S3 Object Lock, Azure immutability) comes with write-once, below.
- **Uploads:** finalize checks the staged object's size before reading it, since no cloud's upload link caps size the same way. A staging object left by an abandoned upload is deleted after a day on GCS (bucket lifecycle rule); Azure and S3 deployments need the same rule on the `.upload` suffix.
- **Pre-signed uploads:** Azure SAS, GCS V4 signed URLs (on GKE, signed by IAM as the Workload Identity service account) and S3 presigned PUTs. The link carries the headers the client must send, so clients never need to know which cloud is behind it.
- **GKE demo:** Terraform creates a private, versioned documents bucket and a `cho-documents` service account that only attachment-service and member-document-service can act as (Workload Identity, no keys).
- **Encryption:** each tenant gets its own key-encryption key (cloud KMS).
- **Write-once:** where the provider supports it (Azure immutable blobs, GCS bucket lock, S3 Object Lock), it backs up the write-once rule. The catalog enforces it either way.

## 9. Build order

1. **Storage providers:** GCS and S3 for `IDocumentStore`; member-document-service moves onto `IDocumentStore`. *Built (2026-10-20).*
2. **Catalog:** the document record and service (evolved from member-document-service), with many-to-many links, sensitivity tags, access audit, and the documents API. *Built (2026-10-20) as a new document-service; member-document-service moves onto it later.*
3. **Move attachment-service onto the catalog:** existing `Attachment` rows migrate to documents with links. Keep the PWK attachment control number on the claim for 275 matching. *Attachments built (2026-10-20): attachment-service files through the catalog; there was no stored attachment data to migrate. Member documents built (2026-10-20, §13). PWK matching built (2026-10-21, §14).*
4. **Correspondence** ([correspondence.md](correspondence.md)), starting with appeal notices. Generated letters are the first new producer on the catalog, and ID cards move onto it in the same phase.
5. **Documents panel** on claim, authorization and appeal screens. *Built (2026-10-23), read-only, and on provider too (§15).*
6. **Claim RFAI:** extend rfai-service from authorizations to claims, with 277 RFAI out, 275 / upload / fax in, matching, and clean-date events.
7. **Fax/scan intake** with OCR and the unmatched-documents queue; paper-claim keying against the image.
8. **Call recordings and transcripts** for CHO Service.

## 10. Open questions

1. **One service or a library?** *Decided 2026-10-20: its own service* (document-service) that others call, so the access audit and retention live in one place. Generated correspondence ([correspondence.md](correspondence.md)) adds another producer, which strengthens that case.
2. **Virus scanning:** ClamAV in-cluster, or the cloud's malware scanning (Defender for Storage, GCS + Security Command Center)? It may need to be configurable per deployment.
3. **OCR:** cloud OCR (Document AI / Azure Document Intelligence / Textract) or Claude vision for paper claims? This ties to the AI-assisted keying in the workbench design.

## 11. What step 2 built

**document-service** (`src/services/document-service`), a new service; member-document-service is unchanged and moves onto it in a later step.

- **Record** as in §3: type, title, content type, size, SHA-256, source channel, links, sensitivity tags, retention policy and date, legal hold with history, scan status, `receivedAt`. Stored in MongoDB (`CatalogDocuments`); bytes through `IDocumentStore` (container `documents`, path `tenants/{tenant}/documents/{id}{ext}`).
- **API** (`/api/v1/documents`):
  - `GET ?subjectKind=&subjectId=`: a subject's documents the caller may see, plus a count of those withheld.
  - `GET /{id}?subject…` and `GET /{id}/content?subject…`: metadata and content, always opened from a linked subject.
  - `POST` multipart (upload) or JSON (pre-signed upload, then `POST /{id}/finalize` by the uploader).
  - `POST /{id}/links?subject…` and `DELETE /{id}/links/{kind}/{id}?subject…`: a document keeps at least one link.
  - `PUT /{id}/legal-hold` (`records:legal-hold`) and `GET /{id}/access-log` (`audit:read`).
- **Access (§5):** `documents:read` / `documents:write`, plus the subject's own read permission (`claims:read` for a claim, `appeals:read` for an appeal or grievance, ...). Not linked to that subject, or another tenant: 404. Call documents need `calls:read`, which no operational role holds until CHO Service defines it.
- **Sensitivity:** any tag needs `documents:read-sensitive`, granted by name to UM coordinators and compliance officers. It is *exact-only*: no wildcard (`*:read`, `*:*`, so not TenantAdmin) and no service token implies it; `calls:read` is exact-only too. **42 CFR Part 2 records are withheld from everyone** until the consent-service check exists. A refusal says only "This document is restricted."; which tag caused it goes to the audit, not the caller.
- **Access audit:** every list (with the ids shown), view, download, upload (including refused and pre-signed ones), link change and legal-hold attempt, allowed or refused, with actor, subject and the `X-CHO-Screen` header. Written before the response or the change; if it can't be written, nothing is served or changed. The access log needs `audit:read`, plus `documents:read-sensitive` for a sensitive document. Legal hold answers only the hold state.
- **Duplicates:** identical content is stored once when the uploader can already open the existing document and it isn't on legal hold; the upload then adds its links and tags to it. Otherwise it becomes its own document, answered like a first upload, so identical bytes never attach anyone to a record they can't see. Two identical uploads at the same moment can still create two records (no unique index, since separate copies are allowed).
- **Scanning:** an `IDocumentScanner` seam with no scanner wired, so documents stay `Pending`. Content is served only when `Documents:AllowUnscannedContent` is on (Development and the GKE demo); `Infected` and `Failed` never.

**Left for later:** the consent check for Part 2; a real scanner; checking that a linked subject exists in its owning service; new versions of a document; the nightly retention-expiry job with tombstones; moving member-document-service and attachment-service onto the catalog (step 3); and the portal's Documents panel (step 5).

## 12. What step 3 built (attachments)

- **attachment-service holds no storage.** It keeps the X12 275 record (control numbers, payer and provider, 999/824 acknowledgments) and files the attachment's file in the catalog, linked to its claim, authorization or appeal. The record keeps the catalog's `DocumentId`. Downloads go through the catalog, opened from that same parent.
- **Identity:** attachment-service calls document-service with its own tenant-scoped service token and names the user it acts for in `X-CHO-On-Behalf-Of`. The catalog records that user in the document (`CreatedOnBehalfOf`) and in every audit entry (`OnBehalfOf`). The header counts only from a service token; a user can't claim to act for someone else, and it never grants access. Service tokens don't imply `documents:read-sensitive`, so a service can't open sensitive documents.
- **The user's own rights still apply.** The catalog trusts the service token, so before filing or opening a file attachment-service checks that the user holds `documents:write` / `documents:read` and the parent's read permission (`claims:read`, `authorizations:read`, `appeals:read`), the same rule the catalog applies to users.
- **Order and failures:** the attachment record is saved first, then the file is filed and the record updated with its `DocumentId`; if filing fails, the record is removed, so no catalog document is left without an attachment pointing at it. The catalog's 403, 409 (e.g. not yet cleared by scanning) and 413 are passed on as such; only an unreachable catalog or a 5xx is a 502.
- **Size:** both services accept up to 64 MiB (the clearinghouse limit for a 275 attachment), and stage files on disk rather than in memory.
- **Source ids:** the catalog stores the attachment id and RFAI reference as source identifiers (`ExternalIds`), and the channel (`X12-275` when the 275 came in as EDI, `Upload` otherwise).
- **Least privilege:** attachment-service is no longer one of the services the GKE document identity trusts (`document_service_accounts`); only document-service and member-document-service touch the bucket.
- **No migration:** there were no stored attachments to keep.

Next: member-document-service onto the catalog (§13), and keeping the 837 PWK attachment control number on the claim so an incoming 275 matches by its TRN02.

## 13. What step 3 built (member documents)

- **Same API, files in the catalog.** member-document-service keeps its endpoints and its record: category and subcategory, effective and expiration dates, state, related members and resources, and the FHIR `DocumentReference` projection. The file is filed in the catalog linked to `Member:{memberId}` and to each related member, and the record keeps the catalog's `DocumentId`. Downloads come back through the catalog, opened from the document's member. Its callers (the portal's member record, idcard-service, personal-representative-service) are unchanged, apart from personal-representative-service reading `uploadPending` instead of the old staging path.
- **Type:** the category becomes the catalog type: `IdCard`, `Lab` → `LabResult`, `Enrollment` → `EnrollmentForm`, `Consent` → `ConsentForm`, `ProofOfAuthority` → `AppointmentOfRepresentative`; any other category that is one identifier (e.g. `EOB`) is a tenant type; anything else is `Other`. The title is `Category/Subcategory`. The source becomes the channel (`Generated`, `Upload`, `Mail` for received).
- **Identity and rights:** the same pattern as attachments. member-document-service calls with its own service token and names the user in `X-CHO-On-Behalf-Of`, and checks the catalog's rule in the user's name first: reading needs `members:read` and `documents:read`, filing `members:write`, `members:read` and `documents:write`. **Enrollment Specialists now hold `documents:read` and `documents:write`**, so they keep access to member documents.
- **Retention:** the catalog accepts `CoverageTerminationDate`, so a member document's retention still runs from coverage end (`TX-10Y` from termination, etc.).
- **Legal hold:** the catalog's hold is the one enforced; member-document-service changes it first and then mirrors it on its record (with the history it already kept). An upload can ask for a hold from the start (`LegalHold`, `LegalHoldReason` on the catalog's upload, `records:legal-hold` required); a held upload is never merged into an existing copy. When a service sets a hold, the catalog records the user it acts for as the hold's actor.
- **Pre-signed uploads** start in the catalog; finalize is the catalog's (size, signature, hash, scan, duplicate merge). Only the same user can finish an upload: for a service, the catalog now also requires the same `X-CHO-On-Behalf-Of`. A finalized duplicate points the record at the existing catalog document.
- **Order and failures:** the record is saved first (pending), the file filed, and the record updated with its `DocumentId`; if filing fails the record is removed. The catalog's 403, 409, 413 and 415 are passed on; anything else is 502.
- **Least privilege:** member-document-service holds no storage credentials and has left the GKE document identity; only document-service touches the bucket.
- **No migration:** there were no stored member documents to keep.
- **Known limit:** when identical content is shared by two member records (a duplicate merge), a hold placed through one record shows on the catalog document but not on the other record's mirror. The catalog still enforces it.

## 14. What step 3 built (PWK and 275 matching)

- **The 837's PWK is kept on the claim.** The inbound 837 parser reads PWK in loop 2300 (the claim) and 2400 (a line): PWK01 report type, PWK02 transmission code (EL, FX, BM, FT, EM, AA) and PWK06, the attachment control number. The claim stores them as `AttachmentReports` (with the line number for 2400). Claims-service answers `GET api/claims/attachment-control-numbers/{controlNumber}`: the tenant's claims that announced it, each by its stable version chain id (so an attachment stays with the claim through adjustments), with claim number and billing NPI and nothing about the member. Indexed on tenant + control number.
- **A 275 can name its claim by TRN02.** attachment-service accepts `AttachmentControlNumber` (the 275's TRN02). With no ClaimId, AuthorizationId or AppealId, it asks claims-service for the claim. A claim fits only when its billing NPI is the 275's provider, even when it is the only match: providers number their own attachments, so the number alone could put one provider's document on another provider's patient's claim. Exactly one fitting claim: the attachment is filed there (`Linked`); otherwise it is held. The inline lookup runs only for a caller who holds `claims:read`, since its answer names a claim; anyone else's 275 is held and linked by the matcher, so probing control numbers reveals no claim ids. A held 275's note names no other provider's claims.
- **A 275 that arrives first is held.** With no single match, the attachment is saved as `Unmatched` and its file filed in the catalog under `Provider:{npi}` (readable with `providers:read`, so examiners can open it). `GET api/attachments/unmatched` lists the tenant's held 275s. A background matcher (`AttachmentMatching:Interval`, default 5 minutes) looks again with attachment-service's own service token; once the claim exists, the catalog document gains the claim link and the attachment becomes `Linked`. After `AttachmentMatching:HoldDays` (default 30) it is rejected: `Failed` with `NO_MATCHING_CLAIM` (an 824 TR, TED01 2). The matcher can run on every replica (the catalog link is idempotent and attachment saves are revision-checked). It refuses database-per-tenant mode (`MongoDb:UseTenantScoping`), logging at critical, like the claims outboxes: without a request it would see only the base database.
- **Explicit parents still win.** A 275 that names its claim, authorization or appeal is filed there as before; its control number is kept on the attachment and as a catalog source id.
- **Not in this step:** the claim does not pend for missing attachments (PWK02 EL/FX/BM without a 275 yet); that comes with claim RFAI (step 6). The matcher polls; claim events could replace the polling later.


## 15. What step 5 built (the Documents panel)

Until this, the catalog was reachable only by a service. The member record had its own documents tab (member-document-service, §13), the authorization record listed its attachments behind Download and Preview buttons that had **no handler at all**, the appeal record listed names with no way to open anything, and the claim and provider records showed nothing. So a 275 matched to a claim (§14), or a letter the correspondence service generated, was filed correctly and invisible to everyone.

- **One component, four records.** `DocumentsPanel` takes a subject (`SubjectKind` + `SubjectId`) and asks the catalog for "documents for this subject". It is on the claim, authorization, appeal and provider records; the authorization and appeal tabs it replaces are gone, so there is one list rather than two that disagree. The member record keeps its own tab, which carries member-specific metadata (category, effective and expiration dates, related members) the catalog does not model; folding it in is its own step.
- **The panel decides nothing.** It applies no access rule, because the catalog decides, audits and withholds (§5), and a panel that filtered as well would only ever disagree with it. What the panel has to get right is saying honestly what it was told: **withheld**, **refused**, **unreachable** and **not yet openable** each read differently from "nothing on file". The withheld count is shown in words, because a filtered list that reads as empty is how somebody decides a claim without the record that mattered.
- **Nothing opens before the scan is clean.** A row whose document is Pending, Infected, Failed or still uploading has no Open link and says which, in visible text rather than a tooltip: somebody scanning a table should not have to hover to learn that a file is quarantined.
- **"Also linked to"** names the other subjects a document supports, so an examiner can see that the record in front of them is also an appeal's evidence. Subjects the caller may not read are counted by the catalog, never named, and the panel shows that count as "n you cannot see".
- **Downloads go through the portal.** `GET /documents/{id}/download?subjectKind=&subjectId=` fetches the content for the signed-in user with their CHO token, exactly as the member-document download does. The subject travels in the query because the catalog refuses a document not asked for through one; a request without it is refused before any call. The response is always an attachment, `nosniff` and `no-store`, and a content type the portal does not render goes out as opaque bytes. A file name the catalog supplies is used only if it is one safe token.
- **Not in this step:** the inline viewer, the upload button, the RFAI "Open requests" strip, the member tab moving onto the panel, and the Call subject (its read permission, `calls:read`, is held by no role — contact-service uses `service:read`, so that mapping has to be settled first).
