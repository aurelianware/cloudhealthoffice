# Cloud Health Office Architecture

**Status:** Canonical platform architecture overview
**Last updated:** October 2026 (v6.0 "Paths of Glory")
**Audience:** engineering, implementation partners, technical evaluators, and diligence reviewers

Cloud Health Office (CHO) is a source-available, Kubernetes-native, multi-tenant platform for health plan operations. It covers claims administration, CMS-0057-F interoperability APIs, and HIPAA X12 transactions. Payers can adopt it in four ways:

- public tools and transactional APIs
- managed healthcare data services
- a compliance accelerator deployed alongside an existing core admin system (QNXT, Facets, HealthEdge, or custom)
- progressive modernization toward a full claims administration platform

This document is the top-level architecture reference. Each section links to a deeper design document. Older Argo/SFTP and attachment-specific designs remain available as historical deep dives.

## At a Glance

| Area | What CHO provides |
| --- | --- |
| Claims administration | Staged adjudication pipeline: eligibility, provider, prior auth, benefits, NCCI/MUE edits, pricing, cost sharing, payment, and 835 remittance |
| CMS-0057-F APIs | Patient Access, Provider Access, Payer-to-Payer, Prior Authorization (PAS/CRD/DTR), and a public Provider Directory, served as FHIR R4 |
| Authorization | SMART App Launch for users and SMART Backend Services for systems, issued by CHO's own authorization server or a customer IdP |
| Bulk data | FHIR Bulk Data `$export` for Provider Access and Payer-to-Payer, behind a feature flag |
| Real-time X12 | CAQH CORE Connectivity endpoints for 270/271 eligibility and 276/277 claim status |
| Core admin integration | Per-domain adapter contracts so CHO can sit beside or progressively replace an existing core admin system |
| Conformance evidence | Nightly ONC Inferno test kits (US Core, CARIN BB, Plan-Net, Bulk Data) and Da Vinci scenario tests run in CI |

## Architecture Principles

| Principle | Implementation |
| --- | --- |
| Multi-tenant by default | The tenant comes from a claim in a validated access token, never from a header alone. Data stores, configuration, and audit records are tenant-scoped. |
| Kubernetes-native | Services run as containerized workloads on local Docker Desktop Kubernetes, AKS, EKS, GKE, or customer-managed Kubernetes. |
| Backend agnostic | Core admin integration goes through per-domain adapter contracts, so CHO can augment or progressively replace QNXT, Facets, HealthEdge, or custom payer platforms. |
| Standards-based | X12 270/271/275/276/277/278/834/835/837, CAQH CORE Connectivity, FHIR R4, US Core, CARIN Blue Button, Da Vinci PAS/CRD/DTR/PDex/Plan-Net/CDex, SMART on FHIR, and FHIR Bulk Data. |
| Evidence-led | Conformance kits, benchmark runs, and operational views separate platform failures, business denials, unsupported scenarios, workflow mismatches, pended outcomes, and payment diagnostics. |
| Replace in layers | Customers can start with compliance APIs or data services, then expand into adjudication, payment, capitation, and full claims administration over time. |

## High-Level Platform View

```text
 Members, Providers, Payers, Apps          Trading Partners / Clearinghouses
        |                                            |
        v                                            v
 Portal, Public Tools,              FHIR R4 APIs          CAQH CORE real-time
 Public Provider Directory     (CMS-0057-F, SMART)      (270/271, 276/277), X12 batch
        |                            |                            |
        +----------------------------+----------------------------+
                                     |
                                     v
             SMART Auth / Token Service + Tenant Context + Audit
                                     |
     +--------------+--------------+-+------------+--------------+--------------+
     |              |              |              |              |              |
     v              v              v              v              v              v
  Claims      Benefit Plan   Member and     Provider and   Authorization   Eligibility
  Service       Service       Coverage       Network         Service         Service
     |              |              |              |              |              |
     +--------------+--------------+-------+------+--------------+--------------+
                                           |
              +----------------------------+----------------------------+
              |                            |                            |
              v                            v                            v
   Adjudication Pipeline        Core Admin Adapters            Bulk Export Worker
   and Engines                  (CHO, QNXT, Facets,            (Provider Access,
              |                  HealthEdge)                    Payer-to-Payer)
              v
   Payment, 835 Remittance, Work Queues, Benchmark Evidence
```

