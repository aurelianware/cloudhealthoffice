# Config-to-Workflow Generator Documentation

## Overview

The Config-to-Workflow Generator is a TypeScript-based automation system that enables **zero-code payer onboarding** by automatically generating all deployment artifacts from a unified payer configuration JSON file.

### What It Generates

From a single configuration file, the generator creates:
- **Logic App Workflows** - Complete workflow.json files for all enabled modules
- **Bicep Infrastructure** - Azure resource templates and deployment scripts
- **Documentation** - Deployment guides, configuration reference, and testing instructions
- **JSON Schemas** - Validation schemas for payer-specific data structures
- **Deployment Package** - Ready-to-deploy bundle with all necessary files

### Key Benefits

- ✅ **Zero-Code Onboarding** - Add new payers without writing code
- ✅ **Consistency** - All payers follow the same patterns and best practices
- ✅ **Validation** - Configuration is validated before generation
- ✅ **Documentation** - Comprehensive docs generated automatically
- ✅ **Maintainability** - Single source of truth for payer configuration

## Installation

### Prerequisites

- Node.js 18+ and npm
- TypeScript 5+
- Azure CLI (for deployment)

### Setup

```bash
# Install dependencies
npm install

# Build TypeScript
npm run build

# Verify installation
node dist/scripts/cli/payer-generator-cli.js --help
```

## Quick Start

### 1. Create Configuration

Start with a template:

```bash
# Generate template from example
node dist/scripts/cli/payer-generator-cli.js template -t medicaid -o my-payer-config.json
```

### 2. Edit Configuration

Edit `my-payer-config.json` with your payer's details:
- Update `payerId`, `payerName`, `organizationName`
- Configure API endpoints for enabled modules
- Set authentication credentials (Key Vault secrets)
- Define infrastructure requirements

### 3. Validate Configuration

```bash
# Validate before generating
node dist/scripts/cli/payer-generator-cli.js validate my-payer-config.json
```

### 4. Generate Deployment

```bash
# Generate complete deployment package
node dist/scripts/cli/payer-generator-cli.js generate -c my-payer-config.json

# Or use short form
node dist/scripts/generate-payer-deployment.js my-payer-config.json
```

### 5. Deploy

```bash
# Navigate to generated deployment
cd generated/YOUR-PAYER-ID

# Review README and configuration
cat README.md
cat docs/DEPLOYMENT.md

# Deploy infrastructure
cd infrastructure
./deploy.sh

# Deploy workflows (after infrastructure)
cd ../workflows
zip -r workflows.zip ./*
az webapp deploy --resource-group YOUR-RG --name YOUR-LA --src-path workflows.zip --type zip
```

## Configuration Guide

### Configuration Structure

```json
{
  "payerId": "MCO001",
  "payerName": "My Payer Name",
  "organizationName": "My Organization",
  "contactInfo": { ... },
  "enabledModules": {
    "appeals": true,
    "ecs": true,
    "attachments": true,
    "authorizations": true
  },
  "appeals": { ... },
  "ecs": { ... },
  "attachments": { ... },
  "infrastructure": { ... },
  "monitoring": { ... }
}
```

### Required Fields

- `payerId` - Unique identifier (uppercase letters and numbers only)
- `payerName` - Display name for the payer
- `organizationName` - Full organization name
- `contactInfo` - Contact information (email, phone, primary contact)
- `enabledModules` - Which modules to enable
- `infrastructure` - Infrastructure configuration
- `monitoring` - Monitoring and alerting configuration

### Module Configuration

Each enabled module requires its own configuration block:

#### Appeals Module

```json
"appeals": {
  "enabled": true,
  "apiEndpoints": {
    "test": "https://test-api.payer.com/appeals/v1",
    "prod": "https://api.payer.com/appeals/v1"
  },
  "authentication": {
    "type": "oauth",
    "keyVaultSecretName": "payer-appeals-oauth-token"
  },
  "timeout": 120000,
  "retryCount": 3,
  "retryInterval": 5000,
  "requestReasons": [...],
  "subStatuses": [...],
  "attachmentRules": {...},
  "modes": {
    "realTimeWeb": true,
    "realTimeB2B": true,
    "ediBatch": false
  }
}
```

#### ECS Module

