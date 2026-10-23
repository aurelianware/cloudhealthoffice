# Operational notifications: topics, subscriptions, acknowledgement and escalation

**Status:** design for review, 2026-10-23. Nothing in this document is built.
Open questions are in §10.

**Why this exists.** CHO has no way to tell a person inside the plan that
something needs them. Several designs already assume one and each would
otherwise invent its own: a background report run finishing
([`portal-reporting.md`](portal-reporting.md) §7.2 "the user is notified (Home
and email link)"), a queue projected to breach a deadline
([`workforce-optimization.md`](workforce-optimization.md) §5.2), a letter that
failed electronic delivery ([`correspondence.md`](correspondence.md) §13), a
payment run awaiting approval, an 834 load that rejected half its rows, a secret
near expiry ([`secret-rotation.md`](secret-rotation.md)). Building one
notification capability is cheaper than building six, and it is the only way the
plan gets a single answer to "who was told, and did anyone act".

**The question that prompted it:** should a notification be a topic people
subscribe to, and should an IT group be able to subscribe as the business's
backstop? Short answer: yes to topics, and the IT backstop is a real need — but
it is served better by **acknowledgement and escalation** than by giving IT a
copy of everything (§5).

---

## 1. What this is, and what it is not

| | |
|---|---|
| **This is** | Telling a *person inside the plan* that something in CHO needs them: a deadline at risk, a job finished, an exception to work, an approval waiting, an integration broken. |
| **Not member or provider correspondence.** | Letters, notices, EOBs and their PHI-free "a document is waiting" emails are [`correspondence.md`](correspondence.md). That service carries regulated template approval, proof of delivery, USPS events and retention obligations. An internal "queue at risk" alert needs none of it, and putting it there would subject operational noise to correspondence's controls. They stay separate. |
| **Not infrastructure monitoring.** | Pod restarts, latency, error rates and saturation are Prometheus `/metrics` ([`observability.md`](observability.md)) into the plan's own monitoring stack, where IT already lives and already has on-call. CHO does not rebuild alertmanager. The overlap with IT is handled in §5 and §6, not by duplicating monitoring. |
| **Not a work queue.** | A notification says *something happened*; a work item is *something to do*, with a queue, a due date, a lease and an owner ([`work-management.md`](work-management.md)). Where work must be tracked to completion, the right answer is a work item — possibly with a notification *about* it. §3 rule 1. |

---

## 2. The shape: topic, subscription, notification

### 2.1 Topics

A topic is a dotted, tenant-scoped name owned by the publishing service, and it
is a **contract**: a stable name, a severity, a documented payload, and a
documented scope dimension.

```
work.queue.deadline_risk        scope: queueId, workType
work.recommendation.proposed    scope: queueId
work.queue.anomaly              scope: queueId, workType
report.run.completed            scope: reportId, runBy
report.run.failed               scope: reportId
correspondence.delivery.failed  scope: channel, lineOfBusiness
payment.run.awaiting_approval   scope: runId
enrollment.load.exceptions      scope: tradingPartner
edi.inbound.failed              scope: tradingPartner
appeals.case.deadline_risk      scope: lineOfBusiness, level
config.secret.expiring          scope: service
```

A topic registry is code, like the report catalog in
[`portal-reporting.md`](portal-reporting.md) §7.1: id, owning service, severity,
default audience, payload schema, scope dimensions, whether it is
acknowledgeable, and its PHI classification. Adding a topic is a backend change
and a doc line, not a configuration free-for-all, so that the subscription
screen can list what exists and a reviewer can see what a plan is exposed to.

Publishing rides the existing `IMessageBus` pub-sub pattern
([`shared-messagebus.md`](shared-messagebus.md)) — this needs no new transport.

### 2.2 Subscriptions

A subscription is **audience → topic → scope filter → channels → delivery
style**:

```
NotificationSubscription
  tenantId, id, topic
  audience      Role { role }                     e.g. ClaimsSupervisor
              | DistributionList { listId }       e.g. "IT operations"
              | User { userId }                   escape hatch, discouraged
              | WorkQueueTeam { teamId }          reuses work-management teams
  scope         { queueId: [...], workType: [...], lineOfBusiness: [...], ... }
                empty dimension = all
  minSeverity   Info | Warning | Critical
  channels      InPortal | Email | Webhook | Digest
  style         Immediate | Digest(daily|hourly, at) | EscalationOnly
  quietHours?   { from, to, timezone, exceptSeverity }
  createdBy, createdAt, isActive
```

