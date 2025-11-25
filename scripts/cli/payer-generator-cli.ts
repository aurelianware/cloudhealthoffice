#!/usr/bin/env node
/**
 * Interactive CLI for Payer Deployment Generator
 * Provides user-friendly interface for generating payer deployments
 */

import { Command } from 'commander';
import * as fs from 'fs';
import * as path from 'path';
import { PayerDeploymentGenerator } from '../generate-payer-deployment';
import { PayerConfig } from '../../core/types/payer-config';
import { DeploymentValidator } from '../../core/validation/config-validator';
import { InteractiveWizard } from './interactive-wizard';

// Note: chalk, ora, and inquirer are ESM modules - we'll use simple console logging instead
function chalk_green(text: string): string { return `\x1b[32m${text}\x1b[0m`; }
function chalk_cyan(text: string): string { return `\x1b[36m${text}\x1b[0m`; }
function chalk_yellow(text: string): string { return `\x1b[33m${text}\x1b[0m`; }
function chalk_red(text: string): string { return `\x1b[31m${text}\x1b[0m`; }
function chalk_bold(text: string): string { return `\x1b[1m${text}\x1b[0m`; }

const chalk = {
  green: (text: string) => chalk_green(text),
  cyan: (text: string) => chalk_cyan(text),
  yellow: (text: string) => chalk_yellow(text),
  red: (text: string) => chalk_red(text),
};

// Simple spinner implementation
class SimpleSpinner {
  private message: string;
  constructor(message: string) {
    this.message = message;
    console.log(`⏳ ${message}`);
  }
  succeed(message: string) {
    console.log(`✅ ${message}`);
  }
  start(message?: string) {
    if (message) {
      this.message = message;
      console.log(`⏳ ${message}`);
    }
    return this;
  }
  stop() {}
}

function ora(message: string): SimpleSpinner {
  return new SimpleSpinner(message);
}

const program = new Command();

program
  .name('payer-generator')
  .description('Generate Logic App deployments from payer configuration')
  .version('1.0.0');

/**
 * Generate command
 */
program
  .command('generate')
  .description('Generate deployment package from payer configuration')
  .option('-c, --config <path>', 'Path to payer configuration file')
  .option('-o, --output <path>', 'Output directory')
  .option('-m, --modules <modules>', 'Comma-separated list of modules to generate')
  .option('-d, --dry-run', 'Show what would be generated without creating files')
  .option('-f, --force', 'Overwrite existing output directory')
  .action(async (options) => {
    try {
      let configPath = options.config;

      // Interactive prompt if config not provided
      if (!configPath) {
        console.error(chalk.red('Error: Config file path is required. Use -c option.'));
        process.exit(1);
      }

      const spinner = ora('Loading configuration...').start();

      const generator = new PayerDeploymentGenerator();
      const config = await generator.loadPayerConfig(configPath);

      spinner.succeed(chalk.green(`Loaded configuration for ${config.payerName}`));

      const outputDir = options.output || path.join(process.cwd(), 'generated', config.payerId);

      // Check if output directory exists
      if (fs.existsSync(outputDir) && !options.force) {
        console.error(chalk.yellow(`Output directory ${outputDir} already exists. Use -f to overwrite.`));
        return;
      }

      if (options.dryRun) {
        console.log(chalk.cyan('\n🔍 Dry run mode - showing what would be generated:\n'));
        console.log(`Output directory: ${outputDir}`);
        console.log(`\nEnabled modules:`);
        Object.entries(config.enabledModules).forEach(([module, enabled]) => {
          console.log(`  ${enabled ? '✅' : '❌'} ${module}`);
        });
        console.log('\nWould generate:');
        console.log('  - Logic App workflows');
        console.log('  - Bicep infrastructure templates');
        console.log('  - Documentation (DEPLOYMENT.md, CONFIGURATION.md, TESTING.md)');
        console.log('  - JSON schemas');
        console.log('  - Deployment package');
        return;
      }

      // Generate deployment
      spinner.start('Generating workflows...');
      await generator.generateWorkflows(config, outputDir);
      spinner.succeed('Workflows generated');

      spinner.start('Generating infrastructure...');
      await generator.generateInfrastructure(config, outputDir);
      spinner.succeed('Infrastructure templates generated');

      spinner.start('Generating documentation...');
      await generator.generateDocumentation(config, outputDir);
      spinner.succeed('Documentation generated');

      spinner.start('Generating schemas...');
      await generator.generateSchemas(config, outputDir);
      spinner.succeed('Schemas generated');

      spinner.start('Packaging deployment...');
      await generator.packageDeployment(config, outputDir);
      spinner.succeed('Deployment packaged');

      console.log(chalk_bold(chalk.green(`\n✅ Successfully generated deployment for ${config.payerName}`)));
      console.log(chalk.cyan(`📦 Output: ${outputDir}`));

    } catch (error) {
      console.error(chalk.red(`\n❌ Error: ${error instanceof Error ? error.message : error}`));
      process.exit(1);
    }
  });

