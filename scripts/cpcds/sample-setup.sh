#!/usr/bin/env bash
# sample-setup.sh — Give a CHO stack what it needs to adjudicate the sample
# CPCDS claims (docs/integration/samples/cpcds) the way the sample core did,
# so "trial.sh shadow" shows matches as well as gaps. The laptop trial in
# docs/integration/CPCDS-TRIAL-KIT.md runs it before "load".
#
#   ./scripts/cpcds/sample-setup.sh --local
#
# It creates, in the tenant (each step is skipped when already done):
#   1. the standard service categories (lab, imaging, office visit, ...)
#   2. a network, "Sample Gold PPO network", with the sample clinic (NPI 1234567893) in it
#   3. the benefit plan SAMPLE-PPO-GOLD (docs/integration/samples/shadow/benefit-plan.json)
#      with that network as its in-network tier
#   4. a mapping from the core's plan code PPO-GOLD (group GRP-ACME) to it
#   5. its default fee schedule (docs/integration/samples/shadow/fee-schedule.csv)
#
# A plan's own setup does the same five things with its own plans, network,
# codes and rates: docs/integration/LOADING-A-PLANS-DATA.md.
#
# Settings (environment):
#   CHO_TOKEN              a tenant administrator's CHO token (required unless --local)
#   CHO_BENEFIT_PLAN_URL   benefit-plan-service (default http://localhost:5002)
#   CHO_PROVIDER_URL       provider-service     (default http://localhost:5004)
#
# --local signs a development token for --tenant (default "demo") with
# scripts/dev-token.sh. Exit status: 0 when every step is done, 1 when one failed.

if [ "${BASH_VERSINFO[0]:-0}" -lt 4 ]; then
  echo "error: needs bash 4 or newer (macOS: brew install bash); found ${BASH_VERSION:-unknown}." >&2
  exit 2
fi
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SAMPLES="$ROOT/docs/integration/samples/shadow"
LOCAL=false
TENANT="demo"
while [[ $# -gt 0 ]]; do
  case $1 in
    --local)  LOCAL=true; shift ;;
    --tenant) TENANT="$2"; shift 2 ;;
    -h|--help) sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

BENEFIT_URL="${CHO_BENEFIT_PLAN_URL:-http://localhost:5002}"
PROVIDER_URL="${CHO_PROVIDER_URL:-http://localhost:5004}"
PLAN_ID="SAMPLE-PPO-GOLD"
NETWORK_NAME="Sample Gold PPO network (synthetic)"
NPI="1234567893"

command -v curl >/dev/null || { echo "curl is required" >&2; exit 2; }
command -v jq >/dev/null || { echo "jq is required" >&2; exit 2; }

if $LOCAL; then
  TOKEN="$("$ROOT/scripts/dev-token.sh" --tenant "$TENANT")"
