# Cloud Health Office Architecture

**Status:** Canonical platform architecture overview
**Last updated:** July 2026
**Audience:** engineering, implementation partners, technical evaluators, and diligence reviewers

Cloud Health Office is a Kubernetes-native, multi-tenant healthcare claims administration platform. It supports payer modernization across four entry points:

- public tools and transactional APIs
- managed healthcare data services
- compliance accelerator deployments alongside existing core admin systems
- progressive modernization toward a full claims administration platform

This document is the current top-level architecture reference. Older Argo/SFTP and attachment-specific designs remain available as deep-dive documents, but they are no longer the best overview of the platform.

## Architecture Principles

| Principle | Implementation |
| --- | --- |
| Multi-tenant by default | Tenant routing through `X-Tenant-ID`, tenant-partitioned data stores, tenant-aware configuration, and tenant-scoped audit signals. |
| Kubernetes-native | Services run as containerized workloads in local Docker Desktop Kubernetes, AKS, EKS, GKE, or customer-managed Kubernetes. |
| Backend agnostic | Core admin integration is adapter-based so CHO can augment or progressively replace QNXT, Facets, HealthEdge, or custom payer platforms. |
| Standards-based | X12 270/271/275/276/277/278/834/835/837, FHIR R4, Da Vinci implementation guides, SMART-on-FHIR scopes, and payer-specific compliance rules. |
| Evidence-led | Benchmark, validation, and operational views separate platform failures, business denials, unsupported scenarios, workflow mismatches, pended outcomes, and payment diagnostics. |
| Replace in layers | Customers can start with compliance APIs or data services, then expand into adjudication, payment, capitation, and full CAPS replacement over time. |

## Deployment Model

Cloud Health Office supports two primary deployment shapes.

### Local Engineering and Validation

Local validation uses Docker Desktop Kubernetes to run the same service boundaries used in cloud environments.

Typical local stack:

- Docker Desktop Kubernetes
- local `cloudhealthoffice` namespace
- claims service, benefit-plan service, member service, coverage service, provider service, and portal
- Million Claim Challenge validator jobs
- local port-forwarding for portal and service inspection

This is the environment used for the Million Claim Challenge local benchmark series. Local benchmark results are useful for repeatability and correctness validation, but they are not production capacity claims.

### Production Cloud

Production deployments are Kubernetes-first and can run on:

- Azure AKS
- AWS EKS
- Google GKE
- customer-managed Kubernetes
- regulated private cloud / on-premises Kubernetes

Common cloud dependencies:

- Azure Service Bus or Kafka-compatible messaging
- MongoDB or Cosmos DB for document persistence
- PostgreSQL for structured reference data where appropriate
- Redis for cache and accumulator acceleration
- object storage for EDI files and audit artifacts
- cloud key management or HashiCorp Vault for secrets
- Prometheus/Grafana, Application Insights, or equivalent observability

## High-Level Platform View

```text
Users / Operators / APIs
        |
        v
Portal, Public Tools, API Gateway, FHIR APIs
        |
        v
Tenant Context + Auth + Rate Limits + Audit
        |
        +-------------------+-------------------+-------------------+
        |                   |                   |                   |
        v                   v                   v                   v
 Claims Service      Benefit Plan Service  Member/Coverage      Provider Service
        |                   |                   |                   |
        +---------+---------+---------+---------+---------+---------+
                  |
                  v
        Adjudication Pipeline and Engines
                  |
    +-------------+-------------+-------------+-------------+
    |                           |                           |
    v                           v                           v
 Payment / Remittance      Work Queues / Pends        Benchmark Evidence
    |                           |                           |
    v                           v                           v
 835 / Finance             Examiner Operations        Mass Adjudication Console
```

## Major Product Surfaces

### Marketing Site and Public Tools

The static site exposes the public product surface:

- CMS-0057-F positioning
- payer and provider solution pages
- claims repricing and pricing API pages
- documentation and quickstarts
- sales/contact capture

Public tools are intended to prove calculation credibility before a commercial relationship exists.

### Portal

The portal is the operator and administrator UI. Current surfaces include:

- dashboard
- claims search and claim detail
- mass adjudication runs
- work queues
- eligibility and enrollment
- authorizations
- provider/member views
- benefit plans
- finance/payment surfaces
- monitoring and compliance views

