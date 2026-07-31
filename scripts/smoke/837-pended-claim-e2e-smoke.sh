#!/usr/bin/env bash
#
# Strict 837 -> adjudication -> examiner work-queue smoke test.
#
# This test provisions an isolated tenant with every prerequisite needed for
# a clean NCCI pend, submits a real X12 837 through claims-service, and proves
# that a human examiner can resolve the pended claim. It intentionally guards
# against metered AI use: by default the local Kubernetes deployment must have
# AiMode=Disabled and claims-examiner-service scaled to zero.
#
# Required port-forwards (defaults may be overridden with environment vars):
#   kubectl port-forward -n cloudhealthoffice svc/claims-service 5001:80
#   kubectl port-forward -n cloudhealthoffice svc/benefit-plan-service 5002:80
#   kubectl port-forward -n cloudhealthoffice svc/member-service 5003:80
#   kubectl port-forward -n cloudhealthoffice svc/provider-service 5004:80
#   kubectl port-forward -n cloudhealthoffice svc/enrollment-import-service 5011:80
#
# Usage:
#   scripts/smoke/837-pended-claim-e2e-smoke.sh
#
# For a non-Kubernetes environment, first independently verify AI is disabled,
# then set AI_DISABLED_CONFIRMED=true. ALLOW_METERED_AI=true deliberately opts
# out of the guard for an explicit AI demo run.
#
# Requires: curl, jq.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

CLAIMS_URL="${CLAIMS_URL:-http://localhost:5001}"
BENEFIT_PLAN_URL="${BENEFIT_PLAN_URL:-http://localhost:5002}"
MEMBER_URL="${MEMBER_URL:-http://localhost:5003}"
PROVIDER_URL="${PROVIDER_URL:-http://localhost:5004}"
ENROLLMENT_IMPORT_URL="${ENROLLMENT_IMPORT_URL:-http://localhost:5011}"
KUBERNETES_NAMESPACE="${KUBERNETES_NAMESPACE:-cloudhealthoffice}"
TENANT_ID="${TENANT_ID:-tenant-smoke-pended-$(date +%s)-$$}"
POLL_TIMEOUT_SECONDS="${POLL_TIMEOUT_SECONDS:-90}"
AI_DISABLED_CONFIRMED="${AI_DISABLED_CONFIRMED:-false}"
ALLOW_METERED_AI="${ALLOW_METERED_AI:-false}"

FIXTURE_834="${REPO_ROOT}/docs/testing/test-x12-834-enrollment-sample.edi"
MEMBER_ID="BSCA123456789"
GROUP_NUMBER="GRP0001"
EXTERNAL_PLAN_CODE="Blue Shield PPO"
PROVIDER_NPI="1234567893"
PLAN_ID="PEND-PPO-$(date +%s)-$$"
SERVICE_DATE_ISO="$(date -u +%Y-%m-%d)"
SERVICE_DATE="$(date -u +%Y%m%d)"
CLAIM_NUMBER="PEND-837-$(date +%s)-$$"

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

for bin in curl jq; do
  command -v "$bin" >/dev/null 2>&1 || fail "Required tool not found: $bin"
done
[[ -f "$FIXTURE_834" ]] || fail "834 fixture not found: $FIXTURE_834"

hdr=(-H "Content-Type: application/json" -H "X-Tenant-ID: ${TENANT_ID}")

assert_ai_cost_guard() {
  if [[ "$ALLOW_METERED_AI" == "true" ]]; then
    echo "  WARNING: ALLOW_METERED_AI=true; metered AI guard explicitly bypassed."
    return
  fi

  if command -v kubectl >/dev/null 2>&1 \
      && kubectl get deployment claims-service claims-examiner-service \
        -n "$KUBERNETES_NAMESPACE" >/dev/null 2>&1; then
    local claims_mode examiner_replicas
    claims_mode=$(kubectl get deployment claims-service -n "$KUBERNETES_NAMESPACE" -o json |
      jq -r '[
        .spec.template.spec.containers[].env[]?
        | select(.name == "Adjudication__Enforcement__AiMode")
        | .value
      ] | first // ""')
    examiner_replicas=$(kubectl get deployment claims-examiner-service \
      -n "$KUBERNETES_NAMESPACE" -o jsonpath='{.spec.replicas}')

    [[ "$claims_mode" == "Disabled" ]] ||
      fail "claims-service AiMode is '${claims_mode:-unset}', expected Disabled. Set ALLOW_METERED_AI=true only for an intentional metered run."
    [[ "$examiner_replicas" == "0" ]] ||
      fail "claims-examiner-service has ${examiner_replicas} replica(s), expected 0."

    echo "  Verified Kubernetes AiMode=Disabled and claims-examiner-service replicas=0."
    return
  fi

  [[ "$AI_DISABLED_CONFIRMED" == "true" ]] ||
    fail "Could not verify Kubernetes AI settings. Confirm AI is disabled, then set AI_DISABLED_CONFIRMED=true."
  echo "  AI_DISABLED_CONFIRMED=true supplied for this non-Kubernetes run."
}

