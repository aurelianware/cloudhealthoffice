# Correspondence: letters, notices and ID cards that prove themselves

**Status:** Draft for review (2026-10-17). Design decisions in §2 approved 2026-10-17; regulatory research in §3 as of 2026-10-17.
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

> **Not legal advice.** §3 summarizes federal and Texas rules as researched on 2026-10-17, with citations. Each rule pack is checked against the current text and the plan's contract before it ships. Items marked *to confirm* were not verified against primary text.

---

## 1. Why build it

**How it works at most plans today:**
- The core system exports a nightly data file.
- A fulfillment vendor merges it into templates the vendor holds, then prints, inserts and mails.
- Sometimes a status file comes back. Often it doesn't.

**What that costs the plan:**
- **The proof lives somewhere else.** The exact letter a member received, and the date it was mailed, are in the vendor's system. When an auditor asks, the plan has to request them.
- **The wrong date gets reported.** CMS audits count a written notice as given when it leaves the plan's or its vendor's possession, and the audit layout says not to report the date a letter was generated or printed (§3.1). Plans that only know the generation date report the wrong one or reconstruct the right one by hand.
- **Templates change slowly.** New appeal-rights language or a new state requirement becomes a vendor change order with lead time.
- **The letter is disconnected from the case.** The appeal clock runs in one system and the mail date sits in another. Timeliness reports are reconciled by hand, which is exactly where audits find problems.
- **Data leaves the plan in bulk.** A nightly extract carries more PHI than any single letter needs.

**What CHO changes:**
- **The notice is an event on the case.** The rendered letter is stored write-once and linked to the appeal, claim or authorization. The date it left the plan's control comes back as an event and moves the case's clock.
- **The plan owns its templates,** as versioned, effective-dated configuration tied to rule packs. Standardized and state-prescribed notices are locked to their required text.
- **The printer is a delivery channel, not a system of record.** Vendors keep printing, inserting and mailing. CHO owns the trigger, the template, the rendered record and the proof.

This is **not** a print shop and not a replacement for a customer communications platform's marketing features. It's the compliance core of correspondence: the right notice, in the right form, on time, provably.

## 2. Decisions (approved 2026-10-17)

