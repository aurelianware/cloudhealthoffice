# cms-0057-f.com ↔ cloudhealthoffice.com — Implementation Contract

**Status:** implementation contract. The receiving side (attribution capture,
service anchors, disclosure) is live on cloudhealthoffice.com. The sending side
lives in the cms-0057-f.com repository and is specified here.

**Decision record:** [ADR 013](../adr/013-cms-0057-f-com-companion-property.md).
**Locked wording:** [`src/site/MESSAGE_SHEET.md`](../../src/site/MESSAGE_SHEET.md).

---

## 1. Division of purpose

| | cms-0057-f.com | cloudhealthoffice.com |
| --- | --- | --- |
| **Primary purpose** | Educational and regulatory discovery | Authoritative commercial destination |
| **Content** | What the rule requires, implementation requirements, architecture guidance, payer questions, FHIR and Da Vinci explanation, dependencies on existing cores, checklists and decision frameworks | Software, deployment, pricing, Professional Services, implementation services, CMS-0057-F assessments, core administration advisory, vendor/SOW review, fractional architecture, product evaluation, technical support |
| **Conversion** | Contextual CTAs only, deep-linked to cloudhealthoffice.com | All detailed service descriptions, engagement information, conversion flows, lead capture, and future assistant escalation |
| **Search intent** | Informational | Commercial |

**The rule:** do not create substantially identical service pages on both
domains. A reader who wants to understand the regulation should never have to
leave cms-0057-f.com; a reader who wants help should always end up here.

## 2. Approved contextual CTAs

Place these where a reader plausibly needs implementation help — at the end of a
requirements explanation, after an architecture decision framework, alongside a
checklist. Not in a sidebar on every page.

### CTA 1 — assessment

> **Need help applying this to your payer environment?**
> Cloud Health Office Professional Services provides CMS-0057-F architecture
> assessments and implementation guidance for health plans.
> **Request an assessment →**

Target: `https://cloudhealthoffice.com/services#offer-assessment`

### CTA 2 — vendor / SOW review

> **Have a CMS-0057-F vendor proposal or SOW already?**
> Get an independent technical review of the architecture, scope, assumptions,
> dependencies, and vendor responsibilities.
> **Request a technical review →**

Target: `https://cloudhealthoffice.com/services#offer-sow-review`

### CTA 3 — core administration

> **Running QNXT, Facets, HealthEdge, or another payer core?**
> Talk with a payer architect about how CMS-0057-F can be implemented alongside
> your existing environment.
> **Discuss your architecture →**

Target: `https://cloudhealthoffice.com/services#offer-core-admin`

### Additional stable anchors

| Anchor | Use when the article is about |
| --- | --- |
| `/services#offer-implementation` | Building or deploying the CMS-0057-F surface |
| `/services#offer-interop` | CRD, DTR, PAS, X12, prior authorization workflow |
| `/services#offer-fractional` | Ongoing architecture leadership on a program |
| `/services#payer-operations` | Fee schedules, repricing, payment validation, core configuration |
| `/deploy` | Where the software runs, SaaS vs. payer cloud, shared responsibility |
| `/platform` | The product itself |
| `/evidence` | Published implementation evidence |

These anchors are a public contract between the two properties and are covered
by a test in this repository (`scripts/tests/site-services-positioning.test.ts`).
Do not rename them without updating the reference site.

## 3. Attribution contract

Append these parameters to every outbound CTA link so attribution survives even
when the browser strips the referrer header:

```
https://cloudhealthoffice.com/services
  ?ref=cms-0057-f.com
  &ref_article=/guides/cms-0057-f-qnxt
  &ref_cta=sow-review
  &utm_source=cms-0057-f.com
  &utm_medium=referral
  &utm_campaign=<content-cluster>
  #offer-sow-review
```

The query string must come **before** the fragment. A URL written as
`/services#offer-sow-review?ref=...` puts the parameters inside the fragment,
where `location.search` is empty and the receiving code captures nothing.

To land directly on the contact form with the topic preselected, use
`/contact?interest=<key>` with the same `ref` parameters. Valid keys are listed
in [`ASK-CLOUD-HEALTH-OFFICE-ASSISTANT.md`](ASK-CLOUD-HEALTH-OFFICE-ASSISTANT.md);
the ones most relevant to referral traffic are `cms0057-assessment`,
`sow-review`, `core-admin`, `interop`, and `implementation`.

