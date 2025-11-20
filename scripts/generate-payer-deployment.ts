/**
 * Main Payer Deployment Generator
 * Generates Logic App workflows, infrastructure, and documentation from payer configuration
 */

import * as fs from 'fs';
import * as path from 'path';
import * as Handlebars from 'handlebars';
import { PayerConfig } from '../core/types/payer-config';
import { DeploymentValidator } from '../core/validation/config-validator';
import { registerHelpers } from './utils/template-helpers';

export class PayerDeploymentGenerator {
  private validator: DeploymentValidator;
  private templatesDir: string;
  private outputBaseDir: string;

  constructor(templatesDir?: string, outputBaseDir?: string) {
    this.validator = new DeploymentValidator();
    this.templatesDir = templatesDir || path.join(__dirname, 'templates');
    this.outputBaseDir = outputBaseDir || path.join(process.cwd(), 'generated');
    
    // Register Handlebars helpers
    registerHelpers();
  }

  /**
   * Load and validate payer configuration from file
   */
  public async loadPayerConfig(configPath: string): Promise<PayerConfig> {
    if (!fs.existsSync(configPath)) {
      throw new Error(`Configuration file not found: ${configPath}`);
    }

    const configContent = fs.readFileSync(configPath, 'utf-8');
    const config: PayerConfig = JSON.parse(configContent);

    // Validate configuration
    const validationResult = this.validator.validateForGeneration(config);
    if (!validationResult.valid) {
      const errorMessages = validationResult.errors
        .map(e => `  - [${e.field}] ${e.message}`)
        .join('\n');
      throw new Error(`Configuration validation failed:\n${errorMessages}`);
    }

    // Log warnings
    if (validationResult.warnings.length > 0) {
      console.warn('⚠️  Configuration warnings:');
      validationResult.warnings.forEach(w => {
        console.warn(`  - [${w.field}] ${w.message}`);
        if (w.suggestion) {
          console.warn(`    Suggestion: ${w.suggestion}`);
        }
      });
    }

    return config;
  }

  /**
   * Generate all Logic App workflow.json files
   */
  public async generateWorkflows(config: PayerConfig, outputDir: string): Promise<void> {
    const workflowsDir = path.join(outputDir, 'workflows');
    fs.mkdirSync(workflowsDir, { recursive: true });

    const workflowsToGenerate: string[] = [];

    // Determine which workflows to generate based on enabled modules
    if (config.enabledModules.ecs && config.ecs?.enabled) {
      workflowsToGenerate.push('ecs_summary_search');
    }

    if (config.enabledModules.attachments && config.attachments?.enabled) {
      workflowsToGenerate.push('ingest275', 'ingest278');
    }

    if (config.enabledModules.appeals && config.appeals?.enabled) {
      workflowsToGenerate.push(
        'appeal_to_payer',
        'appeal_update_from_payer_to_availity',
        'appeal_get_details',
        'appeal_document_download',
        'appeal_update_to_payer'
      );
    }

    // Generate each workflow
    for (const workflowName of workflowsToGenerate) {
      await this.generateWorkflow(config, workflowName, workflowsDir);
    }

    console.log(`✅ Generated ${workflowsToGenerate.length} workflows in ${workflowsDir}`);
  }

  /**
   * Generate a single workflow from template
   */
  private async generateWorkflow(
    config: PayerConfig,
    workflowName: string,
    workflowsDir: string
  ): Promise<void> {
    const templatePath = path.join(
      this.templatesDir,
      'workflows',
      `${workflowName}.template.json`
    );

    if (!fs.existsSync(templatePath)) {
      console.warn(`⚠️  Template not found: ${templatePath}, skipping...`);
      return;
    }

    const templateContent = fs.readFileSync(templatePath, 'utf-8');
    const template = Handlebars.compile(templateContent);
    const rendered = template(config);

    // Validate generated JSON
    try {
      JSON.parse(rendered);
    } catch (error) {
      throw new Error(`Generated workflow ${workflowName} is not valid JSON: ${error}`);
    }

    const workflowDir = path.join(workflowsDir, workflowName);
    fs.mkdirSync(workflowDir, { recursive: true });

    const outputPath = path.join(workflowDir, 'workflow.json');
    fs.writeFileSync(outputPath, rendered, 'utf-8');

    console.log(`  ✓ ${workflowName}/workflow.json`);
  }

