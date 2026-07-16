# ADR 008: FHIR R4 Projections

## Status

Accepted

## Context

CMS-0057-F and healthcare interoperability require FHIR R4 APIs, but payer
administration domains such as claims, authorizations, eligibility, benefits,
and payments have internal workflows that are broader than FHIR resources alone.

## Decision

Expose FHIR R4 through projections from domain models rather than making FHIR
the only internal persistence schema.

## Consequences

Positive:

- Internal services can keep workflow-specific models.
- FHIR conformance work remains explicit and testable.
- Projection gaps can be documented without blocking internal domain evolution.

Tradeoffs:

- Projection code needs clear ownership.
- FHIR docs must distinguish implemented resources from planned resources.
- Consumers should not assume every internal workflow has a complete FHIR
  projection yet.

## References

- [FHIR conformance](../architecture/fhir-conformance.md)
- [FHIR endpoint projection](../architecture/fhir-endpoint-projection.md)
- [Claim FHIR projection](../architecture/claim-fhir-projection.md)
- [CMS-0057-F readiness matrix](../compliance/CMS-0057-F-READINESS-MATRIX.md)
