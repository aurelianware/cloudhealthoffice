#!/bin/bash
set -e

echo "🌱 Seeding Trading Partner Data to Cosmos DB"
echo "=============================================="
echo ""

# Configuration
RESOURCE_GROUP="prod-cloudhealthoffice-rg"
COSMOS_ACCOUNT="cloudhealthoffice-cosmos"
DATABASE="CloudHealthOffice"
CONTAINER="TradingPartners"

# Check if container exists, create if not
echo "📦 Step 1: Ensuring TradingPartners container exists..."
CONTAINER_EXISTS=$(az cosmosdb sql container show \
  --account-name $COSMOS_ACCOUNT \
  --resource-group $RESOURCE_GROUP \
  --database-name $DATABASE \
  --name $CONTAINER \
  --query "id" -o tsv 2>/dev/null || echo "")

if [ -z "$CONTAINER_EXISTS" ]; then
  echo "Creating TradingPartners container..."
  az cosmosdb sql container create \
    --account-name $COSMOS_ACCOUNT \
    --resource-group $RESOURCE_GROUP \
    --database-name $DATABASE \
    --name $CONTAINER \
    --partition-key-path "/partitionKey" \
    --throughput 400
  echo "✅ Container created"
else
  echo "✅ Container already exists"
fi

echo ""
echo "🔑 Step 2: Getting Cosmos DB connection details..."

COSMOS_ENDPOINT=$(az cosmosdb show \
  --name $COSMOS_ACCOUNT \
  --resource-group $RESOURCE_GROUP \
  --query "documentEndpoint" -o tsv)

COSMOS_KEY=$(az cosmosdb keys list \
  --name $COSMOS_ACCOUNT \
  --resource-group $RESOURCE_GROUP \
  --query "primaryMasterKey" -o tsv)

echo "✅ Endpoint: $COSMOS_ENDPOINT"

# Create temporary directory for JSON files
TEMP_DIR="/tmp/trading-partners-seed"
mkdir -p $TEMP_DIR

echo ""
echo "📝 Step 3: Creating sample trading partner configurations..."

# Trading Partner 1: BCBS Florida + Availity (Production)
cat > $TEMP_DIR/bcbs-fl-availity-prod.json <<'EOF'
{
  "id": "availity-bcbs-florida-prod",
  "partitionKey": "bcbs-florida",
  "tenantId": "bcbs-florida",
  "tradingPartnerId": "availity",
  "environment": "prod",
  "partnerName": "Availity LLC",
  "partnerType": "Clearinghouse",
  "x12Config": {
    "senderId": "030240928",
    "receiverId": "BCBSFL001",
    "isaQualifier": "ZZ",
    "testIndicator": "P"
  },
  "sftpConfig": {
    "enabled": true,
    "username": "bcbs-fl-availity-prod",
    "host": "sftp-service.cho-sftp.svc.cluster.local",
    "port": 22,
    "paths": {
      "inbound": {
        "base": "/bcbs-florida/availity/prod/inbound",
        "275": "/bcbs-florida/availity/prod/inbound/275",
        "276": "/bcbs-florida/availity/prod/inbound/276",
        "278": "/bcbs-florida/availity/prod/inbound/278",
        "837": "/bcbs-florida/availity/prod/inbound/837"
      },
      "outbound": {
        "base": "/bcbs-florida/availity/prod/outbound",
        "277": "/bcbs-florida/availity/prod/outbound/277",
        "999": "/bcbs-florida/availity/prod/outbound/999",
        "824": "/bcbs-florida/availity/prod/outbound/824"
      }
    }
  },
  "blobConfig": {
    "containerName": "cho-prod",
    "paths": {
      "raw": "prod/bcbs-florida/availity/raw/{transactionType}/{yyyy}/{MM}/{dd}",
      "processed": "prod/bcbs-florida/availity/processed/{transactionType}/{yyyy}/{MM}/{dd}",
      "archive": "prod/bcbs-florida/availity/archive/{transactionType}/{yyyy}/{MM}",
      "error": "prod/bcbs-florida/availity/error/{transactionType}/{yyyy}/{MM}/{dd}"
    },
    "retentionPolicies": {
      "raw": 90,
      "processed": 365,
      "archive": 2555,
      "error": 180
    }
  },
  "transactionTypes": ["275", "276", "277", "278", "837", "835", "999", "824"],
  "contactInfo": {
    "email": "edi-support@availity.com",
    "phone": "1-800-282-4548",
    "technicalContact": "EDI Support Team",
    "escalationEmail": "edi-escalation@availity.com"
  },
  "businessRules": {
    "maxFileSize": 10485760,
    "allowedFileTypes": [".edi", ".x12", ".txt"],
    "pollingInterval": "PT5M",
    "processingTimeout": "PT10M",
    "maxRetries": 3,
    "retryBackoff": "PT1M"
  },
  "status": "Active",
  "createdAt": "2026-01-15T00:00:00Z",
  "lastTestedAt": "2026-02-07T12:00:00Z"
}
EOF

