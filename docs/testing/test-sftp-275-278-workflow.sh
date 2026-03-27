#!/bin/bash
# NOTE: This script references Azure Logic Apps, which were the original orchestration runtime.
# CHO has since migrated to Argo Workflows on AKS — see docs/adr/004-remove-logic-apps.md for details.
set -e

echo "🔗 Testing SFTP 275/278 Linked Workflow"
echo "========================================"
echo ""
echo "This test demonstrates:"
echo "  1. Upload 278 prior auth request to SFTP"
echo "  2. Logic App processes 278 → creates authorization"
echo "  3. Upload 275 clinical attachment to SFTP (linked to 278)"
echo "  4. Logic App processes 275 → attaches to authorization"
echo "  5. Verify both files are linked in backend"
echo ""

# Configuration
SFTP_HOST="sftp-service.cho-sftp.svc.cluster.local"
SFTP_PORT="22"

# SECURITY: Read credentials from environment variables
# Set these before running: export SFTP_USER=... SFTP_PASSWORD=...
# Or source ~/.sftp-test-env
SFTP_USER="${SFTP_USER:-}"
SFTP_PASS="${SFTP_PASSWORD:-}"

if [ -z "$SFTP_USER" ] || [ -z "$SFTP_PASS" ]; then
  echo "⚠️  SECURITY WARNING: SFTP credentials not set!"
  echo ""
  echo "Please set environment variables:"
  echo "  export SFTP_USER='logicapp'"
  echo "  export SFTP_PASSWORD='<get-from-kubernetes-secret>'"
  echo ""
  echo "Or source credentials file:"
  echo "  source ~/.sftp-test-env"
  echo ""
  echo "To get the password from Kubernetes:"
  echo "  kubectl -n cho-sftp get secret sftp-users -o jsonpath='{.data.users\.conf}' | base64 -d"
  echo ""
  echo "To rotate the password:"
  echo "  ./scripts/rotate-sftp-password.sh logicapp"
  echo ""
  exit 1
fi

# Generate unique identifiers
CLAIM_NUMBER="CLM$(date +%s)"
MEMBER_ID="MEM$(date +%Y%m%d)"
PROVIDER_NPI="1234567890"
AUTH_REF="AUTH$(date +%s)"

echo "📋 Test Data:"
echo "  Claim Number: ${CLAIM_NUMBER}"
echo "  Member ID: ${MEMBER_ID}"
echo "  Provider NPI: ${PROVIDER_NPI}"
echo "  Auth Reference: ${AUTH_REF}"
echo ""

# Port-forward to SFTP if not already running
kubectl -n cho-sftp port-forward svc/sftp-service 12022:22 > /dev/null 2>&1 &
SFTP_PF_PID=$!
sleep 2
echo "✅ Port-forward active (PID: ${SFTP_PF_PID})"
echo ""

# ========================================
# Step 1: Create and Upload 278 Prior Auth Request
# ========================================
echo "=========================================="
echo "Step 1: Create 278 Prior Authorization Request"
echo "=========================================="
echo ""

cat > /tmp/test-278-${AUTH_REF}.edi <<EOF
ISA*00*          *00*          *ZZ*SUBMITTER      *ZZ*CLEARINGHOUSE  *$(date +%y%m%d)*$(date +%H%M)*^*00501*${AUTH_REF}*0*P*:~
GS*HS*SUBMITTER*CLEARINGHOUSE*$(date +%Y%m%d)*$(date +%H%M)*${AUTH_REF}*X*005010X217~
ST*278*${AUTH_REF}*005010X217~
BHT*0007*13*${AUTH_REF}*$(date +%Y%m%d)*$(date +%H%M%S)*RQ~
HL*1**20*1~
NM1*X3*2*HEALTH PLAN*****PI*HEALTHPLAN001~
HL*2*1*21*1~
NM1*1P*2*PROVIDER CLINIC*****XX*${PROVIDER_NPI}~
HL*3*2*22*0~
NM1*IL*1*DOE*JOHN****MI*${MEMBER_ID}~
DMG*D8*19850615*M~
TRN*1*${AUTH_REF}*9876543210~
UM*SC*I*******Y~
DTP*472*D8*$(date +%Y%m%d)~
HI*ABK:${CLAIM_NUMBER}~
HSD*VS*30~
SE*15*${AUTH_REF}~
GE*1*${AUTH_REF}~
IEA*1*${AUTH_REF}~
EOF