1. **Template format:** templates are structured blocks (letterhead, address block, body paragraphs, required regulatory blocks, tables). Text inside blocks uses Liquid merge fields (Fluid in .NET). QuestPDF renders the PDF. This keeps print-exact layout (envelope window, barcode zone) and deterministic output, and gives plan staff an editor built around the parts they change.
2. **Delivery order:** the member portal inbox and one print-and-mail API adapter come first, for demos and smaller plans. The batch adapter for a plan's existing fulfillment center is designed around the first pilot plan's actual vendor file format, not a guessed generic one.
3. **Electronic delivery:** paper is the default for adverse determinations and appeal and grievance resolutions. Electronic delivery is allowed only where the rule pack permits it for that letter type and the member's consent is on file, and it supplements paper unless the rule pack says otherwise.
4. **Postal tracking:** only events that affect clocks or follow-up (left the plan's control, returned) become case events. The full mail-scan history stays on the letter record.

## 3. What the regulations require

### 3.1 When a notice counts as given

This is the most important finding for the design, because it decides which date drives every clock.

| Program | Rule | Source |
|---|---|---|
| **Medicare Advantage** | A written notice is delivered when it has **left the possession of the plan or its delegated entity**: deposited in an external mail or courier receptacle, or sent, for electronic delivery. Placing it in the plan's internal outgoing mail doesn't count. Oral notice counts when the plan speaks to the enrollee or leaves a voicemail. | Parts C & D Enrollee Grievances, Organization/Coverage Determinations, and Appeals Guidance (eff. July 6, 2026) §10.5.3 |
| Medicare Advantage audits | The ODAG organization-determination universe asks for the date and time written notification was provided, and says **not** to enter the date a letter was generated or printed. Grievance case files can be asked to show when a letter was printed and mailed, plus mailroom policies. | Part C ODAG audit protocol (2026 OMB submission; *final status to confirm*) |
| **Medicaid managed care** | The adverse benefit determination notice must be **mailed** within the timeframes (§438.404(c)). The appeal window runs **from the date on the notice** (§438.402(c)(2)(ii)), and CMS expects plans to mail notices on the same day they're dated (81 FR 27511). Continuation of benefits runs from the date the plan **sends** the notice (§438.420(a)). "Send" means by mail or electronically under §435.918 (§431.201). | 42 CFR 438.402, 438.404, 438.420, 431.201 |
| **Texas Medicaid/CHIP** | For HHSC service authorization notices, the mailing date is the **postmark**; without one, the meter date; without either, the date on the notice. | UMCM 3.21 (*version reviewed: v2.6, May 2022; current version to confirm*) |

**What follows for CHO:**
- **A letter carries several dates, and they're kept separately:** the letter date (printed on it), rendered, released, handed to the vendor, **left the plan's control** (USPS acceptance or induction, postmark or meter date, or electronic send), and delivery or return.
- **The clock uses "left the plan's control,"** not generation, printing or handoff to the vendor. A print vendor is the plan's delegated entity, so a letter at the vendor hasn't left the plan's possession until the vendor deposits it with USPS.
- **The letter date is the planned mail date.** Letters are dated for the day the channel will mail them. If the "left control" date differs from the letter date, that's an exception work item: depending on the rule pack, the letter is re-dated and reissued, or the difference is recorded and justified.
- **Proof is per piece.** Each mailed letter keeps its Intelligent Mail barcode serial, the USPS scan events, and the vendor's mailing record (§3.6).

### 3.2 Medicare Advantage notices

| Notice | Timing | Source |
|---|---|---|
| Standard pre-service organization determination | 14 calendar days; **7 days** for items under the §422.122 prior authorization rules from Jan 1, 2026; Part B drug 72 hours | 422.568(b) |
| Extension of an organization determination | Up to 14 days; written notice of the reasons and the right to an expedited grievance | 422.568(b)(2) |
| Expedited organization determination | 72 hours (Part B drug 24 hours). Oral notice allowed if written confirmation is mailed within **3 calendar days** | 422.572 |
| Denial of a request to expedite | Prompt oral notice, then a letter within 3 calendar days | 422.570(d), 422.584(d) |
| Reconsideration | 30 days service, 60 days payment, 7 days Part B drug; expedited 72 hours. Upheld decisions go to the IRE; **the plan isn't required to notify the enrollee of the forward**, though it may use the model Notice of Appeal Status | 422.590; PCD Guidance §50.10.1 |
| Grievance | 30 days, extendable 14 with immediate written notice of the reasons. Written grievances get written responses; quality-of-care grievances always get a written response with QIO rights. Expedited grievances: 24 hours | 422.564 |
| Late decision | Missing the deadline is itself an adverse decision | 422.568(f), 422.572(f), 422.590 |

**Form and content:**
- **The denial notice is standardized.** The Integrated Denial Notice (CMS-10003) must be used without alteration except in its variable fields (422.2267(b)(1), (e)(16)). Its instructions require 12-point font and the OMB number; the current OMB approval expires **11/30/2027**.
- An EOB may stand in for the IDN only if it carries the IDN's approved text verbatim and goes out within the deadline (PCD Guidance §40.12.1).
- **Denial content** (422.568(e)): specific reasons, the right to a reconsideration, the standard and expedited processes, and the rest of the appeal process. CMS guidance adds the specific coverage rule relied on, what would be needed for approval, the right to a representative, where to send an appeal, and the right to submit evidence.
- **Other standardized or model notices** in 422.2267(e): NOMNC and DENC (standardized), Organization Determination Notice, Notice of Right to an Expedited Grievance, Notice of Appeal Status, Notice of Dismissal of Appeal (models).
- **Providers:** since the CY2026 rule, organization determination notices and timeliness rules include "the physician or provider involved, as appropriate" (422.568(b)(1), (d), (f); 90 FR 15910).
- **Integrated D-SNPs:** integrated notices describe both Medicare and Medicaid rights, reductions get 10 days' advance notice, grievances and reconsiderations get written acknowledgment, and some notices are due within 2 calendar days instead of 3 (422.629, 422.631, 422.633).

**Formatting and language** (422.2267(a)):
- 12-point Times New Roman or equivalent.
- Translation into any language that is the primary language of **5% or more** of a plan benefit package's service area.
- A standing request for another language or an accessible format is honored on all future materials.

**Electronic delivery** (422.2267(d)):
- Beneficiary-specific materials are mailed in hard copy unless the enrollee **opts in**.
- Opt-in consent must name **both the media type and the specific materials**.
- The plan explains how and when to access them, mails hard copy on request within **3 business days** (one time or permanently, for all or some materials), and **mails hard copy automatically if electronic delivery fails**.
- Portal delivery under opt-in is conditioned on the system generating a notification (such as an email) when a document is posted (PCD Guidance §10.4.4).

### 3.3 Medicaid managed care notices

| Notice | Timing | Source |
|---|---|---|
| Termination, suspension or reduction of authorized services | At least **10 days** before the date of action, with exceptions (§§431.211–431.214) | 438.404(c)(1) |
| Denial of payment | At the time of the action affecting the claim | 438.404(c)(2) |
| Standard authorization denial | **7 calendar days** for rating periods from Jan 1, 2026 (CMS-0057-F), extendable 14 | 438.210(d)(1) |
| Expedited authorization | 72 hours | 438.210(d)(2) |
| Extension of an authorization | Written notice of the reason and the right to file a grievance | 438.404(c)(4) |
| Decision not made in time | Treated as a denial; notice due when the time runs out | 438.404(c)(5) |
| Appeal resolution | Standard 30 days, expedited 72 hours (State maximums) | 438.408(b) |
| Appeal extension the plan takes | Prompt oral notice, then **written notice within 2 calendar days** with the reason and grievance rights | 438.408(c)(2) |
| Grievance resolution | Within 90 days (State maximum); method set by the State | 438.408(b), (d) |
| Grievance and appeal acknowledgment | Required; no federal deadline or form (State contract sets it) | 438.406(b)(1) |

**Content:**
- **Adverse benefit determination** (438.404(b)): the decision; the reasons, with the right to free copies of relevant documents and criteria; appeal rights, including that the plan's single appeal level comes first; State fair hearing rights; how to use them; when and how to expedite; and continuation of benefits, including possible cost to the enrollee.
- **Appeal resolution** (438.408(e)): the result and date; if not wholly favorable, the right to a State fair hearing and how to request it (within 90–120 days, per State), and continuation of benefits.
- **Missing notice or timing requirements means deemed exhaustion:** the enrollee can go straight to a State fair hearing (438.408(c)(3)).
- **Providers:** the plan notifies the requesting provider of a denial or reduced authorization (438.210(c)). From 2026, the denial sent to the provider must include a **specific reason**, however it's communicated (438.242(b)(8) → 431.80(a)).
- **State model notices:** States must develop model enrollee notices and require plans to use them (438.10(c)(4)).

**Formatting, language and electronic delivery** (438.10):
- Easily understood language and format; font **no smaller than 12 point**; available in alternative formats at no cost.
- Appeal, grievance, denial and termination notices are available in the State's **prevalent non-English languages**, with taglines in a **conspicuously visible** font size. (*The former 18-point tagline size was replaced by this standard in 2020; to confirm.*)
- Electronic delivery requires **all** of: a readily accessible format, a prominent location on the website, the ability to save and print, the same content and language requirements, and notice that paper is free on request **within 5 business days** (438.10(c)(6)). *How the website condition applies to a notice addressed to one person, and the opt-in rules in §435.918, are to confirm.*

### 3.4 Language access and accessibility (Section 1557)

- **Notice of availability** (45 CFR 92.11): a notice that free language assistance and auxiliary aids are available, in English and at least the **15 most common languages** of people with limited English proficiency in the State, must accompany significant communications. These include **notices of denial or termination of benefits or coverage, EOBs, notices of appeal and grievance rights**, and member handbooks.
  - A member can opt out annually; alternatively the plan records the member's primary language and sends materials in it.
  - In force since July 5, 2025. The October 2025 court vacatur (*Tennessee v. Kennedy*; HHS notice June 2, 2026) covers only listed gender-identity provisions; §§92.11, 92.201 and 92.202 remain in force.
  - **For Medicare Advantage,** the CY2027 rule removed the separate Part C Notice of Availability (422.2267(e)(31) is now reserved) because §92.11 already requires it. The obligation continues under §92.11.
- **Translation quality** (92.201): machine translation must be reviewed by a qualified human translator when accuracy is critical or the text is complex. **CHO doesn't machine-translate notices at send time.** Translations are template versions, reviewed and approved by a qualified translator.
- **Accessible formats** (92.202): large print, Braille and accessible electronic formats are free and timely on request.

### 3.5 Texas Medicaid/CHIP

From UMCM 3.21 (*v2.6, May 2022; current version to confirm*):
- **HHSC templates are required:** Notice 1 (A, B, C) for adverse benefit determinations and Notice 2 (A, B, C) for internal appeal decisions, each a letter, flyer and form, in English and Spanish. The MCO fills bracketed fields and removes language that doesn't apply.
- **Plain language at a 4th–6th grade reading level** unless HHSC prescribes the wording.
- **Timing:** terminations, suspensions and reductions are mailed at least **15 business days** before the change (stricter than the federal 10 days). Final prior authorization decisions go to member and provider by the **next business day**; provider notices may go by fax, portal or mail.
- **Appeals:** 60 days to appeal; acknowledgment within **5 business days**; resolution in 30 days (72 hours expedited), extendable 14; 120 days to request a State fair hearing; External Medical Review decided within 15 calendar days.
- **Mailing date is the postmark** (§3.1).
- **ID cards:** UMCM 3.6 (Medicaid) and 3.7 (CHIP) set the required card content as templates. *Issuance timing, threshold languages beyond Spanish, and the general member-materials reading level are to confirm against the current UMCC and UMCM 4.3.*

**Where Texas and federal rules differ, the stricter value wins.** UMCM 3.21 gives 11- or 12-point sans-serif type as an example of readable formatting; federal Medicaid requires at least 12 point, so CHO enforces 12.

### 3.6 Proving mailing

- **Intelligent Mail barcode and Informed Visibility:** each piece with a unique IMb gets USPS scan events (facility, operation, date and time). IMb Tracing was replaced by Informed Visibility in 2017. Scans show processing, **not delivery**, and not every piece is guaranteed a scan.
- **Certificates of mailing** (PS Form 3817, or the PS Form 3665 firm sheet for three or more pieces) are postmarked at mailing and prove the mailing date of specific pieces. PS Form 3606-D proves only a count. Certified Mail with return receipt proves delivery.
- **Address hygiene:** presort First-Class mail must meet Move Update within 95 days before mailing (NCOALink, or Full-Service ACS, which also reports undeliverable mail and new addresses). *CASS/DPV requirements to confirm.*
- **What CHO keeps for every mailed letter:** the letter image, data snapshot hash, IMb serial, USPS acceptance and scan events, the vendor's mailing record, and any certificate of mailing.

### 3.7 Records

- **Medicare Advantage:** 10 years, sufficient for CMS to evaluate timeliness, with audit rights reaching delegated entities (422.504(d), (e)(4), (i)).
- **Medicaid:** grievance and appeal records for at least 10 years (438.3(u), 438.416).
- Neither regulation names notice copies or proof of mailing explicitly. Keeping them is what the audit evidence requests in §3.1 call for, so CHO keeps them under the document store's 10-year default retention.

### 3.8 ID cards

- **Medicare Advantage:** new enrollees get the card within **10 calendar days** of CMS confirming enrollment, or by the last day of the month before the effective date, whichever is later. Cards are reissued when the information on them changes. ID cards are exempt from the translation and 12-point rules (422.2267(e)(30)). Integrated D-SNPs need an integrated Medicare-Medicaid card from CY2027.
- **Medicaid:** 438.10 doesn't set ID card rules; States do (Texas: UMCM 3.6, 3.7).
- **Standards:** INCITS 284 and the WEDI Health Identification Card Implementation Guide for physical cards; the HL7 CARIN Digital Insurance Card IG (FHIR R4 `Coverage`) for digital cards.

## 4. Principles

1. **One producer.** Every outbound document goes through the correspondence service. Domain services say *what happened*; they don't render or send.
2. **Freeze what was sent.** The rendered letter and a hash of the data it was built from are stored write-once. Re-rendering later never changes the record of what the member received.
3. **The case owns the clock; correspondence reports to it.** The "left the plan's control" date comes back as a case event. Nobody types a mail date, and the generation or print date is never reported as the notice date.
4. **Required text is locked.** Standardized federal notices and State-prescribed notices can only be filled, never reworded. Plan wording lives only in templates that allow it.
5. **Templates are configuration, not code and not vendor property.** Versioned, effective-dated, reviewable, and pinned to the letter that used them.
6. **Stricter rule wins.** Where federal, State and contract rules differ, the rule pack carries the strictest value and records why.
7. **Minimum necessary.** A delivery channel gets one letter's content, not a nightly extract. Notification emails and texts carry no PHI.
8. **Vendors are replaceable.** Delivery goes through adapters. Changing print vendors is configuration and a new adapter, not a re-implementation.

## 5. The model

```
LetterType      id, tenantId, code ("APPEAL_RESOLUTION_UPHELD"), category (Appeal|Grievance|Authorization|Claim|Member|Provider|IdCard),
                audience (Member|Provider|Representative), requiresApproval, documentType (document store taxonomy),
                isSignificantCommunication (Section 1557), isRequiredMaterial (MA 422.2267)

Template        id, letterTypeId, version, effectiveFrom, effectiveTo?, scope { lineOfBusiness?, state?, product?, sponsorId?, planId? },
                kind (Standardized | StatePrescribed | Model | PlanAuthored), formNumber?, ombNumber?, formExpiresOn?,
                language, translation { translatorId, reviewedAt }?, format (Standard | LargePrint | Braille | AccessiblePdf),
                blocks[] (layout + Liquid text + lockedText flag), requiredBlocks[], attachments[] (e.g. NoticeOfAvailability),
                readingLevel?, status (Draft | Approved | Retired), approvedBy, approvedAt

Letter          id, tenantId, letterTypeId, templateId + version (pinned), subjects[] (member, claim, auth, appeal, provider, call),
                recipient { party, name, address | fax | portal account, addressSource, addressVerifiedAt },
                trigger { event, sourceId } | manual { by, reason }, ruleRef (rule pack + notice id), dueBy,
                letterDate, dataSnapshotHash, documentId (document store), copies[] (enrollee, representative, provider),
                mailPiece { imbSerial?, vendorJobId?, certificateOfMailing? }, status, events[]

LetterEvent     append-only: Requested, Rendered, ApprovalRequested, Approved, Rejected(reason), Released,
                HandedToVendor(channel, batchId), Printed, LeftPlanControl(occurredAt, evidence: UspsAcceptance | Postmark | Meter | ElectronicSend),
                UspsScan(operation, facility, at), Delivered, PostedToPortal, Viewed, NotificationSent,
                Returned(reason), Undeliverable, ElectronicDeliveryFailed, PaperFallbackSent, Reissued(newLetterId), Cancelled
                — each with occurredAt (when it happened), recordedAt, source (vendor file, API callback, USPS, portal, user)
```

**Rule pack `notices` entry** (extends [appeals-grievances-compliance.md](appeals-grievances-compliance.md) §3):

```
notices: [ {
  id, letterType, when (event + timing: "within 2 calendar days of ExtensionTaken", "≥ 15 business days before dateOfAction"),
  oralFirstAllowed, writtenConfirmationWithin,
  givenWhen: LeftPlanControl | Postmark | Sent,            // which event satisfies the notice
  letterDateRule: PlannedMailDate,  onDateMismatch: Reissue | RecordAndJustify,
  templateKinds: [Standardized | StatePrescribed | Model | PlanAuthored],
  electronic: NotAllowed | OptInPerMaterial (paperOnRequestWithin: 3 business days, autoPaperOnFailure) | Medicaid438_10c6 (paperWithin: 5 business days),
  providerCopy: None | Required (channels: fax | portal | mail), specificDenialReason: bool,
  attachments: [ NoticeOfAvailability_92_11, StateTaglines ], minFontPt: 12, readingLevelMax?,
  proofOfMailing: ImbScans | CertificateOfMailing | CertifiedMail
} ]
```

## 6. How a letter moves

1. **Trigger.** A domain event arrives over the shared message bus (see [shared-messagebus.md](shared-messagebus.md)), for example appeal `Decided`, `ExtensionTaken`, an authorization denial, a claim RFAI, or a new enrollment. A user can also request a letter from any record page, with a reason. The rule pack sets `dueBy`.
2. **Resolve the template.** Letter type plus the case's line of business, state and product, and the recipient's language and format preference, pick the template version in effect on the trigger date. A missing template or translation fails loudly and opens a work item; it never silently skips a notice.
3. **Compose and freeze.** CHO verifies the address, gathers the merge data, sets the **letter date to the channel's planned mail date**, records a hash of the data, renders the PDF with its required attachments (notice of availability, taglines), and stores it in the document store as *Correspondence: outbound letter*, linked to every subject. Copies for a representative or the provider are separate letters linked to the same case.
4. **Check.** Automated checks before release: locked text unchanged, required blocks and attachments present, font sizes at or above the minimum, reading level within the template's limit, address verified.
5. **Approve, where required.** Letter types marked `requiresApproval` become a work item. The reviewer sees the rendered letter next to the case. A correction produces a new version.
6. **Release.** The letter goes to its channels per the rule pack, the recipient's preferences and any consent on file, early enough to leave the plan's control by `dueBy`.
7. **Deliver and prove.** Channels report back. `LeftPlanControl` becomes the case's notice event; later scans and returns stay on the letter.

## 7. Templates

- **Four kinds:**
  - **Standardized** (CMS IDN, NOMNC, DENC): text is locked; only the form's variable fields can be filled. CHO tracks the form number, OMB number and expiration date, and opens a work item 90 days before a form expires or when CMS publishes a new version.
  - **State-prescribed** (Texas HHSC Notices 1 and 2): locked text with the State's bracketed fields; the plan removes only language the State marks as conditional.
  - **Model** (CMS model notices, State models): starts from the model; the plan may adapt within the rules for models.
  - **Plan-authored:** acknowledgments and other letters with no prescribed form, built from required blocks plus plan text.
- **Seeded by CHO, owned by the plan.** CHO ships the standardized and State-prescribed forms and starter templates for its rule packs (Texas Medicaid and Medicare Advantage first). Plans edit what they may; CHO never overwrites a plan's approved version.
- **Tied to rule packs.** Activating a pack checks that every required letter type has an approved template in every language the plan must support (MA 5% languages, Medicaid prevalent languages, Texas English and Spanish).
- **Translations are reviewed by people.** A translated template version records the qualified translator who reviewed it (§3.4).
- **Formats.** Standard, large print, Braille (through the print vendor) and accessible PDF are template formats. A member's standing request is stored once and honored on every letter.
- **Preview and test.** Authors preview against synthetic members and cases, see a diff against the previous version, and see the automated checks from §6 step 4 before approval.

## 8. Delivery channels

Delivery uses the same adapter pattern as `IIdCardAdapter`: one `IDeliveryChannel` interface, implementations chosen per tenant and letter type. Per §2, the portal inbox and one print-and-mail API come first.

| Channel | How | Proof it returns |
|---|---|---|
| **Member portal inbox** | Letter appears in the member's documents. | Posted and viewed events. |
| **Print and mail API** | Per-letter submission to a print-and-mail provider under a BAA. Each piece carries a unique IMb. | USPS acceptance and scan events, returns. *Some providers expose the "mailed" event only on higher tiers; required for MA, so it's a selection criterion.* |
| **Print and mail vendor: batch** | Encrypted batch of print-ready PDFs plus a manifest over the plan's SFTP setup ([SFTP-ARCHITECTURE.md](SFTP-ARCHITECTURE.md)), for a plan's existing fulfillment center. Format designed with the first pilot plan's vendor. | Per-piece status file: printed, accepted by USPS (with IMb serial), returned. |
| **Notification** | Email or SMS saying a new letter is available, **with no PHI**. | Sent and bounced events. |
| **Provider fax** | Through the fax gateway shared with fax intake in [document-store.md](document-store.md) §4. | Transmission confirmation. |
| **FHIR** | `Communication` and CDex for providers; letters visible through Patient Access as `DocumentReference`. | Retrieval where the API records it. |

**Requirements on every mail channel:**
- Address standardization before mailing, Move Update compliance, and Full-Service ACS for returns and new addresses.
- A unique IMb serial per piece, stored on the letter.
- Per-piece evidence of the USPS acceptance or postmark date.
- Optional certificate of mailing (PS 3665 firm sheet) or Certified Mail for letter types where the rule pack asks for it.

**Electronic delivery** (§2 decision 3):
- Paper stays the default for adverse determinations and resolutions.
- Medicare Advantage: electronic only with opt-in consent naming the media and the specific letter types; paper on request within 3 business days; **automatic paper if electronic delivery fails**.
- Medicaid: electronic only where the §438.10(c)(6) conditions are met and the State allows it; paper free on request within 5 business days.
- Consent is recorded per member, per letter type and per medium, with its source and date.

## 9. Proof and the clock

- **`LeftPlanControl` is the notice event.** Its evidence is USPS acceptance or induction, a postmark or meter date, or the electronic send time. appeals-service records `WrittenNoticeSent` from it, replacing the hand-entered `WrittenNoticeSentAt`.
- **The rule pack decides which evidence counts** (`givenWhen`): for example, left the plan's control for Medicare Advantage, postmark for Texas service authorization notices.
- **Date mismatch:** if the letter left the plan's control on a day other than its letter date, an exception work item opens and the rule pack's `onDateMismatch` applies.
- **At-risk letters:** a released letter with no `LeftPlanControl` event in time to meet `dueBy` becomes a priority work item before the case clock is missed. A missed Medicaid notice also feeds deemed exhaustion in appeals-service.
- **Returned mail** records the return on the letter, flags the address on the member record, opens an address-research work item, and posts the letter to the portal inbox.
- **Reprints and re-dated letters are new letters** linked to the original. The original record never changes.
- **Audit:** universe exports take notice dates from `LeftPlanControl`. For any case, an auditor can open the exact letter, its data snapshot, its IMb serial, its USPS events and the vendor's mailing record. Every view goes through the document store's access audit.

## 10. ID cards

**CHO owns the ID card process, but not the card printing.**
- **idcard-service stays the card domain owner:** card records, templates, signed QR, scan verification, revocation, key rotation and the QNXT mirror don't change.
- **Physical cards use the shared delivery layer.** A mailed card is a card plus a carrier letter; the carrier letter is a correspondence template. `FulfillmentVendorAdapter` becomes a thin adapter onto the print-and-mail channel. Card stock printing and affixing stay with a card vendor.
- **Deadlines are rule pack values:** for Medicare Advantage, 10 calendar days from CMS enrollment confirmation or the last day of the month before the effective date, whichever is later; State values for Medicaid.
- **Triggers:** new enrollment, plan or product change, any change to information printed on the card (which requires reissue for Medicare Advantage), a lost-card request on a call, and revocation.
- **Templates follow the standards:** State templates where required (Texas UMCM 3.6, 3.7), INCITS 284 and the WEDI guide for layout, and an integrated card for integrated D-SNPs from CY2027.
- **Digital cards** are offered as a FHIR `Coverage` following the CARIN Digital Insurance Card IG, alongside the existing PDF, PNG and signed QR.
- **Bulk reissue** (open enrollment, plan-wide changes) is a correspondence batch job: one batch, one manifest, per-member proof.
- **Storage moves to the document store.** Today idcard-service writes through member-document-service, which uses Azure Blob directly. Once the document store's storage providers and catalog exist, cards are stored and linked like any other document.

## 11. Beside another core system

The correspondence service runs inside CHO or beside QNXT, Facets or another core system, the same way [cho-service.md](cho-service.md) does. A plan on another core is a primary audience: it keeps its core system and its print vendor, and gains template control and proof of mailing.

**Rules in both modes:**
- **CHO never writes to the core system.** Results go back as files or messages the plan loads, the same way idcard-service mirrors to QNXT.
- **A plan can start with one letter family** (for example, appeal and grievance notices) and leave everything else with its current fulfillment process.
- **Reading the core is adapter work, scoped per plan.** The QNXT and Facets adapters are implemented per engagement, because each plan's configuration, extracts and reporting databases differ (scoped through a requirements questionnaire, like Availity's QREs).

