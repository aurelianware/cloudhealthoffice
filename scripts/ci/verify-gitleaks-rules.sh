#!/usr/bin/env bash
#
# Prove that gitleaks is actually detecting secrets.
#
# Why this exists: .gitleaks.toml once declared a [allowlist] but no [[rules]]
# and no [extend]. Gitleaks loaded it, ran with ZERO detection rules, reported
# "no leaks found" and exited 0 — so the CI secret-scan job passed no matter
# what the repository contained, and could never have failed. A scanner that
# cannot fail is worse than no scanner, because it is reported as a control.
#
# This plants a canary secret in a scratch directory, scans it with the repo
# config, and fails if gitleaks does not find it.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CONFIG="${REPO_ROOT}/.gitleaks.toml"

if ! command -v gitleaks >/dev/null 2>&1; then
  echo "ERROR: gitleaks is not on PATH." >&2
  exit 1
fi

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

# Build the canary from fragments at runtime.
#
# The assembled values are deliberately secret-SHAPED, which means a complete
# literal in this file would be caught by GitHub push protection and by this
# repository's own pre-commit hooks — the script could not be committed. So the
# prefixes are concatenated here and only ever exist inside $WORKDIR, which is
# a mktemp directory outside the repository and is removed on exit.
#
# Neither value is real: the AWS one is Amazon's published documentation
# example, and the Stripe one is random filler after the prefix.
AWS_CANARY="AKIA""IOSFODNN7EXAMPLE"
STRIPE_CANARY="sk""_""live""_51H8qwCanaryNotARealKeyAbcdefghijklmn"

{
  printf 'aws_access_key_id = "%s"\n' "$AWS_CANARY"
  printf 'stripe_secret = "%s"\n' "$STRIPE_CANARY"
} > "$WORKDIR/canary.txt"

echo "Verifying gitleaks detects a planted canary using ${CONFIG}..."

set +e
gitleaks detect \
  --no-git \
  --source "$WORKDIR" \
  --config "$CONFIG" \
  --redact \
  --no-banner \
  --exit-code 1
RC=$?
set -e

if [ "$RC" -ne 1 ]; then
  cat >&2 <<'MSG'

FAIL: gitleaks did not detect the canary secret.

The secret-scanning configuration is not effective. The usual cause is
.gitleaks.toml missing its rule set — it must either declare [[rules]] or
inherit the built-in ones:

    [extend]
    useDefault = true

Until this passes, the Secret Scanning CI job proves nothing.
MSG
  exit 1
fi

echo "OK: gitleaks detected the canary (exit ${RC}) — rules are active."

# Service configuration files are scanned too. A blanket path allowlist for
# services/*/appsettings*.json once exempted every service's config, so a real
# connection string or key committed there would never have been reported.
# Exemptions for those files must be by value, not by path.
for config_file in appsettings.json appsettings.Development.json; do
  CASE_DIR="$WORKDIR/path-$config_file"
  mkdir -p "$CASE_DIR/src/services/canary-service"
  printf '{ "Stripe": { "SecretKey": "%s" } }\n' "$STRIPE_CANARY" \
    > "$CASE_DIR/src/services/canary-service/$config_file"

  set +e
  gitleaks detect --no-git --source "$CASE_DIR" --config "$CONFIG" --redact --no-banner --exit-code 1 >/dev/null 2>&1
  RC=$?
  set -e
  if [ "$RC" -ne 1 ]; then
    echo "FAIL: a secret in src/services/<service>/$config_file is not detected; .gitleaks.toml exempts that path." >&2
    exit 1
  fi
  echo "OK: secrets in src/services/<service>/$config_file are detected."
done


# A public Secret Manager resource ID is not key material. Exempt only that
# exact value, and prove actual secrets at the same locations remain detected.
RESOURCE_ID="appeal-body-encryption-key-v1"
# Avoid sample words (for example real/credential) ignored by the generic rule.
GENERIC_CANARY="n8Q4v7J2""x6B9m3R5s1C8""k2P7d4F6h9L3z5T1"
for spec in "infrastructure/gcp/appeals-secrets/main.tf:secret_id" "scripts/demo/gcp/initialize_appeals_key.py:SECRET"; do
  SOURCE_PATH="${spec%:*}"
  FIELD_NAME="${spec##*:}"
  CASE_DIR="$WORKDIR/resource-${FIELD_NAME}"
  mkdir -p "$CASE_DIR/$(dirname "$SOURCE_PATH")"
  printf '%s = "%s"\n' "$FIELD_NAME" "$RESOURCE_ID" > "$CASE_DIR/$SOURCE_PATH"
  set +e
  gitleaks detect --no-git --source "$CASE_DIR" --config "$CONFIG" --redact --no-banner --exit-code 1 >/dev/null 2>&1
  RC=$?
  set -e
  if [ "$RC" -ne 0 ]; then
    echo "FAIL: the public resource identifier still triggers scanning in $SOURCE_PATH." >&2
    exit 1
  fi
  printf '%s = "%s"\n' "$FIELD_NAME" "$STRIPE_CANARY" > "$CASE_DIR/$SOURCE_PATH"
  set +e
  gitleaks detect --no-git --source "$CASE_DIR" --config "$CONFIG" --redact --no-banner --exit-code 1 >/dev/null 2>&1
  RC=$?
  set -e
  if [ "$RC" -ne 1 ]; then
    echo "FAIL: secret-shaped values in $SOURCE_PATH are not detected (exit $RC)." >&2
    exit 1
  fi
  printf '%s = "%s"\n' "$FIELD_NAME" "$GENERIC_CANARY" > "$CASE_DIR/$SOURCE_PATH"
  set +e
  gitleaks detect --no-git --source "$CASE_DIR" --config "$CONFIG" --redact --no-banner --exit-code 1 >/dev/null 2>&1
  RC=$?
  set -e
  if [ "$RC" -ne 1 ]; then
    echo "FAIL: generic credential values in $SOURCE_PATH are not detected (exit $RC)." >&2
    exit 1
  fi
  echo "OK: public identifier allowed; secrets still detected in $SOURCE_PATH."
done