## Deployment Model

### Local Engineering and Validation

Local validation uses Docker Desktop Kubernetes to run the same service boundaries used in cloud environments:

- a local `cloudhealthoffice` namespace
- claims, benefit plan, member, coverage, provider, FHIR, and portal services
- Million Claim Challenge validator jobs
- local port-forwarding for portal and service inspection

Local benchmark results are useful for repeatability and correctness. They are not production capacity claims.

### Production Cloud

Production deployments are Kubernetes-first and can run on Azure AKS, AWS EKS, Google GKE, customer-managed Kubernetes, or regulated private cloud and on-premises Kubernetes.

Common cloud dependencies:

- Azure Service Bus or Kafka-compatible messaging
- MongoDB wire protocol for document persistence (MongoDB or Azure Cosmos DB for MongoDB)
- PostgreSQL for reference code sets only
- Redis for cache and accumulator acceleration
- object storage for EDI files, bulk export files, and audit artifacts
- cloud key management or HashiCorp Vault for secrets
- Prometheus/Grafana, Application Insights, or equivalent observability

## Services

The platform is organized around payer operating domains. Each service lives under `src/services/` and is deployed as its own container.

| Domain | Services | Responsibilities |
| --- | --- | --- |
| Claims | `claims-service`, `claims-examiner-service`, `encounter-service`, `encounter-submission-service`, `ffs-service` | Claim submission, adjudication orchestration, edits, pends, denials, versioning, encounters, real-time 276/277 claim status |
| Benefit Plan | `benefit-plan-service`, `accumulator-service` | Declarative benefit rules, plan versioning, cost sharing, accumulators |
| Member and Coverage | `member-service`, `coverage-service`, `enrollment-import-service`, `idcard-service`, `member-document-service`, `personal-representative-service`, `consent-service`, `sponsor-service` | Subscribers, dependents, eligibility windows, COB, 834 enrollment, ID cards, representatives, consent and opt-out |
| Provider and Network | `provider-service`, `provider-contracts-service`, `provider-verification-service`, `provider-eligibility-api` | Provider identity, NPI, network participation, contracts, exclusion checks, credentialing |
| Authorization | `authorization-service`, `attachment-service`, `rfai-service` | Prior authorization, 278 and PAS intake, attachments, requests for additional information |
| Eligibility | `eligibility-service` | X12 270/271 (batch and CAQH CORE real-time), FHIR coverage eligibility, clearinghouse adapters |
| Interoperability | `fhir-service` | All FHIR R4 and CMS-0057-F APIs, CDS Hooks, bulk export, the public Provider Directory |
| Identity and Access | `smart-auth-service`, `token-service`, `tenant-service`, `trading-partner-service` | SMART authorization server, service-to-service tokens, tenant registry, trading partner registry |
| Pricing and Reference Data | `CloudHealthOffice.PricingApi`, `CHO.TerminologyService`, `reference-data-service` | Fee schedules, repricing, DRG/APC/RBRVS, terminology and `$translate`, NCCI/MUE and other reference tables |
| Payment and Finance | `payment-service`, `ar-service`, `premium-billing-service`, `capitation-service` | Payment runs, 835 remittance, receivables, premium billing, capitation |
| Operations | `work-management-service`, `appeals-service`, `care-management-service`, `correspondence-service`, `contact-service`, `document-service`, `risk-adjustment-service` | Work queues, appeals and grievances, care management, member and provider letters, contact center, documents, risk adjustment |

Shared libraries live in `src/services/shared/`. `CloudHealthOffice.Infrastructure` holds the cross-cutting pieces every service uses: tenant middleware, authentication, the message bus abstraction, resilient service clients, observability, and the CAQH CORE connectivity processor.

## Identity, Tenancy, and Access

Every API call is authenticated, with one deliberate exception (the public Provider Directory, below).