### 11.1 Mode A: letters only

The plan keeps working cases where it does today: in the core system or a case tool beside it. CHO takes over what the fulfillment vendor's data feed does now, with proof added.

| Direction | What | How |
|---|---|---|
| Core → CHO | **Notice request:** letter type, case or claim id, recipient, decision and reason fields, due-by time, and the rule pack (line of business, state) | An event or API call where the core or its integration layer can send one; otherwise an intraday or nightly extract from the plan's reporting database or existing fulfillment feed |
| Core → CHO | **Reference data** the letters need: member name, address, language and format preferences | Same feed, or adapter reads of the reporting database |
| CHO → Core | **Per-letter result:** request id, letter id, document link, letter date, **left-plan-control date and evidence**, returns | A result file or message the plan loads into its case records |

- **What the plan gets:** locked standardized and State templates, rendered letters stored and auditable, per-piece proof of mailing, and the left-plan-control date to report in place of the generation date.
- **What it doesn't get:** the case clock stays in the core. CHO flags letters at risk of missing their own due-by time, but it can't see a case that never produced a notice request.
- **Effort:** one notice-request feed and one result feed. This is the smallest footprint and the fastest to pilot.

### 11.2 Mode B: cases and letters

The plan runs appeals, grievances and complaints in CHO (see [appeals-grievances-compliance.md](appeals-grievances-compliance.md)), beside the core.

