# Architecture

This section maps CloudHealthOffice from system-level shape to service-level
responsibilities. The architecture is Kubernetes-first, API-first, and oriented
around auditable healthcare workflows.

CloudHealthOffice is source-available under BSL 1.1. The diagrams below describe
the repository architecture and current direction; they are not a production
deployment guarantee.

## Architecture Map

| Topic | Start here |
| --- | --- |
| System architecture | [ARCHITECTURE.md](ARCHITECTURE.md) |
| Claims processing pipeline | [claim-adjudication-pipeline.md](claim-adjudication-pipeline.md), [ADJUDICATION-PIPELINE.md](ADJUDICATION-PIPELINE.md) |
| FHIR integration | [fhir-conformance.md](fhir-conformance.md), [fhir-endpoint-projection.md](fhir-endpoint-projection.md), [claim-fhir-projection.md](claim-fhir-projection.md) |
| X12 processing | [837 claims pipeline](../features/837-CLAIMS-PIPELINE.md), [834 enrollment](../features/834-IMPLEMENTATION-SUMMARY.md), [276/277](../features/276-277-IMPLEMENTATION-COMPLETE.md), [277 value-adds](../features/VALUEADDS277-README.md) |
| Event flow | [shared-messagebus.md](shared-messagebus.md) |
| Authorization flow | [Prior Authorization API](../features/PRIOR-AUTHORIZATION-API.md), [authorization request](../features/AUTHORIZATION-REQUEST.md), [authorization inquiry](../features/AUTHORIZATION-INQUIRY.md) |
| Sentinel and rules | [declarative-benefit-model.md](declarative-benefit-model.md), [claim-ai-examination.md](claim-ai-examination.md), [claim-scrubbing-pipeline.md](claim-scrubbing-pipeline.md) |
| Benefit administration | [declarative-benefit-model.md](declarative-benefit-model.md), [benefit-plan-adapter-pattern.md](benefit-plan-adapter-pattern.md), [plan-versioning.md](plan-versioning.md) |
| Accumulators | [accumulator-service.md](accumulator-service.md), [family-accumulator-models.md](family-accumulator-models.md) |
| Multi-tenant model | [network-as-organization.md](network-as-organization.md), [SFTP multi-tenant architecture](SFTP-MULTI-TENANT-ARCHITECTURE.md), [multi-tenant SaaS architecture](../features/MULTI-TENANT-SAAS-ARCHITECTURE.md) |
| Deployment architecture | [deployment guide](../deployment/DEPLOYMENT.md), [Kubernetes microservices architecture](../features/KUBERNETES-MICROSERVICES-ARCHITECTURE.md) |
| Observability | [observability.md](observability.md), [monitoring guide](../features/MONITORING-AND-OBSERVABILITY.md) |

## Overall Platform

```mermaid
flowchart TB
    Users["Operators, engineers, and trading partners"] --> Portal["Operations Portal"]
    Users --> Api["Service APIs"]
    Users --> Fhir["FHIR R4 APIs"]
    Clearinghouses["Clearinghouses and EDI partners"] --> X12["X12 pipelines"]

    Portal --> Claims["claims-service"]
    Portal --> Authz["authorization-service"]
    Portal --> Benefits["benefit-plan-service"]
    Portal --> Members["member-service"]
    Portal --> Providers["provider-service"]

    Api --> Claims
    Api --> Authz
    Api --> Benefits
    Fhir --> Claims
    Fhir --> Members
    Fhir --> Providers
    X12 --> Claims
    X12 --> Members

    Claims --> Engines["Benefit, fee, NCCI, COB, scrub, accumulator engines"]
    Authz --> PriorAuth["Prior-auth rule engine"]
    Benefits --> Engines
    Claims --> Store["Operational stores"]
    Members --> Store
    Providers --> Store

    Claims --> Events["Event bus / Kafka-ready topics"]
    Authz --> Events
    Members --> Events
    Events --> Evidence["Audit, telemetry, benchmark evidence"]
```

## Claims Lifecycle

```mermaid
sequenceDiagram
    participant Source as Claim Source
    participant Claims as claims-service
    participant Validate as Scrub and validation
    participant Benefits as Benefit engine
    participant Pricing as Fee/NCCI/COB engines
    participant Persist as Persistence
    participant Evidence as Run evidence

    Source->>Claims: Submit synthetic or operational claim
    Claims->>Validate: Normalize and validate
    Validate->>Benefits: Resolve plan and coverage context
    Benefits->>Pricing: Apply pricing, edits, COB, accumulators
    Pricing->>Claims: Paid, denied, pended, or failed result
    Claims->>Persist: Write claim status and financials
    Claims->>Evidence: Publish timing, outcome, scoring metadata
```