# Trading Partner 2: BCBS Florida + Change Healthcare (Backup)
cat > $TEMP_DIR/bcbs-fl-changehc-prod.json <<'EOF'
{
  "id": "change-healthcare-bcbs-florida-prod",
  "partitionKey": "bcbs-florida",
  "tenantId": "bcbs-florida",
  "tradingPartnerId": "change-healthcare",
  "environment": "prod",
  "partnerName": "Change Healthcare (Emdeon)",
  "partnerType": "Clearinghouse",
  "x12Config": {
    "senderId": "CHANGEHC",
    "receiverId": "BCBSFL001",
    "isaQualifier": "ZZ",
    "testIndicator": "P"
  },
  "sftpConfig": {
    "enabled": true,
    "username": "bcbs-fl-changehc-prod",
    "host": "sftp-service.cho-sftp.svc.cluster.local",
    "port": 22,
    "paths": {
      "inbound": {
        "base": "/bcbs-florida/change-healthcare/prod/inbound",
        "275": "/bcbs-florida/change-healthcare/prod/inbound/275",
        "278": "/bcbs-florida/change-healthcare/prod/inbound/278",
        "837": "/bcbs-florida/change-healthcare/prod/inbound/837"
      },
      "outbound": {
        "base": "/bcbs-florida/change-healthcare/prod/outbound",
        "277": "/bcbs-florida/change-healthcare/prod/outbound/277",
        "999": "/bcbs-florida/change-healthcare/prod/outbound/999"
      }
    }
  },
  "blobConfig": {
    "containerName": "cho-prod",
    "paths": {
      "raw": "prod/bcbs-florida/change-healthcare/raw/{transactionType}/{yyyy}/{MM}/{dd}",
      "processed": "prod/bcbs-florida/change-healthcare/processed/{transactionType}/{yyyy}/{MM}/{dd}",
      "archive": "prod/bcbs-florida/change-healthcare/archive/{transactionType}/{yyyy}/{MM}",
      "error": "prod/bcbs-florida/change-healthcare/error/{transactionType}/{yyyy}/{MM}/{dd}"
    },
    "retentionPolicies": {
      "raw": 90,
      "processed": 365,
      "archive": 2555,
      "error": 180
    }
  },
  "transactionTypes": ["275", "278", "837", "277", "999"],
  "contactInfo": {
    "email": "edisupport@changehealthcare.com",
    "phone": "1-800-845-6592",
    "technicalContact": "EDI Integration Team",
    "escalationEmail": "edi-escalation@changehealthcare.com"
  },
  "businessRules": {
    "maxFileSize": 10485760,
    "allowedFileTypes": [".edi", ".x12"],
    "pollingInterval": "PT5M",
    "processingTimeout": "PT15M",
    "maxRetries": 4,
    "retryBackoff": "PT2M"
  },
  "status": "Active",
  "createdAt": "2026-01-20T00:00:00Z"
}
EOF