**Audiences are groups first, individuals last.** A subscription attached to one
person is a single point of failure: they go on leave, they leave the plan, and
nobody notices the alerts stopped. Three group forms, in order of preference:

1. **Role** — `ClaimsSupervisor`, `ComplianceOfficer`, `FinanceApprover`. These
   already exist in `ChoRolePermissions` and are already how CHO decides who may
   see what, so a role subscription can never deliver to someone not entitled to
   the content (§4).
2. **Work-queue team** — `work-management-service` already has teams with
   memberships, maintained by supervisors. A team subscription means the people
   who work a queue hear about that queue, and it stays correct as the roster
   changes without anyone maintaining a second list.
3. **Distribution list** — a named list of users (and optionally external email
   addresses or a webhook), maintained by a tenant admin. This is what "the IT
   group" is: IT is not a CHO role and should not be given one to receive mail.
   A list's members are still permission-checked individually (§4).

**Both push and pull.** A tenant admin can subscribe a role or a list
(*"be subscribed"*), and an individual can subscribe themselves to a topic and
scope they are entitled to (*"subscribe"*), and can mute a topic for themselves
unless the topic is marked `mandatory` — compliance and approval topics are. The
two are separate records, so an admin's subscription cannot be quietly deleted
by a user, only muted, and the mute is visible to the admin.

### 2.3 Notifications

```
Notification
  tenantId, id, topic, severity, key, scope{}
  firstFiredAt, lastFiredAt, fireCount
  title, bodyPhiFree, bodyDetailed?, detailLink, sourceRef?
  figures{}                      the numbers, named, for rendering and audit
  state         Open | Acknowledged | Resolved | Expired | Escalated
  acknowledgedBy, acknowledgedAt, acknowledgeNote?
  resolvedAt, resolvedBy | resolvedAutomatically
  escalatedAt, escalationTier
  deliveries[]  { subscriptionId, userId, channel, sentAt, result, openedAt? }
```

`key` is the **identity of the condition, not of the message**:
`{tenant}.work.queue.deadline_risk.{queueId}.{horizon}`. The same condition
firing again updates the open notification and bumps `fireCount`; it does not
create a second one (§7).

---

## 3. Rules

1. **If it must be tracked to completion, it is a work item, not a notification.**
   A notification is a nudge with an optional acknowledgement. Returned mail
   already opens a work item ([`correspondence.md`](correspondence.md) §6.1), and
   that is the right pattern. Notifications that keep getting raised for the same
   kind of thing are a sign the thing wants a queue.
2. **One definition per condition.** The threshold that fires
   `work.queue.deadline_risk` is the same server-side definition the strip, the
   Insights figure and the report use — [`portal-reporting.md`](portal-reporting.md)
   rule 2 applies to alerts too, or the alert and the dashboard will disagree and
   the alert will lose.
3. **Every notification carries its figures,** named, so the body is rendered from
   data rather than from a string a publisher baked, and so a reader can check it.
4. **A notification is never the only record.** Everything it says is visible in
   the portal to someone entitled, whether or not a notification was delivered.
   Delivery is best-effort; the state is not.
5. **Severity is about consequence, not volume.** `Critical` is reserved for a
   regulatory deadline, a financial control, or a failed integration that stops
   data arriving. A capacity warning is `Warning` however loud it feels.
6. **No topic fires without a subscription.** There is no implicit "everybody gets
   critical". Silence is a configuration finding, surfaced in §8's coverage view,
   not something the system papers over by broadcasting.

---

## 4. PHI and entitlement — the hard rule

**Delivery is permission-checked per recipient, at send time, not at subscribe
time.** A subscription to a topic does not grant sight of its content; the
recipient's own `ChoRolePermissions` at the moment of delivery decides what they
receive, exactly as [`portal-reporting.md`](portal-reporting.md) §7.3 requires of
report schedule recipients. Someone who loses the permission stops receiving, and
the subscription's owner is told.

**Two renderings, and the channel decides which can be used:**