## FHIR API Interactions

```mermaid
sequenceDiagram
    participant Consumer as FHIR consumer
    participant Fhir as fhir-service
    participant Domain as Domain service
    participant Store as Domain store
    participant Terminology as Terminology service

    Consumer->>Fhir: FHIR R4 request
    Fhir->>Domain: Fetch domain resource or projection source
    Domain->>Store: Read tenant-scoped state
    Store-->>Domain: Domain model
    Domain-->>Fhir: Projection source
    Fhir->>Terminology: Optional code display/crosswalk lookup
    Terminology-->>Fhir: Code metadata
    Fhir-->>Consumer: FHIR resource or OperationOutcome
```

## Claim Adjudication Detail

```mermaid
flowchart TB
    Intake["Claim intake"] --> Normalize["Normalize claim"]
    Normalize --> Eligibility["Eligibility and coverage"]
    Eligibility --> Benefits["Benefit resolution"]
    Benefits --> Edits["Scrub, NCCI, MUE, prior-auth, COB edits"]
    Edits --> Pricing["Allowed amount and payment calculation"]
    Pricing --> Accumulators["Accumulator update"]
    Accumulators --> Status["Paid, denied, pended, or failed"]
    Status --> Persist["Persist claim and financial projections"]
    Status --> Evidence["Publish run evidence and audit metadata"]
```

## Service Dependencies

```mermaid
flowchart LR
    Portal["Portal"] --> Claims["claims-service"]
    Portal --> WorkQueues["work queues"]
    Claims --> Benefits["benefit-plan-service"]
    Claims --> Coverage["coverage-service"]
    Claims --> Members["member-service"]
    Claims --> Providers["provider-service"]
    Claims --> Terminology["terminology-service"]
    Claims --> Engines["adjudication engines"]
    Benefits --> Accumulators["accumulator-service"]
    Authz["authorization-service"] --> Attachments["attachment-service"]
    Fhir["fhir-service"] --> Claims
    Fhir --> Members
    Fhir --> Providers
```

## Event Processing

```mermaid
sequenceDiagram
    participant Producer as Service producer
    participant Bus as Event bus
    participant Consumer as Service consumer
    participant Audit as Audit/evidence store

    Producer->>Bus: Publish domain event
    Bus->>Consumer: Deliver by topic and consumer group
    Consumer->>Consumer: Process idempotently
    Consumer->>Audit: Persist handling result
    Consumer-->>Bus: Commit offset or route failure
```

## Authorization Flow

```mermaid
flowchart LR
    Request["Authorization request"] --> AuthService["authorization-service"]
    AuthService --> Rules["PriorAuthRuleEngine"]
    AuthService --> Attachments["attachment-service"]
    AuthService --> Benefits["benefit-plan-service"]
    Rules --> Decision["Approve, deny, pend, request information"]
    Decision --> Fhir["FHIR prior-authorization projection"]
    Decision --> Events["Authorization events"]
    Events --> Portal["Portal review queues"]
```

## Event Flow

```mermaid
flowchart LR
    Claims["claims-service"] --> Topics["Kafka-ready topics"]
    Authz["authorization-service"] --> Topics
    Enrollment["enrollment-import-service"] --> Topics
    Payment["payment-service"] --> Topics
    Topics --> Consumers["Service consumers"]
    Topics --> Audit["Audit and observability"]
    Topics --> Evidence["Mass adjudication run evidence"]
```

## Deployment Topology

```mermaid
flowchart TB
    Dev["Developer workstation"] --> Docker["Docker Desktop Kubernetes"]
    Docker --> Namespace["cloudhealthoffice namespace"]
    Namespace --> PortalPod["Portal pod"]
    Namespace --> ServicePods["Service pods"]
    Namespace --> Jobs["Benchmark and workflow jobs"]
    Namespace --> Datastores["Mongo/PostgreSQL-compatible stores and caches"]
    Namespace --> Observability["Logs, health checks, run summaries"]
```

## Tenant Isolation

```mermaid
flowchart LR
    Tenant["Tenant context"] --> Auth["Authentication and authorization"]
    Auth --> Services["Tenant-aware services"]
    Services --> Data["Tenant-scoped data access"]
    Services --> Events["Tenant-scoped event metadata"]
    Services --> Portal["Tenant-aware portal views"]
```

## Extension Points

- Add service adapters around existing payer systems instead of coupling portal
  workflows directly to legacy databases.
- Add adjudication rules through engine-specific tests and deterministic claim
  fixtures.
- Add FHIR projections from persisted domain models with explicit conformance
  documentation.
- Add X12 workflows as parse, validate, normalize, persist, and publish stages.
- Add benchmark scenarios through the generator, answer key, validator, and
  evidence docs together.
