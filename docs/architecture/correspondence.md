# Correspondence: letters, notices and ID cards that prove themselves

**Status:** Draft for review (2026-10-17)
**Scope:** How CHO produces and delivers everything a plan sends to members and providers, and how it proves what was sent and when:
- appeal, grievance and complaint notices (acknowledgment, extension, resolution, fair hearing and IRE notices);
- authorization and claim notices (denials, RFAI letters, EOBs);
- member notices (welcome kits, coverage notices, ID card carriers);
- provider letters.

**Related:**
- [document-store.md](document-store.md): where every generated letter is stored and linked.
- [appeals-grievances-compliance.md](appeals-grievances-compliance.md): the clocks and rule packs that decide which notices are due and when.
- [idcard-service.md](idcard-service.md): card issuance, which uses the delivery layer described here for physical cards.
- [cho-service.md](cho-service.md): calls that trigger letters, and reps who need to see what a member was sent.

---

## 1. Why build it

**How it works at most plans today:**
- The core system exports a nightly data file.
- A fulfillment vendor merges it into templates the vendor holds, then prints, inserts and mails.
- Sometimes a status file comes back. Often it doesn't.

**What that costs the plan:**
- **The proof lives somewhere else.** The exact letter a member received, and the date it was mailed, are in the vendor's system. When an auditor asks, the plan has to request them.
- **Templates change slowly.** New appeal-rights language or a new state requirement becomes a vendor change order with lead time.
- **The letter is disconnected from the case.** The appeal clock runs in one system and the letter's mail date sits in another. Timeliness reports are reconciled by hand, which is exactly where audits find problems.
- **Data leaves the plan in bulk.** A nightly extract carries more PHI than any single letter needs.

**What CHO changes:**
- **The notice is an event on the case.** The rendered letter is stored write-once and linked to the appeal, claim or authorization. The mail date comes back as an event and moves the case's clock.
- **The plan owns its templates.** Templates are versioned and effective-dated configuration, like rule packs, and tied to them.
- **The printer is a delivery channel, not a system of record.** Vendors keep doing what they do well: printing, inserting, postage and mail. CHO owns the trigger, the template, the rendered record and the proof.

This is **not** a print shop and not a replacement for a customer communications platform's marketing features. It's the compliance core of correspondence: the right notice, on time, provably.

## 2. What exists today

| Piece | State |
|---|---|
| Appeal notices | **Tracked, not produced.** appeals-service records `WrittenNoticeSentAt` on deadline extensions and validates it, but a person enters that date. CHO doesn't generate the letter. |
| Notices in the compliance design | [appeals-grievances-compliance.md](appeals-grievances-compliance.md) §6 step 3 plans acknowledgment, extension and resolution letters "generated into the document store, with sent events." Not built. |
| PDF rendering | **QuestPDF in idcard-service only.** No other service renders documents. |
| Delivery | **None.** No print/mail vendor, member email or SMS, provider fax-out, or member inbox. The only outbound email is the website's contact form. |
| ID cards | **Phase 1 is real:** templates, PDF/PNG rendering, signed QR, scan verification, revocation, QNXT mirror. Physical mail (`FulfillmentVendorAdapter`) deliberately throws `NotSupportedException`. |
| Document store | Designed in [document-store.md](document-store.md) (catalog, links, access audit, retention). Not built. "Letters CHO generates" is already one of its intake channels. |

## 3. Principles

1. **One producer.** Every outbound document goes through the correspondence service. Domain services say *what happened*; they don't render or send.
2. **Freeze what was sent.** The rendered letter and a hash of the data it was built from are stored write-once. Re-rendering later never changes the record of what the member received.
3. **The case owns the clock; correspondence reports to it.** Delivery events flow back to the case as events. Nobody types a mail date.
4. **Templates are configuration, not code and not vendor property.** Versioned, effective-dated, reviewable, and pinned to the letter that used them.
5. **Minimum necessary.** A delivery channel gets one letter's content, not a nightly extract. Notification emails and texts carry no PHI.
6. **Vendors are replaceable.** Delivery goes through adapters. Changing print vendors is configuration and a new adapter, not a re-implementation.

## 4. The model

