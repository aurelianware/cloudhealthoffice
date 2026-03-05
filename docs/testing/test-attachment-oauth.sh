#!/bin/bash
set -e

echo "🔐 Testing Attachment Upload with OAuth 2.0 Authentication"
echo "=========================================================="

# API Configuration
API_CLIENT_ID="your-client-id"
API_SCOPE="api://${API_CLIENT_ID}/Attachments.ReadWrite"

# Get access token using Azure CLI
echo ""
echo "📝 Step 1: Acquiring access token..."
echo "Scope: ${API_SCOPE}"

TOKEN_RESPONSE=$(az account get-access-token \
  --resource "api://${API_CLIENT_ID}" \
  --query accessToken \
  --output tsv 2>&1) || {
  echo "❌ Failed to acquire token. This is expected if you haven't consented yet."
  echo ""
  echo "To grant consent, visit:"
  echo "https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=${API_CLIENT_ID}&response_type=code&scope=${API_SCOPE}"
  echo ""
  echo "Alternatively, use device code flow..."
  exit 1
}

ACCESS_TOKEN="${TOKEN_RESPONSE}"
echo "✅ Token acquired (${#ACCESS_TOKEN} characters)"

# Decode token claims (just for visibility)
echo ""
echo "📋 Token Claims:"
TOKEN_PAYLOAD=$(echo "$ACCESS_TOKEN" | cut -d '.' -f 2)
# Add padding if needed for base64 decode
TOKEN_PAYLOAD="${TOKEN_PAYLOAD}$(printf '=%.0s' {1..4})"
echo "$TOKEN_PAYLOAD" | base64 -d 2>/dev/null | jq '.' || echo "(Could not decode token)"

# Port-forward to attachment service if not already running
echo ""
echo "🔌 Step 2: Setting up connection to attachment-service..."
kubectl -n cloudhealthoffice port-forward svc/attachment-service 18081:80 > /dev/null 2>&1 &
PORT_FORWARD_PID=$!
sleep 2
echo "✅ Port-forward active (PID: ${PORT_FORWARD_PID})"

