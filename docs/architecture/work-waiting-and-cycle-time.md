# Waiting work, and where a plan's cycle time actually goes

**Status:** design for review, 2026-10-23. Nothing in this document is built.
Open questions are in §12.

**The premise.** For most operational work a health plan does, **waiting is most
of cycle time and touch time is the small term.** An examiner may spend eight
minutes on a pended claim that sits for eighteen days. A plan that wants its
cycle time down is mostly trying to shorten waits, not make people faster — and
CHO today cannot tell it where the eighteen days went, because an item that is
open and un-workable is indistinguishable from one waiting for a person.

This document adds one state so that distinction exists, says what ends a wait,
decomposes cycle time into its three parts, and is explicit about which of this
is plumbing, which is arithmetic, and which is a job for a model.

**Builds on:**
- [`work-management.md`](work-management.md) — the work engine, leases, the
  priority scale, `SlaDays`, the audit stream;
- [`workforce-optimization.md`](workforce-optimization.md) — the snapshot series
  and the deadline-risk projection, both of which are **wrong today** for the
  reason in §10;
- [`operational-notifications.md`](operational-notifications.md) — how a wake, a
  chase and a breach warning reach a person;
- [`cho-service.md`](cho-service.md) — contacts, follow-ups, and the sampling
  pattern §9 reuses;
- [`correspondence.md`](correspondence.md) — the only sanctioned way CHO speaks
  to a provider or member.

---

## 1. What exists, and the two bad options

`WorkItemStatus` has four values: `Open`, `Leased`, `Done`, `Cancelled`.

**"I got interrupted, let me come back" already works.** Get Next returns the
caller's own active lease first (`resumed: true`), `GET mine` lists what they
hold, and `renew` / `release` are theirs. One rough edge: the 30-minute lease
expires silently, so somebody who goes to lunch loses their place with no
warning. That is a UI fix (time remaining in My Work, a warning before expiry),
not a design question, and §11 phase 1 carries it.

**"I have done my part and am waiting on someone else" has no state at all** —
the RFAI to a provider, the callback to a member, the question sent to another
department, the appeal whose clinical review is out. The examiner's two options
are both bad:

| Option | What it costs |
|---|---|
| **Hold the lease** | The item is parked invisibly while its SLA and any regulatory clock keep running. A supervisor-pushed item holds for 480 minutes by default. The queue says somebody is working it; nobody is. |
| **Release to the queue** | The context is lost. The next person to pull it re-reads the file, re-reaches the same conclusion, and either re-sends the request or parks it again. The plan pays for the same diagnosis twice and the member waits longer. |

Neither records *what* is being waited for, so nothing can wake the item, nothing
can chase, and no report can say how much of the plan's inventory is simply
blocked.

---

## 2. One state, not a workflow engine

**Plans vary enormously in process. They do not vary at all in "I am waiting on
someone else."** So this models the fact, not the procedure: no configurable
statuses, no per-plan state machines, no second queue, no designer. One state
with a reason, a wake and a clock.

```
WorkItemStatus
  Open | Leased | Waiting | Done | Cancelled
                  ^^^^^^^ new

WorkItem (added fields)
  waitReason        code from the tenant's wait taxonomy (§3)
  waitNote          the rep's own words, optional, no PHI beyond what the item has
  waitOwnerId       who parked it; it returns to them, not to the queue
  waitStartedAt
  waitUntil?        the date fallback, when no event can end it (§4)
  waitOn?           { kind, ref }  e.g. { rfai, rfai:9f2… }, { contact, member:… }
  waitWakeCount     times this item has been parked (abuse signal, §7)
  expectedWaitDays? the measured median for this reason and counterparty (§9)
```

Rules that make it a state rather than a hiding place:

1. **It stays owned.** A `Waiting` item is not in the pullable pool and does not
   appear in anyone else's Get Next. It returns to `waitOwnerId`, who already
   knows the file. A supervisor can still reassign it, audited as now.
