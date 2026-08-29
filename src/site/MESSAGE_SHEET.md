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
- A clearinghouse (Availity / Change / Stedi are pipes; CHO is the plan's system)
- A hosted black-box SaaS that takes PHI out of the plan's boundary by default
- An overnight Facets/QNXT replacement
- Apache-licensed open source

---

## Allowed proof (always with the limit)
**Million Claim Challenge:** 1,000,000 deterministic synthetic claims on local Docker
Desktop Kubernetes at 155.89 claims/sec, zero dead letters, published artifacts.
This is local engineering evidence, not a production-cloud capacity claim.

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
Primary nav: **Product · CMS-0057-F · Evidence · Docs · Pricing · Contact**

Pages: `/` · `/what-is` · `/platform` (Product) · `/cms-0057f-compliance` ·
`/evidence` · `/docs` · `/pricing` · `/contact` · `/deploy` · `/start`

Founding-client economics live on `/deploy` as **First production deployment terms**
(waived platform license, engineer access, reference). No program branding.
