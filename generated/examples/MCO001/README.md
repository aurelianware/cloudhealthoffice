# State Medicaid MCO - HIPAA Attachments Deployment

## Overview

This deployment package contains all necessary files for deploying HIPAA attachments processing for **State Medicaid MCO** (MCO001).

**Generated**: 2025-11-19T08:47:27.531Z

## Contents

- `workflows/` - Logic App workflow definitions
- `infrastructure/` - Bicep infrastructure templates
- `docs/` - Deployment and configuration documentation
- `config/` - Payer configuration file
- `schemas/` - JSON schemas for validation

## Quick Start

1. Review configuration: `config/payer-config.json`
2. Deploy infrastructure: `cd infrastructure && ./deploy.sh`
3. Configure secrets in Azure Key Vault
4. Deploy workflows: See `docs/DEPLOYMENT.md`
5. Test deployment: See `docs/TESTING.md`

## Enabled Modules

- ✅ **appeals**
- ✅ **ecs**
- ✅ **attachments**
- ✅ **authorizations**

## Environment

- **Environment**: dev
- **Location**: eastus
- **Resource Prefix**: mco001

## Contact

- **Primary Contact**: John Smith
- **Email**: john.smith@mco001.com
- **Support**: support@mco001.com

## Documentation

- [Deployment Guide](docs/DEPLOYMENT.md)
- [Configuration Guide](docs/CONFIGURATION.md)
- [Testing Guide](docs/TESTING.md)

## Support

For questions or issues, contact john.smith@mco001.com
