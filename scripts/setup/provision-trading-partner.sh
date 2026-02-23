#!/bin/bash
set -e

echo "🤝 Trading Partner Provisioning Tool"
echo "====================================="
echo ""

# Check arguments
if [ $# -lt 3 ]; then
  echo "Usage: $0 <tenant-id> <trading-partner-id> <trading-partner-name> [--transactions TYPE1,TYPE2,...]"
  echo ""
  echo "Arguments:"
  echo "  tenant-id              Existing tenant ID (e.g., bcbs-florida)"
  echo "  trading-partner-id     Partner slug (e.g., availity, change-healthcare)"
  echo "  trading-partner-name   Full name (e.g., 'Availity Clearinghouse')"
  echo "  --transactions         Comma-separated X12 transaction types (e.g., 276,277,278,837)"
  echo ""
  echo "Examples:"
  echo "  $0 bcbs-florida availity 'Availity Clearinghouse' --transactions 276,277,278,837"
  echo "  $0 bcbs-florida change-healthcare 'Change Healthcare' --transactions 835,837"
  echo "  $0 bcbs-florida quest-diagnostics 'Quest Diagnostics' --transactions 275"
  echo ""
  exit 1
fi

TENANT_ID="$1"
PARTNER_ID="$2"
PARTNER_NAME="$3"
NAMESPACE="cho-sftp"
SECRET_NAME="sftp-tenant-users"

# Parse transactions parameter
TRANSACTIONS=""
if [ "$4" = "--transactions" ] && [ -n "$5" ]; then
  TRANSACTIONS="$5"
else
  # Default to common transaction types
  TRANSACTIONS="276,277,278,834,835,837"
fi

# Validate tenant ID format
if [[ ! "$TENANT_ID" =~ ^[a-z0-9-]+$ ]]; then
  echo "❌ Invalid tenant ID format"
  echo "   Must be lowercase alphanumeric with hyphens only"
  exit 1
fi

# Validate partner ID format
if [[ ! "$PARTNER_ID" =~ ^[a-z0-9-]+$ ]]; then
  echo "❌ Invalid trading partner ID format"
  echo "   Must be lowercase alphanumeric with hyphens only"
  echo "   Example: availity, change-healthcare, quest-diagnostics"
  exit 1
fi

echo "Configuration:"
echo "  Tenant ID: ${TENANT_ID}"
echo "  Trading Partner ID: ${PARTNER_ID}"
echo "  Trading Partner Name: ${PARTNER_NAME}"
echo "  Transaction Types: ${TRANSACTIONS}"
echo ""

# Check if tenant exists
echo "🔍 Verifying tenant exists..."
TENANT_EXISTS=$(kubectl -n ${NAMESPACE} get secret ${SECRET_NAME} -o jsonpath='{.data.users\.conf}' 2>/dev/null | base64 -d | grep "^${TENANT_ID}:" || true)

if [ -z "$TENANT_EXISTS" ]; then
  echo "❌ Tenant '${TENANT_ID}' does not exist!"
  echo ""
  echo "First provision the tenant:"
  echo "  ./scripts/provision-sftp-tenant.sh ${TENANT_ID} 'Tenant Name'"
  exit 1
fi

# Extract tenant UID/GID
TENANT_UID=$(echo "$TENANT_EXISTS" | cut -d: -f3)
TENANT_GID=$(echo "$TENANT_EXISTS" | cut -d: -f4)

echo "✅ Tenant found (UID: ${TENANT_UID}, GID: ${TENANT_GID})"
echo ""

# Get SFTP pod name
echo "📁 Creating trading partner directory structure..."
SFTP_POD=$(kubectl -n ${NAMESPACE} get pod -l app=sftp-server -o jsonpath='{.items[0].metadata.name}')

if [ -z "$SFTP_POD" ]; then
  echo "❌ SFTP pod not found"
  echo "   Ensure SFTP server is running in namespace: ${NAMESPACE}"
  exit 1
fi

echo "   Pod: ${SFTP_POD}"

# Check if partner directory already exists
EXISTING_DIR=$(kubectl -n ${NAMESPACE} exec ${SFTP_POD} -- bash -c "test -d /home/tenants/${TENANT_ID}/${PARTNER_ID} && echo 'exists' || echo 'new'" 2>/dev/null)

if [ "$EXISTING_DIR" = "exists" ]; then
  echo "⚠️  Trading partner directory already exists: /tenants/${TENANT_ID}/${PARTNER_ID}"
  echo ""
  read -p "Overwrite existing directory structure? (y/N): " -n 1 -r
  echo ""
  if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "❌ Cancelled"
    exit 1
  fi
fi

# Convert comma-separated transactions to array
IFS=',' read -ra TRANS_ARRAY <<< "$TRANSACTIONS"

# Build directory creation command
DIR_CREATE_CMD="set -e\n"
DIR_CREATE_CMD+="mkdir -p /home/tenants/${TENANT_ID}/${PARTNER_ID}/{inbound,outbound}\n"

# Create transaction type directories
for trans in "${TRANS_ARRAY[@]}"; do
  trans=$(echo "$trans" | xargs) # trim whitespace
  DIR_CREATE_CMD+="mkdir -p /home/tenants/${TENANT_ID}/${PARTNER_ID}/inbound/${trans}\n"
  DIR_CREATE_CMD+="mkdir -p /home/tenants/${TENANT_ID}/${PARTNER_ID}/outbound/${trans}\n"
done

DIR_CREATE_CMD+="chown -R ${TENANT_UID}:${TENANT_GID} /home/tenants/${TENANT_ID}/${PARTNER_ID}\n"
DIR_CREATE_CMD+="chmod 750 /home/tenants/${TENANT_ID}/${PARTNER_ID}\n"
DIR_CREATE_CMD+="chmod 770 /home/tenants/${TENANT_ID}/${PARTNER_ID}/inbound\n"
DIR_CREATE_CMD+="chmod -R 770 /home/tenants/${TENANT_ID}/${PARTNER_ID}/inbound/*\n"
DIR_CREATE_CMD+="chmod 550 /home/tenants/${TENANT_ID}/${PARTNER_ID}/outbound\n"
DIR_CREATE_CMD+="chmod -R 550 /home/tenants/${TENANT_ID}/${PARTNER_ID}/outbound/*\n"
DIR_CREATE_CMD+="ls -lah /home/tenants/${TENANT_ID}/${PARTNER_ID}\n"

# Execute directory creation
kubectl -n ${NAMESPACE} exec ${SFTP_POD} -- bash -c "$(echo -e "$DIR_CREATE_CMD")"

echo "✅ Directory structure created"
echo ""

# Create trading partner metadata
echo "📊 Creating trading partner metadata..."

METADATA_JSON=$(cat <<EOF
{
  "id": "${TENANT_ID}-${PARTNER_ID}",
  "tenantId": "${TENANT_ID}",
  "tradingPartnerId": "${PARTNER_ID}",
  "tradingPartnerName": "${PARTNER_NAME}",
  "sftpConfig": {
    "paths": {
      "inbound": {},
      "outbound": {}
    }
  },
  "x12Config": {
    "transactionTypes": $(printf '%s\n' "${TRANS_ARRAY[@]}" | jq -R . | jq -s .)
  },
  "createdAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "status": "active"
}
EOF
)

# Build inbound/outbound paths dynamically
INBOUND_PATHS=""
OUTBOUND_PATHS=""
for trans in "${TRANS_ARRAY[@]}"; do
  trans=$(echo "$trans" | xargs)
  INBOUND_PATHS+=",\"${trans}\": \"/tenants/${TENANT_ID}/${PARTNER_ID}/inbound/${trans}\""
  OUTBOUND_PATHS+=",\"${trans}\": \"/tenants/${TENANT_ID}/${PARTNER_ID}/outbound/${trans}\""
done
INBOUND_PATHS="${INBOUND_PATHS:1}" # Remove leading comma
OUTBOUND_PATHS="${OUTBOUND_PATHS:1}"

# Update JSON with paths
METADATA_JSON=$(echo "$METADATA_JSON" | jq ".sftpConfig.paths.inbound = {${INBOUND_PATHS}}")
METADATA_JSON=$(echo "$METADATA_JSON" | jq ".sftpConfig.paths.outbound = {${OUTBOUND_PATHS}}")

echo "$METADATA_JSON" | jq '.' > /tmp/trading-partner-${TENANT_ID}-${PARTNER_ID}.json
echo "✅ Metadata saved to: /tmp/trading-partner-${TENANT_ID}-${PARTNER_ID}.json"
echo ""

# Test directory access
echo "🧪 Verifying directory permissions..."
kubectl -n ${NAMESPACE} exec ${SFTP_POD} -- bash -c "
  stat -c '%a %U:%G %n' /home/tenants/${TENANT_ID}/${PARTNER_ID} /home/tenants/${TENANT_ID}/${PARTNER_ID}/inbound /home/tenants/${TENANT_ID}/${PARTNER_ID}/outbound
" || echo "⚠️  Permission check failed"

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "✅ Trading Partner Provisioned!"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "Trading Partner Details:"
echo "  Tenant ID: ${TENANT_ID}"
echo "  Partner ID: ${PARTNER_ID}"
echo "  Partner Name: ${PARTNER_NAME}"
echo "  Base Directory: /tenants/${TENANT_ID}/${PARTNER_ID}"
echo ""
echo "Transaction Types:"
for trans in "${TRANS_ARRAY[@]}"; do
  trans=$(echo "$trans" | xargs)
  echo "  ${trans}:"
  echo "    Inbound:  /${PARTNER_ID}/inbound/${trans}/"
  echo "    Outbound: /${PARTNER_ID}/outbound/${trans}/"
done
echo ""
echo "SFTP Test:"
echo "  sftp ${TENANT_ID}@sftp.cloudhealthoffice.com"
echo "  cd ${PARTNER_ID}/inbound/276"
echo "  put test-claim-status-request.edi"
echo ""
echo "Next Steps:"
echo "  1. Import trading partner metadata to CosmosDB:"
echo "     cat /tmp/trading-partner-${TENANT_ID}-${PARTNER_ID}.json | \\"
echo "       az cosmosdb sql container item create \\"
echo "         --account-name cloudhealthoffice-cosmos \\"
echo "         --database-name CloudHealthOffice \\"
echo "         --container-name TradingPartners \\"
echo "         --partition-key-value '${TENANT_ID}' \\"
echo "         --body @-"
echo "  2. Update Argo Workflows to route files for this partner"
echo "  3. Test file exchange with tenant"
echo "  4. Monitor /tenants/${TENANT_ID}/${PARTNER_ID}/ for activity"
echo ""
echo "Documentation:"
echo "  - See docs/SFTP-MULTI-TENANT-ARCHITECTURE.md for architecture details"
echo "  - See services/trading-partner-service/README.md for API usage"
echo ""
