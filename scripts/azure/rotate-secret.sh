#!/usr/bin/env bash
# Publishes a new logical version of a rotating key to Azure Key Vault.
#
# The rotation model uses operator-controlled version strings (v1, v2, …)
# encoded in the secret NAME rather than opaque KV version IDs — see
# docs/architecture/secret-rotation.md for the full model and end-to-end
# sequence. This script only handles step 1 (provisioning the secret);
# app config (CurrentKeyVersion / AcceptedKeyVersions) is updated
# separately so infra and app config concerns stay separate.
#
# Idempotent: re-running with the same SECRET_PREFIX + NEW_VERSION updates
# the named secret in place without error.
#
# Required env vars:
#   SECRET_PREFIX     — the logical prefix, e.g. "member-identifier-encryption-key"
#   NEW_VERSION       — the operator version string, e.g. "v2"
#   KEY_VAULT_NAME    — the target Key Vault (name, not URI)
#
# Optional env vars:
#   SECRET_VALUE      — override the generated 32-byte random key with a
#                       caller-supplied value (base64 or plaintext). When
#                       unset, this script generates a fresh key with
#                       `openssl rand -base64 32`.
set -euo pipefail

: "${SECRET_PREFIX:?SECRET_PREFIX is required (e.g. member-identifier-encryption-key)}"
: "${NEW_VERSION:?NEW_VERSION is required (e.g. v2)}"
: "${KEY_VAULT_NAME:?KEY_VAULT_NAME is required}"

SECRET_NAME="${SECRET_PREFIX}-${NEW_VERSION}"

echo "==> Rotating key: ${SECRET_NAME} in ${KEY_VAULT_NAME}"

if [[ -z "${SECRET_VALUE:-}" ]]; then
  SECRET_VALUE="$(openssl rand -base64 32)"
  GENERATED=1
else
  GENERATED=0
fi

az keyvault secret set \
  --vault-name "$KEY_VAULT_NAME" \
  --name "$SECRET_NAME" \
  --value "$SECRET_VALUE" >/dev/null

echo "==> Published secret: ${SECRET_NAME}"
echo "==> Next steps (app config, performed separately):"
echo "    1. Add '${NEW_VERSION}' to AcceptedKeyVersions"
echo "    2. Wait one IConfiguration reload interval for services to pick it up"
echo "    3. Verify the RotatingKeyProviderHealthCheck reports Healthy"
echo "    4. Set CurrentKeyVersion=${NEW_VERSION} to begin emitting new envelopes"
echo "    5. (Eventually) backfill old records + drop the prior version from AcceptedKeyVersions"
echo ""
if [[ "$GENERATED" -eq 1 ]]; then
  echo "Version string: ${NEW_VERSION}"
  echo "(The secret value was generated locally and is NOT echoed here.)"
else
  echo "Version string: ${NEW_VERSION}"
fi
