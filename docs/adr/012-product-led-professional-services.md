# ADR 012: Product-Led Professional Services And Labelled Operating Models

## Status

Accepted

## Context

Cloud Health Office is a payer technology product: CMS-0057-F interoperability,
progressive modernization, and coexistence with legacy core administration
platforms. Two things were true at the same time and neither was represented on
the public site:

1. Real payer demand exists for **expertise** — CMS-0057-F readiness assessment,
   core administration advisory, vendor and SOW technical review, and payer-side
   architecture leadership — independent of whether the plan ever licenses the
   product.
2. Health plans do not agree on **how software should run**. Security posture,
   data residency, cloud governance, procurement, operational ownership,
   internal engineering capacity, vendor-management policy, network
   architecture, compliance controls, disaster recovery, observability, support
   responsibilities, and release management all differ by plan.

The risk in addressing (1) is repositioning the company as a consultancy, which
would weaken the software story and the fundraising and acquisition posture that
depends on it. The risk in addressing (2) is publishing operating models the
product and the business cannot actually deliver — a hosted SaaS in particular.

A third pressure: marketing pages tend to describe planned capability in the
present tense. `MESSAGE_SHEET.md` already exists precisely because of that drift,
and it bans phrases like "production SaaS live".

## Decision

**1. Professional Services is an extension of the product company, not a pivot.**

- The homepage hero stays product-led. Services appear in bands lower on the
  page, after the product story is made.
- Primary navigation keeps its product-first shape and adds one item:
  `Product · CMS-0057-F · Evidence · Docs · Pricing · Services · Contact`.
- `/services` is a single strong landing page covering six offerings. No
  per-keyword doorway pages are created; future dedicated service pages must be
  justified by demand, not by keyword coverage.
- The independence of advisory work is stated explicitly on the page: a health
  plan does not need to purchase or deploy Cloud Health Office to engage
  Professional Services, and an assessment may legitimately conclude that a
  different path is better.

**2. Deployment lives on `/deploy`, not on a new `/platform/deployment` page.**

`/deploy` already existed as the "how it runs" page. A second deployment page
under `/platform` would be a near-duplicate competing for the same queries, so
`/deploy` was rewritten instead as the deployment and operating-model page in
the Evidence Hub design system.

**3. Every operating model carries an explicit status label.**

| Model | Status | Meaning |
| --- | --- | --- |
| Payer-controlled cloud deployment | **Available** | Shipped and documented: Helm charts, Azure IaC, deployment docs, quickstart |
| Local evaluation | **Available** | Free, no license required |
| Aurelianware-managed deployment | **By engagement** | Offered; responsibilities defined per environment. Not a standard package with published SLAs |
| Hybrid / shared responsibility | **By engagement** | A supported operating pattern, shaped per plan |
| Aurelianware-operated SaaS | **Under evaluation** | Not offered today. No availability, timing, or terms may be implied |

The comparison table on `/deploy` marks hosted-SaaS cells as "proposed" so a
reader cannot mistake design intent for a purchasable model.

**4. Coexistence, never displacement.**

Named vendors — Cognizant, TriZetto, QNXT, Facets, HealthEdge, Availity,
clearinghouses, system integrators — are described as systems Cloud Health Office
works alongside. No certification, affiliation, endorsement, or implementation
partnership is claimed with any of them, because no such documented relationship
exists. Deep QNXT experience is presented as career experience, linked to the
founder page, and explicitly distinguished from a vendor relationship.

**5. One interest taxonomy across contact, analytics, and the future assistant.**

The contact form, the GA4 events, and the planned "Ask Cloud Health Office"
assistant share one set of intent keys (`platform`, `saas`, `payer-cloud`,
`managed`, `hybrid`, `cms0057-assessment`, `implementation`, `core-admin`,
`sow-review`, `interop`, `fractional-architect`, `managed-ops`,
`payer-operations`, `fee-schedule`, `repricing-validation`,
`core-admin-support`, `support`, `other`). The assistant's topics carry the
same keys so its handoff CTA preselects the matching contact topic. `/contact?interest=<key>` preselects the topic so a CTA can carry
intent across the page boundary.

## Consequences

Positive:

- A payer executive landing on the site sees a software company that also has
  implementation expertise, rather than a consultancy.
- Availability language is auditable: `MESSAGE_SHEET.md` holds the status labels
  and a test asserts that `/deploy` and `/services` do not describe SaaS as
  available.
- Advisory engagements can be sold without implying a predetermined product
  recommendation, which is what makes independent review worth buying.
- Lead records now carry deployment preference, core platform, role, and whether
  the visitor is evaluating software, services, or both.

Tradeoffs:

- Hedged availability language ("by engagement", "under evaluation") reads as
  less confident than competitors' copy. That is the intended trade.
- Every new services or deployment claim now has to be reconciled against
  `MESSAGE_SHEET.md` before it ships, which slows copy changes.
- Keeping `/services`, `/deploy`, `MESSAGE_SHEET.md`, and
  `assistant/knowledge.md` consistent is ongoing work; the site positioning test
  guards the parts that can be checked mechanically.
- A single `/services` page defers SEO coverage of individual service queries
  until there is enough substance to justify separate pages.

## References

- [`src/site/services.html`](../../src/site/services.html) — Professional Services landing page
- [`src/site/deploy.html`](../../src/site/deploy.html) — deployment and operating models
- [`src/site/MESSAGE_SHEET.md`](../../src/site/MESSAGE_SHEET.md) — locked positioning and status labels
- [`src/site/assistant/knowledge.md`](../../src/site/assistant/knowledge.md) — constrained assistant knowledge pack
- [Ask Cloud Health Office assistant plan](../sales-materials/ASK-CLOUD-HEALTH-OFFICE-ASSISTANT.md)
- [Services and deployment content plan](../sales-materials/SERVICES-CONTENT-PLAN.md)
- [ADR 011](011-rules-and-evidence-model.md) — separating rules, scoring, and public claims
- [`scripts/tests/site-services-positioning.test.ts`](../../scripts/tests/site-services-positioning.test.ts)