  /**
   * Generate Bicep infrastructure templates
   */
  public async generateInfrastructure(config: PayerConfig, outputDir: string): Promise<void> {
    const infraDir = path.join(outputDir, 'infrastructure');
    fs.mkdirSync(infraDir, { recursive: true });

    // Generate main.bicep
    await this.generateInfrastructureFile(config, 'main', infraDir);

    // Generate parameters.json
    await this.generateParametersFile(config, infraDir);

    // Generate module files if templates exist
    const modulesDir = path.join(infraDir, 'modules');
    fs.mkdirSync(modulesDir, { recursive: true });

    if (config.enabledModules.appeals && config.appeals?.enabled) {
      await this.generateInfrastructureFile(config, 'appeals-api', modulesDir);
    }

    if (config.enabledModules.ecs && config.ecs?.enabled) {
      await this.generateInfrastructureFile(config, 'ecs-api', modulesDir);
    }

    // Generate deployment script
    await this.generateDeploymentScript(config, infraDir);

    console.log(`✅ Generated infrastructure templates in ${infraDir}`);
  }

  /**
   * Generate a single infrastructure file from template
   */
  private async generateInfrastructureFile(
    config: PayerConfig,
    fileName: string,
    outputDir: string
  ): Promise<void> {
    const templatePath = path.join(
      this.templatesDir,
      'infrastructure',
      `${fileName}.template.bicep`
    );

    if (!fs.existsSync(templatePath)) {
      console.warn(`⚠️  Infrastructure template not found: ${templatePath}, creating placeholder...`);
      // Create a simple placeholder
      const placeholder = this.createInfrastructurePlaceholder(config, fileName);
      const outputPath = path.join(outputDir, `${fileName}.bicep`);
      fs.writeFileSync(outputPath, placeholder, 'utf-8');
      return;
    }

    const templateContent = fs.readFileSync(templatePath, 'utf-8');
    const template = Handlebars.compile(templateContent);
    const rendered = template(config);

    const outputPath = path.join(outputDir, `${fileName}.bicep`);
    fs.writeFileSync(outputPath, rendered, 'utf-8');

    console.log(`  ✓ ${fileName}.bicep`);
  }

  /**
   * Generate parameters.json file
   */
  private async generateParametersFile(config: PayerConfig, infraDir: string): Promise<void> {
    const parameters = {
      $schema: 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#',
      contentVersion: '1.0.0.0',
      parameters: {
        baseName: {
          value: config.infrastructure.resourceNamePrefix
        },
        location: {
          value: config.infrastructure.location
        },
        environment: {
          value: config.infrastructure.environment
        },
        tags: {
          value: config.infrastructure.tags
        }
      }
    };

    const outputPath = path.join(infraDir, 'parameters.json');
    fs.writeFileSync(outputPath, JSON.stringify(parameters, null, 2), 'utf-8');
    console.log(`  ✓ parameters.json`);
  }

  /**
   * Generate deployment script
   */
  private async generateDeploymentScript(config: PayerConfig, infraDir: string): Promise<void> {
    const script = `#!/bin/bash
# Deployment script for ${config.payerName} (${config.payerId})
# Generated: ${new Date().toISOString()}

set -e

RESOURCE_GROUP="${config.infrastructure.resourceNamePrefix}-rg"
LOCATION="${config.infrastructure.location}"
TEMPLATE_FILE="main.bicep"
PARAMETERS_FILE="parameters.json"

echo "🚀 Deploying ${config.payerName} infrastructure..."

# Create resource group if it doesn't exist
az group create \\
  --name "$RESOURCE_GROUP" \\
  --location "$LOCATION" \\
  --tags Project="HIPAA-Attachments" Payer="${config.payerId}" Environment="${config.infrastructure.environment}"

# Validate deployment
echo "📋 Validating deployment..."
az deployment group validate \\
  --resource-group "$RESOURCE_GROUP" \\
  --template-file "$TEMPLATE_FILE" \\
  --parameters "@$PARAMETERS_FILE"

# Deploy infrastructure
echo "🏗️  Deploying infrastructure..."
az deployment group create \\
  --resource-group "$RESOURCE_GROUP" \\
  --template-file "$TEMPLATE_FILE" \\
  --parameters "@$PARAMETERS_FILE" \\
  --verbose

echo "✅ Deployment completed successfully!"
`;

    const outputPath = path.join(infraDir, 'deploy.sh');
    fs.writeFileSync(outputPath, script, 'utf-8');
    fs.chmodSync(outputPath, '755');
    console.log(`  ✓ deploy.sh`);
  }

