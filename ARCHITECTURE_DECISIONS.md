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
