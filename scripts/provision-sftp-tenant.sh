#!/bin/bash
set -e

echo "🏥 SFTP Tenant Provisioning Tool"
echo "================================="
echo ""

# Check arguments
if [ $# -lt 2 ]; then
  echo "Usage: $0 <tenant-id> <tenant-name> [key-vault-name] [--environments prod,preprod,dev]"
  echo ""
  echo "Examples:"
  echo "  $0 bcbs-florida 'Blue Cross Blue Shield of Florida'"
  echo "  $0 aetna 'Aetna Health Plans' cho-keyvault-prod"
  echo "  $0 cigna 'Cigna HealthSpring' cho-keyvault-prod --environments prod,preprod,dev"
  echo "  $0 humana 'Humana' cho-keyvault-prod --environments prod,preprod"
  echo ""
  exit 1
fi

TENANT_ID="$1"
TENANT_NAME="$2"
KEY_VAULT="${3:-cho-keyvault-prod}"
ENVIRONMENTS="prod"  # Default to production only
NAMESPACE="cho-sftp"
SECRET_NAME="sftp-tenant-users"

# Parse optional --environments flag
shift 2  # Remove first two args
shift || true  # Remove key vault arg if present
while [[ $# -gt 0 ]]; do
  case $1 in
    --environments)
      ENVIRONMENTS="$2"
      shift 2
      ;;
    *)
      shift
      ;;
  esac
done

# Validate tenant ID format (lowercase, alphanumeric, hyphens only)
if [[ ! "$TENANT_ID" =~ ^[a-z0-9-]+$ ]]; then
  echo "❌ Invalid tenant ID format"
  echo "   Must be lowercase alphanumeric with hyphens only"
  echo "   Example: bcbs-florida, aetna, cigna-healthspring"
  exit 1
fi

echo "Tenant Configuration:"
echo "  Tenant ID: ${TENANT_ID}"
echo "  Tenant Name: ${TENANT_NAME}"
echo "  Key Vault: ${KEY_VAULT}"
echo "  Environments: ${ENVIRONMENTS}"
echo ""

# Check if tenant already exists
echo "🔍 Checking for existing tenant..."
EXISTING=$(kubectl -n ${NAMESPACE} get secret ${SECRET_NAME} -o jsonpath='{.data.users\.conf}' 2>/dev/null | base64 -d | grep "^${TENANT_ID}:" || true)

if [ -n "$EXISTING" ]; then
  echo "❌ Tenant '${TENANT_ID}' already exists!"
  echo "   Existing configuration: ${EXISTING}"
  echo ""
  echo "To update this tenant, use:"
  echo "  ./scripts/update-sftp-tenant.sh ${TENANT_ID}"
  exit 1
fi

# Generate secure password
echo "🔐 Generating secure password..."
PASSWORD=$(openssl rand -base64 32 | tr -d "=+/" | cut -c1-32)
echo "✅ Password generated (32 chars)"
echo ""

# Get next available UID
echo "🔢 Calculating next UID..."
CURRENT_USERS=$(kubectl -n ${NAMESPACE} get secret ${SECRET_NAME} -o jsonpath='{.data.users\.conf}' 2>/dev/null | base64 -d || echo "")
if [ -z "$CURRENT_USERS" ]; then
  NEXT_UID=1100
else
  MAX_UID=$(echo "$CURRENT_USERS" | awk -F: '{print $3}' | sort -n | tail -1)
  NEXT_UID=$((MAX_UID + 1))
fi
NEXT_GID=$NEXT_UID

echo "✅ Assigned UID/GID: ${NEXT_UID}"
echo ""

# Create user entry
USER_ENTRY="${TENANT_ID}:${PASSWORD}:${NEXT_UID}:${NEXT_GID}:inbound,outbound:/bin/false:/tenants/${TENANT_ID}"

echo "📝 Creating user entry..."
echo "   Format: username:password:uid:gid:dirs:shell:chroot"
echo "   Entry: ${TENANT_ID}:<password>:${NEXT_UID}:${NEXT_GID}:inbound,outbound:/bin/false:/tenants/${TENANT_ID}"
echo ""

# Update Kubernetes secret
echo "💾 Updating Kubernetes secret..."

# Get current config
CURRENT_CONFIG=$(kubectl -n ${NAMESPACE} get secret ${SECRET_NAME} -o jsonpath='{.data.users\.conf}' 2>/dev/null | base64 -d || echo "")

# Append new user
NEW_CONFIG="${CURRENT_CONFIG}${USER_ENTRY}"

# Update secret
kubectl -n ${NAMESPACE} create secret generic ${SECRET_NAME} \
  --from-literal=users.conf="${NEW_CONFIG}" \
  --dry-run=client -o yaml | kubectl apply -f -

echo "✅ Secret updated"
echo ""

# Create directory structure in SFTP pod
echo "📁 Creating tenant home directory..."

# Get SFTP pod name
SFTP_POD=$(kubectl -n ${NAMESPACE} get pod -l app=sftp-server -o jsonpath='{.items[0].metadata.name}')

if [ -z "$SFTP_POD" ]; then
  echo "⚠️  SFTP pod not found - directories will be created on next deployment"
else
  echo "   Pod: ${SFTP_POD}"
  
  # Create tenant home directory with environment subdirectories
  IFS=',' read -ra ENV_ARRAY <<< "$ENVIRONMENTS"
  for ENV in "${ENV_ARRAY[@]}"; do
    ENV=$(echo "$ENV" | xargs)  # Trim whitespace
    echo "   Creating environment: ${ENV}"
    
    kubectl -n ${NAMESPACE} exec ${SFTP_POD} -- bash -c "
      set -e
      
      # Create environment directory
      mkdir -p /home/tenants/${TENANT_ID}/${ENV}
      
      # Set ownership
      chown ${NEXT_UID}:${NEXT_GID} /home/tenants/${TENANT_ID}/${ENV}
      
      # Set permissions
      chmod 750 /home/tenants/${TENANT_ID}/${ENV}
    "
  done
  
  # Create base tenant directory and set ownership
  kubectl -n ${NAMESPACE} exec ${SFTP_POD} -- bash -c "
    set -e
    chown ${NEXT_UID}:${NEXT_GID} /home/tenants/${TENANT_ID}
    chmod 750 /home/tenants/${TENANT_ID}
    ls -lah /home/tenants/${TENANT_ID}
  "
  
  echo "✅ Tenant directory structure created"
  echo "   Environments: ${ENVIRONMENTS}"
  echo "ℹ️  Trading partner directories will be created via provision-trading-partner.sh"
fi
echo ""

# Store password in Azure Key Vault
echo "🔑 Storing credentials in Azure Key Vault..."
az keyvault secret set \
  --vault-name "${KEY_VAULT}" \
  --name "sftp-${TENANT_ID}-password" \
  --value "${PASSWORD}" \
  --content-type "application/x-sftp-password" \
  --tags "tenant=${TENANT_ID}" "tenant-name=${TENANT_NAME}" "created=$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --output none

echo "✅ Password stored in Key Vault: sftp-${TENANT_ID}-password"
echo ""

# Create tenant metadata in CosmosDB (optional)
echo "📊 Creating tenant metadata..."
METADATA_JSON=$(cat <<EOF
{
  "id": "${TENANT_ID}",
  "tenantName": "${TENANT_NAME}",
  "sftpUsername": "${TENANT_ID}",
  "sftpUid": ${NEXT_UID},
  "sftpGid": ${NEXT_GID},
  "sftpHomeDirectory": "/tenants/${TENANT_ID}",
  "environments": [$(echo "${ENVIRONMENTS}" | sed 's/,/", "/g' | sed 's/^/"/' | sed 's/$/"/')],
  "tradingPartners": [],
  "createdAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "status": "active",
  "keyVaultSecretName": "sftp-${TENANT_ID}-password"
}
EOF
)

echo "$METADATA_JSON" | jq '.' > /tmp/tenant-${TENANT_ID}.json
echo "✅ Metadata saved to: /tmp/tenant-${TENANT_ID}.json"
echo ""

# Test connection (if sshpass is available)
if command -v sshpass &> /dev/null; then
  echo "🧪 Testing SFTP connection..."
  
  # Port-forward temporarily
  kubectl -n ${NAMESPACE} port-forward svc/sftp-service 12022:22 > /dev/null 2>&1 &
  PF_PID=$!
  sleep 2
  
  # Test connection
  if sshpass -p "${PASSWORD}" sftp -o StrictHostKeyChecking=no -P 12022 ${TENANT_ID}@localhost <<EOF
ls
pwd
exit
EOF
  then
    echo "✅ SFTP connection test successful"
  else
    echo "⚠️  SFTP connection test failed - check configuration"
  fi
  
  # Cleanup port-forward
  kill $PF_PID 2>/dev/null || true
else
  echo "⚠️  sshpass not installed - skipping connection test"
fi

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "✅ Tenant Provisioned Successfully!"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "Tenant Details:"
echo "  Tenant ID: ${TENANT_ID}"
echo "  Tenant Name: ${TENANT_NAME}"
echo "  SFTP Username: ${TENANT_ID}"
echo "  UID/GID: ${NEXT_UID}"
echo "  Home Directory: /tenants/${TENANT_ID}"
echo ""
echo "SFTP Access:"
echo "  Host: sftp.cloudhealthoffice.com"
echo "  Port: 22"
echo "  Username: ${TENANT_ID}"
echo "  Password: <stored-in-key-vault>"
echo ""
echo "Connection Command:"
# nosec - SFTP over SSH (port 22) is encrypted, not HTTP
echo "  sftp ${TENANT_ID}@sftp.cloudhealthoffice.com"
echo ""
echo "Directory Structure:"
echo "  Home directory: /tenants/${TENANT_ID}/"
echo "  Environments: ${ENVIRONMENTS}"
IFS=',' read -ra ENV_ARRAY <<< "$ENVIRONMENTS"
for ENV in "${ENV_ARRAY[@]}"; do
  ENV=$(echo "$ENV" | xargs)
  echo "    - ${ENV}/"
done
echo "  Trading partner subdirectories will be created when you add partners"
echo ""
echo "Retrieve Password:"
echo "  az keyvault secret show --vault-name ${KEY_VAULT} --name sftp-${TENANT_ID}-password --query value -o tsv"
echo ""
echo "Next Steps:"
echo "  1. Add trading partners for this tenant (specify environment with --environment flag):"
echo "     ./scripts/provision-trading-partner.sh ${TENANT_ID} availity 'Availity Clearinghouse' --environment prod"
echo "     ./scripts/provision-trading-partner.sh ${TENANT_ID} change-healthcare 'Change Healthcare' --environment preprod"
echo "  2. Send credentials to tenant via secure channel (NOT email)"
echo "  3. Configure trading partner metadata in Trading Partner Service"
echo "  4. Test file exchange with tenant"
echo "  5. Monitor /tenants/${TENANT_ID}/<environment>/<partner>/ for activity"
echo ""
echo "Documentation:"
echo "  - See docs/SFTP-MULTI-TENANT-ARCHITECTURE.md for details"
echo "  - See docs/SFTP-CREDENTIALS-SECURITY.md for security best practices"
echo ""