  /**
   * Generate documentation files
   */
  public async generateDocumentation(config: PayerConfig, outputDir: string): Promise<void> {
    const docsDir = path.join(outputDir, 'docs');
    fs.mkdirSync(docsDir, { recursive: true });

    // Generate DEPLOYMENT.md
    await this.generateDeploymentDoc(config, docsDir);

    // Generate CONFIGURATION.md
    await this.generateConfigurationDoc(config, docsDir);

    // Generate TESTING.md
    await this.generateTestingDoc(config, docsDir);

    // Generate main README.md
    await this.generateReadme(config, outputDir);

    console.log(`✅ Generated documentation in ${docsDir}`);
  }

  /**
   * Generate DEPLOYMENT.md
   */
  private async generateDeploymentDoc(config: PayerConfig, docsDir: string): Promise<void> {
    const content = `# Deployment Guide - ${config.payerName}

## Overview

This deployment package was generated for **${config.payerName}** (${config.payerId}) on ${new Date().toISOString()}.

## Prerequisites

- Azure CLI (version 2.40.0 or higher)
- Azure subscription with appropriate permissions
- Logic Apps Standard runtime
- PowerShell 7+ (for Windows deployments)

## Deployment Steps

### 1. Review Configuration

Review the configuration file at \`config/payer-config.json\` and ensure all settings are correct.

### 2. Deploy Infrastructure

\`\`\`bash
cd infrastructure
./deploy.sh
\`\`\`

### 3. Configure Secrets

Add the following secrets to Azure Key Vault:

${config.appeals?.authentication.keyVaultSecretName ? `- \`${config.appeals.authentication.keyVaultSecretName}\`: Appeals API credentials` : ''}
${config.ecs?.authentication.keyVaultSecretName ? `- \`${config.ecs.authentication.keyVaultSecretName}\`: ECS API credentials` : ''}
${config.attachments?.sftpConfig.keyVaultSecretName ? `- \`${config.attachments.sftpConfig.keyVaultSecretName}\`: SFTP private key` : ''}

### 4. Deploy Workflows

\`\`\`bash
cd workflows
zip -r workflows.zip ./*
az webapp deploy \\
  --resource-group ${config.infrastructure.resourceNamePrefix}-rg \\
  --name ${config.infrastructure.resourceNamePrefix}-la \\
  --src-path workflows.zip \\
  --type zip
\`\`\`

### 5. Verify Deployment

Check that all workflows are enabled and running in the Azure Portal.

## Enabled Modules

${config.enabledModules.appeals ? '- ✅ Appeals Processing' : '- ❌ Appeals Processing'}
${config.enabledModules.ecs ? '- ✅ Enhanced Claim Status (ECS)' : '- ❌ Enhanced Claim Status (ECS)'}
${config.enabledModules.attachments ? '- ✅ Attachments (X12 275/277/278)' : '- ❌ Attachments (X12 275/277/278)'}
${config.enabledModules.authorizations ? '- ✅ Authorizations' : '- ❌ Authorizations'}

## Environment

- **Environment**: ${config.infrastructure.environment}
- **Location**: ${config.infrastructure.location}
- **Resource Prefix**: ${config.infrastructure.resourceNamePrefix}

## Support

For deployment issues, contact: ${config.contactInfo.supportEmail || config.contactInfo.email}
`;

