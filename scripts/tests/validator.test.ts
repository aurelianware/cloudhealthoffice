/**
 * Tests for Config Validator
 */

import { ConfigValidator, DeploymentValidator } from '../../core/validation/config-validator';
import { PayerConfig } from '../../core/types/payer-config';

describe('ConfigValidator', () => {
  let validator: ConfigValidator;

  beforeEach(() => {
    validator = new ConfigValidator();
  });

  describe('validate', () => {
    it('should validate correct configuration', () => {
      const config: PayerConfig = {
        payerId: 'TEST001',
        payerName: 'Test Payer',
        organizationName: 'Test Organization',
        contactInfo: {
          primaryContact: 'John Doe',
          email: 'john@test.com',
          phone: '+1-555-0100'
        },
        enabledModules: {
          appeals: true,
          ecs: false,
          attachments: false,
          authorizations: false
        },
        appeals: {
          enabled: true,
          apiEndpoints: {
            test: 'https://test.example.com',
            prod: 'https://api.example.com'
          },
          authentication: {
            type: 'oauth',
            keyVaultSecretName: 'test-secret'
          },
          timeout: 60000,
          retryCount: 3,
          retryInterval: 5000,
          requestReasons: [],
          subStatuses: [],
          attachmentRules: {
            pattern: 'pre-appeal',
            maxFileSize: 10485760,
            allowedFormats: ['pdf'],
            maxAttachments: 5,
            requiredMetadata: []
          },
          modes: {
            realTimeWeb: true,
            realTimeB2B: false,
            ediBatch: false
          }
        },
        infrastructure: {
          resourceNamePrefix: 'test001',
          location: 'eastus',
          environment: 'dev',
          tags: {},
          storageConfig: {
            sku: 'Standard_LRS',
            containers: [],
            lifecycleRules: []
          },
          serviceBusConfig: {
            sku: 'Standard',
            topics: [],
            queues: []
          },
          logicAppConfig: {
            sku: 'WS1',
            workerCount: 1,
            alwaysOn: true
          },
          keyVaultConfig: {
            sku: 'standard',
            enableSoftDelete: true,
            softDeleteRetentionDays: 90
          }
        },
        monitoring: {
          applicationInsights: {
            enabled: true,
            samplingPercentage: 100
          },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const result = validator.validate(config);
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('should detect missing required fields', () => {
      const config: Partial<PayerConfig> = {
        payerId: 'TEST001',
        // Missing other required fields
      };

      const result = validator.validate(config as PayerConfig);
      expect(result.valid).toBe(false);
      expect(result.errors.length).toBeGreaterThan(0);
    });

    it('should detect invalid payerId format', () => {
      const config: any = {
        payerId: 'test-with-lowercase', // Should be uppercase only
        payerName: 'Test',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: false,
          ecs: false,
          attachments: false,
          authorizations: false
        }
      };

      const result = validator.validate(config);
      expect(result.valid).toBe(false);
    });

    it('should detect missing module configuration when module is enabled', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: true, // Enabled but no appeals config
          ecs: false,
          attachments: false,
          authorizations: false
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'dev',
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 100 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const result = validator.validate(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.field === 'appeals')).toBe(true);
    });

    it('should generate warnings for best practices', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: false,
          ecs: false,
          attachments: false,
          authorizations: false
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'prod', // Production
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: {
            sku: 'WS1',
            workerCount: 1, // Low worker count for prod
            alwaysOn: true
          },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: {
            enabled: true,
            samplingPercentage: 50 // Less than 100%
          },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const result = validator.validate(config);
      expect(result.warnings.length).toBeGreaterThan(0);
    });
  });
});

