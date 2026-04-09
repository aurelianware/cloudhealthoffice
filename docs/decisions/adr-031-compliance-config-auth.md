# ADR-031 — ComplianceConfigController Authorization

**Status:** Pending  
**Date:** April 2026

## Context

The `PUT /api/compliance-config/{tenantId}` endpoint in reference-data-service
upserts `TenantComplianceConfig` documents (FMMIS credentials, prompt pay
deadlines, MPIP flags). This is a sensitive mutation that was initially protected
by `[Authorize(Policy = "AdminPolicy")]`.

The `AdminPolicy` attribute was removed during PR #634 to align with the existing
pattern across other service controllers — none of which enforce per-controller
authorization policies. In this codebase, authorization is handled at the API
Gateway / Azure AD layer rather than per-controller policies. The E2E test seed
calls also bypass auth headers, which would cause test failures under strict
per-endpoint auth.

## Decision

Track this as a security hardening item. Before general availability of the
compliance-config endpoint to external tenants, implement one of:

1. **AdminPolicy on the PUT endpoint** with E2E tests acquiring a test JWT
   from the SMART on FHIR / Azure AD test authority, or
2. **A dedicated internal seeding endpoint** behind `InternalOnlyPolicy`
   (accessible only from within the cluster network), or
3. **Gateway-level route protection** restricting `/api/compliance-config` PUT
   to admin roles at the API Management / ingress layer.

## Consequences

Until resolved, the PUT endpoint is callable by any authenticated tenant.

**Mitigations in place:**
- HTTPS-only transport (enforced at infrastructure level)
- Azure AD authentication required (JWT validation in middleware)
- Audit logging on all mutations (structured logs with tenantId, timestamps)
- Cosmos DB partition key enforcement (tenantId isolation prevents cross-tenant writes)
- The endpoint is internal-facing (not exposed through the public API gateway by default)
