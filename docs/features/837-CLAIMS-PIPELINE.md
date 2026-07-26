# 837 Claims Ingestion Pipeline

How a raw X12 837 file becomes an adjudicated claim.

> **Note:** This doc previously described an SFTP → Argo Workflow → Kafka → Argo Events
> pipeline with a stub `x12-parser` container. That design was never wired up end-to-end
> and has been superseded by the in-process pipeline below, built directly into
> `claims-service`. If you find references elsewhere (`containers/x12-parser/`,
> `argo-workflows/x12-837-ingest.yaml`, `kafka/topics.yaml`'s `claims-adjudication` topic)
> treat them as historical/unused rather than the current path.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                       837 Claims Import Pipeline                        │
└─────────────────────────────────────────────────────────────────────────┘

Evaluator / integration                POST /api/v1/claims/import/raw837
   drops a raw 837 file    ──────────▶  (ClaimsV1Controller, claims-service)
                                                    │
                                                    ▼
                                   X12837Parser.Parse(ediContent)
                                   (hand-rolled segment walker — 837P/SV1
                                    and 837I/SV2, multi-claim batches,
                                    2000C dependent loops)
                                                    │
                                                    ▼
                                   X12837ClaimMapper.Map(parsed, tenantId)
                                   (→ AdapterClaim; deliberately leaves
                                    BenefitPlanId blank — see below)
                                                    │
                                                    ▼
                                   IClaimSubmissionService.SubmitAsync
                                   (validates, writes via tenant-routed
                                    IClaimAdapter, persists a
                                    ClaimImportTransaction row — accepted
                                    or rejected — either way)
                                                    │
                                                    ▼
                                   Service Bus: ClaimVersionSubmitted
                                                    │
                                                    ▼
                              ClaimAdjudicationOrchestrator (background
                              message consumer) runs the 8-stage pipeline
                              in Order:
                                100  Scrubbing
                                150  ProviderIntegrity
                                200  NetworkCredentialing
                                (resolve BenefitPlanId from the member's
                                 active coverage here if it arrived blank
                                 — ICoverageResolver, coverage-service)
                                300  BenefitCalculation
                                400  NcciEdits
                                500  CoordinationOfBenefits
                                600  AiExamination
                                999  Persistence
                                                    │
                                                    ▼
                                   Claim.Status = Approved / Denied / Pended
```

## Why `BenefitPlanId` starts blank

`X12837ClaimMapper` deliberately does not try to resolve `BenefitPlanId`/`CoverageId` from
the raw 837 — an unrecognized member should surface as a real pend during adjudication, not
get silently papered over during mapping. `ClaimAdjudicationOrchestrator` resolves it from
the member's active coverage (via `ICoverageResolver` → coverage-service's
`GET /api/v1/coverage/member/{memberId}/active`) immediately before plan resolution runs, but
only when the claim arrived without one — this is what lets a member seeded through the 834
enrollment pipeline actually reach a priced outcome from an 837, instead of pending on
"missing BenefitPlanId" regardless of how correctly they were enrolled.

## Testing end-to-end

```bash
# Seeds a benefit plan + plan-code mapping, imports a sample 834 (creating
# Sponsor/Member/Coverage), submits a matching 837, and polls until the
# claim reaches a terminal adjudication status.
scripts/smoke/834-to-837-e2e-smoke.sh
```

Or manually:

```bash
# 1. Submit a raw 837 file
curl -X POST http://claims-service.cloudhealthoffice/api/v1/claims/import/raw837 \
  -H "X-Tenant-ID: <tenant>" \
  -F "file=@my-claim.837"

# 2. Check the transaction log (admin view — accepted AND rejected imports)
curl http://claims-service.cloudhealthoffice/api/v1/claims/import-transactions \
  -H "X-Tenant-ID: <tenant>"

# 3. Poll the claim itself for adjudication status
curl http://claims-service.cloudhealthoffice/api/claims/{claim-id} \
  -H "X-Tenant-ID: <tenant>"
```

The portal's **EDI Transactions** console (`/edi-transactions`) shows both 834 and 837
import history — accepted/rejected status and error text — without needing raw API calls.

## Troubleshooting

### 837 file rejected at upload (400)
- No `CLM` segments found → not a valid 837, or wrong transaction type.
- Parse failure → check the error message; `X12837Parser` throws `X12FormatException` with
  the specific segment/reason.

### Claim accepted but never prices (stuck pending/rejected on "missing BenefitPlanId")
- The member has no active coverage in coverage-service for the claim's service date /
  insurance line — most likely the 834 onboarding step (plan-code mapping,
  `scripts/onboard-plan-code-mappings.sh`) wasn't completed for this employer group before
  their 837s started arriving.
- Check `GET /api/v1/claims/import-transactions` for the transaction's `Status`/`Errors`, and
  `GET /api/claims/{id}` for `BenefitPlanId` and `PendDetails`.

### Adjudication never completes
- Check the Service Bus subscription is live (`ClaimAdjudicationOrchestrator`'s background
  consumer) — this replaced the old Argo Events/Kafka trigger entirely.
