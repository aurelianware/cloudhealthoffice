# CloudHealthOffice Documentation

This is the documentation home for CloudHealthOffice. It is organized for a
first-time engineer who needs to understand the platform, run it locally, inspect
the architecture, and find an area to contribute.

## Start Here

| Need | Start with |
| --- | --- |
| Understand the project | [Repository README](../README.md) |
| Run it locally | [Quickstart](guides/QUICKSTART.md) |
| Understand services and data flow | [Architecture](architecture/README.md) |
| Learn payer-domain concepts | [Healthcare domain](domain/README.md) |
| Reproduce benchmark evidence | [Benchmarks](benchmarks/README.md) |
| Deploy beyond local development | [Deployment](deployment/DEPLOYMENT.md) |
| Contribute | [Developer guide](developer/README.md) and [CONTRIBUTING.md](../CONTRIBUTING.md) |

## Getting Started

- [Quickstart](guides/QUICKSTART.md)
- [Feature overview](guides/FEATURES.md)
- [Repository layout](developer/README.md#repository-layout)
- [Running locally](developer/README.md#running-locally)
- [Testing](../tests/README.md)

## Architecture

- [Architecture index](architecture/README.md)
- [System architecture](architecture/ARCHITECTURE.md)
- [Claim adjudication pipeline](architecture/claim-adjudication-pipeline.md)
- [FHIR conformance](architecture/fhir-conformance.md)
- [FHIR endpoint projection](architecture/fhir-endpoint-projection.md)
- [Claim FHIR projection](architecture/claim-fhir-projection.md)
- [Shared message bus](architecture/shared-messagebus.md)
- [Declarative benefit model](architecture/declarative-benefit-model.md)
- [Accumulator service](architecture/accumulator-service.md)
- [Temporal eligibility](architecture/temporal-eligibility.md)
- [Observability](architecture/observability.md)

## Deployment And Operations

- [Deployment guide](deployment/DEPLOYMENT.md)
- [Deployment gates](deployment/DEPLOYMENT-GATES-GUIDE.md)
- [Known deployment gaps](deployment/KNOWN-GAPS.md)
- [Kubernetes infrastructure](../infrastructure/k8s/README.md)
- [Health-check dependency matrix](health-check-dependency-matrix.md)
- [Monitoring and observability](features/MONITORING-AND-OBSERVABILITY.md)

## Healthcare Domain

- [Domain index](domain/README.md)
- Claims
- Benefits
- Provider networks
- Pricing
- Authorizations
- Eligibility
- Members and employers
- Appeals
- Accumulators

The domain index is intentionally written for software engineers who are new to
healthcare payer systems.

## Compliance And Interoperability

- [CMS-0057-F readiness matrix](compliance/CMS-0057-F-READINESS-MATRIX.md)
- [CMS-0057-F compliance guide](features/CMS-0057-F-COMPLIANCE.md)
- [FHIR integration](features/FHIR-INTEGRATION.md)
- [Patient Access API](features/PATIENT-ACCESS-API.md)
- [Prior Authorization API](features/PRIOR-AUTHORIZATION-API.md)
- [HIPAA compliance matrix](features/HIPAA-COMPLIANCE-MATRIX.md)

## Benchmarks

- [Benchmark index](benchmarks/README.md)
- [Million Claim Challenge podcast series](million-claim-challenge/podcast/README.md)
- [100K local Kubernetes result](million-claim-challenge/podcast/episode-008/article.txt)
- [100K benchmark results](million-claim-challenge/podcast/episode-008/benchmark-results.txt)
- [Pended-claim validation](million-claim-challenge/pend-validation.md)

## Developer Guide

- [Developer guide](developer/README.md)
- [Coding standards](developer/coding-standards.md)
- [Debugging guide](developer/debugging.md)
- [CI/CD overview](developer/ci-cd.md)
- [Testing guide](../tests/README.md)

## Architecture Decisions

- [Architecture decision index](../ARCHITECTURE_DECISIONS.md)
- [ADR directory](adr/)
- [Argo vs Airflow](adr/001-argo-vs-airflow.md)
- [Kafka vs NATS](adr/002-kafka-vs-nats.md)
- [pyx12 library](adr/003-pyx12-library.md)
- [Remove Logic Apps](adr/004-remove-logic-apps.md)
- [Kubernetes-first runtime](adr/005-kubernetes-first.md)
- [Persistence boundaries](adr/006-persistence-boundaries.md)
- [Blazor/Razor portal](adr/007-blazor-portal.md)
- [FHIR R4 projections](adr/008-fhir-r4-projections.md)
- [X12 remains first-class](adr/009-x12-first-class.md)
- [Event evidence before full event sourcing](adr/010-event-evidence-not-full-event-sourcing.md)
- [Separate rules, scoring, and claims](adr/011-rules-and-evidence-model.md)

## Roadmap

- [Public roadmap](roadmap/README.md)
- [Claims phase 2 backlog](roadmap/claims-phase-2-backlog.md)

## Documentation Maintenance

- Keep claims factual and dated when they rely on benchmark results.
- Label planned capabilities as planned.
- Do not include PHI, production credentials, real member data, or real claim
  data in docs or screenshots.
- Prefer Mermaid for diagrams that should render in GitHub.
- Add new major docs to this index.
