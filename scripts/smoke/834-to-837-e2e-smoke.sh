#!/usr/bin/env bash
#
# 834 -> 837 end-to-end smoke test.
#
# Proves the evaluator on-ramp actually works as a loop, against a running
# instance: an 834 seeds a member + coverage, then an 837 for that same
# member reaches a real, priced adjudication outcome — not a pend on
# "missing BenefitPlanId" (the gap fixed by the coverage-resolution PR).
#
#   1. POST a benefit plan to benefit-plan-service        (seed data)
#   2. POST a plan-code mapping matching the 834 fixture's HD04 code
#   3. POST the 834 fixture to enrollment-import-service   (raw834)
#      -> creates Sponsor, Member, Coverage (delegated writes)
#   4. POST an 837 for the same member to claims-service   (raw837)
#   5. Poll claims-service until adjudication settles, then report:
#      status, resolved BenefitPlanId, allowed/paid amounts or pend/deny
#      reason. Hard-fails only on the specific bug this proves fixed
#      (BenefitPlanId never resolving) — any other terminal outcome is
#      reported, not treated as failure, since plan/rule tuning is a
#      separate concern from "does the pipe connect end to end."
#
# Usage:
#   BENEFIT_PLAN_URL=http://localhost:5002 \
#   ENROLLMENT_IMPORT_URL=http://localhost:5011 \
#   CLAIMS_URL=http://localhost:5001 \
#     scripts/smoke/834-to-837-e2e-smoke.sh
#
# Requires: curl, jq.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

BENEFIT_PLAN_URL="${BENEFIT_PLAN_URL:-http://localhost:5002}"
ENROLLMENT_IMPORT_URL="${ENROLLMENT_IMPORT_URL:-http://localhost:5011}"
CLAIMS_URL="${CLAIMS_URL:-http://localhost:5001}"
TENANT_ID="${TENANT_ID:-tenant-smoke-834-837}"
POLL_TIMEOUT_SECONDS="${POLL_TIMEOUT_SECONDS:-60}"

FIXTURE_834="${REPO_ROOT}/docs/testing/test-x12-834-enrollment-sample.edi"

# These three values are fixed by the 834 fixture — do not change without
# also updating the fixture (or vice versa): REF*0F is the subscriber's
# member id, REF*1L is the group number, HD04 on the first HD segment is
# the trading partner's own plan code for the HLT line.
FIXTURE_MEMBER_ID="BSCA123456789"
FIXTURE_GROUP_NUMBER="GRP0001"
FIXTURE_EXTERNAL_PLAN_CODE="Blue Shield PPO"

PLAN_ID="${TENANT_ID}-DEFAULT-PPO"
SERVICE_DATE="$(date -u +%Y%m%d)"
CLAIM_NUMBER="SMOKE-834837-$(date +%s)"

for bin in curl jq; do
  command -v "$bin" >/dev/null 2>&1 || { echo "Required tool not found: $bin" >&2; exit 1; }
done
[[ -f "$FIXTURE_834" ]] || { echo "834 fixture not found: $FIXTURE_834" >&2; exit 1; }

hdr=(-H "Content-Type: application/json" -H "X-Tenant-ID: ${TENANT_ID}")

echo "== Config =="
echo "  Tenant:            ${TENANT_ID}"
echo "  benefit-plan-service:      ${BENEFIT_PLAN_URL}"
echo "  enrollment-import-service: ${ENROLLMENT_IMPORT_URL}"
echo "  claims-service:            ${CLAIMS_URL}"
echo ""

# ── 1. Seed a real benefit plan ─────────────────────────────────────────────
echo "==> [1/5] POST ${BENEFIT_PLAN_URL}/api/v1/plans (planId=${PLAN_ID})"
plan_payload=$(jq -n --arg tenantId "$TENANT_ID" --arg planId "$PLAN_ID" '{
  tenantId: $tenantId,
  planId: $planId,
  planName: "Smoke Test Default PPO",
  payer: "Smoke Test Payer",
  effectiveDate: "2026-01-01T00:00:00Z",
  planType: "PPO",
  lineOfBusiness: "Commercial",
  isActive: true,
  costSharing: {
    individualDeductible: 1500.00,
    familyDeductible: 3000.00,
    individualOutOfPocketMax: 6000.00,
    familyOutOfPocketMax: 12000.00,
    inNetworkDeductible: 1500.00,
    outOfNetworkDeductible: 3000.00,
    inNetworkOutOfPocketMax: 6000.00,
    outOfNetworkOutOfPocketMax: 15000.00
  },
  benefits: [
    {
      serviceCategory: "98",
      description: "Professional Office Visit",
      inNetworkCopay: 30.00,
      outNetworkCopay: 60.00,
      deductibleApplies: false,
      priorAuthRequired: false
    }
  ],
  networkTiers: []
}')
plan_response=$(curl -sS -f -X POST "${BENEFIT_PLAN_URL}/api/v1/plans" "${hdr[@]}" -d "$plan_payload")
echo "$plan_response" | jq '{id, planId, planName}'

