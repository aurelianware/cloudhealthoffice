# ADR 006: Fit-For-Purpose Persistence Boundaries

## Status

Accepted. Narrowed by [ADR 017](017-mongodb-api-only-persistence.md): document
storage uses the MongoDB API only (Azure DocumentDB in Azure); the native Cosmos
DB SDK path is removed.

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
- Documentation must be explicit when a guide references MongoDB or
  PostgreSQL-like stores. Since ADR 017, "Cosmos DB" in a service guide means
  only Azure DocumentDB / Cosmos DB for MongoDB reached through the MongoDB
  driver.

## References

- [Health-check dependency matrix](../health-check-dependency-matrix.md)
- [Shared cache](../architecture/shared-cache.md)
- [Shared JSON options](../architecture/shared-json-options.md)
