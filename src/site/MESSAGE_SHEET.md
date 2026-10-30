# Cloud Health Office — Message Sheet (LOCKED)

This file holds the approved, locked marketing sentences for cloudhealthoffice.com.
**Do not drift.** If a page needs new phrasing, update this sheet first, then the page.
The on-site assistant knowledge pack (`assistant/knowledge.md`) must stay consistent
with this sheet.

---

## Verified capability wording — October 19, 2026

Use the [canonical capability ledger](../../docs/CAPABILITY_RECONCILIATION.md) for status, date, evidence and limits; the wording below applies that evidence and does not maintain a separate readiness assessment.

- PAS submission stores the request and decision before responding; inquiry returns stored outcomes. CHO validates and persists service-line decisions and adverse reasons and projects them into PAS responses.
- CRD unresolved/uncoded inputs and evaluation failures return uncertainty rather than a no-authorization-needed assertion.
- DTR creates, conditional questionnaire updates, response submission, reads, searches and existence checks use MongoDB across pods. Full answer validation, canonical URL/version resolution and complete packages remain incomplete; do not claim full DTR conformance.
- SMART tenant/client-bound launch and patient/encounter token context are implemented; withdrawing an application approval affects refresh. This is not a claim of every SMART response field or revocation mechanism.
- CMS reporting computes standard/expedited counts, rates, mean/median and timeliness. Appeal/extension metrics and complete public reporting remain separate.
- Ordinary CHO authorization workflows enforce transition guards, audit cancellation and expire approvals atomically while preserving clinical outcomes. Tenant-database sweeps and separate reopening/overturn workflows remain outside this scope. See CAP-23 and `docs/operations/AUTHORIZATION-LIFECYCLE.md`.
- CHO receipt-v1 persists authorization receipt/due timestamps and preserves the original clock across additional-information receipt; deployment-specific policy and alerting require validation.

These implementation statements do not assert full PAS/CRD/DTR/SMART conformance, universal compliance, production readiness or customer deployments. Unverified is not equivalent to missing. Preserve precise remaining limitations and reverify changed paths before stronger claims.

## Canonical one-liner
Cloud Health Office is a source-available, FHIR-native health plan administration platform that runs in a tenant dedicated to each plan, in the plan's own cloud or operated for it, so plans keep control of their data and integrations while connecting to every partner they choose.

## Homepage lead — approved by Mark Phillips, October 30, 2026
The homepage hero leads with the CMS-0057-F conformance check. Sovereignty stays the category and the reason to choose Cloud Health Office; the check is the reason to engage now.
**Homepage H1:** Find and close your CMS-0057-F gaps, in your own cloud.
**Homepage supporting:** We test your Patient Access, Provider Access, Provider Directory and Payer-to-Payer APIs with the ONC Inferno test kits and show you what fails and why. Cloud Health Office can then run the fixes in a tenant your plan controls, alongside QNXT, Facets or HealthEdge, with no core replacement required.
**Homepage primary CTA:** Run a conformance check (→ /contact?interest=conformance-check). The readiness review becomes the homepage secondary CTA; the nav CTA is unchanged.
**Conformance check facts:** fixed fee, four weeks, run against the plan's sandbox or test endpoints, no PHI. Kits: US Core, CARIN for Blue Button, Bulk Data with SMART Backend Services, Da Vinci Plan-Net; prior authorization and CMS metrics reviewed by checklist. Never publish the fee. Always say that passing Inferno is not CMS certification or a legal determination of compliance. Never imply Cloud Health Office itself passes a kit unless the published nightly results show it.