- **Tenant resolution.** The shared `TenantMiddleware` takes the tenant from a claim in an access token whose signature, issuer, audience, and lifetime have been validated. A token without a tenant claim is rejected. An `X-Tenant-ID` header is accepted only when it matches the token's tenant.
- **SMART authorization server.** `smart-auth-service` implements SMART App Launch (patient and provider apps, consent, launch context) and SMART Backend Services (`client_credentials` with `private_key_jwt` client assertions) for system-to-system access such as Provider Access and Payer-to-Payer bulk export. `fhir-service` publishes `/.well-known/smart-configuration` and a JWKS endpoint.
- **Service-to-service tokens.** `token-service` issues workload tokens between CHO services and can exchange tokens from a customer IdP (for example Entra ID or Cloudflare Access).
- **Customer IdP.** Plans that already run an identity provider can federate it instead of using CHO's login.

Deep dives: [SMART OAuth trust](smart-oauth-trust.md), [IdP integration contract](idp-integration-contract.md), [Consent](consent.md).

## CMS-0057-F Interoperability APIs

`fhir-service` serves the CMS Interoperability and Prior Authorization Final Rule (CMS-0057-F) APIs as FHIR R4 under `/fhir/r4`. It also serves the CMS-9115-F APIs those build on.

| API | What it serves | Implementation guides |
| --- | --- | --- |
| Patient Access | Claims, encounters, clinical data, coverage, and prior authorization decisions for a member's own app | US Core 3.1.1, CARIN Blue Button 2.0.0, PDex |
| Provider Access | The same data for in-network providers, scoped to their attributed members, with member opt-out | PDex, Bulk Data, SMART Backend Services |
| Payer-to-Payer | Member match and data exchange with a member's previous or concurrent payer, single-member and bulk | PDex Payer-to-Payer, HRex member match, Bulk Data |
| Prior Authorization | Coverage requirements discovery, documentation templates, and PA submission and status | Da Vinci CRD (CDS Hooks), DTR, PAS 2.2.1 |
| Provider Directory | The plan's networks, practitioners, locations, and plans, with no login | Da Vinci PDex Plan-Net 1.2.0 |
| Clinical data exchange | Requests for additional documentation between payer and provider | Da Vinci CDex |

Supporting pieces:

- **API usage reporting.** `/api/v1/interoperability/usage` computes the Patient Access usage metrics plans must report to CMS.
- **Data sources.** The FHIR surfaces read CHO's own domain stores (claims, members, providers, plans). A plan running another core admin system populates those stores to expose its data through the APIs.

Deep dives: [FHIR conformance](fhir-conformance.md), [Provider Access](provider-access.md), [Provider Access attribution](provider-access-attribution.md), [Payer-to-Payer](payer-to-payer.md), [Prior authorization](prior-authorization.md), [CDex](cdex-additional-information.md), [Da Vinci interop testing](../interop/davinci.md).

### Bulk Export

