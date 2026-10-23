# Claim RFAI — implementation design

**Status:** proposal. §7 records the decisions, the research behind them and the recommendation taken.
**Implements:** [document-store.md](document-store.md) step 6.
**Policy already settled elsewhere:** [claim-review-workbench.md](claim-review-workbench.md) §3 defines the
reviewer flow, the 45-day default, the deadline behaviours and the clean-date / prompt-pay rules. This
document does not restate those. It covers only *how the code gets there* from what exists today.

---

## 1. The gap in one paragraph

CHO can receive an X12 275, match it to a claim with care, and file it in the document catalog. Nothing
ever **asks** for one on a claim, and no claim ever **waits** for one. The request lifecycle exists in
`rfai-service` but is bound to prior authorization: `RfaiCase.AuthNumber` is a required field, every
lookup is `by-auth/...`, and claims-service contains no reference to RFAI at all. So the expensive half
is built and attached to the wrong subject.

[work-waiting-and-cycle-time.md](work-waiting-and-cycle-time.md) reached the same conclusion from the
other direction: a wait should end on an **event**, not a date, and a claim waiting on records has no
RFAI to wake it, so the commonest wait in practice falls back to a timer. Its Q1 recommends extending
RFAI to claim attachment requests rather than waking on a bare attachment arriving, because an
attachment alone cannot tell solicited from unsolicited. This design is that recommendation worked out.

**There is a date on it.** CMS-0053-F, final 24 March 2026, requires the 275 and the **277 Request for
Additional Information** by **26 May 2028** — and dropped prior authorization attachments from the final
rule. So the authorization RFAI CHO has is the part that did not make the rule, and the claim RFAI it
lacks is the part that did. See §7.1.

---

## 2. What already exists

Worth reading before writing anything, because most of this is reusable.