The Mass Adjudication console is the benchmark evidence surface for the Million Claim Challenge. It shows run history, claims/sec, latency, workflow checks, unsupported scenarios, mismatches, payment delta, and claim-level drilldown.

### APIs and Integration

Cloud Health Office exposes:

- REST APIs for service operations
- FHIR R4 APIs for interoperability and CMS-0057-F surfaces
- X12 ingestion and generation pipelines
- service-specific APIs for pricing, benefit plans, claims, eligibility, authorizations, provider verification, terminology, and payment workflows

## Core Service Domains

The platform is organized around payer operating domains, not one monolithic application.

| Domain | Responsibilities |
| --- | --- |
| Claims | claim submission, status, adjudication orchestration, claim persistence, line/header edits, pends, denials, payment outputs |
| Benefit Plan | benefit rules, accumulator reads/writes, cost sharing, covered/uncovered services, prior-auth-sensitive benefits |
| Member and Coverage | subscribers, dependents, eligibility windows, COB coverage, plan enrollment, PCP assignment |
| Provider | provider identity, NPI, network participation, exclusion checks, verification, credentialing evidence |
| Authorization | prior authorization intake, rule evaluation, PAS/CRD/DTR surfaces, authorization linkage |
| Eligibility | X12 270/271 and FHIR coverage eligibility responses |
| Pricing and Fee Schedules | Medicare/custom fee schedules, repricing, DRG/APC/RBRVS logic, line-level allowed amount calculation |
| Terminology | SNOMED, CPT, ICD, HCPCS, FHIR ConceptMap, `$translate` support |
| Payment and Finance | payment runs, remittance, statements, capitation, premium billing, balances |
| Reference Data | codes, rule tables, state Medicaid policy data, NCCI/MUE data, regulatory reference inputs |

## Claims Adjudication Architecture

Claims adjudication is staged and observable. The pipeline is designed so each stage can be tested, measured, and replaced independently.

Typical adjudication flow:

1. Load claim and tenant context.
2. Normalize claim data and service dates.
3. Resolve member, coverage, plan, and line of business.
4. Resolve provider identity, network participation, and exclusion status.
5. Validate prior authorization requirements and authorization linkage.
6. Apply benefit rules and coverage policy.
7. Apply NCCI/MUE and other line/header edits.
8. Resolve allowed amount through fee schedule, contract, or pricing logic.
9. Calculate cost sharing and plan payment.
10. Persist final status, pend details, denial reasons, and adjudication evidence.

Claims can correctly finish as:

- paid
- denied for a valid business reason
- pended for workflow or downstream review
- unsupported by the current validation path
- failed due to platform error

The architecture deliberately separates valid business denials from platform failures.

Key deep dives:

- [Claim adjudication pipeline](claim-adjudication-pipeline.md)
- [ADJUDICATION-PIPELINE](ADJUDICATION-PIPELINE.md)
- [COB pipeline](claim-cob-pipeline.md)
- [NCCI pipeline](claim-ncci-pipeline.md)
- [Claim submission API](claim-submission-api.md)
- [Claim versioning](claim-versioning.md)
- [Claim remittance generation](claim-remittance-generation.md)

## Benchmark Evidence Architecture

The Million Claim Challenge is not only a load test. It is an evidence system for claims correctness and platform behavior.

Current benchmark evidence captures:

- processed claims
- claims/sec
- P95/P99 latency
- stage timing
- platform failures
- pended observations
- business denial codes
- workflow checks
- workflow mismatches
- unsupported scenarios
- payment delta diagnostics
- claim-level samples prioritized for review

Evidence-first sampling preserves:

1. platform failures
2. observation failures
3. workflow mismatches
4. unsupported scenarios
5. slowest remaining claims for latency triage

The Mass Adjudication console exposes this evidence inside the portal so benchmark claims can be inspected rather than merely reported.

Relevant docs:

- [Million Claim Challenge site docs](../../src/site/docs/million-claim-challenge.html)
- [Episode 006: honest edge-case scoring](../million-claim-challenge/podcast/episode-006/README.txt)
- [Episode 007: operator console evidence](../million-claim-challenge/podcast/episode-007/README.txt)

## Messaging and Events

Messaging is used for asynchronous workflows, integration events, and production decoupling. Depending on deployment target and customer constraints, the architecture supports Azure Service Bus or Kafka-compatible infrastructure.

