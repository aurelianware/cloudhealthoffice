# Claim review workbench: edit overrides, documents, corrections and intake

**Status:** Approved (2026-10-15). Section 7 records the decisions.
**Scope:** What a claims reviewer can see and do on a claim:
- override decisions, and how they are made and audited;
- documents and requests for additional information (RFAIs) on claims;
- which fields a reviewer may edit;
- how paper, fax and member-submitted claims get in.

**Already shipped:** line Pay/Deny/Pend status with reasons on claim detail (#1354), and append-only claim notes (#1355).

---

## 1. The reviewer's view

A reviewer opens a pended claim to answer one question: what should happen on each line, and why? The screen answers in this order.

1. **Header money row:**
   - Billed, Allowed, Plan paid, Member owes.
   - **New:** a fifth tile, *Contractual adjustment*, equal to billed minus allowed. Under it, a network chip, *In-network* or *Out-of-network*.
     - In-network: the gap is a CO (contractual obligation) write-off the provider absorbs and can't bill the member for.
     - Out-of-network: that same gap may be the member's (PR), subject to the No Surprises Act and state law.
   - Today the gap is unexplained, so it's the first thing a reviewer or a demo audience asks about.
2. **Lines** (shipped in #1354). Each line shows:
   - an outcome chip (Pay / Deny / Pend);
   - a one-line summary;
   - the edits on that line;
   - a "Why" table of CARC/RARC adjustments in plain language.
3. **Edits panel** (new). One row per edit that fired, with:
   - the rule (NCCI PTP, MUE, duplicate, authorization, eligibility, timely filing…), the lines it touches, and the message;
   - the edit's **override policy** (§2.3), so the reviewer can see before acting whether they may override it;
   - an **Override** action when they may.
4. **Documents and RFAIs** (§3).
5. **Notes** (shipped in #1355).
6. **Finish** (§2.4). Commits everything at once.

## 2. Edit-level overrides

### 2.1 What an override is

An override is a reviewer's decision that **one specific edit** should not apply to this claim. It's not a decision on the whole claim.
- Overriding the MUE edit on line 2 says nothing about the duplicate edit on line 4.
- It's different from today's whole-claim approve, which clears every reviewed pend at once.

An override is keyed by **edit identity**:
`(edit type, rule id, affected line numbers, column-1/column-2 codes where present)`.
Re-adjudication uses that key to recognise "the same edit the reviewer already decided on".

### 2.2 Re-adjudication doesn't undo the reviewer

The examiner-approval re-run already does this for pends. A pend the examiner reviewed passes on the re-run; one they didn't see keeps the claim pended (`ExaminerApproval.ReviewedPend`, `ClaimAdjudicationOrchestrator`). Step 2 extends that from the whole pend to individual edits:

- The re-run carries the claim's **edit dispositions**. When an edit fires whose identity matches an overridden disposition, the edit is **suppressed**: it's recorded on the stage result, but it doesn't pend or deny.
- An edit that fires and was **not** on the claim when the reviewer looked is new information. It still pends, and the Finish is refused with "new edit since you opened this claim", the same rule as today.
- If the claim data changes (corrected claim, frequency 7, or a keying correction from §4), dispositions whose identity no longer matches are dropped. The edit re-fires and needs a fresh decision. An override is a decision about specific facts, and it doesn't outlive them.

### 2.3 Who may override what: the override policy table

Each tenant gets an override policy table. The defaults:

| Edit | Default policy | Why |
|---|---|---|
| NCCI PTP, modifier indicator **0** | **Never** | CMS says no modifier makes the pair payable. |
| NCCI PTP, modifier indicator **1** | Examiner, reason required | A modifier is allowed. The reviewer confirms distinct service from the record. |
| MUE, MAI **1** (line edit) | Examiner | A claim-line limit. Extra units are often legitimate on separate lines with a modifier. |
| MUE, MAI **2** (policy) | **Never** | Absolute date-of-service limit set by policy (anatomy, code definition). |
| MUE, MAI **3** (clinical) | Supervisor, documentation required | The limit is clinical. Exceeding it needs medical records. |
| Duplicate claim / line | Examiner | The reviewer confirms it's a distinct service (e.g., a different laterality or time). |
| No authorization on file | Supervisor | Paying without an auth is a UM decision, not a claims one. |
| Eligibility / terminated coverage | Supervisor | Usually retro enrollment, so verify first. |
| Timely filing | Supervisor | Exceptions need proof of timely original submission. |

- Policy values: `Never`, `Examiner`, `Supervisor`. Each can also require a reason, documentation, or both.
- `Never` can't be loosened above the federal floor for Medicaid and Medicare lines of business. The UI says why the Override button is missing.
- Tenants edit the table in Configuration. Changes are audited.

### 2.4 Uphold by default, commit once on Finish

- There's **no Uphold button.** An edit the reviewer doesn't touch stands.
- While the reviewer works, overrides and manual line denials are a **draft**. The money row and line chips recalculate live as a preview, so they can see what Finish will do.
- **Finish** shows a summary:
  > 3 edits: 1 overridden (MUE line 2 → pay 2 units), 2 upheld. Plan pays $5,165 → $5,820.
- On confirm, the claim is re-adjudicated once with the dispositions, then finalized under the existing resolution lock.
- The audit records **every** edit's disposition. Untouched edits are recorded as *upheld by [reviewer] at Finish*, so the record shows a person looked at every edit, not just the ones they changed.
- Each override's reason is also written as a **Decision note**, so the notes tell the story without opening the audit.

### 2.5 Manual line denial

A reviewer can deny a line the engine would pay. For example, the records don't support the service, or the service was billed under the wrong code.
- A CARC (and RARC, where needed) is required, picked from a short list of reviewer denial reasons. The list is configurable.
- It shows on the line exactly like an engine denial, including on the 835.
- It's part of the same draft and the same Finish.

### 2.6 Permissions

| Permission | Grants |
|---|---|
| `claims:work` | Work claims, override `Examiner`-policy edits, deny lines. (Existing.) |
| `claims:note` | **New.** Add notes to a claim. Held by claims staff, member services and provider relations. |
| `claims:override-edit` | **New.** Override `Supervisor`-policy edits. |
| `claims:override-approve` | Existing whole-claim supervisor override and payer-order override. Unchanged. |

**Optional second approver:**
- Off by default. A tenant can turn it on to require a second, different person for overrides over a dollar threshold (e.g., more than $10,000 of added payment) or on chosen edit types.
- The Finish then parks the claim in "awaiting approval" instead of finalizing.
- This reuses the existing `SecondApproverId` path.

## 3. Documents and claim RFAIs

### 3.1 Documents panel

Lists everything attached to the claim:
- the scanned paper claim (§5);
- PWK/275 attachments from the 837;
- RFAI responses;
- uploads by staff.

Each entry shows type, source, received date and who added it, and opens in a viewer. Documents are stored with the attachments service (already used for authorizations), linked by claim id.

### 3.2 Requesting records (claim RFAI)

1. **Request.** The reviewer selects "Request information", picks what's needed (records, itemized bill, operative report, prior payer EOB…) and a due date. The default is **45 days**, configurable per tenant and line of business.
   - CHO sends a 277 RFAI to the provider, or a letter when the provider isn't electronic.
2. **The claim waits.** It leaves the work queue and its work item is **paused**: off Get Next, and the SLA clock is stopped where the line of business allows it.
3. **Documents arrive.** A 275 or upload is matched to the request, the work item resumes, and the claim goes back to the queue, ideally to the reviewer who asked.
4. **Nothing arrives by the deadline.** One of two tenant settings applies:
   - **Back to a person** for a decision. This is the default.
   - **Auto-deny** with CARC 252 (*additional information required*) / RARC N706 (*missing documentation*), when the tenant turns it on.

**Prompt pay:** whether and how an RFAI moves the prompt-pay clock depends on the line of business and state: reset, pause or extend. See §3.3.

### 3.3 Clean date and prompt-pay interest

**The clean date is the day the plan has everything it needs from the provider to pay the claim.** It records whose turn it is, not when the plan gets around to it. Internal pends, examiner queues, the plan's own medical review, pricing vendor delays and payer-initiated COB investigation **never** move it.

**CHO records events and derives dates.** It doesn't store a hand-maintained date. The events are:
- received (front door: clearinghouse or portal receipt, mailroom stamp);
- information requested (what, when, how);
- information received;
- corrected claim received.

The clean date and the prompt-pay due date are computed from those events by the rule for the claim's line of business and state. The rules differ in *how* a request affects the clock:

| Model | Rule | Example |
|---|---|---|
| Reset | Clean date = the date the requested information arrives. | Medicaid's clean-claim definition: processable "without obtaining additional information from the provider" (42 CFR 447.45). |
| Pause | The clock stops while waiting and resumes on receipt. | ERISA post-service claims: 30 days, plus 15 with notice; the claimant gets 45 days to respond, and the wait doesn't count. |
| Extend | The claim stays clean. One specific request, made within 30 days, moves the deadline to the later of the original date or 15 days after the attachment arrives. | Texas HMOs, Tex. Ins. Code § 843.3385. |

**Guardrails:**
- A request made after the rule's window, or a second request where only one is allowed, doesn't move the clock. The screen says so before the reviewer sends it.
- Interest on a claim reprocessed after a plan error runs from the original clean date.

**On screen:** the claim header shows the received date, the clean date and the due date, with the reason in one line:

> Due Nov 14. Received Oct 15. Records requested Oct 20, received Oct 30. Texas: the later of the original due date or 15 days after the records.

An **at-risk queue** lists claims approaching their due date.

**Interest rules are dated configuration, not one rate:**
- **Applies to:** line of business, state, provider type, paper or electronic.
- **Starts:** a number of days after the clean or received date.
- **Method:** daily simple interest, per month or part of a month, or a penalty formula.
- **Rate:** fixed, or a named series with dated values.
- **Optional:** a minimum amount and a payment split.

Examples:
- **Medicare and MA:** the Treasury prompt-payment rate, 4.125% for January–June 2026, reset every January 1 and July 1.
- **Arizona Medicaid (AHCCCS ACOM 203):** hospital claims pay 1% a month or part of a month from day 61; other claims pay 10% a year, prorated daily, from day 46.
- **Texas HMOs (§ 843.342):** a penalty on billed minus contracted, plus 18% a year from day 91. Part of an institutional provider's penalty goes to the state.

CHO ships rule packs, and tenants and provider contracts can override them. Interest is computed at payment and paid on the 835 as claim-level interest (AMT*I). Reference data already holds a per-state prompt-pay window and penalty rate; this extends it.

**Attachments standard:** the plan's requests and the provider's responses use the X12 277 RFAI (006020X313) and the 275 (006020X314) adopted by CMS-0053-F. Compliance is required by May 26, 2028. Fax, portal and FHIR responses go through the same matching (see [document-store.md](document-store.md) §4).

## 4. What a reviewer may edit

| Data | Editable? | Notes |
|---|---|---|
| Decisions (overrides, line denials, payer order) | Yes | §2. |
| Payer-side data: auth number linked, COB payer order, prior-payer amounts from an EOB | Yes, `claims:work` | Reason required. Audited, old → new. |
| Pricing adjustments (contract rate, manual price) | Supervisor only | Reason required. Audited. |
| Notes | Append only | Shipped. |
| **Billed data** (codes, units, charges, diagnoses, dates, NPIs) on an **electronic** claim | **Never** | That's what the provider billed. Corrections come from the provider as a corrected claim (frequency 7) or void (8). The reviewer can deny or override, not rewrite. |
| Billed data on a **paper-origin** claim | **Keying corrections only** | §5. Correcting a data-entry mistake against the scanned image, with the image shown side by side. Audited as a keying correction, with the old and new value. |

Rewriting billed data on an electronic claim breaks the audit trail and the 835 the provider reconciles against, so it's left out deliberately.

## 5. Intake channels: paper, fax and member-submitted claims

Health plans still have to accept paper (CMS-1500, UB-04), faxes and member reimbursement requests. Medicaid and Medicare Advantage plans can't refuse a paper claim from a provider who isn't required to bill electronically.

- **Paper-origin flag and source image.** Every claim records its channel: EDI, paper, fax, member, or manual. A non-EDI claim links to its source image in the documents panel.
- **Manual entry form.**
  - A keying screen per form (1500 / UB-04 / member reimbursement), laid out like the form.
  - It validates the same way the 837 intake does.
  - It produces the same claim the EDI path does, and adjudicates through the same pipeline.
  - The image sits beside the form.
- **AI-assisted extraction (later, optional).** Claude reads the scanned form and pre-fills the entry form, field by field with confidence. A person always reviews and submits; nothing is adjudicated straight from extraction. Low-confidence fields are highlighted.
- **Member reimbursement.** A member-submitted claim (receipt plus form) gets the same flow, plus a check that the member paid, so payment goes to the member rather than the provider.

## 6. Build order (step 2 onward)

1. **Money row and network chip** (§1). Small and demo-visible.
2. **Edit dispositions in the pipeline** (§2.1–2.2): identity key, suppression on re-run, and the "new edit" refusal. Tests against the real stages.
3. **Override policy table with defaults** (§2.3), plus permissions (§2.6).
4. **Edits panel, draft preview and Finish summary** (§2.4), plus manual line deny (§2.5).
5. **Documents panel** (§3.1), then **claim RFAI** (§3.2).
6. **Editable payer-side fields** (§4).
7. **Paper intake:** flag, image, manual entry form (§5). AI extraction last.

## 7. Decisions

Agreed 2026-10-15:

1. **Notes get their own permission, `claims:note`.** Member services and provider relations hold it as well as claims staff, so a rep can record a member or provider call on the claim (§2.6).
2. **RFAI window:** 45 days by default, configurable per tenant and line of business (§3.2).
3. **No records by the deadline:** the claim goes back to a person by default. Auto-deny (CARC 252 / N706) is an opt-in tenant setting (§3.2).
4. **Second approver:** off by default. A tenant can turn it on with a dollar threshold or for chosen edit types (§2.6).