wait_for_service() {
  local name="$1"
  local base_url="$2"
  local _
  for _ in $(seq 1 15); do
    if curl -sS -f "${base_url}/health" >/dev/null 2>&1; then
      echo "  ${name}: ready"
      return
    fi
    sleep 1
  done
  fail "${name} is not healthy at ${base_url}/health"
}

echo "== Config =="
echo "  Tenant:                    ${TENANT_ID}"
echo "  Claims:                    ${CLAIMS_URL}"
echo "  Benefit plan:              ${BENEFIT_PLAN_URL}"
echo "  Member:                    ${MEMBER_URL}"
echo "  Provider:                  ${PROVIDER_URL}"
echo "  Enrollment import:         ${ENROLLMENT_IMPORT_URL}"
echo

echo "==> [1/11] Verify the no-metered-AI guard"
assert_ai_cost_guard

echo
echo "==> [2/11] Wait for required services"
wait_for_service "claims-service" "$CLAIMS_URL"
wait_for_service "benefit-plan-service" "$BENEFIT_PLAN_URL"
wait_for_service "member-service" "$MEMBER_URL"
wait_for_service "provider-service" "$PROVIDER_URL"
wait_for_service "enrollment-import-service" "$ENROLLMENT_IMPORT_URL"

echo
echo "==> [3/11] Create an active provider network"
network_payload=$(jq -n --arg tenant "$TENANT_ID" '{
  tenantId: $tenant,
  name: "Pended Claim Smoke PPO Network",
  networkType: "PPO",
  lineOfBusiness: "Commercial",
  effectiveDate: "2025-01-01T00:00:00Z",
  status: "Active",
  identifiers: []
}')
network_response=$(curl -sS -f -X POST "${PROVIDER_URL}/api/v1/networks" \
  "${hdr[@]}" -d "$network_payload")
network_id=$(echo "$network_response" | jq -r '.organizationId // empty')
[[ -n "$network_id" ]] || fail "provider-service did not return organizationId"
echo "$network_response" | jq '{organizationId, name, status, versionState}'

echo
echo "==> [4/11] Create an active, participating, credentialed provider"
provider_payload=$(jq -n \
  --arg tenant "$TENANT_ID" \
  --arg npi "$PROVIDER_NPI" \
  --arg networkId "$network_id" \
  --arg planId "$PLAN_ID" '{
    tenantId: $tenant,
    npi: $npi,
    providerType: "Organization",
    organizationName: "Pended Claim Smoke Medical Group",
    primarySpecialty: "Internal Medicine",
    taxonomyCode: "207R00000X",
    address: "1 Smoke Plaza",
    city: "Phoenix",
    state: "AZ",
    zipCode: "85001",
    status: "Active",
    credentialingStatus: "Unknown",
    acceptingNewPatients: true,
    networkParticipations: [{
      planId: $planId,
      networkId: $networkId,
      lineOfBusiness: "Commercial",
      networkTier: "Tier1",
      effectiveDate: "2025-01-01T00:00:00Z",
      acceptingNewPatients: true,
      acceptedLobs: ["Commercial"]
    }]
  }')
provider_response=$(curl -sS -f -X POST "${PROVIDER_URL}/api/v1/providers" \
  "${hdr[@]}" -d "$provider_payload")
provider_id=$(echo "$provider_response" | jq -r '.providerId // empty')
[[ -n "$provider_id" ]] || fail "provider-service did not return providerId"

credential_response=$(curl -sS -f -X PUT \
  "${PROVIDER_URL}/api/v1/providers/${provider_id}/credentialing" \
  "${hdr[@]}" \
  -d '{
    "status": "Approved",
    "credentialingDate": "2025-01-01T00:00:00Z",
    "recredentialingDueDate": "2099-01-01T00:00:00Z"
  }')
echo "$credential_response" | jq '{providerId, npi, status, credentialingStatus, recredentialingDueDate}'

membership_response=$(curl -sS -f \
  "${PROVIDER_URL}/api/v1/networks/${network_id}/members/${PROVIDER_NPI}?asOf=${SERVICE_DATE_ISO}" \
  -H "X-Tenant-ID: ${TENANT_ID}")
[[ "$(echo "$membership_response" | jq -r '.isActiveMember')" == "true" ]] ||
  fail "provider is not an active member of network ${network_id}"