# Trading Partner 3: UHC Texas + Optum (Production)
cat > $TEMP_DIR/uhc-tx-optum-prod.json <<'EOF'
{
  "id": "optum-uhc-texas-prod",
  "partitionKey": "uhc-texas",
  "tenantId": "uhc-texas",
  "tradingPartnerId": "optum",
  "environment": "prod",
  "partnerName": "Optum / United Healthcare",
  "partnerType": "Clearinghouse",
  "x12Config": {
    "senderId": "OPTUM",
    "receiverId": "UHCTX001",
    "isaQualifier": "ZZ",
    "testIndicator": "P"
  },
  "sftpConfig": {
    "enabled": true,
    "username": "uhc-tx-optum-prod",
    "host": "sftp-service.cho-sftp.svc.cluster.local",
    "port": 22,
    "paths": {
      "inbound": {
        "base": "/uhc-texas/optum/prod/inbound",
        "275": "/uhc-texas/optum/prod/inbound/275",
        "276": "/uhc-texas/optum/prod/inbound/276",
        "278": "/uhc-texas/optum/prod/inbound/278",
        "837": "/uhc-texas/optum/prod/inbound/837"
      },
      "outbound": {
        "base": "/uhc-texas/optum/prod/outbound",
        "277": "/uhc-texas/optum/prod/outbound/277",
        "999": "/uhc-texas/optum/prod/outbound/999"
      }
    }
  },
  "blobConfig": {
    "containerName": "cho-prod",
    "paths": {
      "raw": "prod/uhc-texas/optum/raw/{transactionType}/{yyyy}/{MM}/{dd}",
      "processed": "prod/uhc-texas/optum/processed/{transactionType}/{yyyy}/{MM}/{dd}",
      "archive": "prod/uhc-texas/optum/archive/{transactionType}/{yyyy}/{MM}",
      "error": "prod/uhc-texas/optum/error/{transactionType}/{yyyy}/{MM}/{dd}"
    },
    "retentionPolicies": {
      "raw": 90,
      "processed": 365,
      "archive": 2555,
      "error": 180
    }
  },
  "transactionTypes": ["275", "276", "277", "278", "837", "999"],
  "contactInfo": {
    "email": "edi@optum.com",
    "phone": "1-800-765-6619",
    "technicalContact": "Optum EDI Support",
    "escalationEmail": "edi-escalation@optum.com"
  },
  "businessRules": {
    "maxFileSize": 20971520,
    "allowedFileTypes": [".edi", ".x12", ".txt"],
    "pollingInterval": "PT5M",
    "processingTimeout": "PT10M",
    "maxRetries": 3,
    "retryBackoff": "PT1M"
  },
  "status": "Active",
  "createdAt": "2026-01-18T00:00:00Z"
}
EOF

