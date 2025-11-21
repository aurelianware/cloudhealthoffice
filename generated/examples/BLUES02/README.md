# Regional Blue Cross Blue Shield - Cloud Health Office Deployment

## Overview

This deployment package contains all necessary files for deploying HIPAA attachments processing for **Regional Blue Cross Blue Shield** (BLUES02).

**Generated**: 2025-11-19T08:47:16.777Z

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

- **Environment**: prod
- **Location**: centralus
- **Resource Prefix**: blues02

## Contact

- **Primary Contact**: Jane Doe
- **Email**: jane.doe@blues02.com
- **Support**: edi-support@blues02.com

## Documentation

- [Deployment Guide](docs/DEPLOYMENT.md)
- [Configuration Guide](docs/CONFIGURATION.md)
- [Testing Guide](docs/TESTING.md)

## Support

For questions or issues, contact jane.doe@blues02.com