| Direction | What | How |
|---|---|---|
| Core → CHO | **Members, eligibility, providers, claims and authorizations** the cases refer to | Adapter reads (API, reporting database or extracts), refreshed per the plan's needs |
| Calls → CHO | **Cases opened from calls** | [cho-service.md](cho-service.md), in CHO or beside the core's call tracking |
| CHO → Core | **Outcomes the core must act on:** effectuation of a reversal (authorize or pay), adjustments | Work items routed to the plan's staff, or a file or message the plan loads; CHO doesn't write to the core |

- **What the plan gets:** the full design: rule-pack clocks, work routing and reviewer separation, notices as case events, and audit universe exports built from those events.
- **Effort:** more adapter work (several read feeds) and a process change for the appeals team.

### 11.3 Moving from A to B

Mode A builds what Mode B needs: the templates, delivery channels, proof and the member reference feed carry over unchanged. A plan can start with letters only for one family, then move case work into CHO when it's ready, one case type at a time.

## 12. Security and privacy

- **Templates may contain PHI merge fields; notifications may not.** Email and SMS templates are validated at approval to contain no PHI merge fields.
- **Per-letter payloads.** Vendors receive print-ready files for the letters in a batch, encrypted in transit, and nothing more. Every print vendor is under a BAA and is treated as the plan's delegated entity for audit purposes.
- **Access audit.** Letters are documents, so every view and download goes through the document store's context-based access and audit.
- **Sensitive letters** inherit the document store's sensitivity tags; for example, a letter about a Part 2 record is restricted the same way.
- **Address used is frozen.** The recipient address comes from the member or provider record at render time, is verified, and is part of the data snapshot.

