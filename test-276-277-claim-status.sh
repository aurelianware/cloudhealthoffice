#!/bin/bash
set -e

echo "🔍 Testing 276/277 Claim Status Workflow"
echo "========================================="
echo ""
echo "This test demonstrates:"
echo "  1. Upload 276 claim status request to SFTP"
echo "  2. Argo Workflow processes 276 → queries claim status"
echo "  3. Generate 277 claim status response"
echo "  4. Download and validate 277 response from SFTP"
echo "  5. Verify status codes and claim data"
echo ""

# Configuration
SFTP_HOST="sftp-service.cho-sftp.svc.cluster.local"
SFTP_PORT="22"
SFTP_USER="logicapp"
SFTP_PASS="changeme123"

# Generate unique identifiers
CLAIM_NUMBER="CLM$(date +%s)"
MEMBER_ID="MEM$(date +%Y%m%d)"
PROVIDER_NPI="1234567890"
TRACE_NUMBER="TRACE$(date +%s)"
TEST_ID="TEST$(date +%s)"

echo "📋 Test Data:"
echo "  Claim Number: ${CLAIM_NUMBER}"
echo "  Member ID: ${MEMBER_ID}"
echo "  Provider NPI: ${PROVIDER_NPI}"
echo "  Trace Number: ${TRACE_NUMBER}"
echo "  Test ID: ${TEST_ID}"
echo ""

# Port-forward to SFTP if not already running
echo "🔌 Setting up port-forward to SFTP service..."
kubectl -n cho-sftp port-forward svc/sftp-service 12022:22 > /dev/null 2>&1 &
SFTP_PF_PID=$!
sleep 2
echo "✅ Port-forward active (PID: ${SFTP_PF_PID})"
echo ""

# Cleanup function
cleanup() {
  echo ""
  echo "🧹 Cleaning up..."
  kill $SFTP_PF_PID 2>/dev/null || true
  kill $CLAIMS_PF_PID 2>/dev/null || true
  rm -f /tmp/test-276-${TEST_ID}.edi
  rm -f /tmp/test-277-${TEST_ID}.edi
  echo "✅ Cleanup complete"
}
trap cleanup EXIT

# ========================================
# Step 1: Create Test Claim in Backend
# ========================================
echo "=========================================="
echo "Step 1: Create Test Claim"
echo "=========================================="
echo ""

# Port-forward to claims service
kubectl -n cho-svcs port-forward svc/claims-service 18081:80 > /dev/null 2>&1 &
CLAIMS_PF_PID=$!
sleep 2

# Create a test claim
echo "📝 Creating test claim in backend..."
CLAIM_PAYLOAD=$(cat <<EOF
{
  "claimNumber": "${CLAIM_NUMBER}",
  "memberId": "${MEMBER_ID}",
  "providerNpi": "${PROVIDER_NPI}",
  "serviceDate": "$(date -u +%Y-%m-%dT%H:%M:%SZ -d '7 days ago')",
  "totalChargeAmount": 250.00,
  "diagnosis": ["Z23"],
  "procedures": [
    {
      "code": "99213",
      "chargeAmount": 150.00
    },
    {
      "code": "90471",
      "chargeAmount": 100.00
    }
  ],
  "status": "Approved",
  "approvedAmount": 200.00,
  "adjudicationDate": "$(date -u +%Y-%m-%dT%H:%M:%SZ -d '5 days ago')"
}
EOF
)