# ── 2. Seed the plan-code mapping the 834 fixture needs ─────────────────────
echo ""
echo "==> [2/5] POST ${BENEFIT_PLAN_URL}/api/v1/plan-code-mappings"
mapping_payload=$(jq -n \
  --arg group "$FIXTURE_GROUP_NUMBER" \
  --arg code "$FIXTURE_EXTERNAL_PLAN_CODE" \
  --arg planId "$PLAN_ID" \
  '{groupNumber: $group, insuranceLineCode: "HLT", externalPlanCode: $code, planId: $planId}')
mapping_status=$(curl -sS -o /dev/null -w '%{http_code}' -X POST \
  "${BENEFIT_PLAN_URL}/api/v1/plan-code-mappings" "${hdr[@]}" -d "$mapping_payload")
if [[ "$mapping_status" == "201" ]]; then
  echo "  Created."
elif [[ "$mapping_status" == "409" ]]; then
  echo "  Already exists (re-run of this script against the same tenant) — continuing."
else
  echo "FAIL: plan-code-mapping create returned HTTP ${mapping_status}" >&2
  exit 1
fi

# ── 3. Submit the 834 — seeds Sponsor + Member + Coverage ──────────────────
echo ""
echo "==> [3/5] POST ${ENROLLMENT_IMPORT_URL}/api/v1/enrollment/import/raw834"
import834_response=$(curl -sS -f -X POST "${ENROLLMENT_IMPORT_URL}/api/v1/enrollment/import/raw834" \
  -H "X-Tenant-ID: ${TENANT_ID}" \
  -F "file=@${FIXTURE_834}")
echo "$import834_response" | jq '{successCount, failedCount, coverageRecordsCreated, coverageMappingsUnresolved}'

# The fixture carries three subscribers across four distinct
# group/insurance-line/plan-code combinations (HLT/PPO, DEN, VIS, and a
# second subscriber's HLT/HMO) -- step 2 above deliberately seeds only the
# one this smoke test's target member (FIXTURE_MEMBER_ID) actually needs.
# Any other combination in the batch legitimately has no mapping, so a
# nonzero coverageMappingsUnresolved here is expected and not this test's
# concern -- what matters is whether the target member's own coverage
# resolved, which step 5 verifies directly via BenefitPlanId.
coverage_created=$(echo "$import834_response" | jq '.coverageRecordsCreated')
if [[ "$coverage_created" -lt 1 ]]; then
  echo "FAIL: no coverage records were created at all — step 2's mapping didn't take." >&2
  exit 1
fi

# ── 4. Submit an 837 for the same member ────────────────────────────────────
echo ""
echo "==> [4/5] POST ${CLAIMS_URL}/api/v1/claims/import/raw837 (member=${FIXTURE_MEMBER_ID}, claim=${CLAIM_NUMBER})"
edi_837="ISA*00*          *00*          *ZZ*SMOKESENDER    *ZZ*SMOKERECEIVER  *$(date -u +%y%m%d)*$(date -u +%H%M)*^*00501*000000001*0*P*:~GS*HC*SMOKESENDER*SMOKERECEIVER*$(date -u +%Y%m%d)*$(date -u +%H%M)*1*X*005010X222A1~ST*837*0001*005010X222A1~BHT*0019*18*${CLAIM_NUMBER}*$(date -u +%Y%m%d)*$(date -u +%H%M)*CH~NM1*41*2*SMOKE SUBMITTER*****46*SMOKESENDER~PER*IC*SMOKE SUBMITTER*TE*0000000000~NM1*40*2*SMOKE RECEIVER*****46*SMOKERECEIVER~HL*1**20*1~NM1*85*2*SMOKE MEDICAL GROUP*****XX*1234567890~N3*ADDRESS ON FILE~N4*SAN FRANCISCO*CA*94102~HL*2*1*22*0~SBR*P*18*****CI~NM1*IL*1*SMITH*JOHN****MI*${FIXTURE_MEMBER_ID}~NM1*PR*2*SMOKE PAYER*****PI*SMOKEPAYER~CLM*${CLAIM_NUMBER}*150.00***11:B:1*Y*A*Y*Y~DTP*472*RD8*${SERVICE_DATE}-${SERVICE_DATE}~HI*ABK:J06.9~LX*1~SV1*HC:99213*150.00*UN*1*11**1~DTP*472*RD8*${SERVICE_DATE}-${SERVICE_DATE}~SE*17*0001~GE*1*1~IEA*1*000000001~"