## 13. Build order

Depends on [document-store.md](document-store.md) §9 steps 1–2 (storage providers and the catalog).

1. **Model, template registry and rendering:** letter types, the four template kinds with locked text, required blocks and attachments, QuestPDF rendering with Fluid merge fields, pre-release checks, write-once storage in the document store.
2. **Appeal notices:** acknowledgment, extension and resolution letters for the Texas Medicaid and Medicare Advantage packs, including the IDN and the HHSC Notice 1 and 2 templates; triggers from appeals-service; rule pack `notices` entries.
3. **Approval gate** through work management.
4. **Portal inbox and the first print-and-mail API adapter,** with IMb serials, USPS events and `LeftPlanControl`.
5. **Proof loop:** `WrittenNoticeSent` from `LeftPlanControl`, at-risk and date-mismatch work items, returned mail, reprints.
6. **Electronic delivery consent and paper fallback.**
7. **Batch adapter** for the first pilot plan's fulfillment vendor.
8. **ID cards on the shared layer:** physical delivery, bulk reissue, CARIN digital card; card storage moves to the document store.
9. **More letter families:** authorization and claim notices, RFAI letters, EOBs, provider letters.
10. **Template authoring UI** with preview, version diff and checks.

## 14. Open questions

1. **Texas currency:** confirm the current UMCM 3.21 version and its HHSC notice templates, ID card issuance timing, and member-materials reading level against the current UMCC and UMCM 4.3.
2. **Medicaid electronic notices:** how §438.10(c)(6)'s website condition applies to an individual notice, and the opt-in rules in §435.918.
3. **Audit protocol:** confirm the final status of the 2026 Part C ODAG data request before building the universe export.
4. **Print-and-mail provider:** choose the first API provider by BAA terms, per-piece USPS acceptance events (including the "mailed" event), IMb support and Certified Mail.