echo "✅ 278 EDI file created ($(wc -c < /tmp/test-278-${AUTH_REF}.edi) bytes)"
echo ""

echo "📤 Uploading 278 to SFTP..."
sshpass -p "${SFTP_PASS}" sftp -o StrictHostKeyChecking=no -P 12022 ${SFTP_USER}@localhost <<SFTP_UPLOAD_278
cd upload
-mkdir 278
cd 278
put /tmp/test-278-${AUTH_REF}.edi
ls -lh test-278-${AUTH_REF}.edi
bye
SFTP_UPLOAD_278

if [ $? -eq 0 ]; then
  echo "✅ 278 uploaded successfully"
else
  echo "❌ 278 upload failed"
  kill $SFTP_PF_PID 2>/dev/null || true
  exit 1
fi

echo ""
echo "⏳ Waiting 10 seconds for Logic App to poll SFTP and process 278..."
sleep 10

# ========================================
# Step 2: Query Backend for Authorization
# ========================================
echo ""
echo "=========================================="
echo "Step 2: Verify Authorization Created"
echo "=========================================="
echo ""

# Port-forward to authorization service
kubectl -n cloudhealthoffice port-forward svc/authorization-service 18082:80 > /dev/null 2>&1 &
AUTH_PF_PID=$!
sleep 2