```json
"ecs": {
  "enabled": true,
  "apiEndpoints": {
    "test": "https://test-api.payer.com/ecs/v1",
    "prod": "https://api.payer.com/ecs/v1"
  },
  "authentication": {
    "type": "apikey",
    "keyVaultSecretName": "payer-ecs-api-key"
  },
  "searchMethods": {
    "serviceDate": true,
    "member": true,
    "checkNumber": true,
    "claimHistory": false
  },
  "timeout": 60000,
  "retryCount": 3
}
```

#### Attachments Module

```json
"attachments": {
  "enabled": true,
  "sftpConfig": {
    "host": "sftp.payer.com",
    "port": 22,
    "username": "hipaa_user",
    "keyVaultSecretName": "payer-sftp-key",
    "inboundFolder": "/inbound/attachments",
    "outboundFolder": "/outbound/responses"
  },
  "x12Config": {
    "isa": {
      "senderId": "PAYERID",
      "receiverId": "AVAILITY",
      "senderQualifier": "ZZ",
      "receiverQualifier": "ZZ"
    },
    "transactionSets": {
      "275": true,
      "277": true,
      "278": true
    }
  },
  "archivalConfig": {
    "storageAccountName": "payerstorage",
    "containerName": "cloud-health-office",
    "retentionDays": 2555
  }
}
```

## CLI Reference

### Commands

#### `generate`

Generate deployment package from configuration.

```bash
node dist/scripts/cli/payer-generator-cli.js generate [options]

Options:
  -c, --config <path>      Path to payer configuration file (required)
  -o, --output <path>      Output directory (default: generated/{payerId})
  -m, --modules <modules>  Comma-separated list of modules to generate
  -d, --dry-run            Show what would be generated without creating files
  -f, --force              Overwrite existing output directory
```

**Examples:**

```bash
# Generate full deployment
node dist/scripts/cli/payer-generator-cli.js generate -c payer-config.json

# Generate to specific output directory
node dist/scripts/cli/payer-generator-cli.js generate -c payer-config.json -o ./my-output

# Dry run to preview
node dist/scripts/cli/payer-generator-cli.js generate -c payer-config.json --dry-run

# Force overwrite existing
node dist/scripts/cli/payer-generator-cli.js generate -c payer-config.json -f
```

#### `validate`

Validate configuration without generating.

```bash
node dist/scripts/cli/payer-generator-cli.js validate <config-path>
```

**Example:**

```bash
node dist/scripts/cli/payer-generator-cli.js validate payer-config.json
```

Output includes:
- ✅ Configuration is valid
- ❌ Errors that must be fixed
- ⚠️  Warnings and suggestions

#### `template`

Generate template configuration file.

```bash
node dist/scripts/cli/payer-generator-cli.js template [options]

Options:
  -o, --output <path>    Output file path (default: ./payer-config.json)
  -t, --type <type>      Template type: medicaid|blues|generic (default: generic)
```

**Examples:**

```bash
# Generate Medicaid MCO template
node dist/scripts/cli/payer-generator-cli.js template -t medicaid -o medicaid-config.json

# Generate Blues template
node dist/scripts/cli/payer-generator-cli.js template -t blues -o blues-config.json
```

#### `list`

List available workflow templates.

```bash
node dist/scripts/cli/payer-generator-cli.js list
```

## Template System

### Handlebars Templates

The generator uses Handlebars templates for flexible, reusable generation.

### Available Helpers

#### String Helpers

- `{{uppercase str}}` - Convert to uppercase
- `{{lowercase str}}` - Convert to lowercase
- `{{camelCase str}}` - Convert to camelCase
- `{{kebabCase str}}` - Convert to kebab-case
- `{{snakeCase str}}` - Convert to snake_case

#### Array Helpers

- `{{join arr ", "}}` - Join array with separator
- `{{first arr}}` - Get first element
- `{{last arr}}` - Get last element
- `{{length arr}}` - Get array length

#### Conditional Helpers

- `{{#if condition}}...{{/if}}` - Standard if
- `{{#eq a b}}...{{/eq}}` - Equality check
- `{{#or a b c}}...{{/or}}` - Logical OR
- `{{#and a b c}}...{{/and}}` - Logical AND

#### JSON Helpers

- `{{json obj}}` - Format as pretty JSON
- `{{jsonInline obj}}` - Format as inline JSON

### Template Variables

All configuration fields are available in templates:

