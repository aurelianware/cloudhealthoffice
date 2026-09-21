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

if [ "$RC" -eq 0 ]; then
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
