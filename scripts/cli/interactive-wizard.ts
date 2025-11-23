/**
 * Interactive CLI Wizard for Cloud Health Office Onboarding
 * Provides step-by-step guided configuration with validation and error handling
 */

import inquirer from 'inquirer';
import * as fs from 'fs';
import * as path from 'path';
import { PayerConfig } from '../../core/types/payer-config';
import { DeploymentValidator } from '../../core/validation/config-validator';

// ANSI color helpers
const chalk = {
  green: (text: string) => `\x1b[32m${text}\x1b[0m`,
  cyan: (text: string) => `\x1b[36m${text}\x1b[0m`,
  yellow: (text: string) => `\x1b[33m${text}\x1b[0m`,
  red: (text: string) => `\x1b[31m${text}\x1b[0m`,
  bold: (text: string) => `\x1b[1m${text}\x1b[0m`,
};

export class InteractiveWizard {
  private validator: DeploymentValidator;

  constructor() {
    this.validator = new DeploymentValidator();
  }

  /**
   * Display welcome banner
   */
  private displayWelcome(): void {
    console.log('\n' + chalk.bold(chalk.cyan('═'.repeat(70))));
    console.log(chalk.bold(chalk.cyan('    Cloud Health Office - Interactive Onboarding Wizard')));
    console.log(chalk.bold(chalk.cyan('═'.repeat(70))));
    console.log(chalk.cyan('\n  Welcome to the #1 open-source Azure-native multi-payer EDI platform'));
    console.log(chalk.cyan('  This wizard will guide you through configuration in <5 minutes\n'));
  }

  /**
   * Run the interactive wizard
   */
  async run(): Promise<PayerConfig> {
    this.displayWelcome();

    try {
      // Step 1: Basic Information
      console.log(chalk.bold('\n📋 Step 1: Basic Information'));
      const basicInfo = await this.collectBasicInfo();

      // Step 2: Trading Partner Configuration
      console.log(chalk.bold('\n🤝 Step 2: Trading Partner Configuration'));
      const tradingPartner = await this.collectTradingPartnerInfo();

      // Step 3: Module Selection
      console.log(chalk.bold('\n📦 Step 3: Module Selection'));
      const modules = await this.collectModules();

      // Step 4: Infrastructure Settings
      console.log(chalk.bold('\n☁️  Step 4: Infrastructure Settings'));
      const infrastructure = await this.collectInfrastructure(basicInfo.payerId);

      // Step 5: Monitoring & Compliance
      console.log(chalk.bold('\n📊 Step 5: Monitoring & Compliance'));
      const monitoring = await this.collectMonitoring();

      // Build complete configuration
      const config: PayerConfig = {
        ...basicInfo,
        enabledModules: modules,
        attachments: tradingPartner,
        infrastructure,
        monitoring,
      };

      // Validate configuration
      console.log(chalk.bold('\n✓ Validating configuration...'));
      const validation = this.validator.validateForGeneration(config);
      
      if (!validation.valid) {
        console.error(chalk.red('\n❌ Configuration validation failed:'));
        validation.errors.forEach((error) => {
          const message = typeof error === 'string' ? error : error.message || JSON.stringify(error);
          console.error(chalk.red(`  • ${message}`));
        });
        throw new Error('Invalid configuration');
      }

      console.log(chalk.green('\n✅ Configuration validated successfully!'));

      // Review and confirm
      console.log(chalk.bold('\n📝 Configuration Summary:'));
      console.log(chalk.cyan(`  Payer: ${config.payerName} (${config.payerId})`));
      console.log(chalk.cyan(`  Organization: ${config.organizationName}`));
      console.log(chalk.cyan(`  Region: ${config.infrastructure.location}`));
      console.log(chalk.cyan(`  Modules: ${Object.entries(modules).filter(([_, v]) => v).map(([k]) => k).join(', ')}`));

      const { confirm } = await inquirer.prompt([
        {
          type: 'confirm',
          name: 'confirm',
          message: 'Does this configuration look correct?',
          default: true,
        },
      ]);

      if (!confirm) {
        console.log(chalk.yellow('\n❌ Configuration cancelled. Please run the wizard again.'));
        process.exit(0);
      }

      return config;
    } catch (error) {
      if (error instanceof Error && error.message === 'User force closed the prompt') {
        console.log(chalk.yellow('\n\n⚠️  Wizard cancelled by user.'));
        process.exit(0);
      }
      throw error;
    }
  }