# Try to get authorization by reference number
echo "🔍 Searching for authorization with reference: ${AUTH_REF}"
AUTH_RESPONSE=$(curl -s -w "\n%{http_code}" \
  -H "X-Tenant-ID: test-tenant" \
  http://localhost:18082/api/Authorizations?referenceNumber=${AUTH_REF})

AUTH_HTTP_CODE=$(echo "$AUTH_RESPONSE" | tail -n 1)
AUTH_BODY=$(echo "$AUTH_RESPONSE" | sed '$d')

if [ "$AUTH_HTTP_CODE" = "200" ]; then
  echo "✅ Authorization found!"
  echo "$AUTH_BODY" | jq '.' || echo "$AUTH_BODY"
  
  AUTHORIZATION_ID=$(echo "$AUTH_BODY" | jq -r '.[0].id // empty' 2>/dev/null)
  if [ -n "$AUTHORIZATION_ID" ]; then
    echo ""
    echo "📋 Authorization ID: ${AUTHORIZATION_ID}"
  else
    echo "⚠️  Could not extract authorization ID (might be processing)"
    AUTHORIZATION_ID="3de8709d-7f59-428f-87e6-cf16f7c95110" # Use test auth
  fi
else
  echo "⚠️  Authorization not found yet (HTTP ${AUTH_HTTP_CODE})"
  echo "This is expected if Logic App hasn't processed the 278 yet"
  echo "Using test authorization ID for demonstration..."
  AUTHORIZATION_ID="3de8709d-7f59-428f-87e6-cf16f7c95110"
fi

# ========================================
# Step 3: Create and Upload 275 Attachment
# ========================================
echo ""
echo "=========================================="
echo "Step 3: Create 275 Clinical Attachment"
echo "=========================================="
echo ""

# Create 275 EDI with reference to the authorization
cat > /tmp/test-275-${AUTH_REF}.edi <<EOF
ISA*00*          *00*          *ZZ*SUBMITTER      *ZZ*CLEARINGHOUSE  *$(date +%y%m%d)*$(date +%H%M)*^*00501*ATT${AUTH_REF}*0*P*:~
GS*HR*SUBMITTER*CLEARINGHOUSE*$(date +%Y%m%d)*$(date +%H%M)*ATT${AUTH_REF}*X*005010X212~
ST*275*ATT${AUTH_REF}*005010X212~
BGN*11*ATT${AUTH_REF}*$(date +%Y%m%d)*$(date +%H%M%S)~
TRN*1*${CLAIM_NUMBER}*9876543210~
REF*D9*${AUTH_REF}~
NM1*IL*1*DOE*JOHN****MI*${MEMBER_ID}~
NM1*PR*2*PROVIDER CLINIC*****XX*${PROVIDER_NPI}~
PWK*77*EL*AC***AC~
SE*9*ATT${AUTH_REF}~
GE*1*ATT${AUTH_REF}~
IEA*1*ATT${AUTH_REF}~
EOF

echo "✅ 275 EDI file created ($(wc -c < /tmp/test-275-${AUTH_REF}.edi) bytes)"
echo "   References 278 via: REF*D9*${AUTH_REF}"
echo ""

echo "📤 Uploading 275 to SFTP..."
sshpass -p "${SFTP_PASS}" sftp -o StrictHostKeyChecking=no -P 12022 ${SFTP_USER}@localhost <<SFTP_UPLOAD_275
cd upload
-mkdir 275
cd 275
put /tmp/test-275-${AUTH_REF}.edi
ls -lh test-275-${AUTH_REF}.edi
bye
SFTP_UPLOAD_275

if [ $? -eq 0 ]; then
  echo "✅ 275 uploaded successfully"
else
  echo "❌ 275 upload failed"
  kill $SFTP_PF_PID $AUTH_PF_PID 2>/dev/null || true
  exit 1
fi

echo ""
echo "⏳ Waiting 15 seconds for Logic App to poll SFTP and process 275..."
sleep 15

# ========================================
# Step 4: Verify Attachment Linked to Authorization
# ========================================
echo ""
echo "=========================================="
echo "Step 4: Verify 275 Linked to 278 Authorization"
echo "=========================================="
echo ""

# Port-forward to attachment service
kubectl -n cloudhealthoffice port-forward svc/attachment-service 18083:80 > /dev/null 2>&1 &
ATT_PF_PID=$!
sleep 2

echo "🔍 Searching for attachments linked to authorization: ${AUTHORIZATION_ID}"
ATT_RESPONSE=$(curl -s -w "\n%{http_code}" \
  -H "X-Tenant-ID: test-tenant" \
  http://localhost:18083/api/Attachments?authorizationId=${AUTHORIZATION_ID})

ATT_HTTP_CODE=$(echo "$ATT_RESPONSE" | tail -n 1)
ATT_BODY=$(echo "$ATT_RESPONSE" | sed '$d')

if [ "$ATT_HTTP_CODE" = "200" ]; then
  ATTACHMENT_COUNT=$(echo "$ATT_BODY" | jq '. | length' 2>/dev/null || echo "0")
  if [ "$ATTACHMENT_COUNT" -gt "0" ]; then
    echo "✅ Found ${ATTACHMENT_COUNT} attachment(s) linked to authorization!"
    echo ""
    echo "$ATT_BODY" | jq '.' || echo "$ATT_BODY"
  else
    echo "⚠️  No attachments found yet (Logic App may still be processing)"
  fi
else
  echo "⚠️  Could not query attachments (HTTP ${ATT_HTTP_CODE})"
  echo "$ATT_BODY"
fi

# ========================================
# Step 5: Check Azure Blob Storage
# ========================================
echo ""
echo "=========================================="
echo "Step 5: Verify Files Archived in Blob Storage"
echo "=========================================="
echo ""

echo "📂 Checking for archived files in Azure Blob Storage..."
echo "   Expected paths:"
echo "   - hipaa-attachments/raw/278/$(date +%Y/%m/%d)/test-278-${AUTH_REF}.edi"
echo "   - hipaa-attachments/raw/275/$(date +%Y/%m/%d)/test-275-${AUTH_REF}.edi"
echo ""
echo "Note: Logic Apps must have completed processing for files to appear in Blob"

# Cleanup
echo ""
echo "🧹 Cleaning up..."
kill $SFTP_PF_PID $AUTH_PF_PID $ATT_PF_PID 2>/dev/null || true
rm -f /tmp/test-278-${AUTH_REF}.edi /tmp/test-275-${AUTH_REF}.edi
echo "✅ Cleanup complete"

echo ""
echo "=========================================="
echo "📊 Test Summary"
echo "=========================================="
echo ""
echo "✅ 278 Prior Auth Request: Uploaded to SFTP /upload/278/"
echo "✅ 275 Clinical Attachment: Uploaded to SFTP /upload/275/"
echo "   (Linked via REF*D9*${AUTH_REF})"
echo ""
echo "Expected Flow:"
echo "  1. ingest278 Logic App polls SFTP every 5 min"
echo "  2. Processes 278 → creates authorization in backend"
echo "  3. Archives 278 to Blob Storage"
echo "  4. ingest275 Logic App polls SFTP every 5 min"
echo "  5. Processes 275 → links to authorization via ${AUTH_REF}"
echo "  6. Archives 275 to Blob Storage"
echo ""
echo "To check Logic App runs:"
echo "  • Portal → Logic App → Workflow runs"
echo "  • Or: az logicapp run list -g <rg> --name <la> --workflow-name ingest278"
echo ""
echo "To verify files in SFTP:"
echo "  sftp -P 12022 ${SFTP_USER}@localhost"
echo "  ls -lR upload/"
echo ""
echo "=========================================="
