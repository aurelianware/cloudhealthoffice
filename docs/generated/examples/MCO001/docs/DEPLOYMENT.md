# Deployment Guide - State Medicaid MCO

## Overview

This deployment package was generated for **State Medicaid MCO** (MCO001) on 2025-11-19T08:47:27.531Z.

## Prerequisites

- Azure CLI (version 2.40.0 or higher)
- Azure subscription with appropriate permissions
- Logic Apps Standard runtime
- PowerShell 7+ (for Windows deployments)

## Deployment Steps

### 1. Review Configuration

Review the configuration file at `config/payer-config.json` and ensure all settings are correct.

### 2. Deploy Infrastructure

```bash
cd infrastructure
./deploy.sh
```

### 3. Configure Secrets

Add the following secrets to Azure Key Vault:

- `mco001-appeals-oauth-token`: Appeals API credentials
- `mco001-ecs-oauth-token`: ECS API credentials
- `mco001-sftp-private-key`: SFTP private key

### 4. Deploy Workflows

```bash
cd workflows
zip -r workflows.zip ./*
az webapp deploy \
  --resource-group mco001-rg \
  --name mco001-la \
  --src-path workflows.zip \
  --type zip
```

### 5. Verify Deployment

Check that all workflows are enabled and running in the Azure Portal.

## Enabled Modules

- ✅ Appeals Processing
- ✅ Enhanced Claim Status (ECS)
- ✅ Attachments (X12 275/277/278)
- ✅ Authorizations

## Environment

- **Environment**: dev
- **Location**: eastus
- **Resource Prefix**: mco001

## Support

For deployment issues, contact: support@mco001.com
