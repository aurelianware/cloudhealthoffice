# Cloud Health Office — Positioning

**Audience:** internal teams, partners, and evaluators deciding how Cloud Health Office fits into their roadmap. Payer-facing pitch materials should derive from this document and not contradict it.

**Last updated:** July 2026

## Summary

Cloud Health Office is a cloud-native, Kubernetes-native, multi-tenant platform for healthcare claims administration. We engage customers across four product lines, each a coherent offering on its own, each connecting naturally to the next:

- **Public Tools** — free utilities (fee schedule lookup, free-tier claims repricing) that establish credibility and serve as the top of the commercial funnel.
- **Transactional Services** — per-call APIs (claims repricing, pricing API) available via self-serve subscription. Developer- and integrator-accessible.
- **Managed Data Services** — subscription services for data that changes constantly (state Medicaid compliance updates, CMS fee schedule updates, provider verification, terminology). Recurring value, recurring revenue.
- **Platform Engagement** — payer-scale relationships priced per member per month (PMPM). Three layers: Compliance Accelerator (Layer 1), Progressive Modernization (Layer 2), Full CAPS Platform (Layer 3). Multi-year contracts, multi-million-dollar deals.

Customers enter at any product line and expand over time. We don't ask customers to commit to one product line. We meet them where they are and let them expand.

## Vision

Our goal is to build the best claims administration platform in healthcare. Not the cheapest add-on, not just a compliance checkbox — the platform that a health plan CTO looking at the next ten years would actually want to be on. We are transparent about the gap between that goal and today's reality, and we're closing it deliberately.

The rest of this document is the evidence and the honest disclosure that back that goal up.

## Public Tools

### What we offer

Free, no-signup-required healthcare data utilities accessible directly from cloudhealthoffice.com. Currently:

- **Fee Schedule Lookup** — search Medicare RBRVS, OPPS APC, and MS-DRG rates for any procedure or diagnosis code, with full RVU breakdown.
- **Free-tier Claims Repricing** — claims repricing against Medicare fee schedules, with no credit card required for the free tier.

### Purpose

Public Tools serve three commercial purposes:

1. **Credibility demonstration** — prospects can verify that our calculation engines produce correct results before any commercial conversation begins.
2. **SEO and discovery** — structured-data-optimized utility pages generate inbound traffic from billing companies, small plans, and developers evaluating healthcare-tech tooling.
3. **Top-of-funnel conversion** — free-tier users encounter paid Transactional Services naturally as usage grows.

### What's shipped today