FHIR Bulk Data `$export` serves Provider Access (a provider's attributed panel) and Payer-to-Payer bulk exchange.

- Jobs are durable: a job store records state, and a background worker takes a lease on each job so a pod restart does not lose work.
- Output files are written to the shared document store in a separate `bulk-exports` bucket, expire after 24 hours, and are purged after two days.
- The feature is off by default and turned on per deployment with `BulkExport:Enabled`.

### Public Provider Directory

The Provider Directory is public by regulation. It is served at `/fhir/r4/directory/{directory}`, where the directory id maps to a tenant only through explicit per-plan configuration. A plan that has not published its directory has no route.

This is the only place in `fhir-service` where the tenant is not chosen by a validated token, so the route is isolated: any credential on the request is ignored, the caller is treated as anonymous, and only directory resources (Practitioner, PractitionerRole, Organization, Location, HealthcareService, OrganizationAffiliation, InsurancePlan, Endpoint) are served.

Deep dive: [Public provider directory](public-provider-directory.md).

## X12 and CAQH CORE Real-Time Connectivity

CHO ingests and generates X12 in batch (837, 834, 835, 278, 275, 277CA) and answers real-time inquiries over CAQH CORE Connectivity (SOAP 1.2 and MIME):

| Endpoint | Transaction | Owning service |
| --- | --- | --- |
| `/core/eligibility` | 270 request, 271 response | `eligibility-service` |
| `/core/claimstatus` | 276 request, 277 response | `claims-service` |

The envelope handling, submitter authentication (X.509, username and password, or CHO OAuth), TA1 and 999 rejections, response time budgets, and published downtime schedules live in the shared `Edi/Core` processor. Each trading partner is registered to a tenant, and the tenant always comes from that registration. The endpoints are off by default (`CoreConnectivity:Enabled`).

Deep dives: [CAQH CORE connectivity](caqh-core-connectivity.md), [Payer eligibility responder](payer-eligibility-responder.md), [Healthcare transaction gateway](healthcare-transaction-gateway.md), [EDI operations](edi-operations.md).

## Core Admin Adapters

CHO does not require a payer to replace its core admin system. Each domain has its own adapter contract, and a tenant's configuration selects the implementation:

- `IClaimAdapter`, `IBenefitPlanAdapter`, `IProviderAdapter`, `IOrganizationAdapter`, `IIdCardAdapter`, `IEligibilityAdapter`
- CHO-native implementations are complete.
- QNXT, Facets, and HealthEdge implementations are contract stubs. Each vendor connector is built as engagement work under the plan's own vendor license, so licensed vendor specifications stay out of this public repository.
- Eligibility also has clearinghouse adapters (Availity, Change Healthcare).

Deep dives: [Core adapter implementer guide](../integration/CORE-ADAPTER-IMPLEMENTER-GUIDE.md), [ADR 027: core adapter engagement boundary](../adr/027-core-adapter-engagement-boundary.md), [Claim adapter pattern](claim-adapter-pattern.md), [Benefit plan adapter pattern](benefit-plan-adapter-pattern.md), [Provider adapter pattern](provider-adapter-pattern.md).

## Claims Adjudication Architecture

Claims adjudication is staged and observable. Each stage can be tested, measured, and replaced independently.

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

Claims can correctly finish as paid, denied for a valid business reason, pended for workflow or downstream review, unsupported by the current validation path, or failed due to platform error. The architecture deliberately separates valid business denials from platform failures.

Deep dives:

- [Claim adjudication pipeline](claim-adjudication-pipeline.md)
- [ADJUDICATION-PIPELINE](ADJUDICATION-PIPELINE.md)
- [COB pipeline](claim-cob-pipeline.md)
- [NCCI pipeline](claim-ncci-pipeline.md)
- [Claim submission API](claim-submission-api.md)
- [Claim versioning](claim-versioning.md)
- [Claim remittance generation](claim-remittance-generation.md)

## Conformance and Benchmark Evidence

CHO publishes evidence rather than claims.

**Conformance (CI).** The nightly [`davinci-interop.yml`](../../.github/workflows/davinci-interop.yml) workflow starts the CHO stack and runs:

- the ONC Inferno test kits for US Core 3.1.1, CARIN Blue Button 2.0.0, Da Vinci Plan-Net, and Bulk Data 2.0.0 with SMART Backend Services
- Da Vinci scenario tests for PAS `$submit`, CRD, and DTR against pinned external reference implementations

Results are uploaded as sanitized evidence artifacts and published on the [Inferno conformance evidence](../../src/site/evidence/inferno-conformance.html) page. An Inferno pass is not CMS certification.

**Benchmark.** The Million Claim Challenge is an evidence system for claims correctness and platform behavior, not only a load test. It captures claims/sec, P95/P99 latency, stage timing, platform failures, pended observations, business denial codes, workflow mismatches, unsupported scenarios, payment delta diagnostics, and claim-level samples prioritized for review. The Mass Adjudication console in the portal shows each run so claims can be inspected rather than merely reported.

Relevant docs:

- [Million Claim Challenge site docs](../../src/site/docs/million-claim-challenge.html)
- [Episode 006: honest edge-case scoring](../million-claim-challenge/podcast/episode-006/README.txt)
- [Episode 007: operator console evidence](../million-claim-challenge/podcast/episode-007/README.txt)

## Product Surfaces

- **Marketing site and public tools.** Product, CMS-0057-F, and pricing pages, documentation, and public calculation tools that prove calculation quality before a commercial relationship exists.
- **Portal.** The operator and administrator UI: claims search and detail, mass adjudication runs, work queues, eligibility and enrollment, authorizations, provider and member views, benefit plans, finance, and monitoring and compliance views.
- **APIs.** REST APIs per service, FHIR R4 APIs in `fhir-service`, X12 batch pipelines, and CAQH CORE real-time endpoints.

## Messaging and Events

Messaging is used for asynchronous workflows, integration events, and production decoupling. Depending on deployment target, the architecture supports Azure Service Bus or Kafka-compatible infrastructure. Services that must not lose events (claims, appeals) write them to an outbox in the same transaction as the data change.

Common event surfaces include claim submitted, versioned, pended, and finalized; accumulator adjusted; payment run created; remittance generated; authorization events; and EDI files received or emitted.

Deep dives: [Shared message bus](shared-messagebus.md), [Claim event outbox](claim-event-outbox.md), [Appeals event outbox](appeals-event-outbox.md).

## Data Architecture

Data is tenant-scoped and domain-owned.

- MongoDB wire protocol (MongoDB or Azure Cosmos DB for MongoDB) is the supported data path for service data. The native Cosmos SDK path is legacy and opt-in.
- PostgreSQL is used only for reference code sets.
- Redis serves low-latency cache and accumulator workloads.
- Object storage (through the shared document store) holds EDI files, bulk export output, generated artifacts, audit packets, and long-lived evidence.

Tenant isolation is enforced through token-derived tenant context, tenant-aware repositories and partition keys, tenant-scoped configuration, and audit records that carry the tenant.

Deep dives: [Document store](document-store.md), [Mongo write concurrency](mongo-write-concurrency.md), [Shared cache](shared-cache.md).

## Operating Modes

| Mode | Purpose |
| --- | --- |
| Public tools | Show calculation quality and product entry points without sales friction. |
| Transactional API | Offer specific calculations or operations by API. |
| Managed data service | Deliver continuously changing healthcare reference and compliance data. |
| Compliance accelerator | Deploy the CMS-0057-F, FHIR, and CORE real-time layers beside an existing core admin system. |
| Progressive modernization | Move selected engines or workflows from augment mode into replace mode. |
| Full claims administration | Run core claims administration workloads on CHO services. |

A payer does not need to replace its entire core admin system on day one. See [Operating mode](OPERATING-MODE.md).

## Security and Compliance

- HIPAA-aligned technical safeguards
- tenant isolation derived from validated tokens
- least-privilege service access with scoped service-to-service tokens
- PHI-safe logs and telemetry
- secrets managed through cloud key management or Vault, with rotation
- private networking where required
- auditability for claim, authorization, payment, EDI, and SMART authorization events

Observability is not a PHI dumping ground. Logs, CI evidence, and benchmark outputs report operational evidence without exposing patient data.

Deep dives: [Secret rotation](secret-rotation.md), [Service client resilience](service-client-resilience.md).

## Observability

Services emit health checks, structured logs, request correlation, stage-level timing, and metrics (including CORE real-time and interoperability counters), along with benchmark run summaries, claim-level evidence samples, and workflow and EDI dashboards. See [observability](observability.md).

## Current Gaps and Boundaries

This architecture is intentionally honest about what is not proven yet.

- Local Million Claim Challenge numbers are not production cloud capacity claims.
- Inferno and Da Vinci results are CI evidence, not CMS certification. A few CARIN Blue Button checks fail because of a known test kit issue with mixed claim types.
- QNXT, Facets, and HealthEdge adapters are contract stubs until built for a plan. There is no import path yet for claims adjudicated outside CHO.
- CAQH CORE real-time endpoints are not CORE-certified, and some AAA rejection codes (subscriber not found) are not yet returned.
- Bulk export is off by default. The remaining Bulk Data work for the January 2027 API deadlines is planned for v6.2.
- Payment amount accuracy is visible as payment delta but still needs a formal amount-level scoring gate.
- Some legacy architecture docs still describe older Argo/SFTP or attachment-specific designs. Use this document as the canonical overview.

## Related Architecture Documents

Interoperability and identity:

- [FHIR conformance](fhir-conformance.md)
- [Provider Access](provider-access.md)
- [Payer-to-Payer](payer-to-payer.md)
- [Prior authorization](prior-authorization.md)
- [Public provider directory](public-provider-directory.md)
- [SMART OAuth trust](smart-oauth-trust.md)
- [CAQH CORE connectivity](caqh-core-connectivity.md)
- [Healthcare transaction gateway](healthcare-transaction-gateway.md)

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