2. **The clock does not stop.** `DueAt` is untouched. Prompt-pay interest, a
   42 CFR 422 grievance clock and an appeal deadline run while an item waits,
   because they do in law. Waiting changes who is expected to act next; it
   changes nothing about when the plan is late.
3. **It is counted, not hidden.** Every queue view, Insights figure and report
   shows **Open / Waiting / Leased** as three numbers. A single "open" count that
   silently includes blocked work is the problem this exists to fix.
4. **Priority keeps accruing.** The item re-scores as it waits (the existing
   rescore job), so it comes back at the top when it wakes, as it should.
5. **One state, one taxonomy, no branching.** There is no "waiting approval"
   state distinct from "waiting records" — those are reason codes. Reasons are
   tenant configuration, the same pattern as the contact reason taxonomy that
   `contact-service` already owns.

---

## 3. The wait taxonomy

Per tenant, seeded with a default set, each reason carrying its default wake
(§4), whether a date is required, and a default chase interval:

| Reason | Ends on | Typical counterparty |
|---|---|---|
| `WaitingProviderRecords` | An RFAI / document arriving | Provider |
| `WaitingMemberCallback` | An inbound contact from that member | Member |
| `WaitingMemberDocument` | A document arriving (AOR, consent, proof) | Member or representative |
| `WaitingClinicalReview` | An authorization or appeal review decision | Internal, UM |
| `WaitingAnotherDepartment` | A date, or the linked item completing | Internal |
| `WaitingExternalEntity` | A date | IRE, state fair hearing, vendor, another payer |
| `WaitingSystemOrConfig` | A date, or a config change publishing | Internal, configuration |
| `WaitingPaymentOrFinance` | A payment run posting | Internal, finance |

A reason is never free text, because every statistic in §8 and §9 is grouped by
it. `waitNote` carries the specifics.

---

## 4. What ends a wait: an event where CHO knows one, a date where it does not

This is the part a generic CRM task cannot do, and it is the single largest win
in this document. CHO owns the systems the plan is waiting on, so in most cases
the wait is **event-terminated**, and nobody has to remember anything.

| Wake | Source today | Notes |
|---|---|---|
| RFAI answered | `rfai-service`: `RfaiStatus.DocsReceived` | **Caveat in §4.1** |
| A document arrives | `document-service` / `member-document-service`, linked to the member, case or claim | Needs the link to be made when the wait is set |
| The member calls | `contact-service`: a new contact whose `subjects[]` names this claim, case or member | Step 1 already links subjects; this is a consumer of it |
| An authorization or appeal decision | authorization-service / appeals-service events | Phase 2 work sources make these items anyway |
| A payment posts | payment-service run completion | |
| A linked work item completes | work-management itself | `WaitingAnotherDepartment` where the other department's work is also a work item |
| Configuration publishes | plan or fee-schedule versioning | `WaitingSystemOrConfig` |
| **A date** | the `waitUntil` fallback | The honest default where CHO cannot know |

A wake sets the item back to `Open`, assigned to `waitOwnerId`, writes a
`WaitEnded` audit event with what ended it, and publishes
`work.item.wait_ended` — a notification topic, delivered by
[`operational-notifications.md`](operational-notifications.md) rather than a
mechanism invented here. A wait that reaches `waitUntil` with no event publishes
`work.item.wait_overdue` instead, which is the chase trigger (§9).

### 4.1 The RFAI caveat, checked rather than assumed

`rfai-service` models additional-information requests raised from **X12 278
authorization review decisions** (`RfaiRequestSources.ReviewDecisionA4`,
`PayerReview`), with `DueDate`, `RespondedAt` and `IsOverdue`. It is a good fit
for a *prior-authorization* item waiting on records.

**A claim waiting on provider records has no RFAI today.** So
`WaitingProviderRecords` on a claim item currently has no event to wake it and
falls back to a date, which is the weakest case in the table and the most common
reason in practice.