- `/claims-repricing` — Medicare claims repricing tool with professional CMS-1500, institutional UB-04, and dental ADA support.
- `/docs/fee-schedule-engine` — fee schedule lookup tool.
- **Free-tier authentication path** — not yet wired end-to-end. See [Customer-surface activation gap](#customer-surface-activation-gap-honest-disclosure) in Transactional Services for status.

### Commercial shape

Free. No commercial relationship required. Usage metering is in place for free-tier enforcement; consumer-grade signup (Google SSO or email + password) is the outstanding gap to fully activate the conversion funnel from Public Tools to paid Transactional Services.

## Transactional Services

### What we offer

Per-call and per-month subscription APIs for specific healthcare calculations and operations. Designed for developers integrating into billing systems, small plans, TPAs, clearinghouses, and provider-side tooling vendors.

Currently productized:

- **Claims Repricing API** — paid tiers above the free tier.
- **Pricing API** — programmatic access to the claims repricing engine for batch and streaming use cases.

Additional Transactional Services are technically feasible given the existing engines (prior-auth decision as a service, eligibility as a service, X12 validation, NCCI edit checks) and will be productized as demand emerges.

### Purpose

Transactional Services deliver specific healthcare calculations dramatically cheaper and with substantially better developer experience than incumbent alternatives. They also serve as a production-scale validation surface for the same calculation engines that power Platform Engagement — every transactional customer running claims through the repricing API exercises the FeeScheduleEngine under real-world conditions.

### What's shipped today

- **Claims Repricing API** — full engine, Stripe tier definitions in place, landing page at cloudhealthoffice.com, SEO-optimized.
- **Rate metering and quota enforcement** — infrastructure in place; enforces free-tier and paid-tier boundaries.

### Customer-surface activation gap (honest disclosure)

Transactional Services have a known gap between "product built" and "customers onboarded." The engines work, the API endpoints respond, the tier definitions are in place, the landing pages are live. The outstanding work is:

- Consumer-grade authentication path (currently Microsoft Entra ID multi-tenant, which requires enterprise-tenant configuration incompatible with self-serve developer signup).
- End-to-end API key provisioning flow.
- Stripe-integrated checkout beyond tier definition.
- Usage telemetry beyond rate-metering.

This is engineering work with a known scope and no unresolved product questions — it is sequenced as a fast-follow behind the positioning-documentation foundation work.

### Commercial shape

Self-serve Stripe subscription. Monthly or annual billing. Free tier → paid tier progression driven by usage. No sales motion required for initial acquisition; inside sales engages for enterprise-scope customers who outgrow the published tiers.

## Managed Data Services

### What we offer

Subscription services for healthcare data that changes constantly and is expensive to track manually. Currently addressable:

- **State Medicaid Compliance Updates** — Texas TMPPM, Florida AHCA/SMMC, and additional state Medicaid programs as demand materializes. Delivered as structured, queryable data with per-tenant override capability.
- **CMS Fee Schedule Updates** — quarterly RBRVS, OPPS, MS-DRG, NCCI edits delivered via API and bulk download with diff-aware changesets.
- **Provider Verification** — composite integrity scoring across NPPES, OIG/LEIE, PECOS, CAQH, and state licensure boards.
- **Terminology Service** — SNOMED ↔ CPT ↔ ICD mapping, FHIR ConceptMap and `$translate` operations for commercial integration use cases.

### Purpose

Managed Data Services convert CHO's data-ingestion and normalization infrastructure into a recurring-revenue product line. Healthcare payers currently pay consultants, commercial vendors, or internal staff six-figure-plus amounts per year to track data that CHO already ingests, normalizes, and serves via API.

For customers who are not yet ready for Platform Engagement, Managed Data Services provide immediate recurring value. For customers who are on a Platform Engagement path, these services are included.

### What's shipped today

- **TMPPM ingestion pipeline** — PDF extraction with hybrid regex + LLM confidence scoring, tenant-scoped persistence in Cosmos DB. Tooling and test corpus in place at `tools/CloudHealthOffice.TmppmIngestionService/`.
- **Provider Verification Engine** — multi-source integrity scoring shipped as `src/engines/CloudHealthOffice.ProviderVerificationEngine/`.
- **Terminology service infrastructure** — FHIR ConceptMap and `$translate` operation scaffolded via `src/services/CHO.TerminologyService/`.
- **Fee Schedule Engine** — rate resolution across RBRVS / OPPS / MS-DRG / custom fee schedules shipped as `src/engines/CloudHealthOffice.FeeScheduleEngine/`.

### Productization status (honest disclosure)

Managed Data Services capabilities are shipped in code. They are not yet productized as customer-subscribable services with published pricing, ongoing update cadences, SLAs, and subscriber portals. Productization work for each service includes:

- Subscriber signup and billing infrastructure (shares auth work with Transactional Services activation).
- Update delivery cadence commitments (e.g., "TMPPM updates delivered within 7 days of TMHP publication").
- Versioning and change-log discipline.
- Subscriber-facing documentation for each service.

The first Managed Data Service to productize is expected to be either TMPPM updates (strong Texas Medicaid payer demand) or Provider Verification (broadest market applicability); sequencing depends on early pilot demand signals.

### Commercial shape

Per-month or per-quarter subscription. Self-serve for small customers (mirrors Transactional Services billing infrastructure); contracted for enterprise customers with custom update cadences or SLAs. Indicative pricing benchmark: low five figures per year for single-state compliance subscriptions, mid-five-figures for provider verification at scale, pilot-scoped for bundled engagements.

## Platform Engagement

Platform Engagement is the highest-investment product line and the one payers typically evaluate when buying core admin systems. Priced PMPM, relationship-oriented, multi-year contracts, the long-term anchor of customer value.

Platform Engagement engages customers at three layers, each a coherent offering on its own, each containing the previous:

### Layer 1 — Compliance Accelerator

#### What we claim

An evidence-backed CMS-0057-F readiness surface that deploys alongside any existing core admin system (QNXT, HealthEdge, Facets, or home-grown) without touching the customer's adjudication path, then supports customer validation and attestation work.

Layer 1 covers the CMS-0057-F interoperability surface end-to-end:

- Patient Access API
- Provider Directory API
- Prior Authorization (CRD → DTR → PAS)
- Payer-to-Payer Data Exchange
- The four CHO-authored appeal FHIR profiles advertised through the `fhir-service` CapabilityStatement
- SMART-on-FHIR scope enforcement at the resource-type level

Everything ships as a Helm chart into an existing Kubernetes cluster or Azure AKS. Layer 1 is read-mostly from the customer's perspective: the customer's existing core admin remains the system of record for every claim, authorization, and payment.

#### Evidence shipped today

- `fhir-service` exposes `/fhir/r4/metadata` with a CapabilityStatement that advertises the four appeal profiles and the `$cho-appeal-submit` operation (PR #680).
- Four CHO-authored FHIR profiles under `http://fhir.cloudhealthoffice.com/StructureDefinition/` (PR #677):
  - `cho-appeal-task`
  - `cho-appeal-communication`
  - `cho-appeal-document-reference`
  - `cho-appeal-claim-response`
  - Plus supporting extensions, CodeSystems, and ValueSets covering appeal level, line-of-business, attachment type, X12 275 transmission code, and X12 275 control number.
- CRD → DTR → PAS prior-authorization pipeline implemented through `authorization-service`, `PriorAuthRuleEngine`, and the `attachment-service`.
- SMART-on-FHIR scope enforcement handled by `smart-auth-service` and enforced inside `fhir-service` at every resource type.
- Argo-orchestrated X12 ingress workflows for 270/271/275/276/277/278 already drive the data flows that Layer 1 surfaces over FHIR.
- Helm charts under `infrastructure/helm/` for every service; AKS deployment path validated through the existing deployment workflows.

#### Who enters here

A payer locked into QNXT, HealthEdge, Facets, or a comparable legacy core, staring down the CMS-0057-F deadline, who needs a compliance surface that can be stood up in weeks without a platform-rebuild project. The decision-maker is typically an IT leader or compliance officer. The critical constraint is "zero change to how claims are adjudicated today."

#### What Layer 1 deliberately does NOT do

- It does not replace the customer's core admin system.
- It does not adjudicate claims, compute capitation, or cut payment.
- It does not write into the customer's production workflows beyond what the CMS-0057-F read APIs require.
- It does not promise that the underlying CHO services are, at this point, running a top-tier payer's production claim volume.

#### Commercial shape

Small annual subscription, weeks-to-deploy, Kubernetes cluster or Azure AKS target. Price structure and list figures live in the pitch materials, not this document.

### Layer 2 — Progressive Modernization

#### What we claim

A strangler-fig path from the legacy core to CHO. CHO takes over one domain at a time, at the customer's pace, while the system of record remains stable for every domain that hasn't moved yet. Each tenant, for each domain, chooses whether CHO runs in **Augment** mode (shadow — legacy is authoritative, CHO calculates in parallel for comparison) or **Replace** mode (CHO is authoritative, legacy is off the critical path).

The proof point is appeals. Appeals is now a complete, end-to-end CHO domain — ingress, domain model, state machine, audit trail, FHIR façade, outbound events — and the legacy appeals pathway can be switched off for any tenant that is ready.

#### Evidence shipped today

The appeals four-PR sequence — the lighthouse Layer 2 proof point:

- **PR #677** — CHO authored four appeal FHIR profiles and advertised them via the `fhir-service` CapabilityStatement.
- **PR #678** — `appeals-service` modernized to current platform conventions: bespoke appeal domain, explicit state machine, audit trail, field-level encryption for PHI, Kafka event publisher on state transitions.
- **PR #680** — `fhir-service` exposes appeals as `Task` / `Communication` / `DocumentReference` / `ClaimResponse`, plus the `$cho-appeal-submit` operation, through the `IFhirAppealAdapter` interface.
- **PR #681** — X12 275 Kafka consumer in `appeals-service` closing the production ingress chain from Argo-orchestrated 275 ingest through to appeal attachments.

The pattern that enables future domain replacements:

- **Operating Mode (Augment / Replace / Legacy)** — `src/engines/CloudHealthOffice.OperatingMode/` — per-tenant, per-claim-type configuration. `AugmentResult<T>` wraps every calculation with CHO-result, legacy-result, and discrepancies, so shadow mode is safe by construction. See `docs/architecture/OPERATING-MODE.md`.
- **`IFhirDataAdapter`** in `fhir-service` — the abstraction that lets each domain service be rendered as FHIR independently, without forcing all domains to ship their FHIR façade on the same schedule.
- **`CloudHealthOffice.Appeals.Contracts`** — shared NuGet-style project under `src/services/shared/` — the template for cross-service DTO sharing, generalizable to every future domain.
- **Argo workflows for the full X12 ingress set** — 270/271/275/276/277/278/834/837 — so when a domain service is ready to take over, the EDI ingress path is already in place.

#### Who enters here

A payer frustrated with QNXT's license cost, innovation pace, or integration friction, who is afraid of a big-bang platform replacement and has a CTO willing to commit to multi-year modernization only if the risk is contained one domain at a time. Appeals is typically the first domain to move because the surface is narrow, the regulatory clock is loud, and the CHO proof is already shipped.

#### What Layer 2 deliberately does NOT do

- We do not force a timeline. QNXT (or equivalent) remains system of record for any domain not yet migrated.
- We do not claim every domain is equally mature for cutover. Appeals is done; capitation, claims, and others have domain services in the repo but have not been through the same end-to-end re-foundation yet.
- We do not deliver multi-year migration programs as a single fixed-price engagement. Scope and sequence are negotiated per domain.

#### Commercial shape

Multi-year relationship, priced per domain or per member per month, expansion over time. The contract structure is explicitly designed for additive scope — a new domain is an amendment, not a renegotiation.

### Layer 3 — Full CAPS Platform

#### What we claim

A full cloud-native claims administration platform. All core domains exist as CHO services, the adjudication pipeline is orchestrated end-to-end, EDI ingress and egress are wired, and the platform is multi-tenant and Kubernetes-native throughout.

Domains shipped as services:

- **Core admin**: member, coverage, eligibility, personal representative, consent, ID card, sponsor.
- **Claims**: claims, claims scrubbing, claims examiner, encounter, encounter submission.
- **Authorization**: authorization (including CRD/DTR/PAS), attachment, RFAI.
- **Appeals**: appeals-service (fully re-founded).
- **Provider**: provider, provider contracts, provider verification.
- **Financial**: AR, payment, premium billing, capitation, FFS, accumulator, risk adjustment.
- **Platform**: tenant, trading partner, reference data, FHIR, SMART auth, terminology, pricing API, enrollment import, member document.

Engines backing adjudication and rules: PriorAuthRuleEngine, BenefitEngine, RiskAdjustmentEngine, ProviderVerificationEngine, FeeScheduleEngine, ClaimsScrubEngine, CobEngine, EncounterEngine, NcciEngine.

EDI coverage via Argo workflows: 270/271/275/276/277/278/834/837.

#### Evidence shipped today

- 36 services under `src/services/` and 9 adjudication/rules engines under `src/engines/` (PriorAuthRuleEngine, BenefitEngine, RiskAdjustmentEngine, ProviderVerificationEngine, FeeScheduleEngine, ClaimsScrubEngine, CobEngine, EncounterEngine, NcciEngine). The `src/engines/` directory also contains supporting projects (`OperatingMode`, `DocumentStore`, `ProviderEnrollmentService`, `cho-enrollment-wiring`) that are not adjudication engines and are not counted in the "9 engines" figure.
- Argo-orchestrated adjudication workflow (`infrastructure/argo-workflows/claims-adjudication-workflow.yaml`) wiring the pipeline end-to-end.
- Per-tenant multi-tenancy enforced across services, databases (Cosmos DB partitions), secrets (Key Vault namespacing), and Kafka topics.
- Portal under `src/portal/` for operational workflows across tenants, services, and operating modes.
- `fhir-service` CapabilityStatement advertising Cloud Health Office-authored profiles in addition to US Core.
- Observability stack (OpenTelemetry with PHI-scrubbing SpanProcessor, merged in PR #666) applied across services.
- Million Claim Challenge local Kubernetes validation: the latest full 1,000,000-claim asynchronous Service Bus run (episode 16) reached 155.89 claims/sec with 910 ms P95 and 1,205 ms P99 latency. The validator observed 999,878 claims inside its 180-second per-claim window and recorded 122 observation timeouts; post-run evidence found all 1,000,000 claims terminal, 2,000,000 lifecycle events, zero dead letters, zero pod restarts, and no claims-service error logs. The observed payment gate was 19,982/19,982 exact within $0.01. Episode 15 remains the strict zero-platform-failure full-corpus baseline at 123.81 claims/sec. A separate episode 16 raw X12 837P run accepted and terminally adjudicated 100,000/100,000 claims at 199.42 claims/sec end-to-end. These are local validation benchmarks, not production-cloud capacity claims. Part 7 added the portal Mass Adjudication console so run summaries, unsupported rows, mismatches, payment delta, and claim-level evidence are visible inside the product.

#### Who enters here

Three concrete personas — all three are real:

- **(a) MA startup / new entrant**: small, greenfield, no legacy platform, needs claims processing live in 6–9 months. Chose CHO because building a CAPS from scratch costs roughly ten times more than licensing one, and QNXT's seven-figure entry price plus multi-quarter setup is not viable at their stage.
- **(b) Small regional Medicaid plan**: 50K–500K members, on an aging CAPS or home-grown platform, under state-specific compliance pressure. Chose CHO because their existing platform cannot meet new state requirements and a QNXT replacement would be an 18-month multi-million-dollar project they cannot absorb.
- **(c) Mid-market commercial plan finishing Layer 2**: has already migrated appeals, capitation, and other domains through Layer 2 over two to three years. Layer 3 is the milestone where QNXT is turned off entirely. Chose this path because they have already proven CHO in production one domain at a time.

#### What Layer 3 honestly is today

Cloud Health Office is a full cloud-native CAPS platform with a working adjudication path, inspectable benchmark evidence, and an explicit hardening backlog. For new entrants and greenfield deployments, it is ready for customer-owned validation. For established payers, we recommend entering through Layer 1 or Layer 2 and progressing to Layer 3 on your timeline.

Gaps we are closing deliberately:

- **Reference customer**: we are in pre-sales with our first pilot partner. No production customer is running CHO as their system of record yet.
- **Test coverage on some core services**: `claims-service` at roughly 24% line coverage, `provider-service` at roughly 12%, `sponsor-service` at roughly 13%. These are the lowest-covered services in the repo and the most operationally critical for Layer 3 at scale; they are being hardened on a known backlog before pilot-scale deployment.
- **IFhirDataAdapter wiring**: the interface exists and the appeal adapter is real, but several domain adapters are still the mock implementations. Replacing them with typed HTTP clients to the live domain services is in-flight.
- **Portal polish**: functional for operational workflows today, not yet at enterprise-demo-day aesthetic maturity.
- **Correspondence / letter generation**: disposition letters following appeal decisions require a correspondence-service that has not yet shipped. Appeal decisions produce structured Kafka events today that the future correspondence-service will consume; nothing blocks adding it in a dedicated PR sequence.
- **Scale testing**: the platform has not yet been run against a top-tier payer's claim volume (10M+ claims/year). The architecture is designed for it; the proof is a pilot deployment away.

We are transparent about these gaps because a platform sold to payers on its architectural merits has to be honest about what those merits have and haven't yet produced in production. We close them on a timeline we will share with pilot partners.

#### Commercial shape

Larger engagement, multi-year relationship, priced per member per month. This is where long-term customer value lives.

### PMPM pricing framework across all layers

Cloud Health Office's Platform Engagement is priced PMPM across all three layers. Pricing is indicative market-rate within each layer; specific terms are negotiated per pilot. Founding-partner terms are available for first pilot engagements in each layer.

Layer 1 entry pricing is market-rate for CMS-0057-F compliance surfaces. Layer 2 PMPM expands per domain migrated. Layer 3 PMPM aspires to the range of incumbent CAPS platforms as the platform matures and production references accumulate; founding partners receive preferential terms reflecting both the strategic advantage of being first and the cost structure a cloud-native architecture supports.

Specific indicative PMPM ranges and ARR projections are documented in `docs/sales-materials/FINANCIAL-MODEL.md`. Pilot-specific terms are set per engagement.

## Million Claim Challenge

The Million Claim Challenge is a source-available benchmarking asset (BSL 1.1, same as the rest of the Cloud Health Office codebase) that sits across all four product lines as credibility infrastructure.

We generate a stratified synthetic corpus of 1,000,000 healthcare claims — professional CMS-1500, institutional UB-04, dental ADA, and named edge-case scenarios — with pre-computed expected adjudication outcomes. Any claims adjudication engine can be benchmarked against the corpus and scored against the expected outcomes.

Latest Cloud Health Office proof point (episode 16): in local Docker Desktop Kubernetes, Cloud Health Office ran the full 1,000,000-claim corpus through platform-owned asynchronous Service Bus adjudication at 155.89 claims/sec with 910 ms P95 and 1,205 ms P99 latency. The validator scored 129,980/130,000 workflow checks and 19,982/19,982 observed payment comparisons exact within $0.01. It also recorded 122 claims outside its 180-second observation window. Post-run verification found all 1,000,000 claims terminal, 2,000,000 lifecycle events, zero dead letters, zero pod restarts, and no claims-service error logs. Episode 15 remains the strict zero-platform-failure baseline at 123.81 claims/sec. This is intentionally framed as local validation, not a production-cloud benchmark.

The published result should not be overstated. The 122 Episode 16 timeouts measured an observation deadline, not lost claims, but they still caused a nonzero validator exit and left 20 workflow checks plus 18 payment scenarios unreconciled inside the run artifact. The next proof priority is post-window reconciliation that re-scores delayed terminal outcomes without manual MongoDB verification. The separate 100,000-claim raw X12 837P result proves parser-to-persistence plumbing and throughput; because it deliberately repeats one COB-secondary fixture, it is not diverse adjudication-correctness evidence.

### What we offer

- `src/CloudHealthOffice.BenchmarkClaimGenerator/` — full .NET 8 library for parallel corpus generation.
- `/docs/million-claim-challenge` — public landing page at cloudhealthoffice.com describing the benchmark.
- Portal Mass Adjudication console — operator-facing run evidence with claim-level drilldown, validation-status filtering, human-readable MCC claim IDs, and evidence-first sampling for failures, observation failures, mismatches, unsupported rows, and slow claims.
- `docs/million-claim-challenge/podcast/` — repeatable podcast packet workflow for turning Medium articles, pull requests, benchmark results, screenshots, and project context into Adobe Podcast / Acrobat Generate Podcast source material.
- Reference data coverage across procedure codes, diagnosis codes, dental codes, taxonomy codes, modifier sets, revenue codes, network tiers, and benefit plan templates.
- Dedicated Cosmos DB seeder scaffold (`CosmosDbSeeder`) for running the corpus against production-shape infrastructure — provides document-shape and adapter wiring; actual Cosmos DB persistence requires a concrete implementation or separate package (the base `WriteDocumentsAsync` is a no-op stub by design).

### Strategic purpose

Claims adjudication accuracy benchmarking is the hardest part of CAPS platform evaluation. Every incumbent claims 99%+ adjudication accuracy; none can prove it because the industry has no agreed ground-truth corpus.

By publishing the benchmark corpus openly and documenting our own engines' performance against it transparently, we invite the category to adopt a common yardstick. Whether a payer runs the benchmark against Cloud Health Office or against any incumbent CAPS, the results are comparable.

This is not a marketing exercise. It is an industry contribution that — if it gains traction — becomes a defensible competitive position: Cloud Health Office is the steward of the benchmark every other CAPS platform has to score against.

## How the portfolio fits together

Think of the four product lines as a progression of investment and relationship depth:

**Public Tools** sit at the outermost ring. Anyone can use them. They cost nothing; they generate traffic, credibility, and top-of-funnel leads. A developer evaluating healthcare-tech options finds the free fee schedule lookup through search, uses it, bookmarks cloudhealthoffice.com, remembers the brand.

**Transactional Services** sit one ring in. Developers graduate naturally from the free tier when their usage grows or their integration stabilizes. A small-plan IT team using the Claims Repricing API for monthly rate validation has converted from "curious evaluator" to "paying customer" without a sales conversation.

**Managed Data Services** sit further in. A customer paying for TMPPM compliance updates or provider verification has accepted that Cloud Health Office is a trusted source of ongoing data value. The relationship is recurring; the commercial conversation is different.

**Platform Engagement** sits at the center. A Layer 1 customer is running Cloud Health Office alongside their core admin system for CMS-0057-F compliance. A Layer 2 customer has let CHO take over appeals, capitation, or other domains one at a time. A Layer 3 customer has turned off their legacy core and runs Cloud Health Office end-to-end. Each layer contains the previous; each relationship is designed to expand.

Customers move outward-to-inward over time. A free-tier Public Tools user may become a Transactional Services subscriber, then a Managed Data Services subscriber, then a Layer 1 Platform Engagement customer, then Layer 2, then Layer 3. A single customer relationship can span $0 to multi-million ARR over five to seven years.

No customer is locked at any product line. Every contract structure is designed for expansion. The same Cloud Health Office codebase serves all four product lines.

## What this positioning means for existing and prospective customers

### For developers and integrators evaluating healthcare-tech tooling

Public Tools are the entry point. The fee schedule lookup and free-tier claims repricing tool let you verify that Cloud Health Office's calculation engines produce correct results with no signup, no credit card, and no commercial conversation required. When usage grows past the free tier — or when an integration needs guaranteed throughput, an SLA, or programmatic access — Transactional Services pick up where the free tier ends, with self-serve Stripe billing and the same engines behind them.

For integrations that need recurring access to data CHO maintains (state Medicaid rules, CMS fee schedule updates, provider verification, terminology mappings), Managed Data Services are the right fit even when no Platform Engagement is in scope. The activation gap honestly disclosed under Transactional Services applies — consumer-grade signup is the outstanding work between today and full self-serve developer onboarding.

### For small health plans, TPAs, and regional Medicaid plans

Transactional Services and Managed Data Services together cover most operational data needs without requiring a Platform Engagement commitment. A TPA running monthly rate validation against the Claims Repricing API and subscribing to quarterly CMS Fee Schedule Updates is a complete relationship at a few-thousand-dollars-per-month scale; no multi-year contract required.

When CMS-0057-F compliance pressure grows, Layer 1 — Compliance Accelerator — is the natural next step. It deploys alongside the existing core admin system without touching the adjudication path. A small plan that has already proven CHO at the Transactional Services and Managed Data Services tiers has substantially de-risked the Layer 1 conversation: the engines are familiar, the data quality is verified, the commercial relationship exists.

### For established payers on existing core admin systems

Our primary engagement frame for established payers is Layer 2 — Progressive Modernization, starting with appeals plus the CMS-0057-F compliance surface that Layer 1 already delivers. Layer 1 alone is available if a multi-year commitment is premature. Layer 3 is the aspirational end-state story for the CTO conversation, with honest disclosure of the gaps listed above.

Managed Data Services supplement any Platform Engagement layer. A payer on Layer 1 today benefits from CMS Fee Schedule Updates, Provider Verification, and state Medicaid compliance subscriptions even before Layer 2 domain migrations begin. The same data infrastructure powers both.

### For new entrants (MA startups, new-license plans, greenfield MCOs)

Layer 3 entry — full platform, day one. The reference-customer gap is real and is disclosed up front. The qualifying proof points are the architectural merits, the 36-service/9-engine inventory, the Argo adjudication pipeline, and the four-PR appeals re-foundation as evidence that the platform can land a domain cleanly and completely.

### For state Medicaid plans

Layer 2 entry typically, with Layer 3 visible on the horizon. State-specific compliance variations (FL-AHCA-SMMC, Texas TMPPM, and similar) are handled through per-tenant configuration rather than per-customer code forks. State compliance subscriptions (TMPPM for Texas, FL-AHCA for Florida) are also available as standalone Managed Data Services and can supplement any Platform Engagement layer — a state Medicaid plan running on its incumbent core can subscribe to TMPPM updates today and adopt Layer 1 or Layer 2 on its own timeline.

### For strategic acquirers

Cloud Health Office is structured to be intelligible to sophisticated acquirers — consulting firms seeking platform assets, private equity firms evaluating healthcare-IT acquisitions, or strategic buyers consolidating the CAPS category. Every architectural decision record (`docs/adr/`), every service's documentation, every compliance claim, and every operational runbook are written to survive rigorous diligence.

The four-product-line portfolio is designed to present multiple value vectors: Public Tools for brand and traffic, Transactional Services for recurring SaaS metrics, Managed Data Services for high-margin recurring subscriptions, Platform Engagement for multi-year payer contracts with PMPM unit economics. Acquirers evaluating Cloud Health Office can independently assess each product line against their thesis; the pieces are architecturally separable but commercially reinforcing.

Intellectual property is cleanly owned by Aurelianware, Inc., under BSL 1.1 licensing. The repository's commit history establishes ownership and timeline. Operational documentation is maintained on an ongoing basis at the standard an acquirer's diligence team would expect to find.

We are not in an active acquisition process. We are building the platform with the disposition that, if and when a strategic conversation emerges in the normal course of category consolidation, the foundation is ready.

## Canonical Facts

*Last verified: July 2026*

These are the ground-truth numbers for Cloud Health Office as of the most recent verification. Any artifact citing service counts, engine counts, test counts, or documentation volume should reconcile to this section. When these numbers drift from reality, update this section first; derivative artifacts then reconcile to it.

| Metric | Value | Source |
| --- | --- | --- |
| Service projects | 36 | `src/services/*/`, including shared projects such as contracts, infrastructure, and events |
| Adjudication/rules engines | 9 | BenefitEngine, FeeScheduleEngine, NcciEngine, CobEngine, RiskAdjustmentEngine, EncounterEngine, ClaimsScrubEngine, PriorAuthRuleEngine, ProviderVerificationEngine |
| Supporting engine projects | 4 | DocumentStore, OperatingMode, ProviderEnrollmentService, enrollment-wiring |
| Test projects | 45 | `*.Tests.csproj` files |
| Test methods | ~4,100 | xUnit Facts + Theories |
| Production C# lines | ~190,000 | Excluding tests, bin, obj, and migrations |
| Documentation lines | ~108,000 | Markdown and text under `docs/` |
| Pricing framework | PMPM-based, pilot-specific | See FINANCIAL-MODEL.md for indicative ranges |

Verification procedure documented at `scripts/verify-canonical-facts.sh` (to be created in a follow-up PR). Numbers should be re-verified at each major release and when the Canonical Facts section is cited by another artifact.

## Governance

This document is the source of truth for CHO positioning. Every other CHO-facing artifact — site pages, pitch deck, sales materials, compliance docs, technical documentation — should derive from and not contradict this document.

When a claim in another artifact appears stronger than what this document supports, the claim is wrong, not this document. Update this document when CHO's real capabilities change; update derivative artifacts when they drift from this document.
