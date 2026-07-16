# ADR 007: Blazor/Razor Operations Portal

## Status

Accepted

## Context

CloudHealthOffice needs an operator console for claims, work queues,
adjudication evidence, and administrative workflows. The portal must integrate
with .NET service models and evolve quickly with backend DTOs.

## Decision

Use Blazor/Razor for the operations portal.

## Consequences

Positive:

- .NET models and service clients can be shared naturally.
- Portal tests can exercise DTO and query behavior close to service contracts.
- Healthcare operations screens can be developed alongside the backend services
  that power them.

Tradeoffs:

- The portal should avoid becoming the only integration surface; APIs remain
  first-class.
- Client-side behavior still needs browser-level verification for important
  workflows.

## References

- `src/portal/CloudHealthOffice.Portal`
- `src/portal/CloudHealthOffice.Portal.Tests`
- [Developer guide](../developer/README.md)