    fs.writeFileSync(path.join(docsDir, 'DEPLOYMENT.md'), content, 'utf-8');
    console.log('  ✓ DEPLOYMENT.md');
  }

  /**
   * Generate CONFIGURATION.md
   */
  private async generateConfigurationDoc(config: PayerConfig, docsDir: string): Promise<void> {
    const content = `# Configuration Guide - ${config.payerName}

## Configuration Overview

This document describes the configuration for ${config.payerName} (${config.payerId}).

## Contact Information

- **Primary Contact**: ${config.contactInfo.primaryContact}
- **Email**: ${config.contactInfo.email}
- **Phone**: ${config.contactInfo.phone}
${config.contactInfo.supportEmail ? `- **Support Email**: ${config.contactInfo.supportEmail}` : ''}

## Module Configuration

### Enabled Modules

${Object.entries(config.enabledModules).map(([key, value]) => `- **${key}**: ${value ? 'Enabled' : 'Disabled'}`).join('\n')}

${config.appeals?.enabled ? `
### Appeals Configuration

- **Test Endpoint**: ${config.appeals.apiEndpoints.test}
- **Prod Endpoint**: ${config.appeals.apiEndpoints.prod}
- **Authentication**: ${config.appeals.authentication.type}
- **Timeout**: ${config.appeals.timeout}ms
- **Retry Count**: ${config.appeals.retryCount}
- **Retry Interval**: ${config.appeals.retryInterval}ms

#### Request Reasons

${config.appeals.requestReasons.map(r => `- **${r.code}**: ${r.description} (Attachments: ${r.requiresAttachments ? 'Required' : 'Optional'})`).join('\n')}

#### Sub-Statuses

${config.appeals.subStatuses.map(s => `- **${s.code}**: ${s.description} ${s.isFinal ? '(Final)' : ''}`).join('\n')}

#### Attachment Rules

- **Pattern**: ${config.appeals.attachmentRules.pattern}
- **Max File Size**: ${(config.appeals.attachmentRules.maxFileSize / 1024 / 1024).toFixed(2)} MB
- **Allowed Formats**: ${config.appeals.attachmentRules.allowedFormats.join(', ')}
- **Max Attachments**: ${config.appeals.attachmentRules.maxAttachments}
` : ''}

${config.ecs?.enabled ? `
### ECS Configuration

- **Test Endpoint**: ${config.ecs.apiEndpoints.test}
- **Prod Endpoint**: ${config.ecs.apiEndpoints.prod}
- **Authentication**: ${config.ecs.authentication.type}
- **Timeout**: ${config.ecs.timeout}ms
- **Retry Count**: ${config.ecs.retryCount}

#### Search Methods

${Object.entries(config.ecs.searchMethods).map(([key, value]) => `- **${key}**: ${value ? 'Enabled' : 'Disabled'}`).join('\n')}
` : ''}

${config.attachments?.enabled ? `
### Attachments Configuration

#### SFTP Configuration

- **Host**: ${config.attachments.sftpConfig.host}
- **Port**: ${config.attachments.sftpConfig.port}
- **Username**: ${config.attachments.sftpConfig.username}
- **Inbound Folder**: ${config.attachments.sftpConfig.inboundFolder}
- **Outbound Folder**: ${config.attachments.sftpConfig.outboundFolder}

#### X12 Configuration

- **Sender ID**: ${config.attachments.x12Config.isa.senderId}
- **Receiver ID**: ${config.attachments.x12Config.isa.receiverId}
- **Transaction Sets**: ${Object.entries(config.attachments.x12Config.transactionSets).filter(([_, v]) => v).map(([k]) => k).join(', ')}
` : ''}

## Infrastructure Configuration

- **Resource Name Prefix**: ${config.infrastructure.resourceNamePrefix}
- **Location**: ${config.infrastructure.location}
- **Environment**: ${config.infrastructure.environment}
- **Logic App SKU**: ${config.infrastructure.logicAppConfig.sku}
- **Worker Count**: ${config.infrastructure.logicAppConfig.workerCount}
- **Always On**: ${config.infrastructure.logicAppConfig.alwaysOn}

## Tags

${Object.entries(config.infrastructure.tags).map(([key, value]) => `- **${key}**: ${value}`).join('\n')}
`;

    fs.writeFileSync(path.join(docsDir, 'CONFIGURATION.md'), content, 'utf-8');
    console.log('  ✓ CONFIGURATION.md');
  }

  /**
   * Generate TESTING.md
   */
  private async generateTestingDoc(config: PayerConfig, docsDir: string): Promise<void> {
    const content = `# Testing Guide - ${config.payerName}

## Testing Overview

This document provides testing instructions for ${config.payerName} (${config.payerId}).

## Prerequisites

- Deployed infrastructure and workflows
- Test credentials configured in Key Vault
- Sample test data

## Testing Workflows

${config.enabledModules.ecs ? `
### ECS Summary Search

Test the ECS summary search workflow:

\`\`\`bash
curl -X POST https://${config.infrastructure.resourceNamePrefix}-la.azurewebsites.net/api/ecs_summary_search/triggers/manual/invoke \\
  -H "Content-Type: application/json" \\
  -d '{
    "searchMethod": "ServiceDate",
    "requestId": "TEST-001",
    "serviceDateSearch": {
      "serviceFromDate": "2024-01-01",
      "serviceToDate": "2024-01-31",
      "providerId": "1234567890"
    }
  }'
\`\`\`
` : ''}