## Executive positioning — approved implementation, October 21, 2026
**Category:** Payer data sovereignty. Interoperable, source-available administration.
**Primary:** Sovereign, interoperable health plan administration.
**Supporting:** Your data, under your control, connected to every partner you choose.
**Deployment line:** Your cloud or ours. Your data either way. In your cloud is available; operated in your cloud by Aurelianware is by engagement; hosted by Cloud Health Office (a dedicated tenant for plans without cloud infrastructure or engineering teams) is under evaluation and not offered today. Both run the same source-available software, so a plan's choice need not be permanent; never promise migration terms.
**Thesis line:** Plans pay to modernize. Too often, the vendor keeps what gets built.
**Promise:** Your data. Your infrastructure. Your business rules. Your future.
This promise expresses an aspiration for customer control; it grants no additional ownership, portability or licensing rights.
**Interoperability line:** Interoperability without giving up control. Connect once, in an environment you own.
**Primary CTA:** Book a readiness review (→ /contact?interest=cms0057-assessment).
**Secondary CTA:** Explore the APIs (→ /api-explorer, read-only OpenAPI specifications).
**Developer CTA:** Request a sandbox (→ /contact?interest=sandbox-access). Sandboxes are provisioned per request with synthetic data; never describe a self-serve or always-on sandbox until one exists.
**Audience paths:** Health plans · Technology vendors · Channel and service partners · Investors · Payer architects and engineers (developer hub at /developers).
**License disclosure:** Source-available under BSL 1.1. Production use requires a commercial license.
**Progression:** Foothold → Domain Cutover → Core Modernization. Each expansion requires evidence, acceptance criteria, reconciliation and rollback planning.
The Founding Transformation Partnership is an invitation to scope evaluation, not a claim of existing commitments, discounts, capacity or production rights.

### Naming rule: avoid "independence"
Do not use "independence" as a category, product label, CTA or headline. "Independence" is the brand of a major payer (Independence Blue Cross / Independence Health Group, which also operates a health plan administration business), so the word creates search and brand confusion. Use "sovereignty", "ownership" or "control". Existing URLs that contain the word (for example /health-plan-independence) stay live for link stability; their visible titles and copy do not use it. The Professional Services "independent advice" wording below is unaffected.
Investor-facing copy describes the company, thesis and evidence only; it never states raise terms, amounts or an offer of securities.
Calculator outputs are buyer-entered scenarios, not quotes or guaranteed savings. Unknown alternative licensing/support must block complete totals. AI implementations are advisory; full standards conformance and production outcomes remain unverified.

## CMS-0057-F definition (reuse verbatim under every mention of the rule)
CMS-0057-F is the federal rule that requires Medicare Advantage, Medicaid, CHIP,
and some exchange plans to offer FHIR APIs for patient access, provider access,
payer-to-payer exchange, and prior authorization by January 1, 2027.

## Required capability qualifications

Code includes advisory EDI rejection suggestions and an advisory claims-examiner
path; do not describe them as independently validated autonomous decision-making.
The committed fraud-model asset is a placeholder. Bulk export remains a scaffold;
default FHIR data-provider bindings are mocks. These limits do not negate verified
CHO authorization persistence or every other FHIR/AI path. Use CAP-14, CAP-18,
CAP-19 and CAP-22 for their exact evidence and scope.

## License sentence
Source-available under BSL 1.1. Evaluate and run locally for free. Production use
requires a commercial license. Deploy in your cloud or as a managed tenant.

## Stage language (only on /deploy or in the assistant after the visitor asks "who is live?")
Cloud Health Office provides public source and evidence for evaluation beside
your existing core. Production use requires a scoped integration, readiness
review and commercial license; customer deployment references must be verified
with the commercial owner before disclosure.
> Never put this sentence in the H1.

## Commercial posture for Layer 1 (do not headline "free CMS-0057-F")
The CMS-0057-F API implementations ship in the repo. A production Layer 1 deploy in
the customer's cloud is discussed as a low-friction entry (license terms can be
waived for an early production tenant). Implementation, core mapping, prior-auth
workflow, and Platform Engagement (claims / payments / core replacement) are paid.

---

## Homepage copy (approved October 21, 2026)
Use the executive positioning above. Preserve CMS-0057-F/FHIR metadata intent and technical acquisition paths. Homepage narrative: sovereign interoperable hero; evidence strip; why sovereignty (data, integrations, modernize at your pace); interoperability hub; CMS-0057-F; incremental modernization; developer path; four ways to work with us; founder; evidence; services; contact. Services remain accessible below the product-led hero.


## Is
- A payer administration platform (claims, benefits, eligibility, prior auth, payments, FHIR)
- Deployed in the plan's Azure / AWS / GCP
- Built to sit beside Facets, QNXT, or HealthEdge
- Source-available (BSL 1.1) so security teams can read the code