```handlebars
Payer ID: {{payerId}}
Payer Name: {{payerName}}
API Endpoint: {{ecs.apiEndpoints.prod}}
Timeout: {{ecs.timeout}}

{{#if enabledModules.appeals}}
Appeals module is enabled
{{/if}}
```

## Customization

### Adding New Workflows

1. Create template in `scripts/templates/workflows/`
2. Name it `workflow-name.template.json`
3. Use Handlebars syntax for dynamic content
4. Update generator to include it in generation

### Adding New Infrastructure Modules

1. Create template in `scripts/templates/infrastructure/`
2. Name it `module-name.template.bicep`
3. Reference from main.bicep
4. Update generator logic

### Extending Configuration Schema

1. Update types in `core/types/payer-config.ts`
2. Update validator in `core/validation/config-validator.ts`
3. Update example configs in `core/examples/`
4. Update templates to use new fields

## Examples

### Example 1: Medicaid MCO

Full example available at `core/examples/medicaid-mco-config.json`

**Features:**
- All modules enabled (Appeals, ECS, Attachments, Authorizations)
- Pre-appeal attachment pattern
- Real-time web + B2B modes
- OAuth authentication

**Generate:**

```bash
node dist/scripts/generate-payer-deployment.js core/examples/medicaid-mco-config.json
```

**Output:** `generated/MCO001/`

### Example 2: Regional Blues

Full example available at `core/examples/regional-blues-config.json`

**Features:**
- All modules enabled
- Post-appeal attachment pattern
- Real-time web + EDI batch modes
- API key authentication
- Production-grade settings

**Generate:**

```bash
node dist/scripts/generate-payer-deployment.js core/examples/regional-blues-config.json
```

**Output:** `generated/BLUES02/`

## Troubleshooting

### Common Issues

#### "Configuration file not found"

**Problem:** File path is incorrect or file doesn't exist.

**Solution:**
```bash
# Use absolute path
node dist/scripts/generate-payer-deployment.js /full/path/to/config.json

# Or use relative path from repo root
node dist/scripts/generate-payer-deployment.js ./core/examples/medicaid-mco-config.json
```

#### "Configuration validation failed"

**Problem:** Configuration has errors.

**Solution:**
1. Run validate command to see errors
2. Fix reported issues
3. Re-run validation

```bash
node dist/scripts/cli/payer-generator-cli.js validate config.json
```

#### "Generated workflow is not valid JSON"

**Problem:** Template has syntax errors.

**Solution:**
1. Check template file for Handlebars syntax errors
2. Ensure all conditionals are properly closed
3. Validate JSON structure

#### "Template not found"

**Problem:** Template doesn't exist for requested workflow.

**Solution:**
- Generator will create placeholder
- Implement template in `scripts/templates/workflows/`
- Rebuild: `npm run build`
- Copy templates: `cp -r scripts/templates dist/scripts/`

### Debug Mode

Enable verbose logging:

```bash
# Set environment variable
export DEBUG=true

# Run generator
node dist/scripts/generate-payer-deployment.js config.json
```

## API Reference

### PayerDeploymentGenerator Class

Main generator class.

```typescript
class PayerDeploymentGenerator {
  constructor(templatesDir?: string, outputBaseDir?: string);
  
  loadPayerConfig(configPath: string): Promise<PayerConfig>;
  generateWorkflows(config: PayerConfig, outputDir: string): Promise<void>;
  generateInfrastructure(config: PayerConfig, outputDir: string): Promise<void>;
  generateDocumentation(config: PayerConfig, outputDir: string): Promise<void>;
  generateSchemas(config: PayerConfig, outputDir: string): Promise<void>;
  packageDeployment(config: PayerConfig, outputDir: string): Promise<void>;
}
```

### DeploymentValidator Class

Configuration validator.

```typescript
class DeploymentValidator {
  validate(config: PayerConfig): ValidationResult;
  validateForGeneration(config: PayerConfig): ValidationResult;
  validateRequiredModules(config: PayerConfig): ValidationResult;
  validateEndpoints(config: PayerConfig): Promise<ValidationResult>;
  validateConnectivity(config: PayerConfig): Promise<ValidationResult>;
  generateValidationReport(config: PayerConfig): string;
}
```

### Types

See `core/types/payer-config.ts` for complete type definitions.

## Contributing

### Development Workflow

1. Make changes to TypeScript files
2. Run `npm run build` to compile
3. Copy templates if modified: `cp -r scripts/templates dist/scripts/`
4. Test with example configs
5. Update documentation

