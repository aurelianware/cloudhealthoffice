#!/bin/bash
set -e

echo "🔐 SFTP Password Rotation Tool"
echo "=============================="
echo ""

# Configuration
NAMESPACE="cho-sftp"
SECRET_NAME="sftp-users"
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

# Get current users.conf
echo "📥 Fetching current SFTP configuration..."
CURRENT_CONFIG=$(kubectl -n ${NAMESPACE} get secret ${SECRET_NAME} -o jsonpath='{.data.users\.conf}' | base64 -d)

echo "Current users:"
echo "$CURRENT_CONFIG"
echo ""

# Update the password for the specified user
echo "🔄 Updating password for user: ${USERNAME}"
UPDATED_CONFIG=$(echo "$CURRENT_CONFIG" | sed "s/^${USERNAME}:[^:]*:/${USERNAME}:${NEW_PASSWORD}:/")

if [ "$UPDATED_CONFIG" = "$CURRENT_CONFIG" ]; then
  echo "❌ User '${USERNAME}' not found in configuration"
  exit 1
fi

echo "Updated configuration:"
echo "$UPDATED_CONFIG"
echo ""

# Encode to base64
ENCODED_CONFIG=$(echo "$UPDATED_CONFIG" | base64)

# Update the secret
echo "💾 Updating Kubernetes secret..."
kubectl -n ${NAMESPACE} patch secret ${SECRET_NAME} \
  --type='json' \
  -p="[{\"op\": \"replace\", \"path\": \"/data/users.conf\", \"value\":\"${ENCODED_CONFIG}\"}]"

echo "✅ Secret updated successfully"
echo ""

# Restart SFTP pods to pick up new password
echo "🔄 Restarting SFTP pods..."
kubectl -n ${NAMESPACE} rollout restart deployment/sftp-service

echo "⏳ Waiting for pods to be ready..."
kubectl -n ${NAMESPACE} rollout status deployment/sftp-service --timeout=60s

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