echo
echo "==> [5/11] Create a benefit plan tied to the provider network"
plan_payload=$(jq -n \
  --arg tenant "$TENANT_ID" \
  --arg planId "$PLAN_ID" \
  --arg networkId "$network_id" '{
    tenantId: $tenant,
    planId: $planId,
    planName: "Pended Claim Smoke PPO",
    payer: "Smoke Test Payer",
    effectiveDate: "2025-01-01T00:00:00Z",
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
    benefits: [{
      serviceCategory: "98",
      description: "Professional Office Visit",
      inNetworkCopay: 30.00,
      outNetworkCopay: 60.00,
      deductibleApplies: false,
      priorAuthRequired: false
    }],
    networkTiers: [{
      tierName: "In-Network",
      tierLevel: 1,
      networkId: $networkId
    }]
  }')
plan_response=$(curl -sS -f -X POST "${BENEFIT_PLAN_URL}/api/v1/plans" \
  "${hdr[@]}" -d "$plan_payload")
echo "$plan_response" | jq '{id, planId, planName, versionState}'

mapping_payload=$(jq -n \
  --arg group "$GROUP_NUMBER" \
  --arg code "$EXTERNAL_PLAN_CODE" \
  --arg planId "$PLAN_ID" \
  '{groupNumber: $group, insuranceLineCode: "HLT", externalPlanCode: $code, planId: $planId}')
curl -sS -f -X POST "${BENEFIT_PLAN_URL}/api/v1/plan-code-mappings" \
  "${hdr[@]}" -d "$mapping_payload" >/dev/null

echo
echo "==> [6/11] Seed baseline NCCI edits"
ncci_response=$(curl -sS -f -X POST "${BENEFIT_PLAN_URL}/api/v1/ncci/seed" \
  -H "X-Tenant-ID: ${TENANT_ID}")
echo "$ncci_response" | jq '{quarter, pairsWritten, mueWritten}'
[[ "$(echo "$ncci_response" | jq '.pairsWritten')" -gt 0 ]] ||
  fail "NCCI seed wrote no edit pairs"

echo
echo "==> [7/11] Import the 834 fixture and activate its target member"
import834_response=$(curl -sS -f -X POST \
  "${ENROLLMENT_IMPORT_URL}/api/v1/enrollment/import/raw834" \
  -H "X-Tenant-ID: ${TENANT_ID}" \
  -F "file=@${FIXTURE_834}")
echo "$import834_response" |
  jq '{successCount, failedCount, coverageRecordsCreated, coverageMappingsUnresolved}'
[[ "$(echo "$import834_response" | jq '.coverageRecordsCreated')" -ge 1 ]] ||
  fail "834 import created no coverage"

member_response=$(curl -sS -f -X PUT \
  "${MEMBER_URL}/api/v1/members/${MEMBER_ID}" \
  "${hdr[@]}" \
  -d "{\"status\":\"Active\",\"eventId\":\"pended-smoke-member-active:${CLAIM_NUMBER}\"}")
[[ "$(echo "$member_response" | jq -r '.status')" == "Active" ]] ||
  fail "target member did not become Active"

echo
echo "==> [8/11] Submit a real two-line X12 837"
edi_837="ISA*00*          *00*          *ZZ*SMOKESENDER    *ZZ*SMOKERECEIVER  *$(date -u +%y%m%d)*$(date -u +%H%M)*^*00501*000000001*0*P*:~GS*HC*SMOKESENDER*SMOKERECEIVER*$(date -u +%Y%m%d)*$(date -u +%H%M)*1*X*005010X222A1~ST*837*0001*005010X222A1~BHT*0019*18*${CLAIM_NUMBER}*$(date -u +%Y%m%d)*$(date -u +%H%M)*CH~NM1*41*2*SMOKE SUBMITTER*****46*SMOKESENDER~PER*IC*SMOKE SUBMITTER*TE*0000000000~NM1*40*2*SMOKE RECEIVER*****46*SMOKERECEIVER~HL*1**20*1~NM1*85*2*SMOKE MEDICAL GROUP*****XX*${PROVIDER_NPI}~N3*ADDRESS ON FILE~N4*PHOENIX*AZ*85001~HL*2*1*22*0~SBR*P*18*****CI~NM1*IL*1*SMITH*JOHN****MI*${MEMBER_ID}~NM1*PR*2*SMOKE PAYER*****PI*SMOKEPAYER~CLM*${CLAIM_NUMBER}*250.00***11:B:1*Y*A*Y*Y~DTP*472*RD8*${SERVICE_DATE}-${SERVICE_DATE}~HI*ABK:J06.9~LX*1~SV1*HC:99213*150.00*UN*1*11**1~DTP*472*RD8*${SERVICE_DATE}-${SERVICE_DATE}~LX*2~SV1*HC:20600*100.00*UN*1*11**1~DTP*472*RD8*${SERVICE_DATE}-${SERVICE_DATE}~SE*20*0001~GE*1*1~IEA*1*000000001~"