${config.enabledModules.appeals ? `
### Appeals Processing

Test appeals workflow by:

1. Uploading test file to SFTP inbound folder
2. Monitor workflow execution in Azure Portal
3. Verify message published to Service Bus
4. Check Application Insights logs
` : ''}

## Test Data

Use the following test data:

- **Test Claim Number**: TEST-CLAIM-001
- **Test Member ID**: TEST-MEMBER-001
- **Test Provider NPI**: 1234567890

## Validation

After testing, verify:

- ✅ Workflows execute successfully
- ✅ Data is stored in correct containers
- ✅ Messages published to Service Bus
- ✅ Logs appear in Application Insights
- ✅ No errors in dead-letter queues

## Troubleshooting

Common issues:

1. **Authentication failures**: Check Key Vault secrets
2. **Timeout errors**: Increase timeout values in configuration
3. **SFTP connection issues**: Verify network connectivity and credentials

## Support

For testing issues, contact: ${config.contactInfo.supportEmail || config.contactInfo.email}
`;

    fs.writeFileSync(path.join(docsDir, 'TESTING.md'), content, 'utf-8');
    console.log('  ✓ TESTING.md');
  }

  /**
   * Generate main README.md
   */
  private async generateReadme(config: PayerConfig, outputDir: string): Promise<void> {
    const content = `# ${config.payerName} - HIPAA Attachments Deployment

## Overview

This deployment package contains all necessary files for deploying HIPAA attachments processing for **${config.payerName}** (${config.payerId}).

**Generated**: ${new Date().toISOString()}

## Contents