### What the receiving side does

`src/site/js/analytics-events.js` captures **first-touch, session-scoped**
attribution on landing and stores it in `sessionStorage` under
`cho_attribution`:

| Field | Source | Sanitization |
| --- | --- | --- |
| `ref_site` | `?ref=`, else referrer hostname | lowercased, `[a-z0-9.-]` only, ≤100 chars |
| `ref_article` | `?ref_article=`, else referrer path | query and fragment stripped, `[A-Za-z0-9._~/-]` only, ≤160 chars |
| `ref_cta` | `?ref_cta=` | whitelisted characters, ≤60 chars |
| `utm_source` / `_medium` / `_campaign` / `_term` / `_content` | query string | whitelisted characters, ≤120 chars |
| `landing_path` | the page that received the visit | query and fragment stripped, `[A-Za-z0-9._~/-]` only, ≤160 chars |

First touch wins: navigating around cloudhealthoffice.com does not overwrite it.
The attribution is attached to Formspree lead submissions (contact form and every
`data-lead-form`) and reported to GA4 as:

- `cross_site_referral` — fired on landing when the originating host is a
  recognized companion property.
- `cross_site_lead` — fired on contact-form submission when attribution is
  present, carrying `ref_site`, `ref_article`, `ref_cta`, `form_topic`, and
  `interest_kind`.
- `contact_form_submit` — now also carries `ref_site`, `ref_article`, `ref_cta`.

This lets a specific regulatory article be tied to the assessment requests,
architecture conversations, product evaluations, SOW reviews, and implementation
engagements it produced.

### What must never cross

No PHI. No member, patient, or claim data. No personal information of any kind.
No identifier beyond the site's own first-party `anonymous_id`. Nothing the
visitor typed. If the reference site ever gains its own forms, it must not
forward their contents in a URL.

## 4. Keyword split

**cms-0057-f.com (informational):** what does CMS-0057-F require · CMS-0057-F
deadline · CMS-0057-F prior authorization requirements · CMS-0057-F and QNXT ·
CMS-0057-F and Facets · CMS-0057-F and HealthEdge · CRD vs DTR vs PAS ·
CMS-0057-F architecture · payer-to-payer API requirements

**cloudhealthoffice.com (commercial):** CMS-0057-F consulting · CMS-0057-F
implementation services · CMS-0057-F readiness assessment · payer
interoperability consulting · QNXT integration consulting · payer core
administration advisory · Da Vinci PAS implementation · FHIR implementation for
health plans · CMS-0057-F vendor SOW review · fractional payer solution architect

Note that `/cms-0057f-compliance` on this site remains the commercial CMS-0057-F
pillar and must not be flattened into the reference site's informational
coverage. The two are distinguishable: the pillar page sells a product surface,
the reference site explains a rule.

## 5. Editorial independence

- Keep cms-0057-f.com technically credible and evidence-based even where Cloud
  Health Office is not required to solve the problem being discussed. An article
  that concludes "your existing vendor can do this" is doing its job.
- Do not convert the reference property into thin promotional content. If a page
  cannot stand as useful guidance with the CTA removed, it should not ship.
- Disclose the relationship where appropriate. Locked sentence: *cms-0057-f.com
  is operated by Aurelianware, Inc., the same company behind Cloud Health
  Office.* The reciprocal disclosure is published on `/services`.

## 6. Measurement

Report, by originating article:

| Question | Signal |
| --- | --- |
| Which articles send traffic? | `cross_site_referral` grouped by `ref_article` |
| Which CTAs convert? | `cross_site_lead` grouped by `ref_cta` |
| What are they asking for? | `cross_site_lead` grouped by `form_topic` / `interest_kind` |
| Product or services intent? | `product_interest_selected` vs. `service_interest_selected` vs. `deployment_interest_selected` |
| Did it become an engagement? | Formspree lead record, which carries the same `ref_*` fields |

## 7. Open items

- Whether the future "Ask Cloud Health Office" assistant should be embeddable on
  cms-0057-f.com in a read-only regulatory mode, or remain here only. Escalation
  and lead capture stay here either way.
- Whether to add durable cross-visit attribution. Currently session-scoped by
  design; revisit only if referral volume justifies it.
- A reciprocal link audit once the reference site's CTA blocks ship, to confirm
  every anchor named in section 2 resolves.