Common event surfaces:

- claim submitted
- claim version changed
- claim pended
- claim finalized
- payment run created
- remittance generated
- authorization event
- EDI file received or emitted

See [shared message bus](shared-messagebus.md) for the current message bus design.

## Data Architecture

Data is tenant-scoped and domain-owned.

Typical storage model:

- MongoDB / Cosmos DB for document-oriented service data
- PostgreSQL for structured reference datasets where relational access is useful
- Redis for low-latency cache and accumulator workloads
- object storage for EDI files, generated artifacts, audit packets, and long-lived evidence

Tenant isolation is enforced through:

- `X-Tenant-ID` request context
- tenant-aware repositories
- tenant partition keys
- tenant-scoped configuration
- audit records that include tenant context

## Operating Modes

Cloud Health Office can operate in multiple adoption modes.

| Mode | Purpose |
| --- | --- |
| Public tools | Show calculation quality and product entry points without sales friction. |
| Transactional API | Offer specific calculations or operations by API. |
| Managed data service | Deliver continuously changing healthcare reference/compliance data. |
| Compliance accelerator | Deploy CMS-0057-F / FHIR / interoperability layers beside existing core admin. |
| Progressive modernization | Move selected engines or workflows from augment mode into replace mode. |
| Full CAPS platform | Run core claims administration workloads on CHO services. |

The operating model is intentionally incremental. A payer does not need to replace its entire core admin system on day one.

## Security and Compliance

Security goals:

- HIPAA-aligned technical safeguards
- least-privilege service access
- tenant isolation
- PHI-safe logs and telemetry
- secrets managed through cloud key management or Vault
- private networking where required
- auditability for claim, authorization, payment, and EDI events

The platform avoids treating observability as a PHI dumping ground. Logs and benchmark outputs should report operational evidence without exposing production patient data.

## Observability

The architecture expects service-level and workflow-level observability:

- health checks
- structured logs
- request correlation
- stage-level timing
- benchmark run summaries
- claim-level evidence samples
- workflow and EDI operational dashboards
- platform failure and timeout counters

See [observability](observability.md) for service observability guidance.

## Current Gaps and Boundaries

The architecture is intentionally honest about what is not proven yet.

- Local Million Claim Challenge numbers are not production cloud capacity claims.
- Payment amount accuracy is visible as payment delta, but still needs a formal amount-level scoring gate.
- Expected-pend observation proves expected pends persisted as pended, but a full false-pend sweep is future work.
- Live in-progress benchmark telemetry is partially designed but not yet complete.
- Some legacy architecture docs still describe older Argo/SFTP or attachment-specific designs; use this document as the canonical overview.

## Related Architecture Documents

Claims and adjudication:

- [Claim adjudication pipeline](claim-adjudication-pipeline.md)
- [COB pipeline](claim-cob-pipeline.md)
- [NCCI pipeline](claim-ncci-pipeline.md)
- [Claim submission API](claim-submission-api.md)
- [Claim versioning](claim-versioning.md)
- [Claim adjustment workflow](claim-adjustment-workflow.md)

Plan, member, provider, and coverage:

- [Declarative benefit model](declarative-benefit-model.md)
- [Plan versioning](plan-versioning.md)
- [Temporal eligibility](temporal-eligibility.md)
- [Member foundation](member-foundation.md)
- [Network as organization](network-as-organization.md)
- [Provider adapter pattern](provider-adapter-pattern.md)
- [Provider versioning](provider-versioning.md)

Infrastructure and shared platform:

- [Shared cache](shared-cache.md)
- [Shared message bus](shared-messagebus.md)
- [Shared JSON options](shared-json-options.md)
- [Secret rotation](secret-rotation.md)
- [Observability](observability.md)

EDI and historical deep dives:

- [SFTP architecture](SFTP-ARCHITECTURE.md)
- [SFTP multi-tenant architecture](SFTP-MULTI-TENANT-ARCHITECTURE.md)
- [Authorization attachments architecture](../features/AUTHORIZATION-ATTACHMENTS-ARCHITECTURE.md)
- [Kubernetes microservices architecture](../features/KUBERNETES-MICROSERVICES-ARCHITECTURE.md)
- [Multi-tenant SaaS architecture](../features/MULTI-TENANT-SAAS-ARCHITECTURE.md)