/**
 * Validate command
 */
program
  .command('validate')
  .description('Validate payer configuration without generating')
  .argument('<config-path>', 'Path to payer configuration file')
  .action(async (configPath) => {
    try {
      if (!fs.existsSync(configPath)) {
        console.error(chalk.red(`Configuration file not found: ${configPath}`));
        process.exit(1);
      }

      const spinner = ora('Validating configuration...').start();

      const configContent = fs.readFileSync(configPath, 'utf-8');
      const config: PayerConfig = JSON.parse(configContent);

      const validator = new DeploymentValidator();
      const report = validator.generateValidationReport(config);

      spinner.stop();
      console.log('\n' + report);

      const result = validator.validateForGeneration(config);
      if (!result.valid) {
        process.exit(1);
      }

    } catch (error) {
      console.error(chalk.red(`\n❌ Error: ${error instanceof Error ? error.message : error}`));
      process.exit(1);
    }
  });

/**
 * Template command
 */
program
  .command('template')
  .description('Generate a template payer configuration file')
  .option('-o, --output <path>', 'Output file path', './payer-config.json')
  .option('-t, --type <type>', 'Template type (medicaid|blues|generic)', 'generic')
  .action(async (options) => {
    try {
      let templatePath: string;

      switch (options.type) {
        case 'medicaid':
          templatePath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
          break;
        case 'blues':
          templatePath = path.join(__dirname, '../../core/examples/regional-blues-config.json');
          break;
        default:
          templatePath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      }

      if (!fs.existsSync(templatePath)) {
        console.error(chalk.red(`Template not found: ${templatePath}`));
        process.exit(1);
      }

      const templateContent = fs.readFileSync(templatePath, 'utf-8');
      fs.writeFileSync(options.output, templateContent, 'utf-8');

      console.log(chalk.green(`✅ Template configuration created: ${options.output}`));
      console.log(chalk.cyan('   Edit the file and run: payer-generator generate -c ' + options.output));

    } catch (error) {
      console.error(chalk.red(`\n❌ Error: ${error instanceof Error ? error.message : error}`));
      process.exit(1);
    }
  });

/**
 * List command
 */
program
  .command('list')
  .description('List available workflow templates')
  .action(async () => {
    try {
      const templatesDir = path.join(__dirname, '../templates/workflows');

      if (!fs.existsSync(templatesDir)) {
        console.log(chalk.yellow('No templates directory found'));
        return;
      }

      const templates = fs.readdirSync(templatesDir)
        .filter(f => f.endsWith('.template.json'))
        .map(f => f.replace('.template.json', ''));

      console.log(chalk_bold(chalk.cyan('\n📋 Available Workflow Templates:\n')));
      templates.forEach(template => {
        console.log(`  - ${template}`);
      });
      console.log('');

    } catch (error) {
      console.error(chalk.red(`\n❌ Error: ${error instanceof Error ? error.message : error}`));
      process.exit(1);
    }
  });

/**
 * Interactive mode - Full guided wizard
 */
