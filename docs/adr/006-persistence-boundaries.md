# ADR 006: Fit-For-Purpose Persistence Boundaries

## Status

Accepted

## Context

CloudHealthOffice stores different kinds of data: claim state, benefit plan
configuration, reference data, audit records, event metadata, benchmark evidence,
and portal projections. These do not all need the same persistence model.

## Decision

Use fit-for-purpose persistence boundaries instead of forcing every service into
one database pattern. Services should own their persistence decisions and expose
data through contracts rather than shared database access.

## Consequences

Positive:

- Claim and evidence documents can evolve without forcing a relational schema
  for every field.
- Reference data can use relational structures where lookup integrity and joins
  matter.
- Services can migrate storage behind repository boundaries.

Tradeoffs:

- Cross-service reporting requires projections or APIs.
- Repository tests are important because behavior can differ by backing store.
- Documentation must be explicit when a guide references MongoDB, Cosmos DB, or
  PostgreSQL-like stores.

## References

- [Health-check dependency matrix](../health-check-dependency-matrix.md)
- [Shared cache](../architecture/shared-cache.md)
- [Shared JSON options](../architecture/shared-json-options.md)
