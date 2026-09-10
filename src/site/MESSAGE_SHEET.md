# Cloud Health Office — Message Sheet (LOCKED)

This file holds the approved, locked marketing sentences for cloudhealthoffice.com.
**Do not drift.** If a page needs new phrasing, update this sheet first, then the page.
The on-site assistant knowledge pack (`assistant/knowledge.md`) must stay consistent
with this sheet.

---

## Canonical one-liner
Cloud Health Office is the claims platform you can put beside QNXT, Facets, or
HealthEdge — so you hit the 2027 FHIR mandate without a core replacement.

## 15-second hero
Health plans must expose FHIR APIs by January 1, 2027 and still run yesterday's
core admin system. Cloud Health Office is a source-available payer platform you
deploy in your own cloud. Start with the CMS-0057-F compliance surface. Move
claims, payments, and provider operations over when you choose.

## Category line (under the logo, every page)
Payer claims platform. Compliance first. Core replacement optional.

## CMS-0057-F definition (reuse verbatim under every mention of the rule)
CMS-0057-F is the federal rule that requires Medicare Advantage, Medicaid, CHIP,
and some exchange plans to offer FHIR APIs for patient access, provider access,
payer-to-payer exchange, and prior authorization by January 1, 2027.

## License sentence
Source-available under BSL 1.1. Evaluate and run locally for free. Production use
requires a license. Deploy in your cloud or as a managed tenant.

## Stage language (only on /deploy or in the assistant after the visitor asks "who is live?")
Cloud Health Office is ready to deploy in your cloud as a compliance layer beside
QNXT, Facets, or HealthEdge. The first production tenant is under discussion; the
evidence and source are already public.
> Never put this sentence in the H1.

## Commercial posture for Layer 1 (do not headline "free CMS-0057-F")
The CMS-0057-F API implementations ship in the repo. A production Layer 1 deploy in
the customer's cloud is discussed as a low-friction entry (license terms can be
waived for an early production tenant). Implementation, core mapping, prior-auth
workflow, and Platform Engagement (claims / payments / core replacement) are paid.

---

## Homepage copy (locked)
- **Eyebrow:** Payer platform · Source-available · Runs in your cloud
- **H1:** Meet the 2027 FHIR deadline without replacing QNXT, Facets, or HealthEdge.
- **H2:** Cloud Health Office deploys beside your core admin system. It serves the
  CMS-0057-F APIs now, then takes over claims domains when you are ready.

**Three tiles (business language only):**
1. Compliance in weeks, not a multi-year replatform
2. Your cloud, your data, inspectable source
3. 1,000,000-claim adjudication evidence, published with limits

**Four-step picture:** Intake → Adjudicate / authorize → Pay → Prove (FHIR + audit)

**Who this is for:**
- Medicare Advantage / Medicaid / CHIP plans facing Jan 1, 2027
- CIOs who will not sign a core replacement this budget cycle
- Architects who want source and customer-owned cloud

**CTAs:** Primary — Talk about a deployment · Secondary — Get the evaluator pack
(register) · Tertiary — Run it locally / view source on GitHub

---

## Is
- A payer administration platform (claims, benefits, eligibility, prior auth, payments, FHIR)
- Deployed in the plan's Azure / AWS / GCP
- Built to sit beside Facets, QNXT, or HealthEdge
- Source-available (BSL 1.1) so security teams can read the code