## Sources

- [42 CFR 422.564](https://www.ecfr.gov/current/title-42/section-422.564), [422.568](https://www.ecfr.gov/current/title-42/section-422.568), [422.570](https://www.ecfr.gov/current/title-42/section-422.570), [422.572](https://www.ecfr.gov/current/title-42/section-422.572), [422.584](https://www.ecfr.gov/current/title-42/section-422.584), [422.590](https://www.ecfr.gov/current/title-42/section-422.590), [422.618](https://www.ecfr.gov/current/title-42/section-422.618), [422.629](https://www.ecfr.gov/current/title-42/section-422.629), [422.631](https://www.ecfr.gov/current/title-42/section-422.631), [422.633](https://www.ecfr.gov/current/title-42/section-422.633), [422.504](https://www.ecfr.gov/current/title-42/section-422.504), [422.2267](https://www.ecfr.gov/current/title-42/section-422.2267)
- [42 CFR 438.3](https://www.ecfr.gov/current/title-42/section-438.3), [438.10](https://www.ecfr.gov/current/title-42/section-438.10), [438.210](https://www.ecfr.gov/current/title-42/section-438.210), [438.242](https://www.ecfr.gov/current/title-42/section-438.242), [438.402](https://www.ecfr.gov/current/title-42/section-438.402), [438.404](https://www.ecfr.gov/current/title-42/section-438.404), [438.406](https://www.ecfr.gov/current/title-42/section-438.406), [438.408](https://www.ecfr.gov/current/title-42/section-438.408), [438.416](https://www.ecfr.gov/current/title-42/section-438.416), [438.420](https://www.ecfr.gov/current/title-42/section-438.420), [431.80](https://www.ecfr.gov/current/title-42/section-431.80), [431.201](https://www.ecfr.gov/current/title-42/section-431.201), [431.211](https://www.ecfr.gov/current/title-42/section-431.211)
- [45 CFR part 92 (Section 1557)](https://www.ecfr.gov/current/title-45/subtitle-A/subchapter-A/part-92); [HHS notice of the October 2025 vacatur, June 2, 2026](https://www.federalregister.gov/documents/2026/06/02/2026-11015/notice-of-vacatur-regarding-certain-provisions-of-the-2024-nondiscrimination-in-health-programs-and)
- [CMS: Parts C & D Enrollee Grievances, Organization/Coverage Determinations, and Appeals Guidance](https://www.cms.gov/medicare/appeals-and-grievances/mmcag/downloads/parts-c-and-d-enrollee-grievances-organization-coverage-determinations-and-appeals-guidance.pdf)
- [CMS: Integrated Denial Notice form instructions (Jan 2025)](https://www.cms.gov/files/document/integrated-denial-notice-form-instructions-cms-10003-eff-jan-2025.pdf); [CMS: MA notices and forms](https://www.cms.gov/medicare/appeals-grievances/managed-care/notices-forms)
- [CY2027 MA and Part D final rule (91 FR 17583)](https://www.federalregister.gov/documents/2026/04/06/2026-06600/medicare-program-contract-year-2027-and-certain-contract-year-2026-policy-and-technical-changes-to); [CMS: marketing guideline changes](https://www.cms.gov/files/document/changes-medicare-advantage-organization-medicare-drug-plan-sponsors-marketing-guidelines.pdf)
- [Part C ODAG audit data request (2026 OMB submission)](https://omb.report/icr/202606-0938-024/doc/170673500.pdf)
- [2016 Medicaid managed care final rule (81 FR 27498)](https://www.govinfo.gov/content/pkg/FR-2016-05-06/html/2016-09581.htm)
- [Texas UMCM 3.21: service authorization notice requirements](https://fhb.hhs.texas.gov/sites/default/files/documents/3-21.pdf); [Texas UMCM index](https://www.hhs.texas.gov/es/node/17074)
- [USPS: Informed Visibility replaces IMb Tracing](https://about.usps.com/postal-bulletin/2018/pb22486/html/updt_002.htm); [USPS: certificates of mailing](https://about.usps.com/postal-bulletin/2017/pb22463/html/updt_003.htm); [USPS: Move Update](https://pe.usps.com/text/qsg300/q602a.htm)
- [HL7 CARIN Digital Insurance Card IG](https://hl7.org/fhir/us/insurance-card/); [WEDI Health Identification Card Implementation Guide](https://www.wedi.org/2011/04/28/health-identification-card-implementation-guide)