| Piece | Where | State |
|---|---|---|
| RFAI case lifecycle — requested items with codes, due date, delivery tracking, responses, attachments received, close, cancel | `src/services/rfai-service/` | Built. **Authorization-only.** |
| PWK segments on the claim — PWK01 report type, PWK02 transmission code, PWK06 control number, claim- and line-level | `claims-service/Models/Claim.cs` (`~L250`, `~L1996`) | Built. Parsed and stored; **nothing acts on it.** |
| 275 receive and control-number matching, billing-NPI agreement required, 30-day hold, background re-matcher, 824 reject | `attachment-service/`, `UnmatchedAttachmentMatcher` | Built. |
| Document catalog with many-to-many links, including `RfaiRequest` | `document-service`, `CloudHealthOffice.DocumentStore` | Built. |
| Pend machinery — `PendDetails` with `PendCode`, `PendReason`, `PendedAt`, fingerprint; `ClaimPendedEvent` | `CloudHealthOffice.Events/ClaimPendedEvent.cs`, `Claim.cs` (`~L1402`) | Built. Codes today: NCCI, MUE, AUTH, NOAUTH, OON, NOCONTRACT, COB, MEDREVIEW, CLINICAL. **No attachment code.** |
| Documents panel on the claim | `Portal/Shared/Documents/DocumentsPanel.razor` | Built 2026-10-23 (#1516). Read-only; no "open requests" strip. |
| CDex `$submit-attachment`, Task mapping | `fhir-service/Services/Cdex/` | Built. |

## 3. What is missing

1. **A claim subject on the RFAI case.** `AuthNumber` is required and non-nullable; there is no claim id.
2. **A reason to wait.** No pend code means a claim needing records cannot pend for it.
3. **A way to wait.** `WorkItemStatus` is `Open | Leased | Done | Cancelled`, so there is no state for
   *waiting on somebody outside the plan* — holding the lease parks the item invisibly while the clock
   runs, and releasing it loses the diagnosis. `Waiting` is **designed but unbuilt**
   ([work-waiting-and-cycle-time.md](work-waiting-and-cycle-time.md), merged in #1514), so this is a
   dependency to implement rather than a question to answer.
4. **Clean-date events.** Nothing in `src/` references a clean date. The workbench specifies deriving it
   from events (received / requested / received-back / corrected); none of those events are recorded.
5. **The outbound ask.** No 277 RFAI (006020X313) encoder, and no letter path for non-electronic providers.
6. **A manual link.** `GET /api/attachments/unmatched` is read-only — a person who can see that a held 275
   belongs to a claim has no way to say so. (Covered separately; see §8.)

---

## 4. Design

### 4.1 Two triggers, one waiting machine

These are different events that the code should keep distinct, because the clock rules in workbench §3.3
treat them differently and because only one of them is the plan's doing.

- **Provider-promised (PWK).** The 837 carried `PWK02 = EL | FX | BM | FT | EM` and no matching 275 has
  arrived. The provider has told us records are coming. The plan asked for nothing.
- **Payer-initiated (RFAI).** A reviewer asked for something specific. This is the one that sends a 277
  RFAI or a letter, and the one the prompt-pay rules key on.

`PWK02 = AA` ("available on request") is **not** a trigger. It means the provider will send records if
asked, which is a payer-initiated request if we want them.

Both end in the same place: a claim that is waiting, a work item that is off Get Next, and a document
that eventually arrives and releases it. Build the waiting machine once.

### 4.2 The RFAI case grows a subject

Make the case polymorphic over its subject rather than adding a parallel claim case:

- `AuthNumber` becomes nullable; add `ClaimId` and `ClaimVersionId`.
- Add a required `SubjectKind` (`Authorization | Claim`) so a case is never ambiguous and so queries can
  be subject-scoped the way the document catalog already is.
- Add `by-claim/{tenantId}/{claimId}` alongside the existing `by-auth` lookups.
- Existing rows backfill to `SubjectKind = Authorization`. The constraint moves from "AuthNumber is
  present" to "the field matching SubjectKind is present", enforced on write.

The alternative — a separate `ClaimRfaiCase` — duplicates the lifecycle, the delivery tracking and the
received-attachment handling, and would drift. Rejected.

### 4.3 The claim waits

- New pend code **`RFAI`**, with `PendReason` naming what was asked for and when it is due.
- A new adjudication stage raises the PWK pend. Order matters: it must sit **after** the stages that
  decide whether records are actually needed and before persistence. Exact order to be fixed during
  implementation against the current stage table.
- The work item goes to **`Waiting`**, which is already designed — see
  [work-waiting-and-cycle-time.md](work-waiting-and-cycle-time.md), merged in #1514 and not yet built.
  This design does not redesign it and should not; it is a consumer of it.

Two things that design settles, and that this one must not quietly contradict:

- **The clock does not stop.** `DueAt` is untouched and prompt-pay interest runs while an item waits,
  because it does in law. Suspending even the internal SLA is deliberately not offered for regulated
  work. So "the claim waits" means *whose turn it is* changes, not that a timer pauses. The prompt-pay
  consequence of a request is handled where it belongs — in the clean-date derivation (§4.6), which
  computes reset / pause / extend from the recorded events — and never by stopping a clock.
  `claim-review-workbench.md` §3.2 reads as though the SLA pauses; the waiting design is the later and
  more careful word, and this is the reading to build.
- **Who may set it.** `Waiting` is person-initiated, with one exception already carved out: a
  source-owned item whose source says it is blocked. A claim pended for records is exactly that
  exception, so raising it from adjudication is in scope rather than a new precedent.

### 4.4 Documents arrive, the wait ends

The matcher already links a 275 to a claim by control number with billing-NPI agreement. Extend it so
that when the linked claim has an open RFAI or PWK pend:

1. the document is linked to the `RfaiRequest` as well as the claim (the catalog already models both);
2. the case moves to responded, or closes if everything asked for has arrived;
3. an **information-received** event is recorded;
4. the work item leaves `Waiting` and returns to the queue, preferring the reviewer who asked;
5. the claim re-adjudicates.

Partial responses must not close the case. Three documents asked for and one received is still waiting,
and the "open requests" strip should show which.

### 4.5 Nothing arrives

Per workbench §3.2, a tenant setting chooses between back-to-a-person (default) and auto-deny with
CARC 252 / RARC N706. A due-date sweeper raises one or the other. The sweeper is the natural place to
enforce the §3.3 guardrail that a late or second request does not move the clock.

### 4.6 Clean-date events

Introduce an append-only claim event log — received, information requested, information received,
corrected claim received — and derive the clean date and prompt-pay due date from it per line of
business and state. The workbench is explicit that the date is **derived, never stored**, and that
internal pends never move it. Note this means the RFAI pend must be distinguishable from the existing
internal pend codes at the point the rule runs; the `RFAI` pend code gives it that.

This is the largest single piece and the one with the most regulatory exposure. It is separable from the
rest: requesting and waiting work without it, they just do not yet compute interest correctly.

---

## 5. Ownership

| Concern | Service |
|---|---|
| The request and its lifecycle | rfai-service |
| Deciding records are needed; pending; re-adjudicating | claims-service |
| Receiving, matching, 824 | attachment-service |
| Storing, linking, audit | document-service |
| Waiting, Get Next, SLA | work-management-service |
| 277 RFAI out | EDI / correspondence |

No new service. The seam that does not exist today is claims-service → rfai-service, which needs a
client and an outbound service token the way the other cross-service calls do.

---

## 6. Build order

Each lands on its own and is useful before the next.

Ordered against the CMS-0053-F compliance date of 26 May 2028 (§7.1), which the payer-initiated path
carries and the provider-promised path does not. The draft had the PWK pend third, ahead of the
reviewer's request; §7.2 reverses that.

1. **`WorkItemStatus.Waiting`** — implementing the design already merged in #1514, not redesigning it.
   Widest blast radius, no dependencies on anything here, and nothing below is safe until it exists.
   It is worth building for its own reasons; this work is one of its callers.
2. **Clean-date event log** — received, information requested, information received, corrected claim
   received. Early because it is append-only and cannot be backfilled (§7.4): anything requested before
   it exists is unmeasurable forever. The per-state derivation is *not* here; it comes last.
3. **RFAI case subject** — nullable `AuthNumber`, `SubjectKind`, `ClaimId`, `by-claim` lookup, backfill.
4. **Payer-initiated RFAI** — the reviewer action, the claims → rfai seam, the "open requests" strip on
   the Documents panel. The first point where the loop closes, and the path with the mandate behind it.
5. **277 RFAI out** (006020X313), plus the letter path for non-electronic providers. With step 4, this
   is what CMS-0053-F requires by May 2028.
6. **Due-date sweeper** — back-to-a-person and the auto-deny setting.
7. **PWK hold** — the provider-promised path, as a plan setting defaulting off, 7 days (§7.2, §7.3).
   Last because it is the one that can cost prompt-pay interest without buying clock relief, and it is
   worth having real traffic on the mandated path before turning it on anywhere.
8. **Per-state clean-date derivation** — reset / pause / extend over the events step 2 records.

Steps 1–5 are what CHO needs to claim claim-attachment handling honestly and to be compliant in 2028.
6–8 make it complete.

---

## 7. Decisions, and the research behind them

Researched 2026-10-23; recommendations below are what this design is written to.

### 7.1 The deadline this work is actually against

**CMS-0053-F is final.** Published in the Federal Register 24 March 2026, effective 26 May 2026,
**compliance date 26 May 2028**. It adopts X12N 275 version 006020X314 and X12N **277 Request for
Additional Information** version 006020X313 — the transaction a health plan uses to *ask* a provider for
documentation — together with the March 2022 HL7 Attachments IG.

Two consequences worth stating plainly:

- **Prior authorization attachments were dropped from the final rule** (they were in the proposed rule;
  CMS cited possible misalignment with the 278 standard). So the half CHO has built, authorization RFAI,
  is the half that did *not* make the final rule, and the half it lacks — claim RFAI with a 277 out — is
  the mandated one.
- The 277 RFAI is therefore not step 5 polish. It is the compliance artefact, with a date.

### 7.2 Pending a claim because the provider announced a PWK

**Recommendation: a plan setting, default off, and built after the payer-initiated path rather than
before it.**

The original draft reasoned only that pending-on-PWK is a behaviour change. The stronger argument is
about the clock. Every clean-date model in `claim-review-workbench.md` §3.3 keys on a **payer request**:
Medicaid resets on the arrival of information the plan *obtained*; ERISA pauses while the *claimant*
responds; Texas extends on a request the plan *made*. A provider volunteering "records are coming" is
none of those. The claim is clean, the clock runs, and pending it buys no clock relief — so the plan
holds a legally clean claim while prompt-pay interest accrues against it.

Texas is the clearest case. Cleanliness there is a property of the **claim form**, not of supporting
documentation: Tex. Ins. Code §1301.131 (PPO/EPO) and §843.336 (HMO) define a clean claim by the correct
form and the required fields, and subsection (f) of each says extra attachments or information do not
disqualify an otherwise-clean claim. A Texas claim with records outstanding was never unclean. (A
secondary guide states flatly that carriers "may not require attachments to establish a clean claim";
the statute does not say that in those words, and the structural point above is what it does support.)

States do differ in the other direction — Washington's WAC 284-83-325 includes "any lack of required
substantiating documentation" in what makes a claim unclean — which is why this belongs in the
per-state rule of §4.6 and not in a global switch.

### 7.3 How long a PWK promise holds the claim

**Recommendation: 7 calendar days, configurable per plan.**

Payer companion guides commonly give providers seven calendar days from claim submission to get the
attachment in when a PWK is present, absent a regulatory or contractual date (Wellpoint's 837I and 837P
guides state it in those terms). Providers already work to that number.

The 30 days in the first draft was a mistake: it borrowed the **unmatched-275 hold**, which is how long
attachment-service keeps an *orphan attachment* waiting for a claim to announce it. That is the opposite
direction and a different clock. The two should not be configured from one setting.

### 7.4 Whether clean-date work is in scope now

**Recommendation: build the event log now; leave the per-state derivation for later.**

These separate cleanly and have opposite reversibility.

- **The event log** (received / information requested / information received / corrected claim received)
  is append-only and **cannot be reconstructed after the fact**. Ship requesting and waiting without it
  and every request made before it exists is permanently unmeasurable — including the interest exposure
  those requests created. That is the one irreversible choice here.
- **The derivation** — reset, pause or extend per line of business and state — is arithmetic over stored
  events. It can land at any point afterwards and be applied retrospectively to everything recorded.

So record now, derive later. Of the three decisions this is the only one that is costly to defer; 7.2
and 7.3 are settings that can be changed once there is real traffic to look at.

## 8. Related, not in this design

**The held-275 queue** was API-only, with no manual link, so a 275 a person could see belonged to a claim
waited 30 days and was rejected with an 824. Built alongside this design; see
[document-store.md](document-store.md) §16.

**Solicited 275s name a parent the intake path does not accept.** Every attachment is classified
`Solicited` or `Unsolicited` from whether an RFAI reference (277 TRN02) came with it, and the unsolicited
path — PWK announcement, control-number match, billing-NPI agreement — is the well-built one. But
`RFAIReference` is not one of the parents the intake path resolves: it accepts `ClaimId`,
`AuthorizationId`, `AppealId` or `AttachmentControlNumber`, and rejects anything else with
*"Must specify one of…"*. A provider answering a 277 has no particular reason to echo the authorization
id, so a solicited 275 that names only its RFAI reference is refused at the door today. Resolving an RFAI
reference to its case needs an attachment-service → rfai-service lookup that does not exist yet — the
same seam §5 adds for claims — so it belongs with this work rather than before it.

---

## 9. Sources

Checked 2026-10-23. Statutes were read rather than taken from summaries; where a secondary source
overstated one, §7.2 says so.

- [CMS-0053-F, Federal Register, 24 March 2026](https://www.federalregister.gov/documents/2026/03/24/2026-05676/administrative-simplification-adoption-of-standards-for-health-care-claims-attachments) — adopted standards, effective and compliance dates.
- [CMS fact sheet, Adoption of Standards for Health Care Claims Attachments](https://www.cms.gov/files/document/nsg-attachments-final-rule-fact-sheet.pdf) — scope change dropping prior authorization attachments.
- [Tex. Ins. Code §1301.131](https://texas.public.law/statutes/tex._ins._code_section_1301.131) and [§843.336](https://texas.public.law/statutes/tex._ins._code_section_843.336) — clean claim defined by form and required fields; subsection (f) on extra attachments.
- [WAC 284-83-325](https://app.leg.wa.gov/wac/default.aspx?cite=284-83-325) — Washington's contrasting definition, which counts missing substantiating documentation against cleanliness.
- [Wellpoint 837I](https://www.wellpoint.com/content/dam/digital/wellpoint/documents/provider/west-virginia/other/wvwp-cd-057810-24-837I-health-care-claim-training-guide.pdf) and [837P](https://www.wellpoint.com/content/dam/digital/wellpoint/documents/provider/west-virginia/other/wvwp-cd-057811-24-837P-health-care-claim-training-guide.pdf) companion guides — the seven-calendar-day attachment window when a PWK is present.
