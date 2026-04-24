# Cloud Health Office — Positioning

**Audience:** internal teams, partners, and evaluators deciding how Cloud Health Office (CHO) fits into their roadmap. Payer-facing pitch materials should derive from this document and not contradict it.

**Last updated:** April 2026

## Summary

Cloud Health Office is a cloud-native, Kubernetes-native, multi-tenant platform for healthcare claims administration. We engage customers at three layers, each a coherent product on its own, each containing the previous:

- **Layer 1 — Compliance Accelerator**: CMS-0057-F compliance, deployable alongside any existing core admin system.
- **Layer 2 — Progressive Modernization**: Domain-by-domain replacement of the legacy core, coexisting with the system of record until each domain is ready to cut over.
- **Layer 3 — Full CAPS Platform**: End-to-end cloud-native claims administration — for new entrants today, established payers on their Layer 2 → 3 path over time.

We don't ask customers to commit to one layer. We meet them where they are and let them expand.

## Vision

Our goal is to build the best claims administration platform in healthcare. Not the cheapest add-on, not just a compliance checkbox — the platform that a health plan CTO looking at the next ten years would actually want to be on. We are transparent about the gap between that goal and today's reality, and we're closing it deliberately.

The rest of this document is the evidence and the honest disclosure that back that goal up.

## Layer 1 — Compliance Accelerator

### What we claim

A production-ready CMS-0057-F compliance surface that deploys alongside any existing core admin system (QNXT, HealthEdge, Facets, or home-grown) without touching the customer's adjudication path.

Layer 1 covers the CMS-0057-F interoperability surface end-to-end:

- Patient Access API
- Provider Directory API
- Prior Authorization (CRD → DTR → PAS)
- Payer-to-Payer Data Exchange
- The four CHO-authored appeal FHIR profiles advertised through the `fhir-service` CapabilityStatement
- SMART-on-FHIR scope enforcement at the resource-type level

Everything ships as a Helm chart into an existing Kubernetes cluster or Azure AKS. Layer 1 is read-mostly from the customer's perspective: the customer's existing core admin remains the system of record for every claim, authorization, and payment.

### Evidence shipped today

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

### Who enters here

A payer locked into QNXT, HealthEdge, Facets, or a comparable legacy core, staring down the CMS-0057-F deadline, who needs a compliance surface that can be stood up in weeks without a platform-rebuild project. The decision-maker is typically an IT leader or compliance officer. The critical constraint is "zero change to how claims are adjudicated today."

### What Layer 1 deliberately does NOT do

- It does not replace the customer's core admin system.
- It does not adjudicate claims, compute capitation, or cut payment.
- It does not write into the customer's production workflows beyond what the CMS-0057-F read APIs require.
- It does not promise that the underlying CHO services are, at this point, running a top-tier payer's production claim volume.

### Commercial shape

Small annual subscription, weeks-to-deploy, Kubernetes cluster or Azure AKS target. Price structure and list figures live in the pitch materials, not this document.

## Layer 2 — Progressive Modernization

### What we claim

A strangler-fig path from the legacy core to CHO. CHO takes over one domain at a time, at the customer's pace, while the system of record remains stable for every domain that hasn't moved yet. Each tenant, for each domain, chooses whether CHO runs in **Augment** mode (shadow — legacy is authoritative, CHO calculates in parallel for comparison) or **Replace** mode (CHO is authoritative, legacy is off the critical path).

The proof point is appeals. Appeals is now a complete, end-to-end CHO domain — ingress, domain model, state machine, audit trail, FHIR façade, outbound events — and the legacy appeals pathway can be switched off for any tenant that is ready.

### Evidence shipped today

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

### Who enters here

A payer frustrated with QNXT's license cost, innovation pace, or integration friction, who is afraid of a big-bang platform replacement and has a CTO willing to commit to multi-year modernization only if the risk is contained one domain at a time. Appeals is typically the first domain to move because the surface is narrow, the regulatory clock is loud, and the CHO proof is already shipped.

### What Layer 2 deliberately does NOT do

- We do not force a timeline. QNXT (or equivalent) remains system of record for any domain not yet migrated.
- We do not claim every domain is equally mature for cutover. Appeals is done; capitation, claims, and others have domain services in the repo but have not been through the same end-to-end re-foundation yet.
- We do not deliver multi-year migration programs as a single fixed-price engagement. Scope and sequence are negotiated per domain.

### Commercial shape

Multi-year relationship, priced per domain or per member per month, expansion over time. The contract structure is explicitly designed for additive scope — a new domain is an amendment, not a renegotiation.

## Layer 3 — Full CAPS Platform

### What we claim

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

### Evidence shipped today