**Now designed elsewhere.** [`claim-rfai.md`](claim-rfai.md) (merged in #1518)
works out exactly the recommendation §12 Q1 made: give `RfaiCase` a claim
subject rather than waking on a bare attachment, since an attachment alone
cannot tell a solicited document from an unsolicited one. It reaches the same
finding from the other direction — the request lifecycle is built and attached
to the wrong subject — and lists `WorkItemStatus.Waiting` among the things that
must exist first, so the two designs are dependencies of each other rather than
alternatives. §11 step 6 is therefore implementing that design, not deciding
what it should be.

---

## 5. Parking and unparking

- `POST /api/v1/work/{id}/wait` — body: reason, optional note, optional
  `waitUntil`, optional `waitOn`. Permission `workqueue:work`; **holder only**,
  the same rule as `renew` and `release`. Validation: the reason must be in the
  tenant's taxonomy, and a reason whose wake is a date requires `waitUntil`
  within a configured maximum (default 30 days).
- `POST /api/v1/work/{id}/resume` — the owner pulls it back early.
- `POST /api/v1/work/{id}/wait/extend` — a new date with a reason, counted
  (§7).
- Supervisor (`workqueue:assign`): `GET /api/v1/work/waiting` with filters, and
  the existing `assign` / `unassign`, which work on a waiting item as they do on
  any other.
- Every transition writes a `WorkAuditEvent`: `WaitStarted`, `WaitExtended`,
  `WaitEnded` (with cause), `WaitResumedEarly`, `WaitAbandoned`.

Concurrency is the existing conditional write on `Version`; parking is a normal
item write.

---

## 6. What this is not

- **Not a hold on the deadline.** §2 rule 2.
- **Not a tickler file.** An item parked on a date with no event is the fallback,
  not the design centre; §9's statistics exist partly to show how much of a
  tenant's waiting is date-based and therefore un-automatable, so it can be
  attacked.
- **Not a second inventory.** Waiting items live in their queue, keep their
  score, and show in its counts.
- **Not an approval workflow.** "Waiting for my supervisor to approve" is a
  reason code; the approval itself belongs to whatever service owns the decision.
- **Not a reason to let go of the item.** Release still exists and still means
  "somebody else should take this".

---

## 7. The abuse this invites, and the controls

A waiting state is the easiest thing in any work system to misuse, because it is
how a queue is made to look clean. The controls are part of the design, not a
later hardening pass:

- **Never suppresses the deadline.** Waiting items appear in the deadline-risk
  projection and in every breach count. A plan cannot park its way out of a
  regulatory clock, and no configuration permits it.
- **A maximum wait per reason,** tenant-configured, past which the item cannot
  be parked again without a supervisor.
- **`waitWakeCount` surfaces re-parking.** An item parked more than twice
  appears on a supervisor list on its own. Re-parking usually means the first
  request was wrong, which is a coaching or template problem.
- **Oldest-waiting is always visible**, per queue and per owner.
- **Waits over their expected duration are flagged** against the measured median
  for that reason and counterparty (§9), not against a guess.
- **Parking is attributed and audited,** like every other transition.
- **A wait with no event and no plausible date is a finding,** not a state: the
  coverage view lists reasons where a tenant's waits never end on an event, which
  is where §4.1-style gaps show up.

---

## 8. Cycle time, decomposed — the number no plan has

With `Waiting` in place, an item's elapsed life splits three ways, measurable
from the audit stream:

```
touch time  = Σ (Leased → Completed / Released) while actually held
queue time  = time Open and pullable, waiting for a person
wait time   = time in Waiting, waiting for someone outside the queue
```

Added to the `WorkQueueSnapshot` in
[`workforce-optimization.md`](workforce-optimization.md) §4.1, per queue per
hour: `waiting`, `waitStarts`, `waitEnds`, `waitEndedByEvent`,
`waitEndedByDate`, `waitAbandoned`, `medianWaitDays` and
`waitingByReason{}` (bounded like the other facet counts).

What that makes answerable, none of which CHO can answer today:

- **Where the cycle time goes,** per queue and per line of business: "receipt to
  payment is 18.4 days — 0.3 touch, 2.1 queue, 16.0 waiting".
- **Which waits dominate:** by reason, counterparty and document type.
- **Whether waits are getting shorter,** which is the only honest measure of
  whether any of this worked.
- **How much is self-inflicted:** waits on another internal department, or on
  configuration, versus genuinely external ones.
- **Prompt-pay and timeliness exposure** attributable to waiting rather than to
  staffing — which is the number that decides whether a plan should hire or fix
  its RFAI process.

This decomposition, not the forecast, is the strongest operations claim CHO can
make. It is also phase 1: it needs the state and the audit stream, no model and
no forecast.

---

## 9. Where AI helps, where arithmetic is enough, and where neither belongs

Set out plainly, because the temptation is to claim the whole area.

### 9.1 Plumbing does the most work (no model, no statistics)

Event-terminated waits (§4) remove the entire class of "nobody followed up".
That is the largest single improvement in this document and it is a consumer of
events CHO already publishes. Claiming it as AI would be dishonest.

### 9.2 Arithmetic does the next most

- **Expected wait duration** per reason and counterparty: "RFAIs to this provider
  return in a median of 11 days, p90 26". Measured, not predicted.
- This is what lets the **deadline-risk projection treat a waiting item
  honestly** instead of assuming it returns tomorrow (§10).
- **Chase timing** from the same distribution: chase at the point where the
  response curve flattens, per counterparty, rather than on a fixed interval.
- **Channel effectiveness** per provider: portal, fax, phone — which actually
  gets an answer. A measured rate, not a judgement.

### 9.3 Two places a model earns its keep

1. **Predicting that a wait will fail.** Given counterparty, document type, how
   many times the plan has asked, elapsed time and history: the probability this
   never comes back. Then the chase goes to the ones that need it, before the
   deadline, instead of a blanket reminder on everything. A genuine
   classification problem, with an outcome that is observed and therefore
   scoreable, and enough signal once there is history. It is gated the same way
   the forecast is: a backtest against the obvious baseline (chase everything at
   day N) before anything is shown.
2. **Reviewing whether the wait was necessary at all.** Of the items parked on
   records, how many were decidable on what was already in the file? This is a
   sampling review over the item and its documents, the same shape as the
   misclassification sampling in [`cho-service.md`](cho-service.md) §4, and it
   attacks the cause rather than the symptom. Advisory, sampled, and surfaced to
   a supervisor — never an automatic decision.

### 9.4 Pattern-level recommendations, not item-level nagging

A new `WorkRecommendation` kind in
[`workforce-optimization.md`](workforce-optimization.md) §5.4:
**`WaitPatternFinding`** — *"68% of this queue's wait time is RFAIs to twelve
providers; median 23 days against a 30-day clock"*. The fix is a
provider-relations conversation, a contract term, or a better request template —
not a queue adjustment. Which is exactly why it is a finding with evidence and
no `change` to apply, like `StaffingGap`.

### 9.5 Where it must not go

- **Deciding to give up on a wait** and adjudicate on what is in hand is a money
  and member decision. Human, always, and the item comes back to a person for it.
- **Chasing the outside world automatically.** An outbound request to a provider
  or member is CHO reaching outside itself, and goes through
  [`correspondence.md`](correspondence.md) or an outbound contact with their
  existing approval, template and PHI rules. A chase may be *recommended* and
  *drafted*; a person or a tenant-configured rule sends it. Same reasoning as the
  no-autonomous-reassignment rule in
  [`workforce-optimization.md`](workforce-optimization.md) §6.1.
- **Parking an item.** Only the person working it knows they are waiting.

---

## 10. What this fixes in the forecasting design

[`workforce-optimization.md`](workforce-optimization.md) as written measures
throughput and backlog from items that are `Open`. With no `Waiting` state, a
queue holding 500 items of which 200 are blocked looks like 500 workable items,
and three things are wrong:

1. **Backlog is overstated**, so the deadline projection over-predicts breaches
   and loses credibility.
2. **Measured throughput is understated**, because hours in which the queue was
   "non-empty" included un-workable items — the §5.3 conditioning counts an hour
   as busy when it was effectively idle.
3. **A rebalance is recommended that cannot help.** Moving two people onto a
   queue whose items are waiting on providers does nothing, and is the fastest
   way for a supervisor to stop trusting the recommendations.

So the projection changes: **workable inventory** excludes `Waiting`, and waiting
items re-enter the projection at their expected wake (§9.2's measured
distribution, with its band), not at once and not never. A breach projection for
a queue dominated by waiting work then says the true thing — *this is not a
staffing problem* — which is `WaitPatternFinding` rather than `StaffingGap`.

This is a correction to that document, not an addition, and it is why the two are
sequenced together in §11.

---

## 11. Build order

1. **The state and the honest counts.** `Waiting`, the tenant wait taxonomy,
   park / resume / extend, the audit events, Open / Waiting / Leased everywhere a
   count appears, My Work showing what the user is waiting on, and the lease-
   expiry warning from §1. Nothing waits on anything unbuilt.
2. **Cycle-time decomposition** (§8): the snapshot fields and the first report —
   where the days go, by queue and reason. This is the deliverable with the
   clearest value and it needs no model.
3. **Event wakes** (§4), in the order the sources exist: linked work item,
   contact, document, authorization and appeal decisions, payment. Plus the
   `work.item.wait_ended` and `work.item.wait_overdue` topics.
4. **Expected durations and chase timing** (§9.2), and the abuse controls in §7
   that depend on a measured expectation.
5. **The forecasting correction** (§10) — ideally in step with
   [`workforce-optimization.md`](workforce-optimization.md)'s own phases 1–3, so
   its projection is never shipped in the wrong version.
6. **The claim-side RFAI wake** (§4.1) — implementing
   [`claim-rfai.md`](claim-rfai.md), which depends on step 1 of this list.
7. **Wait-failure prediction and the necessity review** (§9.3), each behind its
   own backtest gate.
8. **`WaitPatternFinding`** (§9.4) in the recommendation engine.

---

## 12. Open questions

| # | Question | Recommendation |
|---|---|---|
| 1 | ~~A claim waiting on provider records has no RFAI to wake it (§4.1). Extend `rfai-service` to claim attachment requests, or wake on the 275 attachment arriving?~~ | **Settled 2026-10-23 by [claim-rfai.md](claim-rfai.md) (#1518)**, in the direction recommended here: a claim subject on the RFAI case, not a bare attachment. That design also names `WorkItemStatus.Waiting` as a prerequisite, and carries its own open decisions (including whether the PWK pend defaults on, which changes adjudication behaviour today) — those are its to answer, not this document's. |
| 2 | Should `Waiting` ever be set by a service rather than a person? | Yes for one case: a source-owned item whose source says it is blocked (an authorization in `RfaiStatus.Open`). That is the source telling the truth about its own state, not a person parking work. Every other case stays person-initiated. |
| 3 | What happens to a waiting item when its source resolves (the claim leaves Pended)? | It closes, as now — `Done` wins over `Waiting`. The wait was about work that no longer needs doing. |
| 4 | Does a wait suspend the **internal** SLA, as distinct from a regulatory clock? | No by default, but worth a tenant setting for internal-only queues, reported separately. The moment it is configurable for regulated work it will be configured wrongly, so that is not offered. |
| 5 | Maximum wait, and what happens at it? | 30 days default per reason; at the limit it returns to the owner with a supervisor-visible flag rather than closing. Closing it would lose the work. |
| 6 | Is the necessity review (§9.3.2) too close to re-adjudicating a claim? | It samples and advises a supervisor, and names no decision. If it ever proposes an outcome it has become AI examination, which is [`claim-ai-examination.md`](claim-ai-examination.md)'s territory and its governance. |
