# Claims V1 API Surface

**Status:** Canonical reference (Claims Phase 1 close, May 2026)
**Scope:** All HTTP endpoints exposing claim-lifecycle operations
across `claims-service` and the customer-facing portion of
`payment-service`.

This document is the canonical API surface reference for Claims
Phase 1. For the architecture behind each capability, see the
per-capability docs cross-linked below. For operational narrative,
see [`docs/architecture/claims-phase-1-closer.md`](../architecture/claims-phase-1-closer.md).

The endpoint shape detail (request bodies, response schemas) is
sourced from the OpenAPI / Swagger specs served by each service in
development at:

- claims-service: `https://<claims-host>/swagger`
- payment-service: `https://<payment-host>/` (Swagger at root)

Both services register Swagger via the shared
`AddChoInfrastructure` / direct `AddSwaggerGen` paths respectively.
See [Phase 2 backlog item 10.4](../roadmap/claims-phase-2-backlog.md#104--payment-service-migration-to-addchoinfrastructure-swagger)
for the planned pattern-parity follow-up.

---

## Surface index

| Service | Controller | Verbs | Section |
|---------|-----------|-------|---------|
| claims-service | ClaimsController (versionless surface; hosts the `[Obsolete]` legacy `POST /api/claims`) | 22 | [Claims (versionless)](#claims-controller-versionless) |
| claims-service | ClaimsV1Controller (canonical V1 submission + member-scoped FHIR-shaped search) | 2 | [ClaimsV1 (canonical V1)](#claimsv1-controller-canonical-v1) |
| claims-service | ClaimAdjustmentsController | 4 | [Adjustments](#claim-adjustments-controller) |
| claims-service | FhirExplanationOfBenefitController | 2 | [FHIR EOB](#fhir-explanation-of-benefit-controller) |
| claims-service | AdminMigrationController | 2 | [Admin migration (operator-only)](#admin-migration-controller-operator-only) |
| payment-service | PaymentRunsController | 6 | [PaymentRuns](#payment-runs-controller) |
| payment-service | ReversalRunsController | 6 | [ReversalRuns](#reversal-runs-controller) |
| payment-service | EraEnvelopesController | 3 | [EraEnvelopes](#era-envelopes-controller) |

**Total: 8 controllers / 47 verbs.**

The `payment-service.PaymentsController` (older payment-domain
surface, 9 verbs) is **out of scope for the Claims V1 surface** — it
serves the broader payment domain and is documented separately in
the payment-service docs.

---

## Cross-cutting concerns

### Authentication & tenant isolation

All endpoints below require authenticated tenant context unless
explicitly marked otherwise.

- **Tenant resolution:** Via `TenantMiddleware` — header or JWT
  claim extracted at the request boundary; flowed to repository
  queries.
- **AuthN/AuthZ:** Bearer token; JWT validation per service config.
- **Cross-service contracts:** HTTP-only via typed `HttpClient` in
  consuming services. No shared DLL contracts for synchronous
  request/response.

### Idempotency

Several endpoints implement explicit idempotency:

- **`FinalizeAsync` / `VoidAsync`** (5.10, 5.12b) — dual-emit
  pattern: first call performs the state transition + emits Kafka;
  subsequent calls are no-ops.
- **`PaymentRun.Execute` / `ReversalRun.Execute`** — re-runnable;
  partial failures aggregate into per-run warnings rather than
  failing the run.
- **Adjustment creation** — Mongo unique index on
  `(TenantId, ClaimVersionId)` enforces depth=1 chain semantics.

### Status code conventions

- `200 OK` — successful read or non-creating mutation
- `201 Created` — successful create
- `202 Accepted` — long-running batch initiation (PaymentRun /
  ReversalRun execute)
- `204 No Content` — successful mutation with no body
- `400 Bad Request` — validation failure
- `401 Unauthorized` — missing / invalid auth
- `403 Forbidden` — tenant context mismatch
- `404 Not Found` — resource not found in tenant scope
- `409 Conflict` — state-transition violation, idempotency conflict,
  unique-index conflict (e.g., depth>1 adjustment)
- `422 Unprocessable Entity` — claim shape valid but rejects
  business validation
- `500 Internal Server Error` — unexpected service error
- `503 Service Unavailable` — downstream resolution-client failure
  (rare; cached resolution clients absorb most)

---

## claims-service

Base URL: `https://<claims-host>`.

### Claims controller (versionless)

`Route("api/claims")` — versionless CRUD + status surface that
predates the canonical `/api/v1/claims` submission path. Hosts the
single `[Obsolete]` legacy submission endpoint plus all the
versionless lifecycle endpoints (status, pend, adjudication,
remittance, void, work-queue, etc.).
Capability source: 5.3 (legacy submission deprecation), 5.4, 5.5,
5.6, 5.7, 5.8, 5.9, 5.10, 5.12a, 5.12b.
See [claim-submission-api.md](../architecture/claim-submission-api.md),
[claim-adjudication-pipeline.md](../architecture/claim-adjudication-pipeline.md),
[claim-remittance-generation.md](../architecture/claim-remittance-generation.md),
[claim-adjustment-workflow.md](../architecture/claim-adjustment-workflow.md).

| Verb | Path | Purpose | Status / Idempotency | Capability |
|------|------|---------|----------------------|------------|
| POST | `/api/claims` | Legacy claim submission | **`[Obsolete]`** — use `POST /api/v1/claims` (canonical V1). Adds `Deprecation: true` and `Link: </api/v1/claims>; rel="successor-version"` response headers per RFC 8594. Routes through the same `IClaimSubmissionService` so the audit chain stays continuous. | 5.3 |
| GET | `/api/claims/recent` | Recent claims for tenant | Read-only | 5.3 |
| GET | `/api/claims/{id}` | Get claim by id | Read-only | 5.3 |
| GET | `/api/claims/number/{claimNumber}` | Get claim by claim number | Read-only | 5.3 |
| GET | `/api/claims/search` | Query-string search | Read-only | 5.3 |
| POST | `/api/claims/search` | Body-shaped search (advanced) | Idempotent search | 5.3 |
| PUT | `/api/claims/{id}/status` | Update claim status | State-transition; rejects illegal transitions | 5.5 |
| PUT | `/api/claims/{id}/pend` | Pend claim with reason | State-transition | 5.5 |
| PUT | `/api/claims/{id}/adjudication` | Set adjudication result | State-transition; emits `ClaimVersionAdjudicated` | 5.5 |
| PUT | `/api/claims/{id}/ai-examination` | Set AI examination result | State-transition; persists `AiExamination` projection | 5.9 |
| GET | `/api/claims/{id}/ai-examination/audit` | AI examination audit history | Read-only | 5.9 |
| POST | `/api/claims/{id}/ai-examination/agreement` | Operator agreement / disagreement on AI advisory | Idempotent per-operator-action | 5.9 |
| POST | `/api/claims/{id}/remittance` | Per-claim finalize + 835 generation (also the cross-service `FinalizeAsync` target invoked by `PaymentRun`) | **Idempotent dual-emit** — emits `claims.finalized.v1` once | 5.10 |
| POST | `/api/claims/{id}/void` | Void claim (used by ReversalRun) | **Idempotent dual-emit** — emits `ClaimVersionVoided` once | 5.12b |
| GET | `/api/claims/summary` | Tenant-level summary metrics | Read-only | 5.3 |
| GET | `/api/claims/{id}/277ca` | Generate / retrieve 277CA acknowledgment | Pull-shaped (event-driven is Phase 2 — see backlog 6.3) | 5.3 |
| DELETE | `/api/claims/{id}` | Soft-delete claim | Operator-only; tenant-scoped | 5.3 |
| GET | `/api/claims/accumulator-totals` | Member-year accumulator totals | Read-only | 5.5 |
| GET | `/api/claims/work-queue/summary` | Work-queue summary by reason | Read-only | 5.5 |
| GET | `/api/claims/work-queue/items` | Work-queue items list | Read-only | 5.5 |
| POST | `/api/claims/work-queue/{claimId}/assign` | Assign to examiner | Idempotent assignment | 5.5 |
| POST | `/api/claims/work-queue/{claimId}/override` | Operator override (Pend → Approved) | State-transition; persists override audit | 5.5 |

> **22 verbs total**, including the legacy `[Obsolete]` `POST /api/claims`
> at the top of the table.
>
> **Cross-service finalize note.** `PaymentRun.FinalizeAsync(claimId)`
> in payment-service calls into this controller's
> `POST /api/claims/{id}/remittance` (capability 5.10); the response
> is the canonical idempotent dual-emit. There is no separate
> `/finalize` route — `/remittance` is the finalize entry point.

### ClaimsV1 controller (canonical V1)

`Route("api/v1/claims")` — **canonical V1 surface** for claim
submission and member-scoped search.
Capability source: 5.3. See
[claim-submission-api.md](../architecture/claim-submission-api.md).

| Verb | Path | Purpose | Status | Capability |
|------|------|---------|--------|------------|
| POST | `/api/v1/claims` | Submit claim (canonical) | **Canonical V1** — accepts `AdapterClaim` (vendor-neutral DTO from 5.2); orchestrates validation + submission + version-event emission via `IClaimSubmissionService`; returns 201 with the created claim version | 5.3 |
| GET | `/api/v1/claims` | Member-scoped FHIR-shaped search powering the portal Member Details Claims tab | Read-only; projects through `IClaimAdapter` (5.2) and `IExplanationOfBenefitProjector` to FHIR R4 EOB; response shape `EobSearchResponse` | 5.3 |

> **Stability posture.** This is the canonical V1 surface for
> claim submission. Wire shape (`AdapterClaim` request body) stays
> stable as the internal `Claim` domain model evolves. The legacy
> versionless `POST /api/claims` on `ClaimsController` is `[Obsolete]`
> and routes through the same `IClaimSubmissionService` so callers
> on either path produce a continuous audit chain; legacy removal
> is post-pilot, after trading-partner integrations have migrated.

### Claim Adjustments controller

(No class-level `Route` attribute; routes specified per-action.)
Capability source: 5.12a. See
[claim-adjustment-workflow.md](../architecture/claim-adjustment-workflow.md).

| Verb | Path | Purpose | Idempotency | Capability |
|------|------|---------|-------------|------------|
| POST | `/api/v1/claims/{predecessorClaimId}/adjustments` | Create adjustment | Mongo unique index on `(TenantId, ClaimVersionId)` enforces depth=1 | 5.12a |
| GET | `/api/v1/claims/{predecessorClaimId}/adjustments` | List adjustments for predecessor | Read-only | 5.12a |
| GET | `/api/v1/adjustments` | Tenant-wide adjustments search | Read-only | 5.12a |
| GET | `/api/v1/adjustments/{id}` | Get adjustment by id | Read-only | 5.12a |

### FHIR ExplanationOfBenefit controller

`Route("fhir")` — FHIR R4 read surface.
Capability source: 5.11. See
[claim-fhir-projection.md](../architecture/claim-fhir-projection.md).

| Verb | Path | Purpose | Auth | Capability |
|------|------|---------|------|------------|
| GET | `/fhir/ExplanationOfBenefit/{id}` | FHIR read | **Authenticated, tenant-scoped** | 5.11 |
| GET | `/fhir/ExplanationOfBenefit` | FHIR search (minimal params) | **Authenticated, tenant-scoped** | 5.11 |

> **Phase 2 deferrals on this surface:** `_history`, `_lastUpdated`,
> `_include`, `_revinclude`, broader search-param set,
> unauthenticated CMS-0057-F access. See
> [Phase 2 backlog Section 2 + 3](../roadmap/claims-phase-2-backlog.md#2-fhir-completeness)
> and [CMS-0057-F readiness](../compliance/claims-cms-0057-f-readiness.md).

### Admin Migration controller (operator-only)

`Route("api/v1/admin/claims/cosmos-migration")` — operator-driven
Cosmos partition migration.
Capability source: 5.1b. See
[claim-versioning.md](../architecture/claim-versioning.md) and
[claims-cosmos-partition-migration.md](../migrations/claims-cosmos-partition-migration.md)
(operator runbook).

| Verb | Path | Purpose | Idempotency | Capability |
|------|------|---------|-------------|------------|
| POST | `/api/v1/admin/claims/cosmos-migration/run` | Execute migration batch | Idempotent — replay-safe via per-document idempotency keys | 5.1b |
| GET | `/api/v1/admin/claims/cosmos-migration/status` | Migration progress / status | Read-only | 5.1b |

> **Operator-only.** Not part of the customer-facing V1 surface. Used
> by SREs during the cutover from `/memberId`-partitioned `Claims`
> container to `/tenantId`-partitioned `ClaimsV2`. Final deletion of
> the legacy container is tracked as
> [Phase 2 backlog item 10.1](../roadmap/claims-phase-2-backlog.md#101--old-claims-cosmos-container-final-deletion).

---

## payment-service

Base URL: `https://<payment-host>`.

### Payment Runs controller

`Route("api/PaymentRuns")` — operator-initiated batched 835 payment
workflow.
Capability source: 5.10. See
[claim-remittance-generation.md](../architecture/claim-remittance-generation.md).

| Verb | Path | Purpose | Idempotency | Capability |
|------|------|---------|-------------|------------|
| POST | `/api/PaymentRuns` | Create payment run (no execute) | Creates draft aggregate | 5.10 |
| POST | `/api/PaymentRuns/execute` | Create + execute in one call | Idempotent on retry — partial failures → per-run warnings | 5.10 |
| POST | `/api/PaymentRuns/{id}/execute` | Execute existing draft run | Same idempotency posture | 5.10 |
| GET | `/api/PaymentRuns/{id}` | Get payment run | Read-only | 5.10 |
| GET | `/api/PaymentRuns` | List payment runs (tenant-scoped) | Read-only | 5.10 |
| POST | `/api/PaymentRuns/{id}/cancel` | Cancel run (only pre-execute) | State-transition | 5.10 |

### Reversal Runs controller

`Route("api/ReversalRuns")` — operator-initiated batched 835
reversal workflow.
Capability source: 5.12b. See
[claim-reversal-run.md](../architecture/claim-reversal-run.md).

| Verb | Path | Purpose | Idempotency | Capability |
|------|------|---------|-------------|------------|
| POST | `/api/ReversalRuns` | Create reversal run (no execute) | Creates draft aggregate | 5.12b |
| POST | `/api/ReversalRuns/execute` | Create + execute in one call | Idempotent retry — partial failures → per-run warnings | 5.12b |
| POST | `/api/ReversalRuns/{id}/execute` | Execute existing draft run | Same idempotency posture | 5.12b |
| GET | `/api/ReversalRuns/{id}` | Get reversal run | Read-only | 5.12b |
| GET | `/api/ReversalRuns` | List reversal runs | Read-only | 5.12b |
| POST | `/api/ReversalRuns/{id}/cancel` | Cancel run (only pre-execute) | State-transition | 5.12b |

### ERA Envelopes controller

`Route("api/v1/era-envelopes")` — 835 envelope retrieval. Output of
PaymentRun and ReversalRun execution.
Capability source: 5.10, 5.12b. See
[claim-remittance-generation.md](../architecture/claim-remittance-generation.md),
[claim-reversal-run.md](../architecture/claim-reversal-run.md).

| Verb | Path | Purpose | Auth | Capability |
|------|------|---------|------|------------|
| GET | `/api/v1/era-envelopes/{id}` | Get envelope metadata + segments | Authenticated, tenant-scoped | 5.10, 5.12b |
| GET | `/api/v1/era-envelopes/{id}/edi` | Get raw 835 EDI string | Authenticated, tenant-scoped | 5.10, 5.12b |
| GET | `/api/v1/era-envelopes` | List envelopes (filterable by run / partner / date) | Authenticated, tenant-scoped | 5.10, 5.12b |

> **Transmission posture:** Phase 1 generates and persists envelopes;
> retrieval is REST-only. sFTP / Availity transmission is
> [Phase 2 backlog item 8.1](../roadmap/claims-phase-2-backlog.md#81--sftp--availity-transmission-of-generated-835-envelopes).

---

## OpenAPI / Swagger surface

Both services serve Swagger UI in development environments:

- **claims-service** — registered via shared `AddChoInfrastructure`
  helper (`CloudHealthOffice.Infrastructure.Extensions`). Service
  metadata: `ServiceName = "Claims Service"`. Swagger UI at
  `/swagger` (default route prefix preserved).
- **payment-service** — registered via direct `AddSwaggerGen`
  registration in `Program.cs`. Service metadata:
  `Title = "Payment Service API"`. Swagger UI at root (`/`) per
  service's local route-prefix configuration.

Both surfaces serve the OpenAPI v3 spec at `/swagger/v1/swagger.json`
(claims-service) or equivalent (payment-service).

### Phase 2 OpenAPI follow-ups

- [Backlog 10.4](../roadmap/claims-phase-2-backlog.md#104--payment-service-migration-to-addchoinfrastructure-swagger):
  payment-service Swagger pattern parity (move to
  `AddChoInfrastructure`).
- [Backlog 10.5](../roadmap/claims-phase-2-backlog.md#105--xml-comment-driven-swagger-surface-enrichment):
  XML-comment-driven Swagger surface enrichment. Phase 1 has XML
  doc comments on all controllers but
  `<GenerateDocumentationFile>` is not enabled in csproj — enabling
  produces richer Swagger UI summaries.

---

## Surface stability commitments

- **`/api/v1/*` paths** — V1 stability commitment. Breaking changes
  require a `/api/v2/*` parallel path before V1 deprecation.
- **`/api/claims/*` paths** — versionless surface used by the
  Cloud Health Office portal. Breaking changes coordinated with
  portal release notes; not committed to external consumers. The
  one `[Obsolete]` route on this surface — `POST /api/claims` —
  is scheduled for removal post-pilot (see ClaimsController section
  above).
- **`/fhir/*` paths** — FHIR R4 conformance. Search-param
  expansion is additive; breaking changes follow FHIR versioning.
- **`/api/PaymentRuns`, `/api/ReversalRuns`** — operator-facing
  surface. Breaking changes coordinated with portal.
- **`/api/v1/admin/*` paths** — operator-only; not customer-facing.
  Breaking changes acceptable with operator coordination.

---

## See also

- [`docs/architecture/claims-phase-1-closer.md`](../architecture/claims-phase-1-closer.md)
  — Closer narrative + capability matrix
- [`docs/roadmap/claims-phase-2-backlog.md`](../roadmap/claims-phase-2-backlog.md)
  — Phase 2 work registry
- [`docs/compliance/claims-cms-0057-f-readiness.md`](../compliance/claims-cms-0057-f-readiness.md)
  — CMS-0057-F readiness assessment
- [`docs/migrations/claims-cosmos-partition-migration.md`](../migrations/claims-cosmos-partition-migration.md)
  — 5.1b operator runbook
- Per-capability architecture docs at
  [`docs/architecture/claim-*.md`](../architecture/)