| | `bodyPhiFree` | `bodyDetailed` |
|---|---|---|
| Content | Counts, queue names, work types, dates, severity, a deep link | May name the member, claim, case or provider the condition is about |
| Channels | In-portal, Email, Webhook, Digest — **any** | **In-portal only** |

So an email never carries PHI, which is the rule
[`correspondence.md`](correspondence.md) §13 already sets for member
notifications ("notification emails and texts carry no PHI", templates validated
at approval to contain no PHI merge fields) and which this capability inherits
rather than re-argues. A topic registered with `phi: none` — and most operational
topics are counts and queue names, so most are — has no detailed rendering at all,
and that is the preferred design for any new topic.

This is what makes an IT subscription safe: *"3 appeals in Expedited Review
breach in under 6 hours, nobody has acknowledged"* is operationally complete and
names no member. It is also why the right IT subscription is the exception
digest (§5), which is structurally PHI-free.

Every delivery is audited with its recipient, channel, rendering and result;
opening a detailed notification is a read, logged as reads are.

---

## 5. The IT backstop: acknowledgement and escalation, not a copy of everything

Mark's instinct is right — the business can miss things, and somebody has to
notice that nobody noticed. The wrong implementation is giving IT a parallel feed
of every business alert: IT cannot judge whether a pricing queue's backlog
matters, the volume guarantees the feed gets filtered to a folder nobody reads,
and it quietly widens who sees operational detail. The useful implementation is
to make **"nobody acted"** a first-class, detectable state.

### 5.1 Acknowledgement

A topic marked `acknowledgeable` carries an **acknowledgement deadline** derived
from its own clock: for `work.queue.deadline_risk`, well before the deadline at
risk; for `payment.run.awaiting_approval`, before the run's cut-off; for
`edi.inbound.failed`, a fixed interval. One person acknowledging clears it for the
whole audience, with their name and an optional note. Acknowledging says *"seen,
and I own it"* — not *"fixed"*; `Resolved` is separate and is usually set
automatically when the condition clears.

Without acknowledgement there is no difference between an alert that was caught
and one that was missed, and therefore no backstop worth having. This is the
single most important element of the design.

### 5.2 Escalation tiers

A topic has an ordered tier list. Tier 1 is the business audience. If the
notification is still `Open` at its acknowledgement deadline, it escalates: state
becomes `Escalated`, and tier 2's subscribers are delivered to.

**Tier 2 is the next wider scope, not a named person and not "my manager".**
At health plans the accountability for a compliance deadline sits a level or two
above the supervisor — a director or VP of claims, with UM and appeals typically
reported under claims. The naive way to express that is a reporting hierarchy,
and CHO should not hold one: it goes stale the week somebody changes jobs, nobody
maintains it, and CHO is not an HR system. What CHO can keep accurate is **scope
of responsibility** — the queues, work types and areas a role assignment covers.
Escalation then walks outward by scope: a notification scoped to one queue
escalates to whoever holds that queue's area, which in practice is the director
the business would have escalated to anyway, and it stays correct without anyone
maintaining it.

That requires two things CHO does not have, both bigger than this capability and
both discussed in [`workforce-optimization.md`](workforce-optimization.md) §6.4:
a role tier above `ClaimsSupervisor` (the `Finance` → `FinanceApprover` pattern),
and scope on `workqueue:admin`, which is tenant-wide today. Until they exist,
tier 2 is a distribution list a tenant admin maintains by hand — which works, and
is honest about being a stopgap rather than pretending the hierarchy is modelled.

A tier-3 tier exists for the handful of topics where a regulatory clock is at
stake. Note what tier 3 is *for*: compliance already reads everything through
`*:read`, so it is not about giving them sight. It is about addressing someone
when the owner of the deadline has not responded either.

Escalation is **a different message**, not a resend: it leads with the fact that
nothing happened, how long it has been open, who was told and when. That is the
message IT can actually act on, because the action is "find out why the business
did not see this", which is an IT-shaped problem.

A subscription with `style: EscalationOnly` receives nothing until escalation.
**This is the recommended setting for the IT group on business topics** — it gives
IT exactly the backstop role without the firehose.

### 5.3 The coverage and exception digest

The other thing IT should have, and the thing most likely to catch what the
business misses, is not alerts at all but a daily digest of the system's own
state:

- notifications escalated or unacknowledged past their deadline, by topic;
- topics with **no active subscription** (rule 6's finding — a condition nobody
  is listening for is the real backstop failure);