program
  .command('interactive')
  .description('Start interactive configuration wizard for guided onboarding')
  .option('-o, --output <path>', 'Output configuration file path', './payer-config.json')
  .option('--generate', 'Automatically generate deployment after configuration')
  .action(async (options) => {
    try {
      const wizard = new InteractiveWizard();
      const config = await wizard.run();

      // Save configuration
      await wizard.saveConfiguration(config, options.output);

      // Optionally generate deployment immediately
      if (options.generate) {
        console.log(chalk.cyan('\n🚀 Generating deployment package...'));
        const generator = new PayerDeploymentGenerator();
        const outputDir = path.join(process.cwd(), 'generated', config.payerId);

        const spinner = ora('Generating workflows...').start();
        await generator.generateWorkflows(config, outputDir);
        spinner.succeed('Workflows generated');

        spinner.start('Generating infrastructure...');
        await generator.generateInfrastructure(config, outputDir);
        spinner.succeed('Infrastructure templates generated');

        spinner.start('Generating documentation...');
        await generator.generateDocumentation(config, outputDir);
        spinner.succeed('Documentation generated');

        spinner.start('Generating schemas...');
        await generator.generateSchemas(config, outputDir);
        spinner.succeed('Schemas generated');

        spinner.start('Packaging deployment...');
        await generator.packageDeployment(config, outputDir);
        spinner.succeed('Deployment packaged');

        console.log(chalk_bold(chalk.green(`\n✅ Successfully generated deployment for ${config.payerName}`)));
        console.log(chalk.cyan(`📦 Output: ${outputDir}`));
      } else {
        console.log(chalk.cyan('\n📝 Next steps:'));
        console.log(chalk.cyan(`  1. Review configuration: ${options.output}`));
        console.log(chalk.cyan(`  2. Generate deployment: payer-generator generate -c ${options.output}`));
        console.log(chalk.cyan(`  3. Deploy to Azure: cd generated/${config.payerId} && ./infrastructure/deploy.sh`));
      }
    } catch (error) {
      console.error(chalk.red(`\n❌ Error: ${error instanceof Error ? error.message : error}`));
      if (error instanceof Error && error.stack) {
        console.error(chalk.red('Stack trace:'));
        console.error(error.stack);
      }
      process.exit(1);
    }
  });

/**
 * CMS-0057-F Quick Deploy - Deploy all CMS-0057-F APIs in under 10 minutes
 */