### Testing

```bash
# Run unit tests
npm test

# Run with coverage
npm run test -- --coverage

# Test generation
npm run build
node dist/scripts/generate-payer-deployment.js core/examples/medicaid-mco-config.json /tmp/test-output
```

## Onboarding Wizard

The platform includes an interactive onboarding wizard to simplify payer configuration:

### Launching the Wizard

```bash
node dist/scripts/cli/payer-onboarding-wizard.js
```

### Wizard Flow

The wizard guides you through a series of interactive prompts:

#### 1. Organization Information
- **Organization Name**: Full legal name of the health plan
- **Payer ID**: Unique identifier (5-20 characters, alphanumeric)
- **Payer Name**: Display name for provider portals
- **Logo URL**: Optional URL to organization logo (234x60px PNG)

#### 2. Contact Information
- **Technical Contact**: Name, email, phone
- **Account Manager**: Name, email, phone
- **Escalation Contact**: Name, email, phone

#### 3. Geography
- **Coverage Type**: Nationwide or State-Specific
- **States**: If state-specific, select applicable states

#### 4. Module Selection
Select which modules to enable:
- [ ] **ECS (Enhanced Claim Status)** - Claim status queries with ValueAdds277
- [ ] **Appeals** - Provider appeals submission and tracking
- [ ] **Attachments (275)** - Clinical and administrative attachments
- [ ] **Authorizations (278)** - Prior authorization and referrals
- [ ] **Eligibility (270/271)** - Real-time eligibility verification
- [ ] **Claims (837)** - Electronic claims submission

#### 5. Backend Configuration
For each enabled module:
- **API Base URL**: Backend API endpoint
- **Authentication Type**: OAuth2, ApiKey, or ManagedIdentity
- **Credentials**: Key Vault secret names for client ID/secret
- **Timeout**: API timeout in seconds (default: 30s)
- **Field Mappings**: Map standard fields to backend fields

#### 6. ValueAdds277 Options (ECS Module)
If ECS is enabled:
- [ ] Financial Fields (8 fields)
- [ ] Clinical Fields (4 fields)
- [ ] Demographics (20+ fields)
- [ ] Remittance Fields (4 fields)
- [ ] Service Line Details (10+ fields per line)
- [ ] Integration Flags (6 flags)

#### 7. Integration Flags
Configure cross-module integration:
- [ ] Enable Appeals integration
- [ ] Enable Attachments integration
- [ ] Enable Corrections integration
- [ ] Enable Messaging integration

#### 8. Infrastructure Settings
- **Azure Region**: Primary deployment region (default: eastus)
- **Environment**: DEV, UAT, or PROD
- **Logic App SKU**: WS1, WS2, or WS3
- **Storage Tier**: Standard_LRS, Standard_GRS, etc.

#### 9. Monitoring & Alerts
- **Application Insights**: Enable monitoring (recommended: true)
- **Alert Email**: Email for critical alerts
- **Log Retention**: Days to retain logs (default: 365)

#### 10. Review & Confirm
- Review complete configuration
- Validate against schema
- Save to file or proceed to deployment

### Wizard Output

The wizard generates:
1. **Configuration File**: `{payerId}-config.json`
2. **Validation Report**: Schema validation results
3. **Deployment Checklist**: Steps to complete deployment
4. **Documentation**: Custom deployment guide for the payer

### Example Wizard Session

```
$ node dist/scripts/cli/payer-onboarding-wizard.js

┌─────────────────────────────────────────────────────┐
│  Availity Integration Platform - Onboarding Wizard  │
│  Version 2.0                                         │
└─────────────────────────────────────────────────────┘

Step 1 of 10: Organization Information
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

? Organization Name: Blue Shield Health Plan
? Payer ID: BSHP-2024
? Payer Name: Blue Shield Health
? Logo URL (optional): https://bshp.com/logo.png

Step 2 of 10: Contact Information
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Technical Contact:
? Name: John Smith
? Email: jsmith@bshp.com
? Phone: 5551234567

... (continues through all steps)

Step 10 of 10: Review & Confirm
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Configuration Summary:
  Organization: Blue Shield Health Plan (BSHP-2024)
  Modules: ECS, Appeals, Attachments
  Region: eastus
  Environment: DEV

✓ Schema validation passed
✓ Configuration file saved: BSHP-2024-config.json

Next Steps:
  1. Review configuration file
  2. Generate deployment: node dist/scripts/cli/payer-generator-cli.js generate -c BSHP-2024-config.json
  3. Deploy to Azure: cd generated/BSHP-2024/infrastructure && ./deploy.sh

Would you like to proceed with deployment generation now? (y/n)
```