  /**
   * Collect basic payer information
   */
  private async collectBasicInfo() {
    const answers = await inquirer.prompt([
      {
        type: 'input',
        name: 'payerId',
        message: 'Payer ID (unique identifier, e.g., MCO001):',
        validate: (input: string) => {
          if (!input || input.length < 3) {
            return 'Payer ID must be at least 3 characters';
          }
          if (!/^[A-Z0-9_-]+$/i.test(input)) {
            return 'Payer ID can only contain letters, numbers, hyphens, and underscores';
          }
          return true;
        },
      },
      {
        type: 'input',
        name: 'payerName',
        message: 'Payer Name (display name):',
        validate: (input: string) => {
          if (!input || input.length < 3) {
            return 'Payer name must be at least 3 characters';
          }
          return true;
        },
      },
      {
        type: 'input',
        name: 'organizationName',
        message: 'Organization Name (legal entity):',
        validate: (input: string) => {
          if (!input || input.length < 3) {
            return 'Organization name must be at least 3 characters';
          }
          return true;
        },
      },
      {
        type: 'input',
        name: 'contactEmail',
        message: 'Primary Contact Email:',
        validate: (input: string) => {
          const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
          if (!emailRegex.test(input)) {
            return 'Please enter a valid email address';
          }
          return true;
        },
      },
    ]);

    return {
      payerId: answers.payerId,
      payerName: answers.payerName,
      organizationName: answers.organizationName,
      contactInfo: {
        primaryContact: '',
        email: answers.contactEmail,
        phone: '',
        supportEmail: answers.contactEmail,
      },
    };
  }

  /**
   * Collect trading partner information
   */
  private async collectTradingPartnerInfo() {
    const answers = await inquirer.prompt([
      {
        type: 'list',
        name: 'clearinghouse',
        message: 'Select your clearinghouse:',
        choices: [
          { name: 'Availity (Recommended)', value: 'availity' },
          { name: 'Change Healthcare', value: 'change' },
          { name: 'Waystar', value: 'waystar' },
          { name: 'Other/Custom', value: 'custom' },
        ],
        default: 'availity',
      },
      {
        type: 'input',
        name: 'senderId',
        message: 'Your X12 Sender ID (ISA qualifier):',
        default: (answers: any) => answers.clearinghouse === 'availity' ? '030240928' : '',
        validate: (input: string) => {
          if (!input || input.length < 6) {
            return 'Sender ID must be at least 6 characters';
          }
          return true;
        },
      },
      {
        type: 'input',
        name: 'receiverId',
        message: 'Your X12 Receiver ID:',
        validate: (input: string) => {
          if (!input || input.length < 6) {
            return 'Receiver ID must be at least 6 characters';
          }
          return true;
        },
      },
      {
        type: 'input',
        name: 'sftpHost',
        message: 'SFTP Host (e.g., sftp.availity.com):',
        default: (answers: any) => answers.clearinghouse === 'availity' ? 'sftp.availity.com' : '',
        validate: (input: string) => {
          if (!input) {
            return 'SFTP host is required';
          }
          return true;
        },
      },
      {
        type: 'input',
        name: 'sftpUsername',
        message: 'SFTP Username:',
        validate: (input: string) => {
          if (!input) {
            return 'SFTP username is required';
          }
          return true;
        },
      },
    ]);

    return {
      enabled: true,
      sftpConfig: {
        host: answers.sftpHost,
        port: 22,
        username: answers.sftpUsername,
        keyVaultSecretName: `${answers.receiverId.toLowerCase()}-sftp-key`,
        inboundFolder: '/inbound/attachments',
        outboundFolder: '/outbound/responses',
      },
      x12Config: {
        isa: {
          senderId: answers.senderId,
          receiverId: answers.receiverId,
          senderQualifier: 'ZZ',
          receiverQualifier: 'ZZ',
        },
        transactionSets: {
          275: true,
          277: true,
          278: true,
        },
      },
      archivalConfig: {
        storageAccountName: '',
        containerName: 'hipaa-attachments',
        retentionDays: 2555,
      },
    };
  }

  /**
   * Collect enabled modules
   */
  private async collectModules() {
    console.log(chalk.cyan('  Select which modules to enable (Space to toggle, Enter to confirm)'));
    
    const answers = await inquirer.prompt([
      {
        type: 'checkbox',
        name: 'modules',
        message: 'Enabled Modules:',
        choices: [
          { name: 'Attachments (275/277 X12 EDI)', value: 'attachments', checked: true },
          { name: 'Authorizations (278 X12 EDI)', value: 'authorizations', checked: true },
          { name: 'Appeals Management API', value: 'appeals', checked: false },
          { name: 'Enhanced Claim Status (ECS/277CA)', value: 'ecs', checked: false },
        ],
      },
    ]);

    return {
      attachments: answers.modules.includes('attachments'),
      authorizations: answers.modules.includes('authorizations'),
      appeals: answers.modules.includes('appeals'),
      ecs: answers.modules.includes('ecs'),
    };
  }