## Is not
- A clearinghouse; the platform is intended for payer administration and can integrate with clearinghouses.
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

**Services-neutrality sentence (reuse verbatim):** A health plan does not need to purchase or deploy
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

## QNXT CMS-0057-F adapter scoping (locked) — `/services/qnxt-cms-0057-f-adapter`

**H1:** Scoping a QNXT-to-CMS-0057-F adapter.

**Subhead:** The four required APIs — Patient Access, Provider Access, Payer-to-Payer, and Prior
Authorization — are due January 1, 2027, and most of the data behind them already lives in QNXT. The hard
part is getting it out cleanly.

**Process:** questionnaire → 90-minute discovery workshop → fixed-scope SOW. Read path first (Patient
Access, Provider Access, Payer-to-Payer, metrics); Prior Authorization write-back is phase 2.

**Credibility line:** Scoped by an architect with 25+ years in payer core systems, nearly two decades of
them on QNXT.

**Gate:** the page shows one or two sample questions per section (A–I). The full questionnaire is sent by
email after the form is submitted and is never published on the site.

Never state or imply a finished, packaged, or production QNXT adapter, or a customer running one. An
adapter is per-implementation scoping and build work. No compliance guarantee, certification, or vendor
partnership. "Cognizant-hosted" appears only as a neutral hosting option in the questionnaire.

## Dental plans (locked) — `/services/dental-payers`

**H1:** Dental plans have the same 2027 FHIR deadline.

**Subhead:** Medicaid, CHIP, and Medicare Advantage dental plans owe the same four APIs as the large medical
plans — Patient Access, Provider Access, Payer-to-Payer, and Prior Authorization — by January 1, 2027.
Cloud Health Office deploys beside the dental claims system you already run, so you can meet the deadline
without replacing it.

**Scope line:** In scope = Medicaid / CHIP dental managed care and Medicare Advantage dental. Delegated DBMs
depend on the contract. Exchange stand-alone dental plans: "confirm with counsel" (exempted from CMS-9115-F;
never state their 0057-F status as settled). Commercial dental is not in scope.

**Process:** dental scoping call and questionnaire → fixed-scope SOW → CMS-0057-F APIs beside the current
core → core administration only when the plan chooses.

**Both sides:** Cloud Dental Office (Aurelianware's dental practice platform) may be cited as how we test
what payers send against what dental offices need. Never imply Cloud Dental Office customers are payer
customers.

Never claim a finished dental benefits engine. The platform accepts 837D claims; tooth/surface edits and
frequency/age limits are scoped and configured per plan. No named dental plan customers, no compliance
guarantee, no ADA/NADP or vendor endorsement.

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
- invented founding customer commitments, discounts or program capacity
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
| Founding Transformation Partnership | Structured evaluation invitation only; commercial terms require agreement |
| we are selecting one / pre-pilot | (remove; use the Stage language only on /deploy) |
| strangler-fig | replace one domain at a time |
| 7–14 days vs <500ms | (remove the apples-to-oranges comparison) |
| 24,000+ clones | (remove) |
| Production SaaS live / multi-tenant at scale | (remove unless a real paying tenant exists) |

---

## Information architecture
Primary nav (one shared nav on every page, applied by `scripts/sync-site-chrome.mjs`): **Solutions · Platform · Interoperability · Developers · Evidence · Company · Book a readiness review**. Services, Pricing, Trust and Contact sit in the dropdowns and the shared footer.

Pages: `/` · `/what-is` · `/platform` (Product) · `/cms-0057f-compliance` ·
`/evidence` · `/docs` · `/pricing` · `/services` · `/contact` · `/deploy` · `/start`

`/deploy` is the deployment and operating-model page. There is deliberately no separate
`/platform/deployment` page — a second deployment page would be a near-duplicate of `/deploy`.
See [ADR 012](../../docs/adr/012-product-led-professional-services.md).

Founding-client economics live on `/deploy` as **First production deployment terms**
(waived platform license, engineer access, reference). Existing terms remain on /deploy; the partnership invitation confers no new terms.
