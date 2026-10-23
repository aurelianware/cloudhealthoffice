# Workforce optimisation: forecasting work, warning about deadlines, and suggesting where people should work

**Status:** design for review, 2026-10-23. Nothing in this document is built.
Open questions are in §12.

**The question this answers.** CHO now holds, in one place, the three things a
health plan's operations manager needs and normally cannot get from one system:
how much work is arriving and why ([`cho-service.md`](cho-service.md) contact
tracking), what is in the queues and when each item is due
([`work-management.md`](work-management.md)), and who is entitled to work what
(the same service's memberships and teams). This document sets out how CHO uses
those inputs to forecast volume, warn a supervisor before deadlines are at risk,
and suggest where people should work — with a person approving every change.

**Builds on:**
- [`work-management.md`](work-management.md) — the one work engine: queues,
  routing criteria and facets, the shared priority scale, `SlaDays`, leases,
  memberships, teams and the audit event stream;
- [`cho-service.md`](cho-service.md) — contacts, reason taxonomy,
  classification, and §6.2's rule that **CHO does not route calls**;
- [`portal-reporting.md`](portal-reporting.md) — the four reporting layers, the
  one-definition-per-metric rule, and the report catalog this work needs;
- [`appeals-grievances-compliance.md`](appeals-grievances-compliance.md) and the
  rule packs — the regulatory clocks that make a deadline a deadline;
- [`claim-ai-examination.md`](claim-ai-examination.md) — CHO's established
  posture for an AI capability: advisory by default, a real kill switch, and a
  structured human path that works when the AI is absent.

---

## 1. Scope, and the line this does not cross

**In scope: CHO's own work inventory.** Every work item in
`work-management-service` — claim pends, enrollment exceptions, person-raised
service follow-ups (`Inquiry`, built 2026-10-23), and appeals and
prior-authorization reviews once those services publish their events — plus
the contact volume that drives it. For these, CHO is the owner of record and of
the deadline, so CHO is the only system that can forecast them honestly.

**Out of scope: contact-centre workforce management.** Agent schedules, shift
bidding, break optimisation, real-time adherence and intraday reforecasting of
*phone* arrival belong to the plan's contact-centre platform, exactly as call
routing does ([`cho-service.md`](cho-service.md) §6.2). Those platforms have
mature products, plans already own one, and rebuilding scheduling would break
the removable, beside-the-core principle. What CHO adds is the payer knowledge
the WFM tool does not have: that 4,000 claims are about to finish a mass
reprocessing run, that an 834 load landed badly, that a fee-schedule correction
went out — all of which drive next week's calls. §9 is the integration.

So the boundary is: **the platform's WFM forecasts calls; CHO forecasts work and
tells the WFM what is coming.**

**Also out of scope, deliberately and permanently: autonomous reassignment.**
See §6. Moving a regulated-deadline item from one person to another changes who
is accountable for a clock and who has touched a member's record. A supervisor
approves every change this capability proposes. There is no mode in this design
that applies a change without a person; that is a decision, not a phase-1
limitation.

---

## 2. Why CHO can do this and the incumbents mostly cannot

The capability is not novel in isolation — it is novel *in one place*.

| Who | What they have | What they are missing |
|---|---|---|
| **Facets Workflow** (Cognizant) | Rule-based routing by data element, skill and line of business; a real-time productivity dashboard; supervisor views of inventory and workload balancing | Vendor material describes monitoring and manual balancing, not forecasting arrival or projecting deadline breach. Call tracking lives in a different product. |
| **QNXT Workflow** (Cognizant) | Real-time routing to user queues; a dashboard with "projected workloads by user or role"; alerts when claims approach interest and penalty status | The closest incumbent to deadline-risk alerting, and it is alerting on a threshold, not a forecast. QNXT's own call tracking is being retired ([`cho-service.md`](cho-service.md) §1). |
| **HealthEdge Workflow Plus** | A live view of team workload, automated task assignment, SLA monitoring, real-time analytics, pre-integrated with HealthRules Payer | A separate product from the core, and described in terms of distribution and monitoring rather than forecast or staffing recommendation. |
| **NiCE WFM / Verint** | Genuine forecasting, multi-skill simulation that mirrors ACD routing, scheduling, shift bidding, intraday reforecast and real-time adherence; a published health-plan case (CareSource) reporting a 4% adherence gain ≈ 13 FTE | Knows contacts, not claims. It cannot see that an appeal is 48 hours from a 42 CFR 422 deadline, because the deadline lives in the core admin system. Its back-office support is generic work-item inventory. |
| **General CRMs** (Salesforce, Dynamics, Pega) | Workforce and queue tooling as configuration | Each has to be taught what a claim, an authorization or a grievance is first — the same objection [`cho-service.md`](cho-service.md) §1 makes about call tracking. |

**CHO's position:** the forecast, the deadline and the entitlement are in the
same database. A recommendation can say *"move two examiners from Pricing to
Medical Review for the rest of today; 31 medical-review items breach prompt-pay
on Friday and Pricing has 11 days of float"* because one service knows the
inventory, both SLAs, the measured throughput of both queues, and which of those
examiners the role and memberships actually permit to work medical review.
Nothing in the table above can assemble that sentence from one system.

**For the investor story,** this is the difference between CHO selling a work
engine and CHO selling operations management. It is also cheap: §3 shows most of
the inputs already exist, and §11 phase 0 is a background job writing counts.

---

## 3. What exists today (the inputs, named)

Everything here is in the repository now, and that is the point: this capability
is mostly a read model over data CHO already produces.

### 3.1 From `work-management-service`

| Input | Where | Use |
|---|---|---|
| Open / leased inventory per queue | `GET /api/v1/work/queues` returns open and leased counts | Current backlog, the starting state of every projection |
| Due date per item | `WorkItem.DueAt`, from `attributes.Deadline ?? (ReceivedDate ?? CreatedAt) + queue.SlaDays` | Deadline-risk projection (§5.2) |
| Priority score and its reason | The one scale in [`work-management.md`](work-management.md); `PriorityReason` carries the terms and their inputs | Explaining *why* an item is at risk without re-deriving it |
| Arrivals and completions | `WorkAuditEvent`: `Created`, `Reopened`, `Completed`, `Cancelled` | Arrival series (§4) and measured throughput (§5.3) |
| Handling time | `Leased` → `Completed`, and `Released` / `LeaseReclaimed` in between | Per-queue service time distribution; also the honest measure of rework |
| Who may work what | Memberships and teams, rank, `IsOverflow`, `AuthorityLimit`, and `GET /admin/users/{userId}/queues` | A rebalancing suggestion that is actually legal (§6) |
| Entitlement | `ChoRolePermissions` checked on the puller's own token at distribution | The hard limit a suggestion can never widen (§6.4) |
| Routing attributes and facets | `WorkQueueCriteria` + `attributes.facets`, one matcher for every work type | Forecasting a *segment* — e.g. expedited appeals, one LOB, one pend code — not just a queue |
| A 15-minute background host | `WorkMaintenanceService`, which already re-scores open items every `RescoreIntervalMinutes` | Where the snapshot writer goes (§4.1); no new host process |

### 3.2 From `contact-service`

`Contact` already carries `StartedAt`, `EndedAt`, `DurationSeconds`, `ClosedAt`,
`Channel`, `Direction`, `Queue` (the phone platform's queue name), `AgentId`,
`Reasons[]` from the tenant taxonomy, and `Subjects[]` (the claims and
authorizations discussed). From those, contact-service can produce an hourly
series of contacts by channel, reason and platform queue, with talk time and
after-call work, which is everything the volume forecast needs — and no contact
content at all (§8).

Classification, and therefore grievance and appeal arrival from calls, needs
step 2 of [`cho-service.md`](cho-service.md) §8; the clocks it will run already
exist, as the Medicare Advantage pack and the per-line-of-business federal floor.

### 3.3 From the rule packs and deadline policy

`RulePackFloor` and `AppealResponseDeadlinePolicy` (appeals-service) hold the
regulatory clocks per line of business — MA 30-day grievances, 72-hour and
24-hour expedited clocks, 14-day extensions. A deadline-risk projection on
appeal and grievance work must read the same policy the service enforces, not a
copy of it, for the reason the floor itself gives: two restatements of one clock
drift.

### 3.4 What does not exist

1. **No history.** Work-management stores the *current* state of each item plus
   its audit trail. There is no time series, so nothing can plot last Tuesday's
   10 a.m. backlog without replaying every audit event. §4.1 fixes this and is
   phase 0.
2. **No reporting service.** [`portal-reporting.md`](portal-reporting.md) is a
   design; the report catalog and runner are its phase 1 and are unbuilt. The
   supervisor reports this capability wants (pended inventory, throughput) are
   named there and marked as needing exactly the data §4.1 produces. The two
   should be built together, and the snapshot series should be built once, in
   work-management, and read by both.
3. **Barely any contact data.** Call tracking step 1 shipped 2026-10-22. A
   forecast trained on days of data is theatre. §4.4 says what to do about a
   cold start instead of pretending.
4. **No throughput-per-person measure the portal shows.** The events are there;
   nothing aggregates them.
5. **No way to tell blocked work from workable work.** `WorkItemStatus` has no
   waiting state, so a queue of 500 items with 200 blocked on provider records
   looks like 500 workable ones. This makes the backlog, the measured throughput
   and therefore the breach projection wrong, in the directions set out in
   [work-waiting-and-cycle-time.md](work-waiting-and-cycle-time.md) §10 — which
   also carries the correction, and the cycle-time decomposition that matters
   more than the forecast does, since waiting is most of cycle time and touch
   time is the small term. That design should be sequenced with phases 1–3
   here.

---

## 4. The data layer: snapshots, and why they come first

### 4.1 `WorkQueueSnapshot` (work-management-service)

One document per tenant, per queue, per hour, written by `WorkMaintenanceService`
on the interval it already runs:

```
WorkQueueSnapshot
  tenantId, queueId, queueName, workType, periodStart (hour, UTC), capturedAt
  open, leased, openAndOverdue, dueWithin24h, dueWithin72h
  arrivals, completions, reopened, cancelled, reroutedIn, reroutedOut
  openAtPeriodStart                              (so §5.3 can tell a quiet
                                                  hour from an idle one)
  oldestAgeHours, medianAgeHours, p90AgeHours
  medianScore, p90Score
  leaseStarts, leaseCompletions, leasesReclaimed, releases
  medianHandleMinutes, p90HandleMinutes          (Leased → Completed)
  distinctWorkers                                (count, not identities)
  facetCounts { facetName: { value: count } }     (bounded; §4.3)
```

Properties that matter:

- **Counts only.** No item ids, no member ids, no claim numbers, no user ids. A
  snapshot is not PHI and is not person-level performance data (§8).
- **Idempotent.** Keyed `{tenant}.{queueId}.{hour}`; re-running the writer for
  an hour replaces that hour. A missed interval is repaired by a backfill that
  replays `WorkAuditEvent` for the window, which is also how a tenant gets its
  first few weeks of history on the day phase 0 ships.
- **Derived from one definition.** `openAndOverdue` means `DueAt <= periodEnd`,
  the same comparison the priority formula makes. Per
  [`portal-reporting.md`](portal-reporting.md) rule 2, the definition lives
  server-side once and Insights, reports, alerts and the forecast all read it.
- **Retention.** 24 months rolling, hourly; daily rollups kept indefinitely.
  Two years is what a seasonal model needs to see the same AEP twice.

### 4.2 `ContactVolumeSnapshot` (contact-service)

The same shape on the contact side: per tenant, per hour, by channel and by
reason code — contacts started, contacts closed, median and p90 talk time,
median after-call work (`EndedAt` → `ClosedAt`), transfers, callbacks scheduled,
follow-ups created, and once step 2 lands, classifications by type and expedited
flag. Exposed as `GET /api/v1/contacts/admin/volume-snapshots` with the same
permission posture as the reason taxonomy admin, and read by work-management's
forecaster over the existing service-to-service client pattern.

Contact-service keeps owning its own data; work-management does not read the
contact store.

### 4.3 Bounding the facet dimension

`facetCounts` is the useful part and the dangerous one: facets are open-ended
(any `a-z0-9_` name), and a snapshot that cross-products every
facet value explodes. Rule: a tenant nominates at most **8 forecast facets** per
work type in configuration, each with at most **40 values** retained plus an
`__other` bucket; anything else is not snapshotted. A forecast is only as fine
as the segments someone chose to measure, which is honest and bounded.

### 4.4 Cold start

A new tenant has no history, and a forecast without history should not be shown.

1. **Phases 0–1 work from day one** — they describe the present and measure
   throughput over whatever window exists, which is already more than
   supervisors have now.
2. **Forecasting is gated on data.** A queue's forecast is published only once
   it has ≥ 8 complete weeks of snapshots covering ≥ 90% of hours, and a
   backtest (§7) within the published error band. Below that the UI says
   "not enough history yet", with the date it expects to have enough. No
   fabricated bands.
3. **An importer, not a guess.** A plan migrating to CHO has years of volume in
   its ACD, its WFM tool and its old core system. A CSV importer that loads
   historical hourly arrivals and completions per queue (mapped to CHO queues by
   the tenant) seeds the series directly. This is the same posture as the
   grouper work: an importer is not a formula, and it is the cheap way to make a
   new tenant's first forecast credible.

---

## 5. What the capability produces

### 5.1 Volume forecast

**Per queue (and per nominated facet segment), an hourly forecast for the next
14 days and a daily forecast for the next 90, each with an 80% band.**

The model is deliberately boring and testable:

```
forecast(h) = level × dayOfWeek[d] × hourOfDay[h] × calendar[d] + trend
band        = empirical quantiles of the last 13 weeks' residuals, by horizon
```

- **Multiplicative day-of-week and hour-of-day profiles** estimated over a
  trailing window, which is what back-office arrival actually looks like
  (Monday claim loads, Tuesday mail, month-end payment runs).
- **A calendar of known drivers**, which is the part only CHO can supply: 834
  load dates, payment run dates, EOB and letter batches
  ([`correspondence.md`](correspondence.md)), plan-year start, ANOC/AEP, a
  scheduled mass adjustment project
  ([`portal-reporting.md`](portal-reporting.md) §4.2), a fee-schedule
  publication. Each is a dated multiplier on the affected queues, fitted from
  past occurrences where there are any and flagged as unfitted where there are
  not.
- **Residual quantiles, not a Gaussian assumption.** Operational arrival is
  skewed; a symmetric band understates the bad day, which is the only day
  anybody cares about.

**No LLM produces a number.** A seasonal-profile model is a few hundred lines of
C#, pinned by unit tests on fixed series, auditable by a supervisor who asks why,
and backtestable (§7). A learned model may replace it per queue later, behind
the same interface and the same backtest gate — but a capability that tells a
regulated plan how to staff a deadline should start with arithmetic somebody can
check. That is the same reasoning
[`claim-ai-examination.md`](claim-ai-examination.md) gives for shipping two real
modes instead of three.

### 5.2 Deadline-risk projection

For each queue and each horizon, the question is not "is anything overdue" (the
score already answers that) but **"how many items will breach if nothing
changes".**

```
inventory due by T          from current items' DueAt
+ forecast arrivals due by T (arrival × the share historically due within T)
− projected completions by T (§5.3's measured throughput over the hours remaining)
= projected breaches by T, with a band from the arrival band and the
  throughput band
```

Risk levels per queue: **At risk** (projected breaches > 0 at the 50th
percentile), **Watch** (> 0 at the 80th), **Clear**. A projection names the
drivers that produced it — the arrival term, the capacity term and the
inventory term, each as a number — because a supervisor's first question is
which one moved.

**Regulated clocks are not SLA days.** Where an item's deadline comes from a
rule pack (MA 30-day grievance, 72-hour expedited appeal, a Medicaid timeframe),
the projection reads `AppealResponseDeadlinePolicy` rather than `queue.SlaDays`,
and those queues are reported separately from internal-SLA queues. A breach of
an internal SLA is a service problem; a breach of 42 CFR 422 is a citation, and
the two must never be summed into one number.

### 5.3 Capacity: measured throughput, not available hours

**CHO does not model who is working, and should not try.** The projection needs
expected throughput, and throughput is directly measurable:

```
baseline(queue, weekday, hour)
  = p50 of completions in that queue in that weekday-hour slot,
    over a trailing window, counting only hours in which the queue
    had work to do
exceptions
  = dated, supervisor-entered deviations (headcount delta or percentage)
holidays
  = the tenant holiday calendar (shared with the arrival side)

projected completions = baseline × (1 + exceptions) × holidays
band                  = measured p10 / p90 of the same slots
```

An earlier draft of this document decomposed capacity as *available hours ×
share of time in this queue × items per hour*, with available hours taken from a
WFM adapter or a shift pattern typed into CHO. That was the wrong abstraction. It
imported a workforce-management model into a problem that does not need one, and
every term in it is something a person has to maintain and will stop
maintaining. Measured throughput already has the staffing level, the shift
patterns, the PTO rate and the real productivity inside it, and it self-corrects:
a team that shrinks produces less, and the projection follows without anyone
telling it anything.

**What measurement cannot see is a known future change,** and over a one-to-two
week horizon that is precisely what matters — three people on leave next week, a
new hire starting, a training day. Hence `exceptions`: dated entries a supervisor
makes *only* for known deviations, a handful a month rather than a roster. This
is deliberately the same shape as the driver calendar on the arrival side
(§5.1), so both terms of the projection are a measured baseline with known,
dated adjustments on top, and a supervisor learns one idea instead of two.

**The holiday calendar is the one thing worth holding as real configuration,**
because the arrival forecast needs it anyway — claims do not arrive on Christmas
either — so one calendar per tenant serves both terms.

**A WFM schedule adapter keeps its place, demoted.** Where a plan runs NiCE,
Verint, Genesys or Amazon Connect and will share its schedule, an adapter
replaces the hand-entered exceptions with real data. It is a refinement, not the
source of truth, so nothing here waits on a procurement conversation or on one
integration per platform.

**Two limitations, stated rather than hidden.**

1. **Measured throughput conflates capacity with demand.** A queue that has been
   starved of work shows low completions because there was nothing to do, not
   because nobody was there. Hence the conditioning above: only hours in which
   the queue was non-empty count toward the baseline. A queue that has never been
   busy has no credible capacity estimate, and the projection says so instead of
   guessing.
2. **Per-queue throughput double-counts shared workers.** Someone who works two
   queues contributes to the measured baseline of both, so projecting both at
   full baseline simultaneously assumes they are in two places. A single queue's
   projection is sound; a plan-wide total is an **upper bound** and is labelled
   one. This is also the argument for §5.4: reallocating shared people is exactly
   what a rebalancing recommendation decides, and it is a decision, not something
   a forecast can assume away.

**Erlang C is the wrong model here and will not be used.** It assumes callers
who wait or abandon in real time; back-office work neither abandons nor is
served immediately, and with offered load at or above capacity the Erlang
steady-state does not exist while the real backlog simply grows. Deferred work
is a workload-and-inventory model: arrivals, inventory, throughput, due dates.
(Where CHO ever needs a queueing model for *phone* — it does not, per §1 — that
is the platform's WFM's job, and simulation rather than Erlang C is the current
recommendation for multi-skill blended operations anyway.)

### 5.4 Rebalancing and staffing suggestions

A **`WorkRecommendation`** is a proposal, with evidence, that a person accepts
or rejects:

```
WorkRecommendation
  id, tenantId, createdAt, expiresAt, horizon
  kind      Rebalance | Overflow | AuthorityRaise | QueueOrder | StaffingGap
            | PrioritySkew | SplitQueue
  fromQueues[], toQueues[], workers[]        (user or team ids)
  change    the exact admin-API mutation it proposes, as data
  before    the current state of everything `change` would touch (for revert)
  evidence  { projectedBreachesBefore, projectedBreachesAfter, floatGivenUp,
              arrivalTerm, capacityTerm, inventoryTerm, assumptions[] }
  confidence, basedOnForecastIds[]
  status    Proposed | Accepted | Rejected | Expired | Reverted | Superseded
  actedBy, actedAt, rejectReason, revertedAt
  narrative (optional; §6.3)
```

The kinds, and what each actually changes:

| Kind | Proposes | Mechanism |
|---|---|---|
| **Rebalance** | Named workers' primary queue membership moves, for a stated window | `admin/memberships` CRUD, time-boxed |
| **Overflow** | An existing overflow membership is promoted to primary, or an overflow link is added between two queues | `IsOverflow`, `Rank` |
| **AuthorityRaise** | A member's `AuthorityLimit` is raised so high-dollar items stop stalling | `AuthorityLimit` |
| **QueueOrder** | A queue's `Order` or criteria change because items are landing in the wrong place (measured by reroute counts and by items worked then reopened) | `admin/queues`, then `admin/recompute` |
| **StaffingGap** | No legal rearrangement closes the gap: the plan needs N more FTE-hours on work type X by date D, or must invoke an extension / overtime | Nothing in CHO. This is a finding, and it is the most valuable one |
| **PrioritySkew** | A queue's priority weights are producing the wrong order (e.g. amount weight starving deadline work) | `admin/queues` weights |
| **SplitQueue** | One queue mixes two populations with different handling times or clocks, and routing should split them | `admin/queues` + criteria/facets |

Two properties are non-negotiable. **Every recommendation is expressed as a
change to configuration that already exists**, so accepting it is the supervisor
making a normal, audited admin change — not a parallel mechanism. And
**`before` makes it revertible** in one action, which is what makes a supervisor
willing to try one at all.

### 5.5 Anomaly detection

Cheap, and the thing supervisors will notice first: an hour whose arrival sits
outside the forecast band for a queue, a handle time that jumps, a queue whose
reroute-out count spikes (routing is wrong), a completion rate that falls while
`distinctWorkers` holds (something is blocking people), a `leasesReclaimed`
spike (people are picking up work and abandoning it). Each is a threshold on
snapshot series, each is explainable, and each is a candidate for the narrative
in §6.3.

---

## 6. Human in the loop, and where Claude is actually used

### 6.1 Modes

`WorkforceOptimization:Mode`, mirroring `AiEnforcementMode`:

| Mode | Behaviour |
|---|---|
| `Off` | Snapshots still written (they are the reporting layer's data too); no forecasts published, no recommendations generated. The kill switch, and like `AiEnforcementMode.Disabled` it runs far enough to record that it was used. |
| `Observe` | Forecasts, risk projections and anomalies are computed and shown. No recommendations are generated. |
| `Advise` (default once past phase 3) | Recommendations are generated and shown to supervisors holding `workqueue:admin`, and apply only when a supervisor accepts. |

**There is no mode that applies a change on its own.** Reasons, stated so a
future reader does not add one casually: a reassignment moves an item with a
regulatory clock to a different accountable person; it changes who has accessed
a member's record, which the access log has to be able to explain; `Get Next`
already distributes work fairly within a queue, so the autonomous win is small;
and a wrong automatic rebalance under load is exactly the failure a plan cannot
absorb. The supervisor is also the only party who knows the things CHO does not
— who is new, who is on a call, who is about to go on leave.

### 6.2 Acceptance is a normal admin action

Accepting a recommendation calls the existing `admin/memberships`,
`admin/queues` or `admin/recompute` endpoints **as the accepting supervisor's
own token**, so the change is authorised, audited and attributed exactly as a
hand-made change is — the same reasoning
[`cho-service.md`](cho-service.md) §9 Q3 gives for writing a claim note with the
rep's own token. A recommendation cannot widen entitlement: it can only move
someone within what `ChoRolePermissions` already allows them, and a proposal
that would need a permission the worker lacks is never generated (it becomes a
`StaffingGap` finding instead). Rejections are kept with their reason, which is
both the audit trail and the evaluation signal (§7).

### 6.3 Where Claude is used, and where it is not

| Claude does | Claude does not |
|---|---|
| Write the supervisor's daily brief in plain language from the computed figures | Produce any number, band or risk level |
| Explain an anomaly by reading the *aggregate* drivers (which reason codes, pend codes, facets and providers moved) and naming the likely cause in a sentence | Read contact transcripts, claim content or member records for this purpose |
| Turn a recommendation's `evidence` into the sentence a supervisor reads, including what it gives up | Decide whether to apply a recommendation, or choose who moves |
| Draft the narrative for a monthly operations review from the series | Change any configuration, directly or indirectly |

Every number in anything Claude writes is passed to it, not computed by it, and
the rendered brief links to the figure it came from so a supervisor can check it.
If the model is unavailable the brief degrades to the figures and tables — the
same "the absence of a recommendation never blocks" posture as
[`claim-ai-examination.md`](claim-ai-examination.md).

### 6.4 Who intervenes when the supervisor cannot

A supervisor can rearrange their own queues. They cannot conjure hours, and they
cannot move people across an organisational boundary. At health plans the
accountability for meeting a compliance deadline sits a level or two up — a
director or VP of claims, with UM and appeals usually reported under claims — and
that person is the one who can authorise overtime, borrow people from another
department, or accept that an extension will be invoked. So a `StaffingGap`
finding that stops at the supervisor stops at the person who already knew and
could not act.

**Escalation follows scope, not a reporting line.** CHO should not hold an org
chart: a reporting hierarchy goes stale the week someone changes jobs, nobody
maintains it, and CHO is not an HR system. What CHO can keep accurate is
**scope of responsibility** — the set of queues and work types a person
administers. A supervisor's scope is their queues; a director's scope spans
several supervisors' scopes. Escalation then walks *outward by scope* rather than
*upward by person*, which gives the same answer in practice and stays correct on
its own.

This produces a tier per recommendation, decided by what the fix would touch:

| The fix touches | Who can accept it |
|---|---|
| One queue (rank, overflow, authority limit, weights) | The supervisor whose scope contains it |
| Two queues in one scope | The supervisor, if their scope contains both |
| Two queues in different scopes (claims people onto appeals work) | The director whose scope contains both. **Nothing in CHO can do this today**, and it is the most valuable of the lot |
| Hours, headcount or overtime | Nobody in CHO. The director, outside it; CHO records the decision and the date against the finding |
| Invoking a regulatory extension | The director or A&G manager, in appeals-service where the rule pack governs it. The finding deep-links there; this capability must not reimplement an extension |

**Two gaps this exposes, both outside this capability.**

1. **There is no director tier in `ChoRolePermissions`.** The roles stop at
   `ClaimsSupervisor`. The precedent for adding one is `Finance` →
   `FinanceApprover` and `ConfigurationAnalyst` → `ConfigurationManager`: a
   separate, narrow role holding the wider-scope and intervention permissions,
   **not** a superset of supervisor. `OperationsDirector` with
   `workqueue:admin` at a wider scope, `reports:*` for its areas, and sight of
   `StaffingGap` is the shape.
2. **There is no scope at all today.** `ClaimsSupervisor` holds tenant-wide
   `workqueue:admin`, under a code comment saying supervisors manage "their
   teams' queue memberships" — an intent the permission does not enforce, so
   every supervisor can currently administer every queue. Introducing a director
   tier is the natural moment to introduce scope, but doing so *narrows* existing
   behaviour for supervisors, so it is its own decision with its own migration
   rather than a detail of this one.

**Compliance sees, the director owns.** `ComplianceOfficer` already reads
everything through `*:read`, so a regulated-clock `StaffingGap` is visible to
compliance without being addressed to them. Addressing it to compliance instead
would be wrong twice: it is not their decision to make, and a supervisor whose
every gap routes to compliance stops raising them honestly, which costs more
than the visibility gains.

---

## 7. Proving it works before anyone relies on it

A forecast nobody scores is a decoration, and an investor will ask.

- **Every forecast is stored** with its as-of time, inputs, model version and
  horizon, and scored against actuals when the hour arrives: MAPE and the
  band's realised coverage, per queue per horizon. Published per queue in the
  portal — a supervisor should be able to see that the 3-day forecast for their
  queue has been within 12% and that the 80% band has covered 78% of hours.
- **Backtest gate.** A queue's forecast is published only after a rolling-origin
  backtest over its history beats seasonal-naive on that queue. If the boring
  baseline wins, the boring baseline ships.
- **Recommendations are scored too.** For each accepted recommendation, the
  realised breach count against the projection; for rejections, the reason
  (categorised: "wrong person", "wrong queue", "already handled", "not possible
  today"). Rejection reasons are the cheapest honest measure of whether this is
  useful, and a `kind` whose rejection rate stays high gets switched off rather
  than tuned forever.
- **Shadow first.** Phases 2–4 each run in `Observe` for a tenant before
  anything is shown as actionable.

---

## 8. PHI and governance

1. **Aggregates, not records.** Forecasting and recommendation run on
   snapshots: counts, durations and distributions. No member id, claim id,
   contact id, transcript or clinical detail is an input. This is the strongest
   privacy property of the design and it should not be traded away for a
   marginal accuracy gain.
2. **Workers are identified only where the mechanism requires it.** Snapshots
   carry `distinctWorkers` as a count, decided and not merely deferred (§12 Q3):
   the audit stream already holds the per-person events, so declining to
   pre-aggregate them loses no answer. Memberships and per-worker throughput are
   read live, at the moment a rebalancing recommendation is computed, and the
   recommendation names the workers it proposes to move because a supervisor
   cannot act on an anonymous one. Per-worker series are **not** snapshotted, so
   this does not quietly become a two-year productivity record on every
   employee. Any per-person reporting is the throughput report in
   [`portal-reporting.md`](portal-reporting.md) §4.1, under its own permission,
   with that document's rules — not a side effect of this capability.
3. **What reaches the model.** Only the computed aggregates and the
   recommendation's own evidence. Named workers are passed as ids, not names, to
   the extent the narrative needs them at all; the portal resolves display names
   locally.
4. **Minimum necessary on the brief.** The daily brief is operational: counts,
   queues, dates, risk. It names no member and no claim.
5. **Audited.** Generating a forecast is logged with its parameters; viewing a
   brief is a read; accepting or rejecting a recommendation writes a
   `WorkAuditEvent` and carries the supervisor's id, which is how a reverted
   change is reconstructed later.
6. **Scope.** Tenant always applies, and a supervisor sees only the queues they
   administer.

---

## 9. Outward: giving the contact-centre platform what it lacks

Per §1, CHO does not schedule agents. It does hold the drivers, and this is the
integration that makes the boundary pay:

| CHO publishes | The platform's WFM does |
|---|---|
| **Expected contact drivers**, next 1–14 days: "a payment run lands Thursday, expect +18% provider payment-status calls Friday"; "4,100 denial letters mail Monday"; "an 834 load created 900 enrollment exceptions" — each a dated, quantified, queue-tagged driver | Feeds them into its own forecast as external drivers or planned events, which every mature WFM supports |
| **Back-office inventory and deadline risk** per work type | Plans blended agents' back-office time against real deadlines rather than a guess |
| **Routing context** already designed in [`cho-service.md`](cho-service.md) §6.2 | Routes the individual call |
| Optionally, **CHO work items into platform task routing** | One agent desktop — this is [`cho-service.md`](cho-service.md) §9 open question 5, and CHO stays owner of record and deadlines either way |

Drivers are attributes and counts, never records — the same rule §6.2 sets for
routing context.

---

## 10. Surfaces

- **`/work-queues` gains a health strip** (`workqueue:read`): per queue, risk
  level, projected breaches at 24h / 72h / 7d, and the three drivers. The
  existing cards and lists are unchanged.
- **Operations › Workforce** (`workqueue:admin`): the forecast chart per queue
  with its band and its measured accuracy, the deadline-risk table, the
  anomaly list, and the recommendation inbox (accept, reject with reason,
  revert). The supervisor's team-by-queue eligibility view being designed in the
  call-tracking work is the natural neighbour, and a rebalancing recommendation
  should link straight into it.
- **Home tile** for a supervisor: queues at risk, and the day's brief.
- **Delivered, not just displayed.** A supervisor who is not looking at the
  portal when a queue starts to slip has not been warned. Deadline risk,
  anomalies and proposed recommendations are published as topics
  (`work.queue.deadline_risk`, `work.queue.anomaly`,
  `work.recommendation.proposed`) through the notification capability designed in
  [operational-notifications.md](operational-notifications.md), which is where
  subscription, acknowledgement, escalation to an operations or IT backstop, and
  the rules that keep an alert from becoming noise live. Two consequences for
  this design: the risk-level thresholds in §5.2 are also the alert thresholds,
  under the one-definition rule, and every figure in §5 must render PHI-free,
  which it does — counts, queue names and dates. That capability does not exist
  either; phases 1 and 3 here depend on its phases 1 and 3.
- **Insights, not a new reporting mechanism.** These figures are
  [`portal-reporting.md`](portal-reporting.md) layer 1 for the work module, and
  the pended-inventory and throughput reports in its §4.1 are layer 2 over the
  same snapshots. One definition per metric: the risk level on the strip, in the
  brief and in the report is one server-side definition.

---

## 11. Build order

Each phase is useful on its own, and each is a reviewable PR or two.

0. **Snapshots.** `WorkQueueSnapshot` in `WorkMaintenanceService` with a
   backfill from `WorkAuditEvent`; `ContactVolumeSnapshot` in contact-service
   with its admin endpoint; facet nomination in configuration; retention. No UI.
   *This is also the data the reporting layer's supervisor reports need, so it
   should be built once, here, rather than twice.*
1. **The present, measured.** Live queue health: inventory, overdue, measured
   throughput and handle time (the whole capacity side of §5.3, which needs no
   new input beyond the snapshots), the tenant holiday calendar, items due in
   24/72h, the health strip on
   `/work-queues`, and the pended-inventory and throughput reports. No forecast,
   no AI. Already more than supervisors have today.
2. **Forecast.** The seasonal model, the driver calendar, stored forecasts,
   accuracy scoring and the backtest gate; the historical importer for cold
   start. Published per queue only when it passes (§4.4, §7).
3. **Deadline risk.** The projection, regulated clocks read from
   `AppealResponseDeadlinePolicy`, risk levels, thresholds and alerts, the Home
   tile. Runs in `Observe` first.
4. **Recommendations with approval.** `WorkRecommendation`, the generators for
   `Rebalance`, `Overflow` and `StaffingGap` first (the three with the clearest
   evidence), acceptance through the existing admin APIs as the supervisor,
   revert, and rejection-reason capture. `QueueOrder`, `PrioritySkew`,
   `AuthorityRaise` and `SplitQueue` after, each gated on its own rejection rate.
5. **Claude narration.** The daily brief, anomaly explanation and recommendation
   rationale, over the figures from 1–4.
6. **Outward.** Drivers to the plan's WFM, and the optional schedule adapter
   that replaces §5.3's hand-entered capacity exceptions with real schedule
   data. Optionally CHO work items into platform task
   routing.

**Dependencies to be honest about:** phase 2 cannot be credible for contact
volume until there are weeks of contacts, which means call-tracking steps 2–4
and real usage, or an import. Phase 3's regulated clocks need appeals as a work
source (step 3 of [`cho-service.md`](cho-service.md) §8). Phase 1's reports want
the report catalog and runner from
[`portal-reporting.md`](portal-reporting.md) §8 step 1 — the two efforts should
be sequenced together rather than each building half a reporting layer.

---

## 12. Open questions

| # | Question | Recommendation |
|---|---|---|
| 1 | Does this live in `work-management-service` or a new service? | **In work-management-service.** It needs queue configuration, memberships, items, due dates and the audit stream, and a separate service would have to copy or call for all of it. Contact volume comes in through one read endpoint. Revisit only if the forecaster's compute starts to interfere with `Get Next`. |
| 2 | ~~Where does **available hours** come from (§5.3)?~~ | **Answered 2026-10-23 (Mark asked what was best).** Nowhere: the question was wrong. §5.3 now measures throughput per queue per weekday-hour from the snapshots, adds a tenant holiday calendar the arrival side needs anyway, and takes sparse dated exceptions from a supervisor for known future changes. No schedule, no shift table, no adapter on the critical path. |
| 3 | ~~Should per-worker throughput be snapshotted so a supervisor can see trends?~~ | **Answered 2026-10-23 (Mark): no, leave it out.** And it costs nothing to leave out, which is why it is an easy no: `WorkAuditEvent` already records every lease and completion with who did it, and is retained, so the throughput report in [portal-reporting.md](portal-reporting.md) §4.1 computes per-person figures **live** from those events when somebody runs it. Nothing becomes unanswerable; what is avoided is a standing per-person productivity record accruing as a by-product of forecasting. The forecast does not want it either, since §5.3 measures capacity per queue rather than per person. The counter-argument on the record: some buyers will ask for agent scorecards and call it the feature. If CHO ever sells that, it should be built deliberately — retention, access rules, a correction path — not inherited from here. |
| 4 | Do we show forecasts to supervisors before the backtest gate passes? | No. Show "not enough history" with the date. A forecast that embarrasses itself once will not be trusted again. |
| 5 | ~~Should `StaffingGap` findings be visible to anyone above the supervisor?~~ | **Answered 2026-10-23 (Mark).** The supervisor sees it and it rolls up to the director or VP who owns meeting compliance, who also needs to be able to intervene. Designed in §6.4, which turns "rolls up" into scope rather than a reporting line, and names the two gaps it exposes (no director role, no scope on `workqueue:admin`). |
| 8 | Does scope on `workqueue:admin` land with the director tier, or on its own? | On its own, before it. It narrows what every existing supervisor can do, so it needs a migration and a conversation with any plan already running CHO — it should not arrive as a side effect of a forecasting capability. |
| 6 | Is this packaged with CHO or sold as a module? | Same open question as [`cho-service.md`](cho-service.md) §9 Q4 and probably has the same answer; the capability is thin without CHO's own work engine, which argues for included. |
| 7 | Do we ever want a learned model per queue? | Behind the same interface and the same backtest gate, once there are enough tenants and enough history that it can beat the seasonal baseline. Not before. |

---

## Sources

Incumbent capability (vendor material unless noted; none independently verified):

- [Cognizant: Facets Workflow datasheet](https://www.cognizant.com/en_us/trizetto/documents/facets-workflow-datasheet.pdf)
- [Cognizant: QNXT Workflow](https://www.cognizant.com/us/en/trizetto/core-administration/qnxt/workflow)
- [Cognizant: QNXT claims workflow](https://www.cognizant.com/en_us/trizetto/documents/cognizant-triZetto-QNXT-claims-workflow.pdf)
- [Cognizant: Cox HealthPlans claims workflow automation (case study)](https://www.cognizant.com/case-studies/cox-healthplans-claims-workflow-automation)
- [HealthEdge: Workflow Plus](https://healthedge.com/resources/brochures/transform-your-operations-with-workflow-plus)
- [NiCE Workforce Management](https://www.nice.com/products/workforce-management-2)
- [NiCE CXone Workforce Management datasheet](https://resources.nice.com/wp-content/uploads/2024/12/CXone-Workforce-Management-WFM-Datasheet-V3.pdf)
- [NiCE: CareSource case study](https://resources.nice.com/wp-content/uploads/2024/03/wcx-caresource-cs.pdf)

Model choice:

- [Call Centre Helper: an introduction to Erlang calculations](https://callcentrehelper.com/an-introduction-to-erlang-calculations-79806.htm)
- [Understanding Erlang C, Erlang A and simulation for workforce management](https://soon.works/blog/understanding-erlang-c-erlang-a-and-simulation-for-workforce-management)
- [Assembled: how to use Erlang C to plan staffing](https://www.assembled.com/university/how-do-i-use-erlang-c-to-plan-staffing)
