# Document store: one home for every document plan operations touch

**Status:** Draft for review (2026-10-15). Step 1 of the build order (storage providers) is built (2026-10-20); the catalog is next. Decided: the catalog is its own service (§10, question 1).
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
| **member-document-service** (`MemberDocument`) | Member documents. Has the most mature model: retention policy (`DEFAULT-10Y`), legal hold with history, SHA-256 hash, content sniffing, two-phase upload (pending → finalize), and a FHIR `DocumentReference` endpoint. | Member-scoped only. (Stores through `IDocumentStore` since step 1, so it runs on any of the three clouds.) |
| **rfai-service** (`RfaiCase`) | Authorization RFAIs (CDex Task, PAS-07). Received attachments carry an attachment control number, storage key and hash. | **Authorization only:** there's no claim RFAI. Attachment metadata lives inside the case rather than in a shared catalog. |
| Claims gateway (`IClaimAttachmentContentStore`) | Attachments that arrive through the clearinghouse gateway, with a scan status. | A separate store, not linked to the others. |

Also:
- The 837 **PWK** segment (paperwork attachment indicator) is validated, but its attachment control number isn't kept on the claim, so a later 275 can't be matched to the claim that announced it.
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
2. **Catalog:** the document record and service (evolved from member-document-service), with many-to-many links, sensitivity tags, access audit, and the documents API.
3. **Move attachment-service onto the catalog:** existing `Attachment` rows migrate to documents with links. Keep the PWK attachment control number on the claim for 275 matching.
4. **Correspondence** ([correspondence.md](correspondence.md)), starting with appeal notices. Generated letters are the first new producer on the catalog, and ID cards move onto it in the same phase.
5. **Documents panel** on claim, authorization and appeal screens.
6. **Claim RFAI:** extend rfai-service from authorizations to claims, with 277 RFAI out, 275 / upload / fax in, matching, and clean-date events.
7. **Fax/scan intake** with OCR and the unmatched-documents queue; paper-claim keying against the image.
8. **Call recordings and transcripts** for CHO Service.

## 10. Open questions

1. **One service or a library?** *Decided 2026-10-20: its own service* (document-service) that others call, so the access audit and retention live in one place. Generated correspondence ([correspondence.md](correspondence.md)) adds another producer, which strengthens that case.
2. **Virus scanning:** ClamAV in-cluster, or the cloud's malware scanning (Defender for Storage, GCS + Security Command Center)? It may need to be configurable per deployment.
3. **OCR:** cloud OCR (Document AI / Azure Document Intelligence / Textract) or Claude vision for paper claims? This ties to the AI-assisted keying in the workbench design.
