# Ask Cloud Health Office — Assistant Plan

**Status:** planning document. Not implemented in the current site beyond the
existing constrained assistant (`src/site/js/assistant.js`).

**Purpose:** define the intent taxonomy, guardrails, and escalation model for a
future "Ask Cloud Health Office" product and advisory assistant, so the site,
the contact form, and the analytics events are already shaped for it.

**Source of truth for anything the assistant may say:**
[`src/site/assistant/knowledge.md`](../../src/site/assistant/knowledge.md), which
must stay consistent with
[`src/site/MESSAGE_SHEET.md`](../../src/site/MESSAGE_SHEET.md).

---

## Intent categories

These are the same keys used by the `/contact?interest=` deep link, the contact
form topic select, and the GA4 interest events, so a conversation, a form
submission, and an analytics report all describe intent the same way.

| Intent key | Category | What the visitor is asking about |
| --- | --- | --- |
| `platform` | Platform evaluation | What Cloud Health Office is, what it does, whether it fits |
| `saas` | Deployment model | Hosted / Aurelianware-operated SaaS |
| `payer-cloud` | Deployment model | Deployment into the plan's own cloud |
| `managed` | Deployment model | Aurelianware-managed deployment |
| `hybrid` | Deployment model | Shared-responsibility operation |
| `managed-ops` | Deployment model | Ongoing managed or shared operations |
| `cms0057-assessment` | CMS-0057-F | Readiness, architecture, sequencing, the rule itself |
| `implementation` | CHO implementation | Deploying, integrating, and going live on the platform |
| `core-admin` | QNXT and core administration | QNXT, Facets, HealthEdge, CAPS modernization |
| `sow-review` | Architecture / vendor review | Vendor proposals, SOWs, estimates, responsibilities |
| `interop` | Professional services | FHIR, Da Vinci, X12, prior authorization implementation |
| `fractional-architect` | Professional services | Ongoing payer-side architecture leadership |
| `payer-operations` | Payer operations | Core administration operations support generally |
| `fee-schedule` | Payer operations | Fee schedule and reimbursement configuration |
| `repricing-validation` | Payer operations | Claims repricing and payment validation |
| `core-admin-support` | Payer operations | Production support on QNXT, Facets, HealthEdge |
| `support` | Technical support | A live technical issue |
| `other` | Other | Anything else |

The condensed set the assistant classifies against:

1. platform evaluation
2. deployment model
3. SaaS versus payer-controlled cloud
4. Cloud Health Office implementation
5. CMS-0057-F
6. QNXT and core administration
7. architecture / vendor review
8. professional services
9. payer operations and core administration
10. technical support
11. other

## What the assistant may say about how Cloud Health Office is consumed

- Operated by Aurelianware — **under evaluation, not offered today.**
- Deployed into a payer-controlled cloud — **available.**
- Managed through a shared-responsibility model — **by engagement.**
- Implemented with professional services — **available.**
- Used alongside existing vendors — **yes, and this is the default framing.**

It must never invent availability, certifications, partnerships, customer
outcomes, uptime, security attestations, or deployment capabilities. When asked
something outside the knowledge pack, it refuses and routes to the contact form
or the calendar rather than guessing.

## Sensitive-data guardrail

The assistant must explicitly discourage submission of PHI, member data, patient
data, claim data, credentials, secrets, or other sensitive production
information — on first open, and again whenever a message looks like it contains
member identifiers, claim numbers, or credentials. A visitor with a live
technical issue is routed to `enterprise@cloudhealthoffice.com`, never asked for
troubleshooting data in a public widget.

The current assistant already treats `member id`, `diagnos`, `symptom`, `treat`,
`hack`, `exploit`, `bypass`, `pmpm`, `quote me`, and `discount` as off-policy.
A future version should extend that to obvious credential and claim-identifier
patterns.

## Escalation model

```
visitor
  → AI product/advisory assistant
    → intent classification (table above)
      → relevant answer from the knowledge pack
        → qualified lead (work email + intent + pages viewed + transcript)
          → human escalation
            → email / SMS notification
              → optional live-chat takeover
```

Notes on each hop:

- **Intent classification** should be reported as a GA4 event so the assistant's
  intent mix can be compared against the contact form's topic mix.
- **Qualified lead** reuses the existing lead record: anonymous id, pages viewed
  this session, transcript, and — only if the visitor offers it — a work email.
  No PII is stored in the browser (see `js/analytics-events.js`).
- **Human escalation** is a person, not a queue. The site's promise is a reply
  within one business day; the assistant must not promise faster.
- **Live-chat takeover** is optional and out of scope for the first
  implementation.

## Analytics

The assistant should emit, at minimum:

- `assistant_open`, `assistant_message` (already emitted today)
- `assistant_intent_classified` with the intent key
- `assistant_refusal` with a coarse reason (off-policy, outside knowledge pack)
- `assistant_lead_captured`

Never send message content, member identifiers, or any health information into
analytics.

## Open questions

- Whether classification runs client-side against the keyword table (today's
  approach, fully auditable) or server-side against a model with the knowledge
  pack as its only context.
- How the knowledge pack is kept in sync automatically with `MESSAGE_SHEET.md`
  rather than by review.
- Whether transcripts should be retained at all when no email is offered.
