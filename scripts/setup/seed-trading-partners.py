#!/usr/bin/env python3
"""
Seed Trading Partner data directly to Cosmos DB using Python SDK
More reliable than shell script for document insertion
"""

import json
import os
import sys
from datetime import datetime
from azure.cosmos import CosmosClient, exceptions

# Configuration
RESOURCE_GROUP = os.getenv("RESOURCE_GROUP", "prod-cloudhealthoffice-rg")
COSMOS_ACCOUNT = os.getenv("COSMOS_ACCOUNT", "cloudhealthoffice-cosmos")
COSMOS_ENDPOINT = os.getenv("COSMOS_ENDPOINT", "")
COSMOS_KEY = os.getenv("COSMOS_KEY", "")
DATABASE_NAME = "CloudHealthOffice"
CONTAINER_NAME = "TradingPartners"

# Sample trading partners
TRADING_PARTNERS = [
    {
        "id": "availity-bcbs-florida-prod",
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
            "enabled": True,
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
    },
    {
        "id": "clearinghouse-sandbox-test-tenant-dev",
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
            "enabled": True,
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
]


def main():
    print("🌱 Seeding Trading Partner Data to Cosmos DB")
    print("=" * 50)
    print()

    # Get credentials if not provided
    if not COSMOS_ENDPOINT or not COSMOS_KEY:
        print("❌ Error: COSMOS_ENDPOINT and COSMOS_KEY environment variables must be set")
        print()
        print("Usage:")
        print("  export COSMOS_ENDPOINT='https://...'")
        print("  export COSMOS_KEY='...'")
        print("  python3 scripts/seed-trading-partners.py")
        print()
        print("Or get from Azure CLI:")
        print("  export COSMOS_ENDPOINT=$(az cosmosdb show --name cloudhealthoffice-cosmos --resource-group prod-cloudhealthoffice-rg --query documentEndpoint -o tsv)")
        print("  export COSMOS_KEY=$(az cosmosdb keys list --name cloudhealthoffice-cosmos --resource-group prod-cloudhealthoffice-rg --query primaryMasterKey -o tsv)")
        sys.exit(1)

    print(f"📦 Endpoint: {COSMOS_ENDPOINT}")
    print(f"📦 Database: {DATABASE_NAME}")
    print(f"📦 Container: {CONTAINER_NAME}")
    print()

    # Initialize Cosmos client
    client = CosmosClient(COSMOS_ENDPOINT, COSMOS_KEY)
    database = client.get_database_client(DATABASE_NAME)
    container = database.get_container_client(CONTAINER_NAME)

    print(f"✅ Connected to Cosmos DB")
    print()

    # Seed each trading partner
    successful = 0
    failed = 0

    for partner in TRADING_PARTNERS:
        partner_id = partner["id"]
        tenant_id = partner["tenantId"]
        trading_partner_id = partner["tradingPartnerId"]
        environment = partner["environment"]

        try:
            # Try to create the item
            container.create_item(body=partner)
            print(f"✅ Created: {tenant_id}/{trading_partner_id}/{environment}")
            successful += 1

        except exceptions.CosmosResourceExistsError:
            print(f"⚠️  Already exists: {tenant_id}/{trading_partner_id}/{environment}")
            # Try to update instead
            try:
                container.upsert_item(body=partner)
                print(f"   ↳ Updated existing record")
                successful += 1
            except Exception as e:
                print(f"   ↳ Update failed: {e}")
                failed += 1

        except Exception as e:
            print(f"❌ Failed: {tenant_id}/{trading_partner_id}/{environment}")
            print(f"   Error: {e}")
            failed += 1

    print()
    print("=" * 50)
    print("📊 Summary")
    print("=" * 50)
    print(f"✅ Successful: {successful}")
    print(f"❌ Failed: {failed}")
    print(f"📦 Total: {len(TRADING_PARTNERS)}")
    print()

    if successful > 0:
        print("Verify with:")
        print(f"  kubectl -n cloudhealthoffice port-forward svc/trading-partner-service 8080:80")
        print(f"  curl http://localhost:8080/api/TradingPartners/tenant/bcbs-florida")
        print()


if __name__ == "__main__":
    main()