elif [[ -n "${CHO_TOKEN:-}" ]]; then
  TOKEN="$CHO_TOKEN"
  # The services take the tenant from the token; the plan document names it too.
  payload="$(printf '%s' "$TOKEN" | cut -d. -f2 | tr '_-' '/+')"
  while (( ${#payload} % 4 )); do payload+="="; done
  TENANT="$(printf '%s' "$payload" | base64 -d 2>/dev/null | jq -r '.tenant_id // empty')"
  [[ -n "$TENANT" ]] || { echo "CHO_TOKEN has no tenant_id claim" >&2; exit 2; }
else
  echo "Set CHO_TOKEN to a tenant administrator's token, or pass --local for a development stack." >&2
  exit 2
fi

STATUS=0
BODY="$(mktemp)"; trap 'rm -f "$BODY"' EXIT
# call <method> <url> [curl args...]: sets CODE, leaves the response in $BODY.
call() {
  local method="$1" url="$2"; shift 2
  CODE="$(curl -sS -o "$BODY" -w '%{http_code}' -X "$method" "$url" \
    -H "Authorization: Bearer $TOKEN" "$@")" || { echo "  could not reach $url" >&2; exit 2; }
}
ok()   { echo "  $*"; }
fail() { echo "  FAILED: $* ($CODE: $(head -c 300 "$BODY"))" >&2; STATUS=1; }

echo "Setting up the shadow adjudication sample in tenant $TENANT"

echo "1. Service categories"
call POST "$BENEFIT_URL/api/v1/service-category-mappings/seed-system-defaults"
case "$CODE" in
  200|201|204) ok "standard categories applied" ;;
  *) fail "the standard categories were not applied. The deployment must allow it (ServiceCategoryMapping__AdminWriteEnabled=true, which the laptop trial sets); until then a lab test billed from an office takes the office copay" ;;
esac

echo "2. Network and the sample clinic"
call GET "$PROVIDER_URL/api/v1/networks?pageSize=200"
NETWORK_ID="$(jq -r --arg n "$NETWORK_NAME" '[.items[]? | select(.name == $n)][0].organizationId // empty' "$BODY")"
if [[ -z "$NETWORK_ID" ]]; then
  call POST "$PROVIDER_URL/api/v1/networks" -H "Content-Type: application/json" \
    -d "$(jq -n --arg n "$NETWORK_NAME" '{name: $n, networkType: "PPO", lineOfBusiness: "Commercial"}')"
  NETWORK_ID="$(jq -r '.organizationId // empty' "$BODY")"
  [[ -n "$NETWORK_ID" ]] && ok "network created" || { fail "network not created"; exit 1; }
else
  ok "network already there"
fi
call GET "$PROVIDER_URL/api/v1/providers/npi/$NPI"
if [[ "$CODE" == 200 ]]; then
  ok "clinic $NPI already there (add it to the network by hand if it was created some other way)"
else
  call POST "$PROVIDER_URL/api/v1/providers" -H "Content-Type: application/json" -d "$(jq -n \
    --arg npi "$NPI" --arg net "$NETWORK_ID" --arg plan "$PLAN_ID" '{
      npi: $npi, providerType: "Organization", organizationName: "Sample Family Clinic (synthetic)",
      taxonomyCode: "261QP2300X", primarySpecialty: "Primary Care", credentialingStatus: "Approved",
      networkParticipations: [{networkId: $net, planId: $plan, lineOfBusiness: "Commercial",
                               networkTier: "Tier1", effectiveDate: "2026-01-01T00:00:00Z"}]}')"
  [[ "$CODE" == 201 ]] && ok "clinic $NPI created, in the network" || fail "clinic not created"
fi

echo "3. Benefit plan $PLAN_ID"
call GET "$BENEFIT_URL/api/v1/plans"
if jq -e --arg p "$PLAN_ID" 'any(.[]?; .planId == $p)' "$BODY" >/dev/null; then
  ok "plan already there"
else
  call POST "$BENEFIT_URL/api/v1/plans" -H "Content-Type: application/json" -d "$(jq \
    --arg t "$TENANT" --arg net "$NETWORK_ID" \
    '.tenantId = $t | .networkTiers = [{tierName: "In-Network", tierLevel: 1, networkId: $net}]' \
    "$SAMPLES/benefit-plan.json")"
  [[ "$CODE" == 201 ]] && ok "plan created" || fail "plan not created"
fi

echo "4. Plan code PPO-GOLD"
call POST "$BENEFIT_URL/api/v1/plan-code-mappings" -H "Content-Type: application/json" \
  -d "$(jq -n --arg p "$PLAN_ID" '{groupNumber: "GRP-ACME", insuranceLineCode: "HLT", externalPlanCode: "PPO-GOLD", planId: $p}')"
case "$CODE" in
  201) ok "mapped to $PLAN_ID" ;;
  409) ok "already mapped" ;;
  *) fail "plan code not mapped" ;;
esac

echo "5. Fee schedule"
call POST "$BENEFIT_URL/api/v1/fee-schedules/import/csv?name=Sample%20Gold%20PPO%202026&effectiveDate=2026-01-01&type=Commercial&planId=$PLAN_ID&dryRun=false" \
  -F "file=@$SAMPLES/fee-schedule.csv"
if [[ "$CODE" == 200 ]]; then
  ok "$(jq -r '"\(.linesLoaded // 0) rates, \(.outcome // .status // "loaded")"' "$BODY")"
else
  fail "fee schedule not loaded"
fi

if [[ $STATUS -eq 0 ]]; then
  echo "Done. Next: trial.sh load, then trial.sh shadow (docs/integration/CPCDS-TRIAL-KIT.md)."
fi
exit $STATUS