describe('ConfigValidator - Additional Coverage', () => {
  let validator: ConfigValidator;

  beforeEach(() => {
    validator = new ConfigValidator();
  });

  describe('ECS module validation', () => {
    it('should detect missing ECS configuration when ECS is enabled', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: false,
          ecs: true, // Enabled but no ecs config
          attachments: false,
          authorizations: false
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'dev',
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 100 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const result = validator.validate(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.field === 'ecs')).toBe(true);
    });
  });

  describe('Authorizations module validation', () => {
    it('should not have validation for authorizations configuration (not implemented)', () => {
      // Note: authorizations validation is not implemented yet in config-validator.ts
      // This test verifies current behavior
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: false,
          ecs: false,
          attachments: false,
          authorizations: true // Enabled but no authorizations config - no validation implemented
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'dev',
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 100 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      // Current behavior: authorizations module validation not implemented
      // No error is expected for missing authorizations config
      const result = validator.validate(config);
      expect(result.errors.some(e => e.field === 'authorizations')).toBe(false);
    });
  });

  describe('Attachments module validation', () => {
    it('should detect missing attachments configuration when attachments is enabled', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: false,
          ecs: false,
          attachments: true, // Enabled but no attachments config
          authorizations: false
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'dev',
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 100 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const result = validator.validate(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.field === 'attachments')).toBe(true);
    });
  });

  describe('API endpoint validation', () => {
    it('should validate appeals endpoints when module is enabled', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: true,
          ecs: false,
          attachments: false,
          authorizations: false
        },
        appeals: {
          enabled: true,
          // Missing apiEndpoints
          authentication: { type: 'oauth', keyVaultSecretName: 'test-secret' },
          timeout: 60000,
          retryCount: 3,
          retryInterval: 5000,
          requestReasons: [],
          subStatuses: [],
          attachmentRules: { pattern: 'pre-appeal', maxFileSize: 10485760, allowedFormats: ['pdf'], maxAttachments: 5, requiredMetadata: [] },
          modes: { realTimeWeb: true, realTimeB2B: false, ediBatch: false }
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'dev',
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 100 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const result = validator.validate(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.field === 'appeals.apiEndpoints')).toBe(true);
    });

    it('should validate ECS endpoints when module is enabled', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: false,
          ecs: true,
          attachments: false,
          authorizations: false
        },
        ecs: {
          enabled: true,
          // Missing apiEndpoints
          timeout: 60000,
          retryCount: 3,
          retryInterval: 5000
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'dev',
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 100 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const result = validator.validate(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.field === 'ecs.apiEndpoints')).toBe(true);
    });
  });

  describe('Resource naming validation', () => {
    it('should detect resource name prefix exceeding 20 characters', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: false,
          ecs: false,
          attachments: false,
          authorizations: false
        },
        infrastructure: {
          resourceNamePrefix: 'this-is-way-too-long-prefix-name', // > 20 chars
          location: 'eastus',
          environment: 'dev',
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 100 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const result = validator.validate(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.field === 'infrastructure.resourceNamePrefix')).toBe(true);
    });
  });

  describe('Environment validation', () => {
    it('should detect invalid environment value', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: false,
          ecs: false,
          attachments: false,
          authorizations: false
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'invalid-env', // Invalid environment
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 100 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const result = validator.validate(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.field === 'infrastructure.environment')).toBe(true);
    });
  });
});

describe('DeploymentValidator - Additional Coverage', () => {
  let validator: DeploymentValidator;

  beforeEach(() => {
    validator = new DeploymentValidator();
  });

  describe('validateRequiredModules', () => {
    it('should validate SFTP host when attachments module is enabled', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: false,
          ecs: false,
          attachments: true,
          authorizations: false
        },
        attachments: {
          sftpConfig: {
            host: '', // Empty host should cause error
            port: 22,
            username: 'user'
          }
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'dev',
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 100 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const result = validator.validateRequiredModules(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.field === 'attachments.sftpConfig.host')).toBe(true);
    });

    it('should pass when attachments SFTP host is configured', () => {
      const config: any = {
        payerId: 'TEST001',
        enabledModules: {
          appeals: false,
          ecs: false,
          attachments: true,
          authorizations: false
        },
        attachments: {
          sftpConfig: {
            host: 'sftp.example.com',
            port: 22,
            username: 'user'
          }
        }
      };

      const result = validator.validateRequiredModules(config);
      expect(result.valid).toBe(true);
    });

    it('should pass when attachments not enabled even without SFTP config', () => {
      const config: any = {
        payerId: 'TEST001',
        enabledModules: {
          appeals: false,
          ecs: false,
          attachments: false,
          authorizations: false
        }
      };

      const result = validator.validateRequiredModules(config);
      expect(result.valid).toBe(true);
    });
  });
});

