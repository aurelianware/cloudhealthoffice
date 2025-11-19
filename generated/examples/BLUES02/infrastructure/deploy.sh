#!/bin/bash
# Deployment script for Regional Blue Cross Blue Shield (BLUES02)
# Generated: 2025-11-19T08:47:16.775Z

set -e

RESOURCE_GROUP="blues02-rg"
LOCATION="centralus"
TEMPLATE_FILE="main.bicep"
PARAMETERS_FILE="parameters.json"

echo "🚀 Deploying Regional Blue Cross Blue Shield infrastructure..."

# Create resource group if it doesn't exist
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --tags Project="HIPAA-Attachments" Payer="BLUES02" Environment="prod"

# Validate deployment
echo "📋 Validating deployment..."
az deployment group validate \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$TEMPLATE_FILE" \
  --parameters "@$PARAMETERS_FILE"

# Deploy infrastructure
echo "🏗️  Deploying infrastructure..."
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$TEMPLATE_FILE" \
  --parameters "@$PARAMETERS_FILE" \
  --verbose

echo "✅ Deployment completed successfully!"
