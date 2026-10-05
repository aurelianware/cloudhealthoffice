#!/bin/bash
set -euo pipefail

echo "🔐 SFTP Password Rotation Tool"
echo "=============================="
echo ""

# Configuration
NAMESPACE="cho-sftp"
SECRET_NAME="sftp-users"
# The Deployment (infrastructure/k8s/sftp-server-deployment.yaml); sftp-service is the Service.
DEPLOYMENT="sftp-server"
USERNAME="${1:-cho-edi}"

if [ -z "$USERNAME" ]; then
  echo "Usage: $0 <username>"
  echo "Example: $0 cho-edi"
  exit 1
fi

echo "Target: ${USERNAME}@${NAMESPACE}"
echo ""

# Generate new password (24 characters, alphanumeric + special chars)
NEW_PASSWORD=$(openssl rand -base64 24 | tr -d "=+/" | cut -c1-24)

echo "✅ Generated new password (24 chars)"
echo ""

# Check the Deployment exists before touching the secret, so a wrong name
# cannot leave the secret changed while the pods keep the old password.
kubectl -n "${NAMESPACE}" get deployment "${DEPLOYMENT}" -o name > /dev/null

# Never print users.conf (it holds every SFTP user's password) and never put
# it on a command line (visible to other users via `ps`). The working copy
# lives in a private temp dir that is removed on exit.
umask 077
WORK_DIR=$(mktemp -d)
trap 'rm -rf "$WORK_DIR"' EXIT INT TERM
CONFIG_FILE="${WORK_DIR}/users.conf"

echo "📥 Fetching current SFTP configuration..."
kubectl -n "${NAMESPACE}" get secret "${SECRET_NAME}" -o jsonpath='{.data.users\.conf}' | base64 -d > "$CONFIG_FILE"
chmod 600 "$CONFIG_FILE"

# Update the password for the specified user (exact username match on field 1).
echo "🔄 Updating password for user: ${USERNAME}"
if ! awk -F: -v u="$USERNAME" '$1 == u { found = 1 } END { exit !found }' "$CONFIG_FILE"; then
  echo "❌ User '${USERNAME}' not found in configuration"
  exit 1
fi
# The new password goes to awk through the environment, not argv.
NEW_PASSWORD="$NEW_PASSWORD" awk -F: -v OFS=: -v u="$USERNAME" \
  '$1 == u { $2 = ENVIRON["NEW_PASSWORD"] } { print }' "$CONFIG_FILE" > "${CONFIG_FILE}.new"
mv "${CONFIG_FILE}.new" "$CONFIG_FILE"
chmod 600 "$CONFIG_FILE"
echo "Changed users: ${USERNAME}"
echo ""

# Update the secret: render it from the file and send it via stdin so the
# contents never appear in argv. `replace`, not client-side `apply`: apply
# copies the whole object (every user's password) into the
# kubectl.kubernetes.io/last-applied-configuration annotation, which
# `kubectl describe` and `get -o yaml` print. replace writes the object as
# rendered (no annotations), which also drops an annotation an earlier
# apply-based run left behind.
LAST_APPLIED="kubectl.kubernetes.io/last-applied-configuration"
echo "💾 Updating Kubernetes secret..."
kubectl -n "${NAMESPACE}" create secret generic "${SECRET_NAME}" \
  --from-file=users.conf="$CONFIG_FILE" \
  --dry-run=client -o yaml | kubectl -n "${NAMESPACE}" replace -f - > /dev/null

# Belt and braces: if the annotation is still there, remove it. The check
# prints only "present", never the annotation's value.
if [ "$(kubectl -n "${NAMESPACE}" get secret "${SECRET_NAME}" \
      -o go-template="{{with .metadata.annotations}}{{if index . \"${LAST_APPLIED}\"}}present{{end}}{{end}}")" = "present" ]; then
  kubectl -n "${NAMESPACE}" annotate secret "${SECRET_NAME}" "${LAST_APPLIED}-" > /dev/null
  echo "Removed the ${LAST_APPLIED} annotation (it held a copy of the secret)."
fi

echo "✅ Secret updated successfully"
echo ""

# Restart SFTP pods to pick up new password
echo "🔄 Restarting SFTP pods..."
kubectl -n "${NAMESPACE}" rollout restart deployment/"${DEPLOYMENT}"

echo "⏳ Waiting for pods to be ready..."
kubectl -n "${NAMESPACE}" rollout status deployment/"${DEPLOYMENT}" --timeout=60s

echo ""
echo "✅ Password rotation complete!"
echo ""
# The new password is written to a file only you can read, never to the
# terminal (where it would land in scrollback, logs or CI output).
ENV_FILE="${HOME}/.sftp-test-env"
( umask 077
  printf "export SFTP_USER='%s'\nexport SFTP_PASSWORD='%s'\n" "$USERNAME" "$NEW_PASSWORD" > "$ENV_FILE" )
chmod 600 "$ENV_FILE"
echo "The new credentials are in ${ENV_FILE} (mode 600)."
echo "Load them before running tests: source ${ENV_FILE}"
