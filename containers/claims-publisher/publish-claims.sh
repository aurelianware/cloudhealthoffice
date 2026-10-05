#!/bin/bash
# Publish parsed claims to Claims Service and Kafka
# Creates claims via REST API and publishes to Kafka for adjudication

set -e

CLAIMS_DIR=${CLAIMS_DIR:-"/work/claims"}
CLAIMS_SERVICE_URL=${CLAIMS_SERVICE_URL:-"http://claims-service.cloudhealthoffice.svc.cluster.local:8080"}
KAFKA_BOOTSTRAP_SERVERS=${KAFKA_BOOTSTRAP_SERVERS:-"cloudhealthoffice-kafka-bootstrap.kafka:9092"}
KAFKA_TOPIC=${KAFKA_TOPIC:-"claims-adjudication"}
TENANT_ID=${TENANT_ID:-"default-payer"}

# CHO workload token (Authorization + X-Tenant-ID), from the cho-workload-lib
# ConfigMap the workflow mounts. See docs/security/argo-service-tokens.md.
CHO_WORKLOAD_LIB=${CHO_WORKLOAD_LIB:-"/opt/cho/cho-workload.sh"}
if [ ! -r "$CHO_WORKLOAD_LIB" ]; then
    echo "CHO workload helper not mounted at $CHO_WORKLOAD_LIB; claims-service requires a CHO token." >&2
    exit 1
fi
# shellcheck source=/dev/null
. "$CHO_WORKLOAD_LIB"

echo "Starting claims publisher..."
echo "Claims directory: $CLAIMS_DIR"
echo "Claims Service URL: $CLAIMS_SERVICE_URL"
echo "Kafka brokers: $KAFKA_BOOTSTRAP_SERVERS"
echo "Kafka topic: $KAFKA_TOPIC"
echo "Tenant ID: $TENANT_ID"

# Count claim files
CLAIM_COUNT=$(ls -1 "$CLAIMS_DIR"/*.json 2>/dev/null | grep -v "summary" | wc -l)
echo "Found $CLAIM_COUNT claim files"

if [ "$CLAIM_COUNT" -eq 0 ]; then
    echo "No claims to publish"
    echo "0" > /work/claims-created.txt
    echo "0" > /work/kafka-published.txt
    exit 0
fi

# Process each claim file
CREATED_COUNT=0
KAFKA_COUNT=0
ERROR_COUNT=0

for claim_file in "$CLAIMS_DIR"/*.json; do
    # Skip summary files
    if echo "$claim_file" | grep -q "summary"; then
        continue
    fi
    
    filename=$(basename "$claim_file")
    echo "Processing: $filename"
    
    # Obtain or refresh the token in this shell, so the $(...) below reuses it.
    if ! cho_token "$TENANT_ID"; then
        echo "  ✗ No CHO token for tenant $TENANT_ID; stopping." >&2
        exit 1
    fi

    # POST to Claims Service
    CLAIM_ID=$(cho_curl "$TENANT_ID" POST "$CLAIMS_SERVICE_URL/api/claims" \
        -H "Content-Type: application/json" \
        -d @"$claim_file" | jq -r '.id // empty')
    
    if [ -n "$CLAIM_ID" ]; then
        echo "  ✓ Created claim: $CLAIM_ID"
        ((CREATED_COUNT++))
        
        # Publish to Kafka for adjudication
        KAFKA_MSG=$(jq -n \
            --arg claimId "$CLAIM_ID" \
            --arg tenantId "$TENANT_ID" \
            --arg timestamp "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
            '{claimId: $claimId, tenantId: $tenantId, submittedDate: $timestamp, source: "837-ingest"}')
        
        # Use kafkacat to publish message
        if echo "$KAFKA_MSG" | kafkacat -P \
            -b "$KAFKA_BOOTSTRAP_SERVERS" \
            -t "$KAFKA_TOPIC" \
            -X security.protocol=SASL_SSL \
            -X sasl.mechanisms=SCRAM-SHA-512 \
            -X sasl.username="$KAFKA_USERNAME" \
            -X sasl.password="$KAFKA_PASSWORD"; then
            echo "  ✓ Published to Kafka: $CLAIM_ID"
            ((KAFKA_COUNT++))
        else
            echo "  ✗ Kafka publish failed: $CLAIM_ID"
        fi
    else
        echo "  ✗ Claim creation failed: $filename"
        ((ERROR_COUNT++))
    fi
done

echo "Publishing complete:"
echo "  Claims created: $CREATED_COUNT"
echo "  Kafka published: $KAFKA_COUNT"
echo "  Errors: $ERROR_COUNT"

# Write output files for workflow
echo "$CREATED_COUNT" > /work/claims-created.txt
echo "$KAFKA_COUNT" > /work/kafka-published.txt

# Write summary
cat > /work/publish-summary.json <<EOF
{
  "totalClaims": $CLAIM_COUNT,
  "createdCount": $CREATED_COUNT,
  "kafkaCount": $KAFKA_COUNT,
  "errorCount": $ERROR_COUNT,
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF

if [ "$ERROR_COUNT" -gt 0 ]; then
    echo "Warning: $ERROR_COUNT claims failed"
fi

echo "All claims published successfully"