- \`workflows/\` - Logic App workflow definitions
- \`infrastructure/\` - Bicep infrastructure templates
- \`docs/\` - Deployment and configuration documentation
- \`config/\` - Payer configuration file
- \`schemas/\` - JSON schemas for validation

## Quick Start

1. Review configuration: \`config/payer-config.json\`
2. Deploy infrastructure: \`cd infrastructure && ./deploy.sh\`
3. Configure secrets in Azure Key Vault
4. Deploy workflows: See \`docs/DEPLOYMENT.md\`
5. Test deployment: See \`docs/TESTING.md\`

## Enabled Modules

${Object.entries(config.enabledModules).map(([key, value]) => `- ${value ? '✅' : '❌'} **${key}**`).join('\n')}

## Environment

- **Environment**: ${config.infrastructure.environment}
- **Location**: ${config.infrastructure.location}
- **Resource Prefix**: ${config.infrastructure.resourceNamePrefix}

## Contact

- **Primary Contact**: ${config.contactInfo.primaryContact}
- **Email**: ${config.contactInfo.email}
- **Support**: ${config.contactInfo.supportEmail || config.contactInfo.email}

## Documentation

- [Deployment Guide](docs/DEPLOYMENT.md)
- [Configuration Guide](docs/CONFIGURATION.md)
- [Testing Guide](docs/TESTING.md)

## Support

For questions or issues, contact ${config.contactInfo.email}
`;

    fs.writeFileSync(path.join(outputDir, 'README.md'), content, 'utf-8');
    console.log('  ✓ README.md');
  }

  /**
   * Generate payer-specific schemas
   */
  public async generateSchemas(config: PayerConfig, outputDir: string): Promise<void> {
    const schemasDir = path.join(outputDir, 'schemas');
    fs.mkdirSync(schemasDir, { recursive: true });

    // Generate schemas based on enabled modules
    if (config.enabledModules.appeals && config.appeals?.enabled) {
      await this.generateAppealSchemas(config, schemasDir);
    }

    console.log(`✅ Generated schemas in ${schemasDir}`);
  }

  /**
   * Generate appeal-specific schemas
   */
  private async generateAppealSchemas(config: PayerConfig, schemasDir: string): Promise<void> {
    // Appeal Request Schema
    const appealRequestSchema = {
      $schema: 'http://json-schema.org/draft-07/schema#',
      title: `Appeal Request - ${config.payerName}`,
      type: 'object',
      required: ['claimNumber', 'memberId', 'requestReason'],
      properties: {
        claimNumber: { type: 'string' },
        memberId: { type: 'string' },
        providerNPI: { type: 'string' },
        requestReason: {
          type: 'string',
          enum: config.appeals!.requestReasons.map(r => r.code)
        },
        attachments: {
          type: 'array',
          items: { type: 'object' },
          maxItems: config.appeals!.attachmentRules.maxAttachments
        }
      }
    };

    fs.writeFileSync(
      path.join(schemasDir, 'Appeal-Request.json'),
      JSON.stringify(appealRequestSchema, null, 2),
      'utf-8'
    );
    console.log('  ✓ Appeal-Request.json');

    // Appeal Sub-Status Schema
    const subStatusSchema = {
      $schema: 'http://json-schema.org/draft-07/schema#',
      title: `Appeal Sub-Status - ${config.payerName}`,
      type: 'object',
      properties: {
        code: {
          type: 'string',
          enum: config.appeals!.subStatuses.map(s => s.code)
        },
        description: { type: 'string' },
        isFinal: { type: 'boolean' }
      }
    };

    fs.writeFileSync(
      path.join(schemasDir, 'Appeal-SubStatus.json'),
      JSON.stringify(subStatusSchema, null, 2),
      'utf-8'
    );
    console.log('  ✓ Appeal-SubStatus.json');
  }

  /**
   * Package deployment for distribution
   */
  public async packageDeployment(config: PayerConfig, outputDir: string): Promise<void> {
    // Copy original config to output
    const configDir = path.join(outputDir, 'config');
    fs.mkdirSync(configDir, { recursive: true });

    const configCopy = path.join(configDir, 'payer-config.json');
    fs.writeFileSync(configCopy, JSON.stringify(config, null, 2), 'utf-8');

    console.log(`✅ Deployment package ready at ${outputDir}`);
  }

  /**
   * Create placeholder infrastructure file
   */
  private createInfrastructurePlaceholder(config: PayerConfig, fileName: string): string {
    return `// ${fileName}.bicep - Generated placeholder for ${config.payerName}
// TODO: Complete infrastructure template

@description('Base name for resources')
param baseName string = '${config.infrastructure.resourceNamePrefix}'

@description('Azure region')
param location string = '${config.infrastructure.location}'

@description('Environment name')
param environment string = '${config.infrastructure.environment}'

@description('Resource tags')
param tags object = ${JSON.stringify(config.infrastructure.tags, null, 2)}

// Add resource definitions here
`;
  }
}

/**
 * Main entry point
 */
export async function main(): Promise<void> {
  const args = process.argv.slice(2);

  if (args.length < 1) {
    console.error('Usage: node generate-payer-deployment.js <config-path> [output-dir]');
    process.exit(1);
  }

  const configPath = args[0];
  const outputDir = args[1];

  try {
    const generator = new PayerDeploymentGenerator();

    console.log('📋 Loading payer configuration...');
    const config = await generator.loadPayerConfig(configPath);

    const finalOutputDir = outputDir || path.join(process.cwd(), 'generated', config.payerId);

    console.log(`\n🚀 Generating deployment for ${config.payerName}...`);
    console.log(`   Output: ${finalOutputDir}\n`);

    await generator.generateWorkflows(config, finalOutputDir);
    await generator.generateInfrastructure(config, finalOutputDir);
    await generator.generateDocumentation(config, finalOutputDir);
    await generator.generateSchemas(config, finalOutputDir);
    await generator.packageDeployment(config, finalOutputDir);

    console.log(`\n✅ Generated deployment for ${config.payerName}`);
    console.log(`📦 Output: ${finalOutputDir}`);
  } catch (error) {
    console.error(`\n❌ Error: ${error instanceof Error ? error.message : error}`);
    process.exit(1);
  }
}

// Run if called directly
if (require.main === module) {
  main();
}