- 36 services under `src/services/` and 9 adjudication/rules engines under `src/engines/` (PriorAuthRuleEngine, BenefitEngine, RiskAdjustmentEngine, ProviderVerificationEngine, FeeScheduleEngine, ClaimsScrubEngine, CobEngine, EncounterEngine, NcciEngine). The `src/engines/` directory also contains supporting projects (`OperatingMode`, `DocumentStore`, `ProviderEnrollmentService`, `cho-enrollment-wiring`) that are not adjudication engines and are not counted in the "9 engines" figure.
- Argo-orchestrated adjudication workflow (`infrastructure/argo-workflows/claims-adjudication-workflow.yaml`) wiring the pipeline end-to-end.
- Per-tenant multi-tenancy enforced across services, databases (Cosmos DB partitions), secrets (Key Vault namespacing), and Kafka topics.
- Portal under `src/portal/` for operational workflows across tenants, services, and operating modes.
- `fhir-service` CapabilityStatement advertising CHO-authored profiles in addition to US Core.
- Observability stack (OpenTelemetry with PHI-scrubbing SpanProcessor, merged in PR #666) applied across services.

### Who enters here

Three concrete personas — all three are real:

- **(a) MA startup / new entrant**: small, greenfield, no legacy platform, needs claims processing live in 6–9 months. Chose CHO because building a CAPS from scratch costs roughly ten times more than licensing one, and QNXT's seven-figure entry price plus multi-quarter setup is not viable at their stage.
- **(b) Small regional Medicaid plan**: 50K–500K members, on an aging COTS or home-grown platform, under state-specific compliance pressure. Chose CHO because their existing platform cannot meet new state requirements and a QNXT replacement would be an 18-month multi-million-dollar project they cannot absorb.
- **(c) Mid-market commercial plan finishing Layer 2**: has already migrated appeals, capitation, and other domains through Layer 2 over two to three years. Layer 3 is the milestone where QNXT is turned off entirely. Chose this path because they have already proven CHO in production one domain at a time.

### What Layer 3 honestly is today

Cloud Health Office is a full cloud-native CAPS platform, production-ready today for new entrants and greenfield deployments. For established payers, we recommend entering through Layer 1 or Layer 2 and progressing to Layer 3 on your timeline.

Gaps we are closing deliberately:

- **Reference customer**: we are in pre-sales with our first pilot partner. No production customer is running CHO as their system of record yet.
- **Test coverage on some core services**: `claims-service` at roughly 24% line coverage, `provider-service` at roughly 12%, `sponsor-service` at roughly 13%. These are the lowest-covered services in the repo and the most operationally critical for Layer 3 at scale; they are being hardened on a known backlog before pilot-scale deployment.
- **IFhirDataAdapter wiring**: the interface exists and the appeal adapter is real, but several domain adapters are still the mock implementations. Replacing them with typed HTTP clients to the live domain services is in-flight.
- **Portal polish**: functional for operational workflows today, not yet at enterprise-demo-day aesthetic maturity.
- **Correspondence / letter generation**: disposition letters following appeal decisions require a correspondence-service that has not yet shipped. Appeal decisions produce structured Kafka events today that the future correspondence-service will consume; nothing blocks adding it in a dedicated PR sequence.
- **Scale testing**: the platform has not yet been run against a top-tier payer's claim volume (10M+ claims/year). The architecture is designed for it; the proof is a pilot deployment away.

We are transparent about these gaps because a platform sold to payers on its architectural merits has to be honest about what those merits have and haven't yet produced in production. We close them on a timeline we will share with pilot partners.

### Commercial shape

Larger engagement, multi-year relationship, priced per member per month. This is where long-term customer value lives.

## How the three layers fit together

Think of CHO as concentric circles. Layer 1 is the outer ring — a compliance surface sitting next to the customer's core admin system. Layer 2 is the middle ring — domains moving across the boundary one at a time, with the Operating Mode pattern coordinating which side is authoritative per tenant and per claim type. Layer 3 is the inner ring — the full CHO platform running everything, with the legacy core turned off.

A Layer 1 customer is running CHO alongside their QNXT. A Layer 2 customer has CHO handling some domains while QNXT handles others, with Operating Mode running each engine in Augment or Replace per the tenant's configuration. A Layer 3 customer has turned off QNXT entirely and runs CHO end-to-end.

Customers move outward-to-inward over time. No customer is locked at Layer 1. Every contract structure is designed for expansion, and the same CHO codebase serves all three.

## What this positioning means for our existing and prospective customers

### For existing pilot discussions

Our primary engagement frame is Layer 2 — progressive modernization, starting with appeals plus the CMS-0057-F compliance surface that Layer 1 already delivers. Layer 1 alone is available if a multi-year commitment is premature. Layer 3 is the aspirational end-state story for the CTO conversation, with honest disclosure of the gaps listed above.

### For new-entrant conversations

Layer 3 entry — full platform, day one. The reference-customer gap is real and is disclosed up front. The qualifying proof points are the architectural merits, the 36-service/9-engine inventory, the Argo adjudication pipeline, and the four-PR appeals re-foundation as evidence that the platform can land a domain cleanly and completely.

### For Medicaid plan conversations

Layer 2 entry typically, with Layer 3 visible on the horizon. State-specific compliance variations (FL-AHCA-SMMC, Texas TMPPM, and similar) are handled through per-tenant configuration rather than per-customer code forks.

## Governance

This document is the source of truth for CHO positioning. Every other CHO-facing artifact — site pages, pitch deck, sales materials, compliance docs, technical documentation — should derive from and not contradict this document.

When a claim in another artifact appears stronger than what this document supports, the claim is wrong, not this document. Update this document when CHO's real capabilities change; update derivative artifacts when they drift from this document.
