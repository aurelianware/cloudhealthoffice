# ADR 010: Event Evidence Before Full Event Sourcing

## Status

Accepted

## Context

Claims and payer workflows need auditability, replayable evidence, operational
telemetry, and reliable asynchronous processing. Full event sourcing across all
domains would add a large modeling and migration burden.

## Decision

CloudHealthOffice should publish durable event evidence and audit trails for
important workflows before adopting full event sourcing as a universal
persistence pattern.

## Consequences

Positive:

- Benchmark and operational evidence can be inspected without requiring every
  service to rebuild state from events.
- Services can publish meaningful events while retaining practical persistence
  models.
- Future event-sourced domains can be introduced where the value justifies the
  complexity.

Tradeoffs:

- Event logs are not automatically the source of truth for every aggregate.
- Replay semantics must be documented per workflow.
- Consumers must not infer full event sourcing from the presence of events.

## References

- [Shared message bus](../architecture/shared-messagebus.md)
- [Observability](../architecture/observability.md)
- [Benchmarks](../benchmarks/README.md)