- delivery failures (bounced email, webhook non-200, a recipient who lost the
  permission and was dropped);
- publishers that have fired nothing in N days where they normally fire daily,
  which is how a broken notifier gets noticed;
- subscriptions whose audience resolves to zero people (a role nobody holds, an
  emptied team, a list whose members all left).

This digest is counts and configuration. It contains no PHI by construction, it
is small, and it is the artefact an IT group will genuinely read.

### 5.4 Where IT's own channel is

IT does not live in the CHO portal. A `Webhook` channel — posting a signed JSON
payload to a URL a tenant admin configures — is what makes this practical, since
it lands in Teams, Slack, ServiceNow, PagerDuty or whatever the plan runs, through
the plan's own connector. Genuinely technical topics (`edi.inbound.failed`,
`config.secret.expiring`, a publisher gone quiet) can additionally be exposed as
Prometheus gauges on the publishing service so they reach the plan's existing
alerting without a second path to maintain.

---

## 6. Which topics are business, which are IT, and which are both

| Topic | Tier 1 | Escalates to | Notes |
|---|---|---|---|
| `work.queue.deadline_risk` | ClaimsSupervisor / the queue's team | The queue's wider scope (director), then IT (EscalationOnly) | PHI-free: counts and queue names |
| `appeals.case.deadline_risk` | A&G manager | The director who owns the clock, then compliance, then IT | A regulated clock; tier 3 warranted |
| `payment.run.awaiting_approval` | FinanceApprover | Finance manager | Mandatory; a financial control |
| `report.run.completed` / `failed` | The user who ran it | — on `completed`; IT on repeated `failed` | Not acknowledgeable when it succeeded |
| `correspondence.delivery.failed` | Correspondence operations | IT when the failure is systemic (a channel, not a letter) | A single returned letter is a work item, not an alert |
| `enrollment.load.exceptions` | Enrollment lead | IT above a share-of-file threshold | A bad file is IT's; bad rows are the business's |
| `edi.inbound.failed` | EDI operations | IT immediately | Primarily IT; also a Prometheus gauge |
| `config.secret.expiring` | — | IT only | Never a business topic |
| `work.recommendation.proposed` | The queue's supervisor | — | Never escalates; a suggestion nobody takes is not a failure |
| `work.staffing_gap` | The queue's supervisor | The director whose scope covers it, immediately rather than on a deadline | The supervisor cannot fix it, so the wider scope is not an escalation but a co-recipient ([workforce-optimization.md](workforce-optimization.md) §6.4) |

The pattern: **the business owns the condition, IT owns the silence**, and
whether a failure is "a bad file" or "bad rows" is the line that decides which.

---

## 7. Not becoming noise

The failure mode of every notification system is being muted, and a muted
backstop is worse than none because it looks like coverage.

- **Dedupe on `key`.** The same condition updates one open notification. A
  recurring 15-minute evaluation does not produce 96 emails.
- **Re-notify only on material change** — severity rises, or the figure crosses
  the next threshold — or at escalation. The portal always shows current state;
  the channel does not need every tick.
- **Digest by default for `Info`,** immediate for `Warning` and `Critical`.
- **Quiet hours** per subscription, with a severity that overrides them. A
  24-hour expedited clock overrides; a backlog warning waits for morning.
- **Per-recipient rate caps,** and when a cap is hit the system sends one "N
  further notifications are waiting" rather than dropping silently.
- **Auto-resolve.** When the condition clears, the notification resolves itself
  and says so. Nothing is left needing a human to close it, because that is how
  a list stops being trusted.
- **Topics are retired.** A topic whose notifications are acknowledged without
  action, or muted by most of its audience, is a candidate for deletion. The
  §5.3 digest surfaces the rate; §8's admin view shows it per topic.

---

## 8. Surfaces

- **A bell in the portal chrome** with the viewer's open notifications, grouped
  by topic, with acknowledge, mute-for-me and open-the-detail.
- **Home**, already the place a user lands: open `Critical` and `Warning` items
  for them, with the module's own figure beside each.
- **Admin › Notifications** (`notifications:admin`, new): the topic catalog with
  each topic's audience, tiers, acknowledgement deadlines and fire rate;
  subscriptions CRUD; distribution lists; the coverage view (topics with no
  subscription, audiences resolving to nobody); the delivery log; and the digest
  configuration.