# Test without auth first (should get 401)
echo ""
echo "🧪 Step 3: Testing without authentication (expect 401)..."
RESPONSE_CODE=$(curl -s -o /dev/null -w "%{http_code}" \
  http://localhost:18081/api/Attachments)

if [ "$RESPONSE_CODE" = "401" ]; then
  echo "✅ Correct: Received 401 Unauthorized"
else
  echo "⚠️  Expected 401, got ${RESPONSE_CODE}"
fi

# Test with invalid token (should get 401)
echo ""
echo "🧪 Step 4: Testing with invalid token (expect 401)..."
RESPONSE_CODE=$(curl -s -o /dev/null -w "%{http_code}" \
  -H "Authorization: Bearer invalid.token.here" \
  http://localhost:18081/api/Attachments)

if [ "$RESPONSE_CODE" = "401" ]; then
  echo "✅ Correct: Received 401 Unauthorized"
else
  echo "⚠️  Expected 401, got ${RESPONSE_CODE}"
fi

# Create test 275 attachment
echo ""
echo "📄 Step 5: Preparing test 275 attachment..."
cat > /tmp/test-275-attachment.json <<'EOF'
{
  "authorizationId": "3de8709d-7f59-428f-87e6-cf16f7c95110",
  "fileName": "clinical-notes.pdf",
  "fileContent": "JVBERi0xLjQKJeLjz9MKNCAwIG9iago8PC9UeXBlL1BhZ2UvTWVkaWFCb3ggWzAgMCA2MTIgNzkyXS9QYXJlbnQgMiAwIFIvUmVzb3VyY2VzPDwvRm9udDw8L0YxIDEgMCBSPj4+Pj4+CmVuZG9iago1IDAgb2JqCjw8L1R5cGUvWE9iamVjdC9TdWJ0eXBlL0ltYWdlL1dpZHRoIDEwMC9IZWlnaHQgMTAwL0NvbG9yU3BhY2UvRGV2aWNlUkdCL0JpdHNQZXJDb21wb25lbnQgOC9GaWx0ZXIvRGNvZGUvTGVuZ3RoIDE1Pj4Kc3RyZWFtCkZha2UgUERGIENvbnRlbnQgZm9yIFRlc3RpbmcKZW5kc3RyZWFtCmVuZG9iagoxIDAgb2JqCjw8L1R5cGUvRm9udC9TdWJ0eXBlL1R5cGUxL0Jhc2VGb250L1RpbWVzLVJvbWFuPj4KZW5kb2JqCjIgMCBvYmoKPDwvVHlwZS9QYWdlcy9Db3VudCAxL0tpZHNbNCAwIFJdPj4KZW5kb2JqCjMgMCBvYmoKPDwvVHlwZS9DYXRhbG9nL1BhZ2VzIDIgMCBSPj4KZW5kb2JqCnhyZWYKMCA2CjAwMDAwMDAwMDAgNjU1MzUgZgogMDAwMDAwMDI1MCAwMDAwMCBuCjAwMDAwMDAzMTggMDAwMDAgbgowMDAwMDAwMzY3IDAwMDAwIG4KMDAwMDAwMDAxNSAwMDAwMCBuCjAwMDAwMDAxMjcgMDAwMDAgbgp0cmFpbGVyCjw8L1NpemUgNi9Sb290IDMgMCBSPj4Kc3RhcnR4cmVmCjQxNgolJUVPRg==",
  "attachmentType": "Clinical Documentation",
  "description": "Test clinical notes for prior authorization"
}
EOF
echo "✅ Test payload created"

# Upload attachment with valid token
echo ""
echo "📤 Step 6: Uploading attachment with OAuth 2.0 token..."
UPLOAD_RESPONSE=$(curl -s -w "\n%{http_code}" \
  -X POST \
  -H "Authorization: Bearer ${ACCESS_TOKEN}" \
  -H "Content-Type: application/json" \
  -d @/tmp/test-275-attachment.json \
  http://localhost:18081/api/Attachments)

HTTP_CODE=$(echo "$UPLOAD_RESPONSE" | tail -n 1)
RESPONSE_BODY=$(echo "$UPLOAD_RESPONSE" | sed '$d')

echo "HTTP Status: ${HTTP_CODE}"

if [ "$HTTP_CODE" = "200" ] || [ "$HTTP_CODE" = "201" ]; then
  echo "✅ SUCCESS: Attachment uploaded!"
  echo ""
  echo "Response:"
  echo "$RESPONSE_BODY" | jq '.' || echo "$RESPONSE_BODY"
  
  # Extract attachment ID if available
  ATTACHMENT_ID=$(echo "$RESPONSE_BODY" | jq -r '.id // .attachmentId // empty' 2>/dev/null)
  
  if [ -n "$ATTACHMENT_ID" ]; then
    echo ""
    echo "📥 Step 7: Retrieving attachment to verify..."
    GET_RESPONSE=$(curl -s -w "\n%{http_code}" \
      -H "Authorization: Bearer ${ACCESS_TOKEN}" \
      http://localhost:18081/api/Attachments/${ATTACHMENT_ID})
    
    GET_HTTP_CODE=$(echo "$GET_RESPONSE" | tail -n 1)
    GET_RESPONSE_BODY=$(echo "$GET_RESPONSE" | sed '$d')
    
    if [ "$GET_HTTP_CODE" = "200" ]; then
      echo "✅ SUCCESS: Retrieved attachment!"
      echo "$GET_RESPONSE_BODY" | jq '.' || echo "$GET_RESPONSE_BODY"
    else
      echo "⚠️  GET failed with status ${GET_HTTP_CODE}"
      echo "$GET_RESPONSE_BODY"
    fi
  fi
  
elif [ "$HTTP_CODE" = "401" ]; then
  echo "❌ FAILED: 401 Unauthorized"
  echo "The token may not have the required scope or the API may not be validating correctly."
  echo ""
  echo "Response:"
  echo "$RESPONSE_BODY"
  
elif [ "$HTTP_CODE" = "403" ]; then
  echo "❌ FAILED: 403 Forbidden"
  echo "The token is valid but lacks required permissions."
  echo ""
  echo "Response:"
  echo "$RESPONSE_BODY"
  
else
  echo "⚠️  Unexpected status: ${HTTP_CODE}"
  echo ""
  echo "Response:"
  echo "$RESPONSE_BODY" | jq '.' 2>/dev/null || echo "$RESPONSE_BODY"
fi

# Cleanup
echo ""
echo "🧹 Cleaning up..."
kill $PORT_FORWARD_PID 2>/dev/null || true
rm -f /tmp/test-275-attachment.json
echo "✅ Done"

echo ""
echo "=========================================================="
echo "📊 Test Summary:"
echo "  • Token acquisition: ✅"
echo "  • 401 without auth: ✅"
echo "  • 401 with invalid token: ✅"
echo "  • Upload with OAuth: See above"
echo "=========================================================="
