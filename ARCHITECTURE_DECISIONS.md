# Architecture Decisions

CloudHealthOffice uses architecture decision records (ADRs) for consequential
technical decisions. ADRs should capture context, decision, consequences, and
follow-up work.

## Existing ADRs

| ADR | Decision |
| --- | --- |
| [001](docs/adr/001-argo-vs-airflow.md) | Argo Workflows over Airflow for Kubernetes-native orchestration |
| [002](docs/adr/002-kafka-vs-nats.md) | Kafka as the durable event backbone direction |
| [003](docs/adr/003-pyx12-library.md) | pyx12 for X12 validation support |
| [004](docs/adr/004-remove-logic-apps.md) | Remove Logic Apps as a primary architecture path |
| [005](docs/adr/005-kubernetes-first.md) | Kubernetes-first service and workflow runtime |
| [006](docs/adr/006-persistence-boundaries.md) | Use fit-for-purpose persistence boundaries instead of one database pattern |
| [007](docs/adr/007-blazor-portal.md) | Blazor/Razor for the operations portal |
| [008](docs/adr/008-fhir-r4-projections.md) | FHIR R4 projections over FHIR-as-primary-schema |
| [009](docs/adr/009-x12-first-class.md) | Keep X12 first-class beside FHIR |
| [010](docs/adr/010-event-evidence-not-full-event-sourcing.md) | Event evidence and audit trails before full event sourcing |
| [011](docs/adr/011-rules-and-evidence-model.md) | Keep adjudication rules, benchmark scoring, and marketing claims separate |
| [012](docs/adr/012-product-led-professional-services.md) | Product-led professional services and explicitly labelled deployment/operating models |
| [013](docs/adr/013-cms-0057-f-com-companion-property.md) | cms-0057-f.com as a companion educational property, with a cross-site attribution contract |
| [017](docs/adr/017-mongodb-api-only-persistence.md) | MongoDB API is the only document persistence path (Azure DocumentDB in Azure) |
| [023](docs/adr/023-managed-fhir-store-projection.md) | Publish FHIR projections into the plan's managed FHIR store (Google Cloud Healthcare API first), streaming to BigQuery and Vertex AI |
| [035](docs/adr/035-server-side-cql-for-quality-measures.md) | Run CQL server-side, in batch, for quality measures only (DTR keeps Q-6) |

## ADR Backlog

These decisions are documented across the repo but would benefit from deeper
ADRs:

- Production cloud reference architecture.
- Tenant isolation enforcement model.
- Payment accuracy scoring model beyond comparable clean professional claims.
- Benchmark fixture isolation policy.

## ADR Template

```markdown
# ADR NNN: Title

## Status

Proposed | Accepted | Superseded

## Context

What forces, constraints, and evidence led to this decision?

## Decision

What are we choosing?

## Consequences

What improves, what gets harder, and what must be revisited?

## References

Links to code, docs, issues, PRs, or benchmark artifacts.
```
