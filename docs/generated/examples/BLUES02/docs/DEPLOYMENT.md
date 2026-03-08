# Deployment Guide - Regional Blue Cross Blue Shield

## Overview

This deployment package was generated for **Regional Blue Cross Blue Shield** (BLUES02) on 2025-11-19T08:47:16.776Z.

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

- `blues02-appeals-api-key`: Appeals API credentials
- `blues02-ecs-api-key`: ECS API credentials
- `blues02-sftp-key`: SFTP private key

### 4. Deploy Workflows

```bash
cd workflows
zip -r workflows.zip ./*
az webapp deploy \
  --resource-group blues02-rg \
  --name blues02-la \
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

- **Environment**: prod
- **Location**: centralus
- **Resource Prefix**: blues02

## Support

For deployment issues, contact: edi-support@blues02.com