## Is not
- A clearinghouse (Availity / Change / Stedi are pipes; Cloud Health Office is the plan's system)
- A hosted black-box SaaS that takes PHI out of the plan's boundary by default
- An overnight Facets/QNXT replacement
- Apache-licensed open source

---

## Allowed proof (always with the limit)
**Million Claim Challenge:** 1,000,000 deterministic synthetic claims on local Docker
Desktop Kubernetes at 155.89 claims/sec, zero dead letters, published artifacts.
This is local engineering evidence, not a production-cloud capacity claim.

---

## Software + services positioning (locked)

**Positioning line:** Payer interoperability software and implementation expertise for health
plans navigating CMS-0057-F, core administration modernization, and complex vendor ecosystems.

**Core message:** Software when a payer needs software. Professional services when a payer
needs expertise. A flexible deployment model when a payer needs control. Often all three.

> Cloud Health Office remains a product company. Professional Services is how the product
> reaches a payer environment intact — never a repositioning as a consultancy. The homepage
> hero stays product-led; services appear lower on the page.

**Services one-liner:** Cloud Health Office Professional Services helps payer teams plan,
deploy, integrate, and operate Cloud Health Office — and make sound architecture decisions
even when the right answer includes other systems.

**Services tagline:** Independent advice. Practical implementation. Software when it fits.

**Independence sentence (reuse verbatim):** A health plan does not need to purchase or deploy
Cloud Health Office in order to engage Professional Services. Cloud Health Office technology is
recommended only where appropriate.

**Continuity sentence:** When Cloud Health Office is the right fit, our product and services
teams can help move from assessment to implementation without losing architectural continuity.

## Coexistence language (locked — use instead of competitive framing)
- Cloud Health Office is designed to work alongside the systems health plans already depend on.
- We help connect modern interoperability requirements to existing payer operations.
- Cloud Health Office can complement core administration platforms, clearinghouses,
  utilization-management systems, provider portals, and implementation partners.
- Our goal is not to replace every system. It is to make the overall payer technology
  environment more capable, adaptable, and easier to modernize.
- We work alongside existing vendors while independently evaluating architecture, scope,
  assumptions, dependencies, and alternatives from the health plan's perspective.

Never frame Cognizant, TriZetto, QNXT, Facets, HealthEdge, Availity, clearinghouses, or system
integrators as enemies or inferior alternatives. Never claim certification by, affiliation with,
endorsement by, or an implementation partnership with any named vendor.

## Deployment & operating models (locked status labels)

| Model | Status label | Never say |
| --- | --- | --- |
| Payer-controlled cloud deployment | **Available** | — |
| Local evaluation | **Available** | — |
| Aurelianware-managed deployment | **By engagement** | "standard managed service", published SLAs |
| Hybrid / shared responsibility | **By engagement** | "standard package" |
| Aurelianware-operated SaaS | **Under evaluation** | "available", "coming in <date>", "production SaaS" |

**Framework sentence:** The right operating model depends on your organization's cloud
standards, security requirements, engineering capacity, procurement process, and desired level
of operational control.

Never suggest that a payer-controlled deployment is automatically easier to sell, or that SaaS
is automatically better.

## Hedged language for capabilities not yet demonstrated publicly
Use: *designed to support · can help with · provides capabilities for · intended to integrate
with · can be deployed according to · subject to implementation scope and environment
requirements · design goal · supported pattern · planned capability.*

## Sensitive-data warning (every form and the assistant)
Do not submit PHI, member data, patient data, claim data, production credentials, security
secrets, or other sensitive production information through this form. Technical support goes to
enterprise@cloudhealthoffice.com, not a public marketing form.

---

## Payer operations & core administration (locked)

**Positioning line:** Technical and operational expertise for maintaining, validating, troubleshooting, and
modernizing payer operations.

**Boundary sentence (reuse verbatim):** This is not business process outsourcing, an outsourced claims
department, or staffing. The work is payer technology — configuration, validation, architecture, and
operational problem solving — performed alongside your team, not instead of it.

**Product loop:** Solve the immediate payer problem. Identify the repeatable pattern. Automate the
repeatable pattern in Cloud Health Office.

Never promise a recovery amount, a payment-accuracy rate, or any financial outcome. Never imply we supply
proprietary fee schedules, licensed code sets, or contractual reimbursement data. Never publish or expose
payer-confidential reimbursement information. Never say Cloud Health Office replaces contracted repricing
networks, provider contracts, or third-party pricing services. Productization candidates are described as
candidates under evaluation, never as shipped features.

## cms-0057-f.com (companion property)

| Property | Primary purpose | Search intent |
| --- | --- | --- |
| **cms-0057-f.com** | Educational and regulatory discovery. What the rule requires, CRD/DTR/PAS, architecture guidance, checklists, decision frameworks, coexistence with existing cores | Informational |
| **cloudhealthoffice.com** | Authoritative commercial destination. Software, deployment, pricing, Professional Services, assessments, engagement, support, lead capture, future assistant escalation | Commercial |

Detailed service descriptions, engagement information, conversion flows, and lead capture live **only** on
cloudhealthoffice.com. cms-0057-f.com carries contextual CTAs that link to the corresponding service anchor
here — never a duplicated services site.

**Disclosure sentence (reuse verbatim):** cms-0057-f.com is operated by Aurelianware, Inc., the same company
behind Cloud Health Office.

**Approved cross-site CTA blocks** (for use on cms-0057-f.com; each links to the matching anchor here):

1. *Need help applying this to your payer environment?* — Cloud Health Office Professional Services provides
   CMS-0057-F architecture assessments and implementation guidance for health plans. **Request an assessment →**
   `/services#offer-assessment`
2. *Have a CMS-0057-F vendor proposal or SOW already?* — Get an independent technical review of the
   architecture, scope, assumptions, dependencies, and vendor responsibilities. **Request a technical review →**
   `/services#offer-sow-review`
3. *Running QNXT, Facets, HealthEdge, or another payer core?* — Talk with a payer architect about how
   CMS-0057-F can be implemented alongside your existing environment. **Discuss your architecture →**
   `/services#offer-core-admin`

Keep cms-0057-f.com technically credible and evidence-based even where Cloud Health Office is not required to
solve the problem being discussed. Thin promotional content on that property defeats its purpose.

---

## BANNED phrases on public marketing pages
- open source
- founding client / founding client program / we are selecting one
- we are pre-pilot
- production SaaS live / multi-tenant at scale (unless a real paying tenant exists)
- 24,000+ clones
- 7–14 day adjudication vs <500ms (apples-to-oranges)
- strangler-fig (say "replace one domain at a time")
- leading with "36 microservices / 9 engines / dead-letter / pod restarts"
- SaaS is "available" / "launching" (it is **under evaluation**)
- guaranteed compliance / guaranteed savings / guaranteed timeline
- certified by, partnered with, or endorsed by any named vendor
- managed service with SLAs / 24x7 support (not offered as a standard package)
- "we replace your core" as a default framing
- guaranteed recovery / guaranteed payment accuracy / guaranteed financial outcome
- BPO / outsourced claims department / staff augmentation (for Payer Operations)
- claiming we supply proprietary fee schedules, licensed code sets, or contracted reimbursement data
- replacing contracted repricing networks or third-party pricing services
- duplicating Professional Services pages onto cms-0057-f.com

## Replacement dictionary (search → replace)
| Search | Replace with |
| --- | --- |
| open source / open-source | source-available |
| Apache 2.0 / Apache-2.0 | BSL 1.1 (source-available) |
| Founding Client / Founding Partner (as program branding) | (remove; move economics to /deploy "First production deployment terms") |
| we are selecting one / pre-pilot | (remove; use the Stage language only on /deploy) |
| strangler-fig | replace one domain at a time |
| 7–14 days vs <500ms | (remove the apples-to-oranges comparison) |
| 24,000+ clones | (remove) |
| Production SaaS live / multi-tenant at scale | (remove unless a real paying tenant exists) |

---

## Information architecture
Primary nav: **Product · CMS-0057-F · Evidence · Docs · Pricing · Services · Contact**

Pages: `/` · `/what-is` · `/platform` (Product) · `/cms-0057f-compliance` ·
`/evidence` · `/docs` · `/pricing` · `/services` · `/contact` · `/deploy` · `/start`

`/deploy` is the deployment and operating-model page. There is deliberately no separate
`/platform/deployment` page — a second deployment page would be a near-duplicate of `/deploy`.
See [ADR 012](../../docs/adr/012-product-led-professional-services.md).

Founding-client economics live on `/deploy` as **First production deployment terms**
(waived platform license, engineer access, reference). No program branding.