- **A per-user preferences page**: self-subscriptions, channel choice, quiet
  hours, and the mutes they hold — with the mandatory topics shown as
  unmutable rather than hidden.

---

## 9. Where it lives, and build order

**A small `notification-service`.** It owns the topic registry, subscriptions,
distribution lists, notification state, acknowledgement, escalation, dedupe,
digests, channel delivery and the delivery audit. Publishers depend on nothing
but the bus: a service fires a topic event and is done. The alternative — a
shared library each service hosts — spreads delivery, retry, dedupe and quiet
hours across a dozen services and makes the delivery log impossible to assemble,
which defeats the one question this capability exists to answer.

It is **not** correspondence-service (§1), and it does not replace Prometheus.
It reads roles from tenant-service and teams from work-management-service at
delivery time rather than copying them.

1. **Topic registry, subscriptions, in-portal delivery, the bell and Home.** Two
   publishers to prove the contract: `report.run.completed` (which
   [`portal-reporting.md`](portal-reporting.md) §7.2 already needs) and one
   work-management topic.
2. **Email channel,** PHI-free rendering with deep links, delivery audit,
   bounce handling, digest and quiet hours.
3. **Acknowledgement and escalation tiers,** plus the §5.3 coverage and
   exception digest. *This is the step that makes the IT backstop real; the first
   two steps are plumbing without it.*
4. **Webhook channel** and distribution lists, so IT receives in its own tools.
5. **The rest of the publishers,** each a small PR in its own service:
   correspondence delivery failures, payment approvals, enrollment loads, EDI,
   appeals clocks, secret expiry.
6. **Prometheus gauges** for the technical topics, and retirement of any ad hoc
   notification a service grew in the meantime.

---

## 10. Open questions

| # | Question | Recommendation |
|---|---|---|
| 1 | Is the IT group a distribution list, or should CHO gain a real group concept in tenant-service? | A distribution list for IT specifically, since "IT" is not a CHO role and never will be. But the scope-based escalation in §5.2 wants the same structure that report schedule recipients and approval routing will want, so the **list belongs in tenant-service beside users and roles**, not as a notification-service concept — otherwise the second and third consumer each grow their own. |
| 7 | Does this capability wait for the director role and scope (§5.2)? | No. Build tiers against distribution lists, and let a scoped role tier replace the hand-maintained list later without changing the topic contracts. Waiting would hold the notification capability behind a permissions change that narrows what supervisors can do today. |
| 2 | Should individuals be able to mute a topic an admin subscribed them to? | Yes, except on topics marked `mandatory` (compliance, financial approvals), and the mute is visible to the admin in the coverage view. A mute an admin cannot see is how coverage silently disappears. |
| 3 | Escalation to IT: every acknowledgeable business topic, or only the ones a plan nominates? | Nominated per tenant, defaulting to the ones with a regulatory or financial consequence. A plan whose IT group wants everything can say so; the default should not hand IT the firehose. |
| 4 | Do we allow `bodyDetailed` (naming a member or claim) on **any** channel but in-portal? | No. It is one rule, it matches correspondence's, and every exception becomes a PHI review. |
| 5 | How long are notifications kept? | 13 months, which covers a program-audit look-back on "were you told"; the delivery log the same. Longer only if a plan's own policy requires it. |
| 6 | Does an unacknowledged `Critical` eventually become a work item? | Probably yes for the regulated clocks, which gives it an owner and a due date rather than an ever-louder alert — but it needs the work type and the queue, so it is a phase-3+ decision, not a phase-1 one. |

---

## Related

- [`workforce-optimization.md`](workforce-optimization.md) — the first substantial
  publisher: deadline risk, anomalies and recommendations.
- [`portal-reporting.md`](portal-reporting.md) §7.2–7.3 — background run
  completion, and the recipient-must-hold-the-permission rule this inherits.
- [`correspondence.md`](correspondence.md) §13 — member and provider
  correspondence, and the PHI-free notification rule.
- [`observability.md`](observability.md) — Prometheus `/metrics`, the IT
  monitoring path this does not replace.
- [`shared-messagebus.md`](shared-messagebus.md) — the pub-sub transport.
- [`work-management.md`](work-management.md) — teams as an audience, and the
  work-item-versus-notification line.
