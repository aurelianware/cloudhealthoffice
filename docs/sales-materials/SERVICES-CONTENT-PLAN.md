# Services & Deployment — SEO Architecture and Content Plan

**Status:** planning document. Companion to
[ADR 012](../adr/012-product-led-professional-services.md).

**Scope:** how `/services` and `/deploy` fit the existing search architecture,
which future pages are justified, and the content backlog that should feed the
Evidence and Insights hubs rather than a promotional blog.

> No third-party keyword-volume data is asserted here. The concept clusters below
> are derived from the existing site's CMS-0057-F and payer-platform pages, the
> Evidence Hub, and the language payer buyers actually use in the offerings on
> `/services`. Treat volumes as unmeasured until Search Console data exists for
> the new pages.

---

## 1. Existing search architecture (do not cannibalize)

| Page | Owns |
| --- | --- |
| `/` | Brand, "Cloud Health Office", the "not VMware CloudHealth" disambiguation |
| `/cms-0057f-compliance` | The CMS-0057-F pillar. Rule explanation, API surface, deadline |
| `/cms-0057-f/implementation-architecture` | CMS-0057-F implementation architecture |
| `/platform` | Product capability: layers, services, product tour |
| `/what-is` | Category definition |
| `/solutions-payers` | Payer solution framing |
| `/evidence` + `/evidence/*` | Benchmark and implementation evidence |
| `/insights/*` | Long-form engineering narrative |
| `/pricing`, `/pricing-api` | Commercial terms, repricing API |
| `/trust` | Security posture |
| `/founder` | Practitioner credibility |

The two pages added in this phase:

| Page | Owns | Deliberately does **not** own |
| --- | --- | --- |
| `/services` | Payer interoperability **consulting / implementation / advisory** intent | The rule explanation (that stays on `/cms-0057f-compliance`) |
| `/deploy` | **Deployment and operating model** intent | Product capability (that stays on `/platform`) |

**Rule of thumb:** product pages use product language; services pages use
services language. A query about *what the rule requires* goes to
`/cms-0057f-compliance`. A query about *who will help us meet it* goes to
`/services`. A query about *where the software runs* goes to `/deploy`.

## 2. Concept clusters and their target page

### Commercial-intent, services

| Concept | Target |
| --- | --- |
| CMS-0057-F consulting | `/services` |
| CMS-0057-F implementation | `/services#offer-implementation` |
| CMS-0057-F readiness assessment | `/services#offer-assessment` |
| payer interoperability consulting | `/services` |
| health plan technology consulting | `/services` |
| payer solution architect / fractional architect | `/services#offer-fractional` |
| payer software implementation services | `/services#offer-implementation` |
| vendor SOW technical review (payer) | `/services#offer-sow-review` |
| payer core administration modernization | `/services#offer-core-admin` |
| QNXT integration / QNXT FHIR / QNXT CMS-0057-F | `/services#offer-core-admin` supported by `/evidence/replace-vs-augment` |
| Da Vinci PAS implementation | `/services#offer-interop` supported by `/evidence/prior-authorization` |

### Commercial-intent, product

| Concept | Target |
| --- | --- |
| CMS-0057-F software | `/cms-0057f-compliance` → `/platform` |
| payer interoperability software | `/platform` |
| health plan interoperability platform | `/platform` |
| payer interoperability SaaS | `/deploy` (with the accurate "under evaluation" status) |
| payer cloud deployment | `/deploy` |

Note the deliberate handling of **payer interoperability SaaS**: the query is
real, the answer today is "not offered — here are the models that are". Ranking
for it with an honest answer is better than either ignoring it or faking it.

## 3. On-page SEO shipped in this phase

- `/services`: title, meta description, canonical, OpenGraph, Twitter card,
  `ProfessionalService` + `BreadcrumbList` + `FAQPage` JSON-LD, visible
  breadcrumb, in-page table of contents with anchor IDs per offering.
- `/deploy`: title, meta description, canonical, OpenGraph, Twitter card,
  `WebPage` + `BreadcrumbList` JSON-LD, visible breadcrumb, TOC, anchor IDs per
  operating model.
- Sitemap entries for `/services` and a refreshed `lastmod` for `/deploy`.
- Clean-URL routing in `_redirects` and `staticwebapp.config.json`.
- Internal links from `/`, `/platform`, `/cms-0057f-compliance`, `/founder`, and
  every page's nav and footer.