describe('DeploymentValidator', () => {
  let validator: DeploymentValidator;

  beforeEach(() => {
    validator = new DeploymentValidator();
  });

  describe('validateForGeneration', () => {
    it('should require at least one enabled module', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: false,
          ecs: false,
          attachments: false,
          authorizations: false
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'dev',
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 100 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const result = validator.validateForGeneration(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.field === 'enabledModules')).toBe(true);
    });
  });

  describe('validateEndpoints', () => {
    it('should validate URL format', async () => {
      const config: any = {
        appeals: {
          apiEndpoints: {
            test: 'invalid-url',
            prod: 'https://valid.example.com'
          }
        }
      };

      const result = await validator.validateEndpoints(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.field === 'appeals.apiEndpoints.test')).toBe(true);
    });

    it('should accept valid URLs', async () => {
      const config: any = {
        appeals: {
          apiEndpoints: {
            test: 'https://test.example.com',
            prod: 'https://api.example.com'
          }
        }
      };

      const result = await validator.validateEndpoints(config);
      expect(result.valid).toBe(true);
    });
  });

  describe('generateValidationReport', () => {
    it('should generate comprehensive report', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test Payer',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: true,
          ecs: false,
          attachments: false,
          authorizations: false
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'dev',
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 100 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const report = validator.generateValidationReport(config);
      expect(report).toContain('TEST001');
      expect(report).toContain('Test Payer');
      expect(report).toContain('Enabled Modules');
    });

    it('should include error details in report', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test Payer',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: false, // No modules enabled - should cause error
          ecs: false,
          attachments: false,
          authorizations: false
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'dev',
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 100 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const report = validator.generateValidationReport(config);
      expect(report).toContain('ERRORS');
      expect(report).toContain('must be fixed');
    });

    it('should include warnings with suggestions in report', () => {
      const config: any = {
        payerId: 'TEST001',
        payerName: 'Test Payer',
        organizationName: 'Test Org',
        contactInfo: {
          primaryContact: 'Test',
          email: 'test@test.com',
          phone: '555-0100'
        },
        enabledModules: {
          appeals: true,
          ecs: false,
          attachments: false,
          authorizations: false
        },
        appeals: {
          enabled: true,
          apiEndpoints: {
            test: 'https://test.example.com',
            prod: 'https://api.example.com'
          },
          authentication: { type: 'oauth', keyVaultSecretName: 'test-secret' },
          timeout: 60000,
          retryCount: 3,
          retryInterval: 5000,
          requestReasons: [],
          subStatuses: [],
          attachmentRules: { pattern: 'pre-appeal', maxFileSize: 10485760, allowedFormats: ['pdf'], maxAttachments: 5, requiredMetadata: [] },
          modes: { realTimeWeb: true, realTimeB2B: false, ediBatch: false }
        },
        infrastructure: {
          resourceNamePrefix: 'test',
          location: 'eastus',
          environment: 'prod',
          tags: {},
          storageConfig: { sku: 'Standard_LRS', containers: [], lifecycleRules: [] },
          serviceBusConfig: { sku: 'Standard', topics: [], queues: [] },
          logicAppConfig: { sku: 'WS1', workerCount: 1, alwaysOn: true },
          keyVaultConfig: { sku: 'standard', enableSoftDelete: true, softDeleteRetentionDays: 90 }
        },
        monitoring: {
          applicationInsights: { enabled: true, samplingPercentage: 50 },
          alertRules: [],
          logRetentionDays: 90
        }
      };

      const report = validator.generateValidationReport(config);
      expect(report).toContain('WARNINGS');
      expect(report).toContain('Suggestion');
    });
  });

  describe('validateEndpoints - ECS', () => {
    it('should validate ECS endpoint URL format', async () => {
      const config: any = {
        ecs: {
          apiEndpoints: {
            test: 'invalid-url',
            prod: 'https://ecs.example.com'
          }
        }
      };

      const result = await validator.validateEndpoints(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.field === 'ecs.apiEndpoints.test')).toBe(true);
    });

    it('should validate ECS prod endpoint URL format', async () => {
      const config: any = {
        ecs: {
          apiEndpoints: {
            test: 'https://test.example.com',
            prod: 'not-a-url'
          }
        }
      };

      const result = await validator.validateEndpoints(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.field === 'ecs.apiEndpoints.prod')).toBe(true);
    });

    it('should accept valid ECS URLs', async () => {
      const config: any = {
        ecs: {
          apiEndpoints: {
            test: 'https://ecs-test.example.com',
            prod: 'https://ecs.example.com'
          }
        }
      };

      const result = await validator.validateEndpoints(config);
      expect(result.valid).toBe(true);
    });

    it('should pass when no endpoints configured', async () => {
      const config: any = {};

      const result = await validator.validateEndpoints(config);
      expect(result.valid).toBe(true);
    });
  });

  describe('validateConnectivity', () => {
    it('should return warning for connectivity validation', async () => {
      const config: any = {
        payerId: 'TEST001',
        appeals: {
          apiEndpoints: {
            test: 'https://test.example.com',
            prod: 'https://api.example.com'
          }
        }
      };

      const result = await validator.validateConnectivity(config);
      expect(result.valid).toBe(true);
      expect(result.warnings.length).toBeGreaterThan(0);
      expect(result.warnings.some(w => w.field === 'connectivity')).toBe(true);
    });
  });
});