tmp_837="$(mktemp /tmp/pended-smoke-837-XXXXXX.edi)"
trap 'rm -f "$tmp_837"' EXIT
printf '%s' "$edi_837" >"$tmp_837"

import837_response=$(curl -sS -f -X POST \
  "${CLAIMS_URL}/api/v1/claims/import/raw837" \
  -H "X-Tenant-ID: ${TENANT_ID}" \
  -F "file=@${tmp_837}")
claim_id=$(echo "$import837_response" | jq -r '.results[0].claimId // empty')
[[ "$(echo "$import837_response" | jq -r '.results[0].success')" == "true" && -n "$claim_id" ]] ||
  fail "837 submission failed: $(echo "$import837_response" | jq -c '.results[0].errors')"
echo "  Submitted claim ${claim_id} (${CLAIM_NUMBER})."

echo
echo "==> [9/11] Assert the claim reaches the NCCI work queue"
deadline=$(( $(date +%s) + POLL_TIMEOUT_SECONDS ))
claim=""
claim_status="1"
while [[ $(date +%s) -lt $deadline ]]; do
  claim=$(curl -sS -f "${CLAIMS_URL}/api/claims/${claim_id}" \
    -H "X-Tenant-ID: ${TENANT_ID}")
  claim_status=$(echo "$claim" | jq -r '.status')
  [[ "$claim_status" != "1" ]] && break
  sleep 2
done

[[ "$claim_status" == "4" ]] ||
  fail "claim reached status ${claim_status}, expected Pended (4): $(echo "$claim" | jq -c '{adjudicationResult,pendDetails}')"
[[ "$(echo "$claim" | jq -r '.pendDetails.pendCode')" == "NCCI" ]] ||
  fail "claim did not pend with code NCCI"
[[ "$(echo "$claim" | jq -r '.pendDetails.editFailures[0].ruleId')" == "NE001" ]] ||
  fail "claim did not retain expected NCCI rule NE001"

if [[ "$ALLOW_METERED_AI" != "true" ]]; then
  [[ "$(echo "$claim" | jq -r '.aiExamination')" == "null" ]] ||
    fail "AI examination unexpectedly ran despite the no-metered-AI guard"
fi

queue_item=$(curl -sS -f \
  "${CLAIMS_URL}/api/claims/work-queue/items?queueType=NCCI&limit=100" \
  -H "X-Tenant-ID: ${TENANT_ID}" |
  jq -c --arg id "$claim_id" '.[] | select(.claimId == $id)')
[[ -n "$queue_item" ]] || fail "pended claim is missing from the NCCI work queue"
echo "$queue_item" |
  jq '{claimId, queueReasonCode, procedureCodes, totalCharged, aiRecommendedDisposition}'

echo
echo "==> [10/11] Resolve the pended claim as a human examiner"
resolution_response=$(curl -sS -f -X POST \
  "${CLAIMS_URL}/api/claims/work-queue/${claim_id}/resolve" \
  "${hdr[@]}" \
  -d '{
    "disposition": "Approved",
    "reason": "Synthetic documentation confirms the services were distinct; examiner completed NCCI review.",
    "examinerUserId": "pended-claim-e2e-smoke"
  }')
[[ "$(echo "$resolution_response" | jq -r '.status')" == "5" ]] ||
  fail "resolved claim is not Approved (5)"
[[ "$(echo "$resolution_response" | jq -r '.versionState')" == "Adjudicated" ]] ||
  fail "resolved claim versionState is not Adjudicated"

echo
echo "==> [11/11] Verify the resolved claim leaves the work queue"
queue_count=$(curl -sS -f \
  "${CLAIMS_URL}/api/claims/work-queue/items?queueType=NCCI&limit=100" \
  -H "X-Tenant-ID: ${TENANT_ID}" |
  jq --arg id "$claim_id" '[.[] | select(.claimId == $id)] | length')
[[ "$queue_count" == "0" ]] || fail "resolved claim remains in the work queue"

echo
echo "== Result =="
echo "  Tenant:             ${TENANT_ID}"
echo "  Claim ID:           ${claim_id}"
echo "  Claim number:       ${CLAIM_NUMBER}"
echo "  Pend:               NCCI / NE001"
echo "  Final status:       Approved (5)"
echo "  Final versionState: Adjudicated"
if [[ "$ALLOW_METERED_AI" == "true" ]]; then
  echo "  AI guard:           bypassed by explicit opt-in"
else
  echo "  AI examination:     disabled / not invoked"
fi
echo
echo "OK: raw 837 pended-claim examiner lifecycle completed."