`FAQPage` structured data is used only where the questions are genuinely
answered on the page. No `Review`, `AggregateRating`, or `Offer` markup is used —
there are no customers, ratings, or published service prices to describe.

## 4. Future dedicated pages — justified or not

| Candidate | Verdict |
| --- | --- |
| `/services/cms-0057-f-assessment` | **Justified later.** Distinct buying intent and enough substance for a real page, once there is engagement experience to describe. Until then the anchor section is stronger than a thin page |
| `/services/sow-review` | **Justified later.** Same reasoning; pairs naturally with the SOW checklist content below |
| `/services/qnxt` | **Hold.** High intent, but a page here risks reading as a QNXT-affiliated offering. Needs careful wording and probably a legal read before it ships |
| `/services/fractional-architect` | **Hold.** Low volume; the anchor section is sufficient |
| `/platform/deployment` | **Rejected.** Near-duplicate of `/deploy`. See ADR 012 |
| Per-keyword landing pages | **Rejected.** Doorway pages |

## 5. Content backlog

These should become **Evidence** or **Insights** content — technical writing that
happens to support discovery — not thin promotional posts. Each supports both
product discovery and services discovery.

| # | Working title | Hub | Supports |
| --- | --- | --- | --- |
| 1 | CMS-0057-F Payer Readiness Checklist | Insights | `/services#offer-assessment` |
| 2 | 10 Questions to Ask Your CAPS Vendor Before Signing a CMS-0057-F SOW | Insights | `/services#offer-sow-review` |
| 3 | QNXT + CMS-0057-F Architecture Checklist | Evidence | `/services#offer-core-admin` |
| 4 | CMS-0057-F Vendor SOW Review Checklist | Insights | `/services#offer-sow-review` |
| 5 | When Should CMS-0057-F Logic Live Inside vs. Outside Your Core? | Evidence | `/services#offer-core-admin`, `/platform` |
| 6 | CRD / DTR / PAS With an Existing Payer Core | Evidence | `/services#offer-interop` |
| 7 | How to Evaluate a Payer Interoperability Architecture | Insights | `/services` |
| 8 | What a Health Plan Should Own vs. What Its Vendors Should Own | Insights | `/services#offer-sow-review` |
| 9 | SaaS vs. Payer-Controlled Cloud for Payer Interoperability Software | Insights | `/deploy` |
| 10 | Choosing a Shared-Responsibility Operating Model | Insights | `/deploy#model-hybrid` |
| 11 | Questions to Ask Before Deploying Payer Software in Your Cloud | Insights | `/deploy#questions` |
| 12 | How Cloud Health Office Can Complement Existing Core Administration Platforms | Evidence | `/services#offer-core-admin`, `/platform` |
| 13 | What Belongs in the Core, Beside the Core, and at the Interoperability Layer? | Evidence | `/platform`, `/services` |
| 14 | Planning a Cloud Health Office Implementation Across Multiple Vendors | Insights | `/services#offer-implementation` |

### Writing constraints for all of the above

- Coexistence framing only. Never position a named vendor as an adversary.
- No claimed customers, production deployments, certifications, partnerships,
  migrations, consulting clients, or managed-service outcomes.
- Performance claims only where already published, always with their limits.
- Hedged language for anything not yet demonstrated publicly: *designed to
  support, can help with, provides capabilities for, intended to integrate with,
  subject to implementation scope and environment requirements.*
- Every piece links to the relevant `/services` anchor or `/deploy` model rather
  than ending in a generic "contact us".

## 6. Measurement

Track, per page, in GA4:

- `services_page_view`, `deployment_page_view`
- `services_cta_click`, `platform_services_cta_click`
- `deployment_discussion_started`, `deployment_model_selected`
- `product_interest_selected`, `service_interest_selected`,
  `deployment_interest_selected`
- Per-offering interest: `cms0057_assessment_interest`,
  `implementation_services_interest`, `core_admin_advisory_interest`,
  `sow_review_interest`, `saas_interest`, `payer_cloud_interest`,
  `managed_operations_interest`
- `advisory_contact_started`, `advisory_contact_submitted`

Revisit the "justified later" pages once there are at least two quarters of
Search Console impressions for the corresponding queries on `/services`.
