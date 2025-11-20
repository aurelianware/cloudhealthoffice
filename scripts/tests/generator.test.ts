/**
 * Tests for PayerDeploymentGenerator
 */

import * as fs from 'fs';
import * as path from 'path';
import { PayerDeploymentGenerator } from '../generate-payer-deployment';
import { PayerConfig } from '../../core/types/payer-config';

describe('PayerDeploymentGenerator', () => {
  let generator: PayerDeploymentGenerator;
  const testOutputDir = '/tmp/generator-test-output';

  beforeEach(() => {
    generator = new PayerDeploymentGenerator();
    // Clean up test output directory
    if (fs.existsSync(testOutputDir)) {
      fs.rmSync(testOutputDir, { recursive: true, force: true });
    }
  });

  afterEach(() => {
    // Clean up after tests
    if (fs.existsSync(testOutputDir)) {
      fs.rmSync(testOutputDir, { recursive: true, force: true });
    }
  });

  describe('loadPayerConfig', () => {
    it('should load valid configuration file', async () => {
      const configPath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      const config = await generator.loadPayerConfig(configPath);

      expect(config).toBeDefined();
      expect(config.payerId).toBe('MCO001');
      expect(config.payerName).toBe('State Medicaid MCO');
    });

    it('should throw error for non-existent file', async () => {
      await expect(generator.loadPayerConfig('/nonexistent/file.json')).rejects.toThrow('Configuration file not found');
    });

    it('should validate configuration on load', async () => {
      const invalidConfigPath = '/tmp/invalid-config.json';
      const invalidConfig = {
        payerId: 'invalid-id-with-lowercase', // Should be uppercase only
        payerName: 'Test',
        // Missing required fields
      };
      fs.writeFileSync(invalidConfigPath, JSON.stringify(invalidConfig));

      await expect(generator.loadPayerConfig(invalidConfigPath)).rejects.toThrow('Configuration validation failed');

      fs.unlinkSync(invalidConfigPath);
    });
  });

  describe('generateWorkflows', () => {
    it('should generate workflows for enabled modules', async () => {
      const configPath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      const config = await generator.loadPayerConfig(configPath);

      await generator.generateWorkflows(config, testOutputDir);

      const workflowsDir = path.join(testOutputDir, 'workflows');
      expect(fs.existsSync(workflowsDir)).toBe(true);

      // Check that at least one workflow was generated
      const workflows = fs.readdirSync(workflowsDir);
      expect(workflows.length).toBeGreaterThan(0);
    });

    it('should create valid JSON workflow files', async () => {
      const configPath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      const config = await generator.loadPayerConfig(configPath);

      await generator.generateWorkflows(config, testOutputDir);

      const workflowsDir = path.join(testOutputDir, 'workflows');
      const workflows = fs.readdirSync(workflowsDir);

      workflows.forEach(workflowName => {
        const workflowPath = path.join(workflowsDir, workflowName, 'workflow.json');
        if (fs.existsSync(workflowPath)) {
          const content = fs.readFileSync(workflowPath, 'utf-8');
          // Should be valid JSON
          expect(() => JSON.parse(content)).not.toThrow();
          
          const workflow = JSON.parse(content);
          expect(workflow).toHaveProperty('definition');
          expect(workflow).toHaveProperty('kind');
          expect(workflow.kind).toBe('Stateful');
        }
      });
    });
  });

  describe('generateInfrastructure', () => {
    it('should generate infrastructure files', async () => {
      const configPath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      const config = await generator.loadPayerConfig(configPath);

      await generator.generateInfrastructure(config, testOutputDir);

      const infraDir = path.join(testOutputDir, 'infrastructure');
      expect(fs.existsSync(infraDir)).toBe(true);
      expect(fs.existsSync(path.join(infraDir, 'main.bicep'))).toBe(true);
      expect(fs.existsSync(path.join(infraDir, 'parameters.json'))).toBe(true);
      expect(fs.existsSync(path.join(infraDir, 'deploy.sh'))).toBe(true);
    });

    it('should create valid parameters.json', async () => {
      const configPath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      const config = await generator.loadPayerConfig(configPath);

      await generator.generateInfrastructure(config, testOutputDir);

      const paramsPath = path.join(testOutputDir, 'infrastructure', 'parameters.json');
      const content = fs.readFileSync(paramsPath, 'utf-8');
      const params = JSON.parse(content);

      expect(params).toHaveProperty('$schema');
      expect(params).toHaveProperty('parameters');
      expect(params.parameters).toHaveProperty('baseName');
      expect(params.parameters.baseName.value).toBe('mco001');
    });

    it('should create executable deploy script', async () => {
      const configPath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      const config = await generator.loadPayerConfig(configPath);

      await generator.generateInfrastructure(config, testOutputDir);

      const deployScriptPath = path.join(testOutputDir, 'infrastructure', 'deploy.sh');
      const stats = fs.statSync(deployScriptPath);
      // Check if file is executable (Unix permissions)
      expect(stats.mode & fs.constants.S_IXUSR).toBeTruthy();
    });
  });

  describe('generateDocumentation', () => {
    it('should generate all documentation files', async () => {
      const configPath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      const config = await generator.loadPayerConfig(configPath);

      await generator.generateDocumentation(config, testOutputDir);

      const docsDir = path.join(testOutputDir, 'docs');
      expect(fs.existsSync(docsDir)).toBe(true);
      expect(fs.existsSync(path.join(docsDir, 'DEPLOYMENT.md'))).toBe(true);
      expect(fs.existsSync(path.join(docsDir, 'CONFIGURATION.md'))).toBe(true);
      expect(fs.existsSync(path.join(docsDir, 'TESTING.md'))).toBe(true);
      expect(fs.existsSync(path.join(testOutputDir, 'README.md'))).toBe(true);
    });

    it('should include payer-specific information in docs', async () => {
      const configPath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      const config = await generator.loadPayerConfig(configPath);

      await generator.generateDocumentation(config, testOutputDir);

      const readmePath = path.join(testOutputDir, 'README.md');
      const content = fs.readFileSync(readmePath, 'utf-8');

      expect(content).toContain('MCO001');
      expect(content).toContain('State Medicaid MCO');
      expect(content).toContain('john.smith@mco001.com');
    });
  });

  describe('generateSchemas', () => {
    it('should generate schemas for enabled modules', async () => {
      const configPath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      const config = await generator.loadPayerConfig(configPath);

      await generator.generateSchemas(config, testOutputDir);

      const schemasDir = path.join(testOutputDir, 'schemas');
      expect(fs.existsSync(schemasDir)).toBe(true);

      // Appeals module is enabled, should have appeal schemas
      if (config.enabledModules.appeals) {
        expect(fs.existsSync(path.join(schemasDir, 'Appeal-Request.json'))).toBe(true);
        expect(fs.existsSync(path.join(schemasDir, 'Appeal-SubStatus.json'))).toBe(true);
      }
    });

    it('should generate valid JSON schemas', async () => {
      const configPath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      const config = await generator.loadPayerConfig(configPath);

      await generator.generateSchemas(config, testOutputDir);

      const schemasDir = path.join(testOutputDir, 'schemas');
      const schemas = fs.readdirSync(schemasDir);

      schemas.forEach(schemaFile => {
        const schemaPath = path.join(schemasDir, schemaFile);
        const content = fs.readFileSync(schemaPath, 'utf-8');
        const schema = JSON.parse(content);

        expect(schema).toHaveProperty('$schema');
        expect(schema).toHaveProperty('title');
        expect(schema).toHaveProperty('type');
      });
    });
  });

  describe('packageDeployment', () => {
    it('should copy configuration to output', async () => {
      const configPath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      const config = await generator.loadPayerConfig(configPath);

      await generator.packageDeployment(config, testOutputDir);

      const configCopyPath = path.join(testOutputDir, 'config', 'payer-config.json');
      expect(fs.existsSync(configCopyPath)).toBe(true);

      const copiedConfig: PayerConfig = JSON.parse(fs.readFileSync(configCopyPath, 'utf-8'));
      expect(copiedConfig.payerId).toBe(config.payerId);
    });
  });

  describe('Full Integration', () => {
    it('should generate complete deployment package', async () => {
      const configPath = path.join(__dirname, '../../core/examples/medicaid-mco-config.json');
      const config = await generator.loadPayerConfig(configPath);

      await generator.generateWorkflows(config, testOutputDir);
      await generator.generateInfrastructure(config, testOutputDir);
      await generator.generateDocumentation(config, testOutputDir);
      await generator.generateSchemas(config, testOutputDir);
      await generator.packageDeployment(config, testOutputDir);

      // Verify all major directories exist
      expect(fs.existsSync(path.join(testOutputDir, 'workflows'))).toBe(true);
      expect(fs.existsSync(path.join(testOutputDir, 'infrastructure'))).toBe(true);
      expect(fs.existsSync(path.join(testOutputDir, 'docs'))).toBe(true);
      expect(fs.existsSync(path.join(testOutputDir, 'schemas'))).toBe(true);
      expect(fs.existsSync(path.join(testOutputDir, 'config'))).toBe(true);
      expect(fs.existsSync(path.join(testOutputDir, 'README.md'))).toBe(true);
    });
  });
});