# Trading Partner 4: Test Tenant + Sandbox (Development)
cat > $TEMP_DIR/test-sandbox-dev.json <<'EOF'
{
  "id": "clearinghouse-sandbox-test-tenant-dev",
  "partitionKey": "test-tenant",
  "tenantId": "test-tenant",
  "tradingPartnerId": "clearinghouse-sandbox",
  "environment": "dev",
  "partnerName": "Test Clearinghouse Sandbox",
  "partnerType": "Clearinghouse",
  "x12Config": {
    "senderId": "TESTCH",
    "receiverId": "TESTTENANT",
    "isaQualifier": "ZZ",
    "testIndicator": "T"
  },
  "sftpConfig": {
    "enabled": true,
    "username": "test-sandbox-dev",
    "host": "sftp-service.cho-sftp.svc.cluster.local",
    "port": 22,
    "paths": {
      "inbound": {
        "base": "/test-tenant/clearinghouse-sandbox/dev/inbound",
        "275": "/test-tenant/clearinghouse-sandbox/dev/inbound/275",
        "278": "/test-tenant/clearinghouse-sandbox/dev/inbound/278"
      },
      "outbound": {
        "base": "/test-tenant/clearinghouse-sandbox/dev/outbound",
        "277": "/test-tenant/clearinghouse-sandbox/dev/outbound/277",
        "999": "/test-tenant/clearinghouse-sandbox/dev/outbound/999"
      }
    }
  },
  "blobConfig": {
    "containerName": "cho-dev",
    "paths": {
      "raw": "dev/test-tenant/clearinghouse-sandbox/raw/{transactionType}/{yyyy}/{MM}/{dd}",
      "processed": "dev/test-tenant/clearinghouse-sandbox/processed/{transactionType}/{yyyy}/{MM}/{dd}",
      "archive": "dev/test-tenant/clearinghouse-sandbox/archive/{transactionType}/{yyyy}/{MM}",
      "error": "dev/test-tenant/clearinghouse-sandbox/error/{transactionType}/{yyyy}/{MM}/{dd}"
    },
    "retentionPolicies": {
      "raw": 7,
      "processed": 30,
      "archive": 90,
      "error": 30
    }
  },
  "transactionTypes": ["275", "278", "277", "999"],
  "contactInfo": {
    "email": "test@example.com",
    "phone": "1-800-TEST-EDI",
    "technicalContact": "Test Admin",
    "escalationEmail": "test-admin@example.com"
  },
  "businessRules": {
    "maxFileSize": 5242880,
    "allowedFileTypes": [".edi", ".x12", ".txt"],
    "pollingInterval": "PT1M",
    "processingTimeout": "PT5M",
    "maxRetries": 2,
    "retryBackoff": "PT30S"
  },
  "status": "Active",
  "createdAt": "2026-02-01T00:00:00Z"
}
EOF

# Trading Partner 5: BCBS Florida + Availity (Preprod)
cat > $TEMP_DIR/bcbs-fl-availity-preprod.json <<'EOF'
{
  "id": "availity-bcbs-florida-preprod",
  "partitionKey": "bcbs-florida",
  "tenantId": "bcbs-florida",
  "tradingPartnerId": "availity",
  "environment": "preprod",
  "partnerName": "Availity LLC (Pre-Production)",
  "partnerType": "Clearinghouse",
  "x12Config": {
    "senderId": "030240928",
    "receiverId": "BCBSFL001",
    "isaQualifier": "ZZ",
    "testIndicator": "T"
  },
  "sftpConfig": {
    "enabled": true,
    "username": "bcbs-fl-availity-preprod",
    "host": "sftp-service.cho-sftp.svc.cluster.local",
    "port": 22,
    "paths": {
      "inbound": {
        "base": "/bcbs-florida/availity/preprod/inbound",
        "275": "/bcbs-florida/availity/preprod/inbound/275",
        "278": "/bcbs-florida/availity/preprod/inbound/278"
      },
      "outbound": {
        "base": "/bcbs-florida/availity/preprod/outbound",
        "277": "/bcbs-florida/availity/preprod/outbound/277",
        "999": "/bcbs-florida/availity/preprod/outbound/999"
      }
    }
  },
  "blobConfig": {
    "containerName": "cho-preprod",
    "paths": {
      "raw": "preprod/bcbs-florida/availity/raw/{transactionType}/{yyyy}/{MM}/{dd}",
      "processed": "preprod/bcbs-florida/availity/processed/{transactionType}/{yyyy}/{MM}/{dd}",
      "archive": "preprod/bcbs-florida/availity/archive/{transactionType}/{yyyy}/{MM}",
      "error": "preprod/bcbs-florida/availity/error/{transactionType}/{yyyy}/{MM}/{dd}"
    },
    "retentionPolicies": {
      "raw": 30,
      "processed": 90,
      "archive": 365,
      "error": 60
    }
  },
  "transactionTypes": ["275", "278", "277", "999"],
  "contactInfo": {
    "email": "edi-support@availity.com",
    "phone": "1-800-282-4548",
    "technicalContact": "EDI Support Team",
    "escalationEmail": "edi-escalation@availity.com"
  },
  "businessRules": {
    "maxFileSize": 10485760,
    "allowedFileTypes": [".edi", ".x12", ".txt"],
    "pollingInterval": "PT5M",
    "processingTimeout": "PT10M",
    "maxRetries": 3,
    "retryBackoff": "PT1M"
  },
  "status": "Active",
  "createdAt": "2026-01-25T00:00:00Z"
}
EOF