```
LetterType      id, tenantId, code ("APPEAL_RESOLUTION_UPHELD"), category (Appeal|Grievance|Authorization|Claim|Member|Provider|IdCard),
                audience (Member|Provider|Representative), requiresApproval, defaultChannels[], documentType (document store taxonomy)

Template        id, letterTypeId, version, effectiveFrom, effectiveTo?, scope { lineOfBusiness?, state?, product?, sponsorId?, planId? },
                language, format (Standard|LargePrint|...), body (layout + merge fields), requiredBlocks[], status (Draft|Approved|Retired),
                approvedBy, approvedAt

Letter          id, tenantId, letterTypeId, templateId + version (pinned), subjects[] (member, claim, auth, appeal, provider, call),
                recipient { party, name, address | fax | portal account }, trigger { event, sourceId } | manual { by, reason },
                dataSnapshotHash, documentId (document store), status, events[]

LetterEvent     append-only: Requested, Rendered, ApprovalRequested, Approved, Rejected(reason), Released, HandedToChannel(channel, batchId),
                Printed, Mailed(occurredAt, tracking?), Delivered, Viewed (portal), Returned(reason), Undeliverable, Reissued(newLetterId), Cancelled
                — each with occurredAt (when it happened), recordedAt, source (vendor file, API callback, portal, user)
```

## 5. How a letter moves

1. **Trigger.** A domain event arrives over the shared message bus (see [shared-messagebus.md](shared-messagebus.md)), for example appeal `Decided`, `ExtensionTaken`, an authorization denial, a claim RFAI, or a new enrollment. A user can also request a letter from any record page, with a reason.
2. **Resolve the template.** The letter type plus the case's line of business, state, product and the recipient's language and format preference pick the template version in effect on the trigger date. The resolution chain follows idcard-service's: specific match, then broader scope, then the tenant default. A missing template fails loudly and opens a work item; it never silently skips a notice.
3. **Compose and freeze.** CHO gathers the merge data (member, case, decision, rights and contacts), records a hash of it, renders the PDF, and stores it in the document store as *Correspondence: outbound letter*, linked to every subject.
4. **Approve, where required.** Letter types marked `requiresApproval` (adverse clinical determinations, for example) become a work item. The reviewer sees the rendered letter next to the case. Rejection with a reason sends it back for correction; a corrected letter is a new version.
5. **Release.** The letter goes to its channels per the letter type, the recipient's preferences and any consent on file.
6. **Deliver and prove.** Each channel reports back. Those reports become letter events and, through them, case events.

## 6. Templates

- **Owned by the plan, seeded by CHO.** CHO ships starter templates for the notices its rule packs require (Texas Medicaid and Medicare Advantage first, matching the appeals rule packs). Plans edit them; CHO never overwrites a plan's approved version.
- **Tied to rule packs.** A rule pack's `notices` section names the letter types it requires. Activating a pack checks that each required letter type has an approved template in every language the plan supports.
- **Required blocks.** Regulatory language (appeal rights, fair hearing and IRE instructions, language-assistance and nondiscrimination notices) lives in shared, versioned blocks. A template can't be approved without the blocks its letter type requires. *The specific required content per line of business and state will be confirmed against current regulation as each pack is built.*
- **Languages and formats.** A member's standing request for another language, large print or another accessible format is stored once and honored on every letter.
- **Preview and test.** Authors preview against synthetic members and cases, and see a diff against the previous version before approval.
- **Rendering.** QuestPDF, already used for ID cards, renders letters. Templates are data (layout plus merge fields), not compiled code, so a plan can change a letter without a deployment.

## 7. Delivery channels

Delivery uses the same adapter pattern as `IIdCardAdapter`: one `IDeliveryChannel` interface, implementations chosen per tenant and letter type.

| Channel | How | Proof it returns |
|---|---|---|
| **Print and mail vendor: batch** | Encrypted batch of print-ready PDFs plus a manifest over the plan's SFTP setup (see [SFTP-ARCHITECTURE.md](SFTP-ARCHITECTURE.md)). This fits plans keeping their current fulfillment center. | Vendor status file: printed, mailed, with postal tracking where the vendor provides it. |
| **Print and mail API** | Per-letter submission to a print-and-mail API provider. Suits smaller plans and expedited notices. | API callbacks: accepted, mailed, delivery scans, returned. |
| **Member portal inbox** | Letter appears in the member's documents. | Posted and viewed events. |
| **Notification** | Email or SMS saying a new letter is available, **with no PHI**. Only where the member has opted in and regulation allows electronic delivery for that letter type. | Sent and bounced events. |
| **Provider fax** | Through the fax gateway shared with fax intake in [document-store.md](document-store.md) §4. | Transmission confirmation. |
| **FHIR** | `Communication` and CDex for providers; letters visible through Patient Access as `DocumentReference`. | Retrieval where the API records it. |

**Fallbacks are configuration.** For example: a resolution notice goes by mail and to the portal inbox; if the address is known to be bad, it opens an address-research work item and still posts to the portal.

