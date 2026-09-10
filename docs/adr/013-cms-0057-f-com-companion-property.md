# ADR 013: cms-0057-f.com As A Companion Educational Property

## Status

Accepted

## Context

Aurelianware operates two public web properties:

- **cloudhealthoffice.com** — the product site: software, deployment, pricing,
  Professional Services, evidence, documentation, and every conversion path.
- **cms-0057-f.com** — a specialized reference property about the CMS-0057-F
  rule itself. Two pages on this site already link to it as a "sister reference
  site" (`/evidence/replace-vs-augment`, `/evidence/prior-authorization`).

Two failure modes were available and both are common:

1. **Duplicate the commercial site onto the reference domain.** Two
   substantially identical service pages compete for the same queries, split
   authority, and turn a credible regulatory resource into a brochure. Readers
   notice, and so does search.
2. **Keep them fully separate.** The regulatory content earns attention and
   then loses the reader with no path to help, and no way to know which article
   produced which conversation.

The reference property's value depends on being useful even when Cloud Health
Office is not the answer. That is precisely what makes a contextual CTA on it
credible.

## Decision

**1. Split by search intent, not by topic.**

| Property | Purpose | Intent |
| --- | --- | --- |
| cms-0057-f.com | Explaining the rule, interpreting requirements, architecture guidance, FHIR and Da Vinci explanation, dependencies on existing cores, checklists and decision frameworks | Informational |
| cloudhealthoffice.com | Software, deployment, pricing, Professional Services, assessments, core administration advisory, vendor/SOW review, fractional architecture, evaluation, support | Commercial |

Concretely: "What does CMS-0057-F require?", "CMS-0057-F deadline", "CRD vs DTR
vs PAS", "CMS-0057-F and QNXT" belong on the reference site. "CMS-0057-F
consulting", "CMS-0057-F readiness assessment", "payer interoperability
consulting", "QNXT integration consulting", "CMS-0057-F vendor SOW review",
"fractional payer solution architect" belong here.

**2. All detailed service content, engagement information, conversion flows,
lead capture, and future assistant escalation live only on
cloudhealthoffice.com.** The reference property carries contextual CTAs that
deep-link to the corresponding service anchor here — never a duplicated
services site. The approved CTA blocks are locked in `MESSAGE_SHEET.md` and each
points at a stable anchor (`/services#offer-assessment`,
`/services#offer-sow-review`, `/services#offer-core-admin`).

**3. Attribution is carried across the boundary, and it carries nothing
sensitive.** `analytics-events.js` captures first-touch, session-scoped
attribution on landing: originating host, originating article path, the CTA
clicked, campaign parameters, and the landing page. Values arrive either as
explicit query parameters (`ref`, `ref_article`, `ref_cta`, `utm_*`) or, when
those are absent, from the referrer header. Every value is whitelisted
character-by-character and length-capped, and the article path is stripped of
its query string and fragment, so a crafted inbound URL cannot inject content
into a lead record. Attribution is attached to Formspree lead submissions and
reported to GA4 as `cross_site_referral` (on landing) and `cross_site_lead` (on
submission).

No PHI, no personal information, and no identifier beyond the site's existing
first-party `anonymous_id` crosses between the properties.

**4. Editorial independence is a requirement, not a preference.** The reference
property keeps technically credible, evidence-based content even where Cloud
Health Office is not required to solve the problem discussed. The relationship
is disclosed: cms-0057-f.com is operated by Aurelianware, Inc., the same company
behind Cloud Health Office — stated on `/services` and reusable verbatim from
`MESSAGE_SHEET.md`.

## Consequences

Positive:

- Each property competes for the intent it can actually win, without
  cannibalizing the other or the existing `/cms-0057f-compliance` pillar.
- Regulatory content becomes measurable: which article produced which assessment
  request, architecture conversation, product evaluation, SOW review, or
  implementation engagement.
- The reference site can honestly say "Cloud Health Office is not required here"
  and remain worth reading, which is what makes its CTAs work.

Tradeoffs:

- The CTA blocks and anchor targets must stay synchronized across two
  repositories. Anchors on `/services` are therefore treated as a public
  contract and covered by a test.
- First-touch attribution is session-scoped in `sessionStorage`, so a visitor
  who returns days later through a different path is attributed to that later
  path. Durable cross-visit attribution would require cookie-based storage that
  is not justified for the volume involved.
- Referrer-derived attribution is unreliable where browsers strip or truncate
  the header, which is why the explicit `?ref=` parameters exist and should be
  preferred on the reference site's outbound links.

## References

- [`src/site/MESSAGE_SHEET.md`](../../src/site/MESSAGE_SHEET.md) — locked CTA blocks, disclosure sentence, property split
- [Cross-site strategy](../sales-materials/CROSS-SITE-CMS-0057-F-STRATEGY.md) — implementation contract for cms-0057-f.com
- [`src/site/js/analytics-events.js`](../../src/site/js/analytics-events.js) — attribution capture and sanitization
- [ADR 012](012-product-led-professional-services.md) — product-led professional services
- [Services content plan](../sales-materials/SERVICES-CONTENT-PLAN.md) — keyword split and content backlog
