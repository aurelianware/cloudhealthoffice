# Claim RFAI — implementation design

**Status:** proposal, awaiting decisions in §7.
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

1. **`WorkItemStatus.Waiting`** — implementing the design already merged in #1514, not redesigning it.
   Widest blast radius, no dependencies on anything here, and nothing below is safe until it exists.
   It is worth building for its own reasons; this work is one of its callers.
2. **RFAI case subject** — nullable `AuthNumber`, `SubjectKind`, `ClaimId`, `by-claim` lookup, backfill.
3. **PWK pend** — the provider-promised path end to end: pend, wait, match, release, re-adjudicate.
   This is the first point where the loop visibly closes, and it needs no new outbound EDI.
4. **Payer-initiated RFAI** — the reviewer action, the claims → rfai seam, the "open requests" strip on
   the Documents panel.
5. **277 RFAI out**, plus the letter path for non-electronic providers.
6. **Due-date sweeper** — back-to-a-person and the auto-deny setting.
7. **Clean-date event log** and the prompt-pay derivation.

Steps 1–3 are the ones that change what CHO can honestly claim about claim attachments. 4–7 make it
complete.

---

## 7. Decisions needed

1. **Does the PWK pend default on or off?** It is a real behaviour change for anyone adjudicating today:
   claims that pay now would start waiting. Recommendation: build it behind a plan setting, default off,
   the same way inpatient grouping landed in #1517.
2. **How long does a PWK promise hold the claim?** The provider said records are coming but named no
   date. The 275 hold is 30 days; matching that is the obvious answer but it is not required to match.
3. **Is step 7 (clean date and prompt-pay interest) in scope now or later?** It is the largest piece and
   carries the most regulatory exposure. Requesting and waiting are useful without it.

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
