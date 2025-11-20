#!/bin/bash
# Deployment script for State Medicaid MCO (MCO001)
# Generated: 2025-11-19T08:47:27.530Z

set -e

RESOURCE_GROUP="mco001-rg"
LOCATION="eastus"
TEMPLATE_FILE="main.bicep"
PARAMETERS_FILE="parameters.json"

echo "🚀 Deploying State Medicaid MCO infrastructure..."

# Create resource group if it doesn't exist
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --tags Project="HIPAA-Attachments" Payer="MCO001" Environment="dev"

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