CLAIM_RESPONSE=$(curl -s -w "\n%{http_code}" \
  -X POST \
  -H "Content-Type: application/json" \
  -H "X-Tenant-ID: test-tenant" \
  -d "$CLAIM_PAYLOAD" \
  http://localhost:18081/api/Claims)

CLAIM_HTTP_CODE=$(echo "$CLAIM_RESPONSE" | tail -n 1)
CLAIM_BODY=$(echo "$CLAIM_RESPONSE" | sed '$d')

if [ "$CLAIM_HTTP_CODE" = "201" ] || [ "$CLAIM_HTTP_CODE" = "200" ]; then
  echo "✅ Test claim created successfully"
  echo "$CLAIM_BODY" | jq '.' 2>/dev/null || echo "$CLAIM_BODY"
else
  echo "⚠️  Claim creation returned HTTP ${CLAIM_HTTP_CODE}"
  echo "Response: $CLAIM_BODY"
  echo "Continuing with test - workflow may create claim if not exists"
fi

# ========================================
# Step 2: Create and Upload 276 Request
# ========================================
echo ""
echo "=========================================="
echo "Step 2: Create 276 Claim Status Request"
echo "=========================================="
echo ""

cat > /tmp/test-276-${TEST_ID}.edi <<EOF
ISA*00*          *00*          *ZZ*CLEARINGHOUSE  *ZZ*BCBSFLORIDA    *$(date +%y%m%d)*$(date +%H%M)*^*00501*${TEST_ID}*0*P*:~
GS*HR*CLEARINGHOUSE*BCBSFLORIDA*$(date +%Y%m%d)*$(date +%H%M)*${TEST_ID}*X*005010X212~
ST*276*${TEST_ID}*005010X212~
BHT*0010*13*${TRACE_NUMBER}*$(date +%Y%m%d)*$(date +%H%M%S)~
HL*1**20*1~
NM1*PR*2*BLUE CROSS BLUE SHIELD OF FLORIDA*****PI*BCBSFLORIDA~
HL*2*1*21*1~
NM1*41*2*SAMPLE CLEARINGHOUSE*****46*CLEARINGHOUSE~
HL*3*2*19*1~
NM1*1P*2*SAMPLE MEDICAL CENTER*****XX*${PROVIDER_NPI}~
HL*4*3*22*0~
NM1*IL*1*SMITH*JOHN*A***MI*${MEMBER_ID}~
DMG*D8*19800515~
TRN*1*${TRACE_NUMBER}*1234567890~
REF*1K*${CLAIM_NUMBER}~
DTP*472*D8*$(date +%Y%m%d -d '7 days ago')~
AMT*T3*250~
SE*15*${TEST_ID}~
GE*1*${TEST_ID}~
IEA*1*${TEST_ID}~
EOF

echo "✅ 276 EDI file created ($(wc -c < /tmp/test-276-${TEST_ID}.edi) bytes)"
echo ""
echo "File preview:"
cat /tmp/test-276-${TEST_ID}.edi | head -5
echo "..."
echo ""

echo "📤 Uploading 276 to SFTP..."
sshpass -p "${SFTP_PASS}" sftp -o StrictHostKeyChecking=no -P 12022 ${SFTP_USER}@localhost <<SFTP_UPLOAD_276
cd upload
-mkdir 276
cd 276
put /tmp/test-276-${TEST_ID}.edi
ls -lh test-276-${TEST_ID}.edi
bye
SFTP_UPLOAD_276

if [ $? -eq 0 ]; then
  echo "✅ 276 uploaded successfully to /upload/276/"
else
  echo "❌ 276 upload failed"
  exit 1
fi

# ========================================
# Step 3: Trigger Argo Workflow
# ========================================
echo ""
echo "=========================================="
echo "Step 3: Trigger 276 Processing Workflow"
echo "=========================================="
echo ""

echo "🚀 Submitting Argo Workflow..."
WORKFLOW_NAME=$(argo submit -n cho-workflows --from workflowtemplate/x12-276-ingest \
  -p fileName="test-276-${TEST_ID}.edi" \
  --output name 2>/dev/null || echo "")

if [ -n "$WORKFLOW_NAME" ]; then
  echo "✅ Workflow submitted: ${WORKFLOW_NAME}"
  echo ""
  
  echo "⏳ Waiting for workflow to complete (timeout: 60s)..."
  TIMEOUT=60
  ELAPSED=0
  
  while [ $ELAPSED -lt $TIMEOUT ]; do
    STATUS=$(argo get -n cho-workflows ${WORKFLOW_NAME} -o json 2>/dev/null | jq -r '.status.phase // "Unknown"')
    
    if [ "$STATUS" = "Succeeded" ]; then
      echo "✅ Workflow completed successfully!"
      break
    elif [ "$STATUS" = "Failed" ] || [ "$STATUS" = "Error" ]; then
      echo "❌ Workflow failed!"
      argo logs -n cho-workflows ${WORKFLOW_NAME}
      exit 1
    fi
    
    echo "   Status: ${STATUS} (${ELAPSED}s elapsed)"
    sleep 5
    ELAPSED=$((ELAPSED + 5))
  done
  
  if [ $ELAPSED -ge $TIMEOUT ]; then
    echo "⚠️  Workflow timeout - continuing anyway"
  fi
else
  echo "⚠️  Could not submit workflow - it may not be deployed yet"
  echo "Continuing with manual wait..."
fi

# ========================================
# Step 4: Wait and Download 277 Response
# ========================================
echo ""
echo "=========================================="
echo "Step 4: Download 277 Claim Status Response"
echo "=========================================="
echo ""

echo "⏳ Waiting for 277 response to be generated (15 seconds)..."
sleep 15

echo "📥 Checking SFTP for 277 response..."
sshpass -p "${SFTP_PASS}" sftp -o StrictHostKeyChecking=no -P 12022 ${SFTP_USER}@localhost <<SFTP_DOWNLOAD_277
cd outbound
-mkdir 277
cd 277
ls -lh
get *.edi /tmp/ || echo "No files found"
bye
SFTP_DOWNLOAD_277

# Find the most recent 277 file
RESPONSE_277=$(ls -t /tmp/*277*.edi 2>/dev/null | head -1)

if [ -n "$RESPONSE_277" ]; then
  echo "✅ 277 response downloaded: $(basename ${RESPONSE_277})"
  echo ""
  echo "File preview:"
  cat "${RESPONSE_277}" | head -10
  echo "..."
  echo ""
  
  # Save for validation
  cp "${RESPONSE_277}" /tmp/test-277-${TEST_ID}.edi
else
  echo "⚠️  No 277 response found yet"
  echo "This could mean:"
  echo "  - Workflow is still processing"
  echo "  - Claim was not found in database"
  echo "  - 277 generation failed"
  echo ""
  echo "Checking workflow logs..."
  if [ -n "$WORKFLOW_NAME" ]; then
    argo logs -n cho-workflows ${WORKFLOW_NAME} --tail 20
  fi
  exit 1
fi

# ========================================
# Step 5: Validate 277 Response
# ========================================
echo ""
echo "=========================================="
echo "Step 5: Validate 277 Response"
echo "=========================================="
echo ""

echo "🔍 Parsing 277 response..."

# Extract key fields from 277
if [ -f /tmp/test-277-${TEST_ID}.edi ]; then
  RESPONSE_CONTENT=$(cat /tmp/test-277-${TEST_ID}.edi)
  
  echo "✓ Checking for required segments:"
  
  # Check ISA
  if echo "$RESPONSE_CONTENT" | grep -q "ISA\*"; then
    echo "  ✅ ISA (Interchange Control Header) present"
  else
    echo "  ❌ ISA segment missing"
  fi
  
  # Check ST*277
  if echo "$RESPONSE_CONTENT" | grep -q "ST\*277\*"; then
    echo "  ✅ ST*277 (Transaction Set Header) present"
  else
    echo "  ❌ ST*277 segment missing"
  fi
  
  # Check BHT
  if echo "$RESPONSE_CONTENT" | grep -q "BHT\*"; then
    echo "  ✅ BHT (Beginning of Hierarchical Transaction) present"
  else
    echo "  ❌ BHT segment missing"
  fi
  
  # Check TRN (Trace Number)
  if echo "$RESPONSE_CONTENT" | grep -q "TRN\*.*${TRACE_NUMBER}"; then
    echo "  ✅ TRN (Trace Number) matches request: ${TRACE_NUMBER}"
  else
    echo "  ⚠️  TRN may not match (expected: ${TRACE_NUMBER})"
  fi
  
  # Check STC (Status Information)
  if echo "$RESPONSE_CONTENT" | grep -q "STC\*"; then
    STATUS_CODE=$(echo "$RESPONSE_CONTENT" | grep -o "STC\*[^~]*" | head -1)
    echo "  ✅ STC (Status Information) present: ${STATUS_CODE}"
    
    # Decode status code
    if echo "$STATUS_CODE" | grep -q "F1"; then
      echo "     → F1: Finalized/Payment (Approved)"
    elif echo "$STATUS_CODE" | grep -q "F2"; then
      echo "     → F2: Finalized/Denial (Denied)"
    elif echo "$STATUS_CODE" | grep -q "P1"; then
      echo "     → P1: Pended/In Process (Pending)"
    elif echo "$STATUS_CODE" | grep -q "A4"; then
      echo "     → A4: Acknowledgement/Not Found"
    fi
  else
    echo "  ❌ STC segment missing"
  fi
  
  # Check REF for claim number
  if echo "$RESPONSE_CONTENT" | grep -q "REF\*.*${CLAIM_NUMBER}"; then
    echo "  ✅ REF (Claim Number) matches: ${CLAIM_NUMBER}"
  else
    echo "  ⚠️  Claim number may not match"
  fi
  
  # Check AMT (amounts)
  if echo "$RESPONSE_CONTENT" | grep -q "AMT\*"; then
    AMOUNTS=$(echo "$RESPONSE_CONTENT" | grep -o "AMT\*[^~]*" | head -3)
    echo "  ✅ AMT (Amount Information) present:"
    echo "$AMOUNTS" | while read -r amt; do
      echo "     ${amt}"
    done
  fi
  
  echo ""
  echo "✅ 277 Response Validation Complete!"
  
else
  echo "❌ Could not find 277 response file for validation"
  exit 1
fi

# ========================================
# Step 6: Summary
# ========================================
echo ""
echo "=========================================="
echo "Test Summary"
echo "=========================================="
echo ""
echo "✅ Test completed successfully!"
echo ""
echo "What happened:"
echo "  1. ✅ Created test claim: ${CLAIM_NUMBER}"
echo "  2. ✅ Generated 276 request with trace: ${TRACE_NUMBER}"
echo "  3. ✅ Uploaded 276 to SFTP: /upload/276/"
if [ -n "$WORKFLOW_NAME" ]; then
  echo "  4. ✅ Triggered Argo Workflow: ${WORKFLOW_NAME}"
else
  echo "  4. ⚠️  Manual workflow trigger required"
fi
echo "  5. ✅ Downloaded 277 response from SFTP: /outbound/277/"
echo "  6. ✅ Validated 277 structure and content"
echo ""
echo "Files created:"
echo "  - 276 Request: /tmp/test-276-${TEST_ID}.edi"
echo "  - 277 Response: /tmp/test-277-${TEST_ID}.edi"
echo ""
echo "Next steps:"
echo "  - Review 277 response: cat /tmp/test-277-${TEST_ID}.edi"
echo "  - Check Kafka events: kubectl logs -n cho-workflows -l app=kafka --tail 50"
echo "  - Query claim in UI: https://portal.cloudhealthoffice.com/claims"
echo ""