### Wizard Features

- **Interactive Prompts**: User-friendly CLI prompts with validation
- **Smart Defaults**: Sensible defaults for common configurations
- **Validation**: Real-time validation of inputs
- **Resume Support**: Save progress and resume later
- **Templates**: Quick-start templates for common scenarios
- **Dry Run**: Preview configuration before saving

## Partner Model

### Partner Program Overview

The Config-to-Workflow Generator supports a **partner ecosystem** for resellers and system integrators:

### Partner Benefits

1. **White-Label Deployment**: Deploy platform under partner brand
2. **Revenue Share**: 20-30% revenue share on customer subscriptions
3. **Training & Certification**: Technical training for partner engineers
4. **Partner Portal**: Self-service portal for configuration and deployment
5. **Co-Marketing**: Joint marketing materials and case studies
6. **Deal Registration**: Protect partner-sourced opportunities

### Partner Tiers

| Tier | Requirements | Revenue Share | Benefits |
|------|--------------|---------------|----------|
| **Bronze** | 1-5 payers | 20% | Standard support, partner badge |
| **Silver** | 6-15 payers, 1 certified engineer | 25% | Priority support, custom training |
| **Gold** | 16+ payers, 3+ certified engineers | 30% | 24/7 support, dedicated account manager |

### Partner Tools

Partners have access to specialized tools:

#### Partner CLI Commands

```bash
# Generate white-labeled deployment
node dist/scripts/cli/payer-generator-cli.js generate \
  -c customer-config.json \
  --white-label \
  --partner-id PARTNER-001 \
  --partner-branding branding.json

# Batch deployment for multiple customers
node dist/scripts/cli/partner-batch-deploy.js \
  --configs ./customer-configs/*.json \
  --environment PROD

# Partner usage reporting
node dist/scripts/cli/partner-reporting.js \
  --partner-id PARTNER-001 \
  --month 2024-11 \
  --format pdf
```

#### White-Label Configuration

```json
{
  "partnerBranding": {
    "partnerName": "HealthTech Solutions",
    "logo": "https://healthtech.com/logo.png",
    "supportEmail": "support@healthtech.com",
    "supportPhone": "8005551234",
    "portalUrl": "https://portal.healthtech.com",
    "documentationUrl": "https://docs.healthtech.com"
  }
}
```

Generated workflows and documentation will use partner branding instead of platform branding.

### Partner Onboarding Process

1. **Partner Application**: Submit application with company info
2. **Technical Review**: Architecture review, capabilities assessment
3. **Contract Signing**: Pricing, revenue share, support SLAs
4. **Training**: 2-day technical training, certification exam
5. **Sandbox Access**: 90-day sandbox environment
6. **Certification**: Complete certification project
7. **Go-Live**: Partner badge, portal access, co-marketing

**Timeline**: 4-8 weeks from application to go-live

### Partner Support

- **Partner Portal**: Configuration management, deployment tracking, usage reporting
- **Technical Support**: Dedicated partner support team
- **Documentation**: Partner-specific documentation and guides
- **Training**: Quarterly training webinars and annual summit
- **Slack Channel**: Private partner Slack workspace

## Support

For questions or issues:
- Review this documentation
- Check example configurations: `core/examples/`
- Examine generated output for reference
- Run generator with `--help` flag
- Review schema documentation: [UNIFIED-CONFIG-SCHEMA.md](./UNIFIED-CONFIG-SCHEMA.md)
- Contact the development team: support@platform.com

## Version History

### v2.0.0 (Current)

- **NEW**: Interactive onboarding wizard
- **NEW**: Partner white-label support
- **NEW**: Batch deployment for partners
- **NEW**: Partner usage reporting
- Enhanced validation with detailed error messages
- Template library with 10+ pre-built configurations
- Improved documentation generation
- Support for custom modules and extensions

### v1.0.0

- Initial release
- Support for Appeals, ECS, Attachments, and Authorizations modules
- Handlebars template system
- CLI with validate, generate, template, and list commands
- Comprehensive documentation generation
- Example configurations for Medicaid MCO and Regional Blues

## License

Apache 2.0
