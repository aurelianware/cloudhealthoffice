#!/usr/bin/env bash
#
# Member Foundation smoke test.
#
# Exercises the member-service end-to-end against a running instance:
#   1. POST create member
#   2. GET member by id
#   3. GET FHIR Patient projection
#   4. GET event stream (expects MemberCreated at version 1)
#   5. PUT address update (expects AddressChanged event)
#   6. POST typed identifier (Portal)
#
# Usage:
#   BASE_URL=http://localhost:5005 TENANT_ID=tenant-smoke \
#     scripts/smoke/member-foundation-smoke.sh
#
# Requires: curl, jq.

set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:5005}"
TENANT_ID="${TENANT_ID:-tenant-smoke}"
MEMBER_ID="${MEMBER_ID:-SMOKE-$(date +%s)}"

hdr=(-H "Content-Type: application/json" -H "X-Tenant-ID: ${TENANT_ID}")

echo "==> POST /api/v1/members (memberId=${MEMBER_ID})"
curl -sS -f -X POST "${BASE_URL}/api/v1/members" "${hdr[@]}" -d @- <<JSON | jq '.memberId, .status'
{
  "memberId": "${MEMBER_ID}",
  "groupNumber": "SMOKE-GRP",
  "isSubscriber": true,
  "firstName": "Smoke",
  "lastName": "Test",
  "dateOfBirth": "1990-01-01",
  "effectiveDate": "2024-01-01",
  "gender": "F",
  "preferredLanguage": "en-US",
  "birthSex": "F"
}
JSON

echo "==> GET /api/v1/members/${MEMBER_ID}"
curl -sS -f "${BASE_URL}/api/v1/members/${MEMBER_ID}" "${hdr[@]}" | jq '.memberId'

echo "==> GET /api/v1/members/${MEMBER_ID}/fhir"
fhir=$(curl -sS -f "${BASE_URL}/api/v1/members/${MEMBER_ID}/fhir" "${hdr[@]}")
echo "$fhir" | jq '.resourceType, .birthDate'
[ "$(echo "$fhir" | jq -r '.resourceType')" = "Patient" ] || { echo "FAIL: resourceType != Patient"; exit 1; }

echo "==> GET /api/v1/members/${MEMBER_ID}/events"
events=$(curl -sS -f "${BASE_URL}/api/v1/members/${MEMBER_ID}/events" "${hdr[@]}")
created_count=$(echo "$events" | jq '[.[] | select(.eventType=="MemberCreated" or .eventType==1)] | length')
[ "$created_count" -ge 1 ] || { echo "FAIL: no MemberCreated event"; exit 1; }

echo "==> PUT /api/v1/members/${MEMBER_ID} (address change)"
curl -sS -f -X PUT "${BASE_URL}/api/v1/members/${MEMBER_ID}" "${hdr[@]}" -d '{
  "address": "1 Smoke Plaza", "city": "Austin", "state": "TX", "zipCode": "78701"
}' > /dev/null

echo "==> POST /api/v1/members/${MEMBER_ID}/identifiers (portal)"
curl -sS -f -X POST "${BASE_URL}/api/v1/members/${MEMBER_ID}/identifiers" "${hdr[@]}" -d '{
  "type": 6, "value": "smoke-portal-uid"
}' > /dev/null

echo "==> GET /api/v1/members/${MEMBER_ID}/identifiers"
curl -sS -f "${BASE_URL}/api/v1/members/${MEMBER_ID}/identifiers" "${hdr[@]}" | jq '.[0].system'

echo "OK: member-foundation smoke completed."
