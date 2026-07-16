# ADR 009: X12 Remains First-Class Beside FHIR

## Status

Accepted

## Context

FHIR is essential for modern interoperability, but payer operations still depend
heavily on X12 transactions: 837 claims, 834 enrollment, 270/271 eligibility,
276/277 claim status, 278 authorization, 835 remittance, and attachments.

## Decision

Keep X12 first-class beside FHIR. Do not treat X12 as a temporary import format
or hide it behind FHIR-only abstractions.

## Consequences

Positive:

- Clearinghouse and trading-partner workflows remain explicit.
- Claims, enrollment, remittance, and authorization workflows can preserve EDI
  control numbers and acknowledgements.
- FHIR and X12 can be reconciled through domain models and projections.

Tradeoffs:

- The platform must maintain both FHIR and X12 expertise.
- Documentation must identify which workflows are FHIR-facing, X12-facing, or
  both.

## References

- [837 claims pipeline](../features/837-CLAIMS-PIPELINE.md)
- [834 implementation summary](../features/834-IMPLEMENTATION-SUMMARY.md)
- [276/277 implementation](../features/276-277-IMPLEMENTATION-COMPLETE.md)
- [pyx12 ADR](003-pyx12-library.md)