  /**
   * Collect infrastructure settings
   */
  private async collectInfrastructure(payerId: string) {
    const answers = await inquirer.prompt([
      {
        type: 'list',
        name: 'location',
        message: 'Azure Region:',
        choices: [
          { name: 'East US (Recommended)', value: 'eastus' },
          { name: 'East US 2', value: 'eastus2' },
          { name: 'West US', value: 'westus' },
          { name: 'West US 2', value: 'westus2' },
          { name: 'Central US', value: 'centralus' },
        ],
        default: 'eastus',
      },
      {
        type: 'list',
        name: 'environment',
        message: 'Environment Type:',
        choices: [
          { name: 'Development', value: 'dev' },
          { name: 'UAT/Staging', value: 'uat' },
          { name: 'Production', value: 'prod' },
        ],
        default: 'dev',
      },
      {
        type: 'input',
        name: 'resourceNamePrefix',
        message: 'Resource Name Prefix:',
        default: payerId.toLowerCase(),
        validate: (input: string) => {
          if (!/^[a-z0-9-]+$/.test(input)) {
            return 'Prefix must contain only lowercase letters, numbers, and hyphens';
          }
          if (input.length > 20) {
            return 'Prefix must be 20 characters or less';
          }
          return true;
        },
      },
    ]);

    return {
      resourceNamePrefix: answers.resourceNamePrefix,
      location: answers.location,
      environment: answers.environment,
      tags: {
        Project: 'Cloud-Health-Office',
        Payer: payerId,
        Environment: answers.environment,
        ManagedBy: 'CloudHealthOffice',
      },
      storageConfig: {
        sku: answers.environment === 'prod' ? 'Standard_GRS' : 'Standard_LRS',
        containers: ['hipaa-attachments', 'appeal-documents', 'ecs-queries'],
        lifecycleRules: [],
      },
      serviceBusConfig: {
        sku: 'Standard',
        topics: [
          {
            name: 'attachments-in',
            maxSizeInMegabytes: 1024,
            defaultMessageTimeToLive: 'P14D',
            requiresDuplicateDetection: true,
            subscriptions: [
              {
                name: 'appeals-processor',
                maxDeliveryCount: 10,
                lockDuration: 'PT5M',
              },
            ],
          },
          {
            name: 'edi-278',
            maxSizeInMegabytes: 1024,
            defaultMessageTimeToLive: 'P14D',
            requiresDuplicateDetection: true,
            subscriptions: [
              {
                name: '278-processor',
                maxDeliveryCount: 10,
                lockDuration: 'PT5M',
              },
            ],
          },
          {
            name: 'rfai-requests',
            maxSizeInMegabytes: 1024,
            defaultMessageTimeToLive: 'P14D',
            requiresDuplicateDetection: true,
            subscriptions: [
              {
                name: 'rfai-processor',
                maxDeliveryCount: 10,
                lockDuration: 'PT5M',
              },
            ],
          },
        ],
        queues: [
          {
            name: 'dead-letter',
            maxSizeInMegabytes: 1024,
            defaultMessageTimeToLive: 'P30D',
            requiresDuplicateDetection: false,
          },
        ],
      },
      logicAppConfig: {
        sku: answers.environment === 'prod' ? 'WS2' : 'WS1',
        workerCount: 1,
        alwaysOn: true,
      },
      keyVaultConfig: {
        sku: 'standard' as 'standard' | 'premium',
        enableSoftDelete: true,
        softDeleteRetentionDays: 90,
      },
    };
  }

  /**
   * Collect monitoring settings
   */
  private async collectMonitoring() {
    const answers = await inquirer.prompt([
      {
        type: 'confirm',
        name: 'enableAppInsights',
        message: 'Enable Application Insights monitoring?',
        default: true,
      },
      {
        type: 'list',
        name: 'logRetention',
        message: 'Log retention period (days):',
        choices: [
          { name: '30 days (Dev/Test)', value: 30 },
          { name: '90 days (Recommended)', value: 90 },
          { name: '180 days (Compliance)', value: 180 },
          { name: '365 days (Extended)', value: 365 },
        ],
        default: 90,
      },
    ]);

    return {
      applicationInsights: {
        enabled: answers.enableAppInsights,
        samplingPercentage: 100,
      },
      logRetentionDays: answers.logRetention,
      alertRules: [],
    };
  }

  /**
   * Save configuration to file
   */
  async saveConfiguration(config: PayerConfig, outputPath: string): Promise<void> {
    const dir = path.dirname(outputPath);
    if (!fs.existsSync(dir)) {
      fs.mkdirSync(dir, { recursive: true });
    }

    fs.writeFileSync(outputPath, JSON.stringify(config, null, 2), 'utf-8');
    console.log(chalk.green(`\n✅ Configuration saved to: ${outputPath}`));
  }
}
