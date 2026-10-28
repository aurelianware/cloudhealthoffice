#!/usr/bin/env bash
# dev-token.sh — Print a development-signed user token for a local stack.
#
#   TOKEN=$(./scripts/dev-token.sh --tenant demo)
#   curl -H "Authorization: Bearer $TOKEN" http://localhost:5001/api/claims/recent
#
# Every CHO service takes the tenant only from a validated token, so local API
# calls need one. This signs a TenantAdmin token (*:* within the tenant) with the
# public development key every service trusts in appsettings.Development.json
# (ChoDevelopmentAuth). ChoAuthOptions.Validate refuses symmetric keys on any
# host that is not Development or Testing, so the token cannot authenticate
# against anything deployed.
#
# Needs only bash and openssl.

set -euo pipefail

TENANT_ID="demo"
HOURS=12
while [[ $# -gt 0 ]]; do
  case $1 in
    --tenant) TENANT_ID="$2"; shift 2 ;;
    --hours)  HOURS="$2"; shift 2 ;;
    *) echo "usage: $0 [--tenant <id>] [--hours <n>]" >&2; exit 2 ;;
  esac
done

KEY_B64="${CHO_DEV_SIGNING_KEY:-Q0hPLWRldmVsb3BtZW50LW9ubHktc2lnbmluZy1rZXktZG8tbm90LXVzZS0yMDI2}"

b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }

now=$(date +%s)
exp=$((now + HOURS * 3600))
header='{"alg":"HS256","typ":"JWT"}'
payload=$(printf '{"iss":"cho-portal-dev","aud":"cho-api","sub":"dev-user-%s","tenant_id":"%s","roles":["TenantAdmin"],"permissions":["*:*","users:manage","roles:manage","settings:manage","operating-mode:manage"],"name":"Local Developer","jti":"%s-%s","iat":%d,"nbf":%d,"exp":%d}' \
  "$TENANT_ID" "$TENANT_ID" "$now" "$RANDOM" "$now" "$now" "$exp")

signing_input="$(printf '%s' "$header" | b64url).$(printf '%s' "$payload" | b64url)"
key_hex=$(printf '%s' "$KEY_B64" | openssl base64 -d -A | od -An -v -tx1 | tr -d ' \n')
signature=$(printf '%s' "$signing_input" | openssl dgst -sha256 -mac HMAC -macopt "hexkey:$key_hex" -binary | b64url)

printf '%s.%s\n' "$signing_input" "$signature"