tmp_837="$(mktemp /tmp/smoke-837-XXXXXX.edi)"
trap 'rm -f "$tmp_837"' EXIT
printf '%s' "$edi_837" > "$tmp_837"

import837_response=$(curl -sS -f -X POST "${CLAIMS_URL}/api/v1/claims/import/raw837" \
  -H "X-Tenant-ID: ${TENANT_ID}" \
  -F "file=@${tmp_837}")
echo "$import837_response" | jq '.'

claim_success=$(echo "$import837_response" | jq -r '.results[0].success')
claim_id=$(echo "$import837_response" | jq -r '.results[0].claimId // empty')
if [[ "$claim_success" != "true" || -z "$claim_id" ]]; then
  echo "FAIL: 837 submission was rejected:" >&2
  echo "$import837_response" | jq '.results[0].errors' >&2
  exit 1
fi
echo "  Submitted as claim ${claim_id}."

# ── 5. Poll for adjudication to settle ──────────────────────────────────────
echo ""
echo "==> [5/5] Polling GET ${CLAIMS_URL}/api/claims/${claim_id} (up to ${POLL_TIMEOUT_SECONDS}s)"
deadline=$(( $(date +%s) + POLL_TIMEOUT_SECONDS ))
claim=""
status=""
while [[ $(date +%s) -lt $deadline ]]; do
  claim=$(curl -sS -f "${CLAIMS_URL}/api/claims/${claim_id}" -H "X-Tenant-ID: ${TENANT_ID}")
  status=$(echo "$claim" | jq -r '.status')
  # ClaimStatus has no JsonStringEnumConverter, so the API returns the raw
  # numeric enum value here, not "Submitted" — Submitted = 1 (Claim.cs).
  # Comparing against the string "Submitted" always mismatched, so this
  # loop used to break on the very first poll, before adjudication had any
  # chance to run asynchronously.
  if [[ "$status" != "1" ]]; then
    break
  fi
  sleep 2
done

resolved_plan_id=$(echo "$claim" | jq -r '.benefitPlanId // empty')
echo ""
echo "== Result =="
echo "  Final status:        ${status}"
echo "  Resolved BenefitPlanId: ${resolved_plan_id:-<none>}"
echo "$claim" | jq '{allowedAmount: .adjudicationResult.allowedAmount, payerPayment: .adjudicationResult.payerPayment, denialReasonCode: .adjudicationResult.denialReasonCode, denialReason: .adjudicationResult.denialReason, pendCode: .pendDetails.pendCode, pendReason: .pendDetails.pendReason}'

# The one hard assertion this smoke test exists to make: BenefitPlanId must
# have resolved from coverage. Everything past that (whether the claim is
# ultimately Approved, Denied, or Pended for some unrelated, legitimate
# reason) is a separate concern from "does 834-seeded coverage let an 837
# find its plan" and is reported above, not asserted on.
if [[ -z "$resolved_plan_id" ]]; then
  echo ""
  echo "FAIL: BenefitPlanId never resolved — the claim is stuck exactly where" >&2
  echo "the pre-fix bug left it. Coverage resolution did not work." >&2
  exit 1
fi

if [[ "$status" == "Submitted" ]]; then
  echo ""
  echo "WARN: claim did not reach a terminal status within ${POLL_TIMEOUT_SECONDS}s" >&2
  echo "(BenefitPlanId did resolve — this is a timing/infra issue, not the bug this test targets)." >&2
  exit 1
fi

echo ""
echo "OK: 834-to-837 loop completed. BenefitPlanId resolved from coverage; claim reached status '${status}'."