program
  .command('cms-deploy')
  .description('Quick deploy CMS-0057-F compliant APIs (Prior Auth, Patient Access, Provider Access)')
  .option('-c, --config <path>', 'Path to payer configuration file (optional, uses interactive if not provided)')
  .option('-r, --resource-group <name>', 'Azure resource group name')
  .option('-l, --location <location>', 'Azure region', 'eastus')
  .option('--skip-fhir', 'Skip FHIR API deployment')
  .option('--skip-pa', 'Skip Prior Authorization API deployment')
  .option('--dry-run', 'Show what would be deployed without actually deploying')
  .action(async (options) => {
    try {
      console.log(chalk_bold(chalk.cyan('\n🚀 CMS-0057-F Quick Deploy\n')));
      console.log(chalk.yellow('Target: Generate all deployment artifacts in <1 minute.\nFull Azure deployment: 8-12 minutes (including manual steps)\n'));

      let config;
      
      // Load or create configuration
      if (options.config) {
        const spinner = ora('Loading configuration...').start();
        const generator = new PayerDeploymentGenerator();
        config = await generator.loadPayerConfig(options.config);
        spinner.succeed(chalk.green(`Loaded configuration for ${config.payerName}`));
      } else {
        console.log(chalk.cyan('No config provided. Starting interactive wizard...\n'));
        const wizard = new InteractiveWizard();
        config = await wizard.run();
        
        // Save for future use
        const configPath = './cms-0057-f-config.json';
        await wizard.saveConfiguration(config, configPath);
        console.log(chalk.green(`✅ Configuration saved to ${configPath}`));
      }

      if (options.dryRun) {
        console.log(chalk.cyan('\n🔍 Dry run mode - showing deployment plan:\n'));
        console.log(`Resource Group: ${options.resourceGroup || 'my-payer-rg'}`);
        console.log(`Location: ${options.location}`);
        console.log(`\nAPIs to Deploy:`);
        console.log(`  ${options.skipFhir ? '❌' : '✅'} FHIR R4 API (Patient Access)`);
        console.log(`  ${options.skipPa ? '❌' : '✅'} Prior Authorization API (Da Vinci PAS)`);
        console.log(`  ✅ Provider Access API (Payer-to-Payer)`);
        console.log(`\nFHIR Resources:`);
        console.log(`  - Patient (US Core)`);
        console.log(`  - Claim (US Core)`);
        console.log(`  - ExplanationOfBenefit (US Core)`);
        console.log(`  - ServiceRequest (Da Vinci PAS)`);
        console.log(`  - CoverageEligibilityRequest`);
        console.log(`\nDeployment Steps:`);
        console.log(`  1. Generate deployment artifacts (1 min)`);
        console.log(`  2. Deploy Azure infrastructure (3-5 min)`);
        console.log(`  3. Configure FHIR endpoints (1-2 min)`);
        console.log(`  4. Deploy Logic App workflows (2-3 min)`);
        console.log(`  5. Run compliance validation (1 min)`);
        console.log(`\nEstimated Total Time: <10 minutes`);
        return;
      }

      const startTime = Date.now();
      const outputDir = path.join(process.cwd(), 'generated', 'cms-0057-f', config.payerId);
      
      console.log(chalk.cyan('\n📦 Step 1/5: Generating deployment artifacts...\n'));
      
      const generator = new PayerDeploymentGenerator();
      
      let spinner = ora('Generating workflows...').start();
      await generator.generateWorkflows(config, outputDir);
      spinner.succeed('Workflows generated');

      spinner = ora('Generating infrastructure...').start();
      await generator.generateInfrastructure(config, outputDir);
      spinner.succeed('Infrastructure templates generated');

      spinner = ora('Generating documentation...').start();
      await generator.generateDocumentation(config, outputDir);
      spinner.succeed('Documentation generated');

      spinner = ora('Generating schemas...').start();
      await generator.generateSchemas(config, outputDir);
      spinner.succeed('Schemas generated');

      spinner = ora('Packaging deployment...').start();
      await generator.packageDeployment(config, outputDir);
      spinner.succeed('Deployment packaged');

      const step1Time = ((Date.now() - startTime) / 1000).toFixed(1);
      console.log(chalk.green(`✅ Step 1 complete (${step1Time}s)\n`));

      console.log(chalk.cyan('📋 Step 2/5: Next Steps for Azure Deployment\n'));
      console.log(chalk.yellow('Run the following commands to complete deployment:\n'));
      console.log(chalk.cyan(`cd ${outputDir}`));
      
      if (options.resourceGroup) {
        console.log(chalk.cyan(`az deployment group create \\`));
        console.log(chalk.cyan(`  --resource-group ${options.resourceGroup} \\`));
        console.log(chalk.cyan(`  --template-file infrastructure/main.bicep \\`));
        console.log(chalk.cyan(`  --parameters infrastructure/parameters.json`));
      } else {
        console.log(chalk.cyan(`./infrastructure/deploy.sh`));
      }

      console.log(chalk.cyan('\n📊 Step 3/5: CMS-0057-F Compliance Validation\n'));
      console.log(chalk.green('After deployment, validate compliance with:'));
      console.log(chalk.cyan(`npm run test:fhir`));
      console.log(chalk.cyan(`node dist/src/fhir/examples.js`));

      console.log(chalk.cyan('\n🔍 Step 4/5: FHIR Endpoint Configuration\n'));
      console.log(chalk.green('Configure your FHIR endpoints:'));
      console.log(chalk.cyan(`  - Patient Access: https://<logic-app>.azurewebsites.net/api/patient`));
      console.log(chalk.cyan(`  - Prior Auth: https://<logic-app>.azurewebsites.net/api/prior-auth`));
      console.log(chalk.cyan(`  - Claims: https://<logic-app>.azurewebsites.net/api/claim`));

      console.log(chalk.cyan('\n✅ Step 5/5: Timeline Monitoring\n'));
      console.log(chalk.green('Monitor CMS-0057-F timeline compliance:'));
      console.log(chalk.cyan(`  - Life-threatening: ≤24 hours`));
      console.log(chalk.cyan(`  - Expedited: ≤72 hours`));
      console.log(chalk.cyan(`  - Standard: ≤7 days`));

      const totalTime = ((Date.now() - startTime) / 1000 / 60).toFixed(1);
      
      console.log(chalk_bold(chalk.green(`\n\n🎉 CMS-0057-F deployment preparation complete!`)));
      console.log(chalk.green(`Time elapsed: ${totalTime} minutes`));
      console.log(chalk.cyan(`Output: ${outputDir}`));
      
      console.log(chalk.yellow(`\n⚡ Quick Start:`));
      console.log(chalk.cyan(`1. Deploy infrastructure: cd ${outputDir} && ./infrastructure/deploy.sh`));
      console.log(chalk.cyan(`2. Test APIs: npm run test:fhir`));
      console.log(chalk.cyan(`3. Validate compliance: node dist/src/fhir/examples.js`));

    } catch (error) {
      console.error(chalk.red(`\n❌ Error: ${error instanceof Error ? error.message : error}`));
      if (error instanceof Error && error.stack) {
        console.error(chalk.red('Stack trace:'));
        console.error(error.stack);
      }
      process.exit(1);
    }
  });

program.parse(process.argv);

// Show help if no command provided
if (!process.argv.slice(2).length) {
  program.outputHelp();
}