## 8. Proof and the clock

- **Delivery events become case events.** When a resolution letter is mailed, appeals-service records `WrittenNoticeSent` with the mailed date as its `occurredAt`. This replaces the hand-entered `WrittenNoticeSentAt`.
- **The rule pack decides which event counts.** Whether a notice counts as given when it's mailed, handed to the carrier or posted to the portal is a rule-pack setting per notice, not a code assumption. *To be confirmed against each regulation as packs are built.*
- **Late proof is visible.** A letter released but with no mailed event within the expected window becomes a work item before the case clock is at risk.
- **Returned mail** records the return on the letter, flags the address on the member record, opens an address-research work item, and posts to the portal inbox.
- **Reprints are new letters.** A reprint or corrected letter links to the original. The original record never changes.
- **Audit:** for any case, the universe exports in [appeals-grievances-compliance.md](appeals-grievances-compliance.md) §5 take notice dates from these events, and an auditor can open the exact letter sent. Every view goes through the document store's access audit.

## 9. ID cards

**CHO should own the ID card process, but not the card printing.**
- **idcard-service stays the card domain owner:** card records, templates, signed QR, scan verification, revocation, key rotation and the QNXT mirror don't change.
- **Physical cards use the shared delivery layer.** A mailed card is a card plus a carrier letter; the carrier letter is a correspondence template. `FulfillmentVendorAdapter` becomes a thin adapter onto the print and mail channel instead of a separate vendor integration. Card stock printing and affixing stay with a card vendor.
- **Triggers come from the same events:** new enrollment, plan or product change, PCP change where the card shows the PCP, a lost-card request on a call, and revocation.
- **Bulk reissue** (open enrollment, plan-wide changes) is a correspondence batch job: one batch, one manifest, per-member proof.
- **Storage moves to the document store.** Today idcard-service writes through member-document-service, which uses Azure Blob directly. Once the document store's storage providers and catalog exist, cards are stored and linked like any other document, which also gives the GKE and AWS deployments durable card storage.

## 10. Beside another core system

The correspondence service runs inside CHO or beside QNXT (or another core), the same way [cho-service.md](cho-service.md) does:
- **Triggers** come from CHO's own services, or from the other core's events or extracts through adapters.
- **CHO never writes to the other core.** Letter status can be mirrored back where a plan wants it, the same way idcard-service mirrors to QNXT.
- **A plan can start with one letter family** (for example, appeal notices) and leave everything else with its current fulfillment process.

## 11. Security and privacy

- **Templates may contain PHI merge fields; notifications may not.** Email and SMS templates are validated at approval to contain no PHI merge fields.
- **Per-letter payloads.** Vendors receive print-ready files for the letters in a batch, encrypted in transit, and nothing more.
- **Access audit.** Letters are documents, so every view and download goes through the document store's context-based access and audit.
- **Sensitive letters.** Letters linked to sensitive subjects inherit the document store's sensitivity tags; for example, a letter about a Part 2 record is restricted the same way.
- **Address verification.** Recipient addresses come from the member or provider record at render time, and the address used is part of the frozen data snapshot.

## 12. Build order

Depends on [document-store.md](document-store.md) §9 steps 1–2 (storage providers and the catalog).

1. **Model, template registry and rendering:** letter types, templates with required blocks, QuestPDF rendering, write-once storage in the document store.
2. **Appeal notices:** acknowledgment, extension and resolution letters for the Texas Medicaid and Medicare Advantage packs; triggers from appeals-service; `WrittenNoticeSent` driven by delivery events.
3. **Approval gate** through work management.
4. **Delivery channels:** portal inbox first, then print and mail batch (SFTP plus status file), then a print and mail API adapter.
5. **Proof loop:** mailed, returned and late-proof work items; reprints.
6. **ID cards on the shared layer:** physical card delivery and bulk reissue; card storage moves to the document store.
7. **More letter families:** authorization and claim notices, RFAI letters, EOBs, provider letters.
8. **Template authoring UI** with preview and version diff.

## 13. Open questions

1. **Template format:** a CHO layout schema rendered by QuestPDF, or HTML templates rendered to PDF? QuestPDF is already in use and deterministic; HTML is easier for plan staff to author.
2. **First print vendor:** batch file to a plan's existing fulfillment center, or a print and mail API first? The batch path matches how plans work today; the API path is faster to demonstrate.
3. **Electronic delivery rules:** which letter types each line of business allows electronically, and what member consent each requires. To be confirmed per pack.
4. **Postal tracking:** how much mail-scan detail plans want on the case, and which vendors provide it.