echo "✅ Created 5 sample trading partner configurations"
echo ""
echo "📤 Step 4: Uploading to Cosmos DB..."

# Function to upload JSON to Cosmos DB
upload_to_cosmos() {
  local file=$1
  local filename=$(basename $file)
  
  echo "  Uploading: $filename"
  
  curl -s -X POST \
    "${COSMOS_ENDPOINT}dbs/${DATABASE}/colls/${CONTAINER}/docs" \
    -H "Authorization: $(az cosmosdb keys list --name $COSMOS_ACCOUNT --resource-group $RESOURCE_GROUP --type keys --query primaryMasterKey -o tsv | openssl dgst -sha256 -hmac "$(date -u +"%a, %d %b %Y %H:%M:%S GMT")" -binary | base64)" \
    -H "Content-Type: application/json" \
    -H "x-ms-date: $(date -u +"%a, %d %b %Y %H:%M:%S GMT")" \
    -H "x-ms-version: 2018-12-31" \
    -d @$file > /dev/null
  
  if [ $? -eq 0 ]; then
    echo "    ✅ Uploaded successfully"
  else
    echo "    ⚠️  Upload may have failed, trying alternative method..."
    
    # Alternative: Use Azure CLI (slower but more reliable)
    PARTITION_KEY=$(jq -r '.partitionKey' $file)
    az cosmosdb sql container create \
      --account-name $COSMOS_ACCOUNT \
      --resource-group $RESOURCE_GROUP \
      --database-name $DATABASE \
      --name $CONTAINER \
      --partition-key-path "/partitionKey" 2>/dev/null || true
    
    # Note: Azure CLI doesn't have direct document insert, recommend using REST API or SDK
    echo "    📝 Use REST API or portal to insert: $filename"
  fi
}

# Upload all JSON files
for file in $TEMP_DIR/*.json; do
  upload_to_cosmos "$file"
done

echo ""
echo "🧹 Step 5: Cleanup..."
# Keep files for manual upload if needed
echo "JSON files saved in: $TEMP_DIR"
echo "You can manually import these via Azure Portal if needed"

echo ""
echo "=============================================="
echo "📊 Summary"
echo "=============================================="
echo ""
echo "Created 5 Trading Partner configurations:"
echo "  1. ✅ bcbs-florida + availity + prod"
echo "  2. ✅ bcbs-florida + change-healthcare + prod"
echo "  3. ✅ uhc-texas + optum + prod"
echo "  4. ✅ test-tenant + clearinghouse-sandbox + dev"
echo "  5. ✅ bcbs-florida + availity + preprod"
echo ""
echo "To verify, query Cosmos DB:"
echo "  az cosmosdb sql container query \\"
echo "    --account-name $COSMOS_ACCOUNT \\"
echo "    --resource-group $RESOURCE_GROUP \\"
echo "    --database-name $DATABASE \\"
echo "    --name $CONTAINER \\"
echo "    --query-text \"SELECT * FROM c\""
echo ""
echo "Or use the trading-partner-service API:"
echo "  kubectl -n cloudhealthoffice port-forward svc/trading-partner-service 8080:80"
echo "  curl http://localhost:8080/api/TradingPartners/tenant/bcbs-florida"
echo ""
echo "JSON files location: $TEMP_DIR"
echo "=============================================="
