/**
 * Cloud Health Office - Claims Scrubbing Service
 * 
 * Main service class implementing pre-adjudication claims validation
 * for 837P (Professional), 837I (Institutional), and 837D (Dental) claims.
 * 
 * Features:
 * - Configurable validation rule engine
 * - Standard and custom rule support
 * - Service Bus integration for claim routing
 * - Cosmos DB for rule storage and audit
 * - First-pass rate metrics tracking
 */

import { ServiceBusClient, ServiceBusSender, ServiceBusReceiver } from '@azure/service-bus';
import { CosmosClient, Container, Database } from '@azure/cosmos';
import { BlobServiceClient, ContainerClient } from '@azure/storage-blob';
import { DefaultAzureCredential } from '@azure/identity';
import { v4 as uuidv4 } from 'uuid';

import {
  X12_837_Claim,
  ValidationRule,
  ClaimValidationResult,
  ClaimsScrubberConfig,
  ClaimValidatedEvent,
  HealthStatus,
  ComponentHealth,
  ValidateClaimRequest,
  ValidateClaimResponse,
  BatchValidateRequest,
  BatchValidateResponse,
  CustomRule,
} from './types';
import { ValidationRuleEngine, DEFAULT_STANDARD_RULES } from './rule-engine';

/**
 * Claims Scrubbing Service
 */
export class ClaimsScrubberService {
  private config: ClaimsScrubberConfig;
  private ruleEngine: ValidationRuleEngine;
  private serviceBusClient: ServiceBusClient | null = null;
  private cleanClaimsSender: ServiceBusSender | null = null;
  private flaggedClaimsSender: ServiceBusSender | null = null;
  private rejectedClaimsSender: ServiceBusSender | null = null;
  private inboundReceiver: ServiceBusReceiver | null = null;
  private cosmosClient: CosmosClient | null = null;
  private database: Database | null = null;
  private rulesContainer: Container | null = null;
  private auditContainer: Container | null = null;
  private blobServiceClient: BlobServiceClient | null = null;
  private archiveContainer: ContainerClient | null = null;
  private startTime: number;
  private metrics = {
    claimsProcessed: 0,
    claimsClean: 0,
    claimsFlagged: 0,
    claimsRejected: 0,
    totalValidationTimeMs: 0,
  };

  constructor(config: ClaimsScrubberConfig) {
    this.config = config;
    this.startTime = Date.now();
    this.ruleEngine = new ValidationRuleEngine(DEFAULT_STANDARD_RULES);
  }

  /**
   * Initialize the service and connect to Azure resources
   */
  async initialize(): Promise<void> {
    const credential = new DefaultAzureCredential();

    // Initialize Service Bus
    if (this.config.serviceBus.connectionString) {
      this.serviceBusClient = new ServiceBusClient(this.config.serviceBus.connectionString);
    } else if (this.config.serviceBus.namespace) {
      this.serviceBusClient = new ServiceBusClient(
        `${this.config.serviceBus.namespace}.servicebus.windows.net`,
        credential
      );
    }

    if (this.serviceBusClient) {
      this.cleanClaimsSender = this.serviceBusClient.createSender(this.config.serviceBus.cleanClaimsTopic);
      this.flaggedClaimsSender = this.serviceBusClient.createSender(this.config.serviceBus.flaggedClaimsTopic);
      this.rejectedClaimsSender = this.serviceBusClient.createSender(this.config.serviceBus.rejectedClaimsTopic);
      this.inboundReceiver = this.serviceBusClient.createReceiver(
        this.config.serviceBus.inboundTopic,
        this.config.serviceBus.subscriptionName
      );
    }

    // Initialize Cosmos DB
    this.cosmosClient = new CosmosClient({
      endpoint: this.config.cosmosDb.endpoint,
      aadCredentials: credential,
    });
    this.database = this.cosmosClient.database(this.config.cosmosDb.databaseName);
    this.rulesContainer = this.database.container(this.config.cosmosDb.rulesContainerName);
    this.auditContainer = this.database.container(this.config.cosmosDb.auditContainerName);

    // Initialize Blob Storage
    if (this.config.storage.connectionString) {
      this.blobServiceClient = BlobServiceClient.fromConnectionString(this.config.storage.connectionString);
    } else if (this.config.storage.accountName) {
      this.blobServiceClient = new BlobServiceClient(
        `https://${this.config.storage.accountName}.blob.core.windows.net`,
        credential
      );
    }

    if (this.blobServiceClient) {
      this.archiveContainer = this.blobServiceClient.getContainerClient(this.config.storage.containerName);
    }

    // Load custom rules from Cosmos DB
    await this.loadCustomRules();
  }

  /**
   * Load custom rules from Cosmos DB
   */
  private async loadCustomRules(): Promise<void> {
    if (!this.rulesContainer) return;

    try {
      const query = {
        query: 'SELECT * FROM c WHERE c.type = @type AND c.enabled = true',
        parameters: [{ name: '@type', value: 'custom' }],
      };

      const { resources } = await this.rulesContainer.items.query<CustomRule>(query).fetchAll();

      for (const rule of resources) {
        this.ruleEngine.addCustomRule(rule);
      }

      console.log(`Loaded ${resources.length} custom rules from Cosmos DB`);
    } catch (error) {
      console.error('Failed to load custom rules:', error);
    }
  }

  /**
   * Validate a single claim
   */
  async validateClaim(request: ValidateClaimRequest): Promise<ValidateClaimResponse> {
    const correlationId = request.correlationId || uuidv4();
    
    const result = await this.ruleEngine.validateClaim(request.claim, {
      skipRules: request.skipRules,
      onlyRules: request.onlyRules,
      parallelExecution: this.config.ruleEngine.parallelExecution,
    });

    // Update metrics
    this.updateMetrics(result);

    // Archive the claim and result
    await this.archiveClaimResult(request.claim, result);

    // Route the claim based on result
    await this.routeClaim(request.claim, result);

    // Publish event
    await this.publishClaimValidatedEvent(request.claim, result);

    // Audit the validation
    await this.auditValidation(request.claim, result, correlationId);

    return {
      result,
      correctedClaim: request.autoCorrect ? undefined : undefined, // Future: auto-correction
      correlationId,
      timestamp: new Date().toISOString(),
    };
  }

  /**
   * Validate a batch of claims
   */
  async validateBatch(request: BatchValidateRequest): Promise<BatchValidateResponse> {
    const startTime = Date.now();
    const correlationId = request.correlationId || uuidv4();
    const results: ClaimValidationResult[] = [];

    for (const claim of request.claims) {
      const result = await this.ruleEngine.validateClaim(claim, {
        skipRules: request.skipRules,
        parallelExecution: this.config.ruleEngine.parallelExecution,
      });
      results.push(result);
      this.updateMetrics(result);
    }

    const cleanClaims = results.filter(r => r.status === 'clean').length;
    const flaggedClaims = results.filter(r => r.status === 'flagged').length;
    const rejectedClaims = results.filter(r => r.status === 'rejected').length;
    const firstPassRate = (cleanClaims / results.length) * 100;

    return {
      totalClaims: results.length,
      cleanClaims,
      flaggedClaims,
      rejectedClaims,
      results,
      firstPassRate,
      totalProcessingTimeMs: Date.now() - startTime,
      correlationId,
    };
  }

  /**
   * Update internal metrics
   */
  private updateMetrics(result: ClaimValidationResult): void {
    this.metrics.claimsProcessed++;
    this.metrics.totalValidationTimeMs += result.totalValidationTimeMs;

    switch (result.status) {
      case 'clean':
        this.metrics.claimsClean++;
        break;
      case 'flagged':
        this.metrics.claimsFlagged++;
        break;
      case 'rejected':
        this.metrics.claimsRejected++;
        break;
    }
  }

  /**
   * Route claim to appropriate destination
   */
  private async routeClaim(claim: X12_837_Claim, result: ClaimValidationResult): Promise<void> {
    const message = {
      body: {
        claim,
        validationResult: result,
        timestamp: new Date().toISOString(),
      },
      contentType: 'application/json',
      correlationId: claim.claimId,
      messageId: uuidv4(),
      subject: result.routing.destination,
    };

    try {
      switch (result.routing.destination) {
        case 'adjudication':
          if (this.cleanClaimsSender) {
            await this.cleanClaimsSender.sendMessages(message);
          }
          break;
        case 'work-queue':
          if (result.routing.queueName === 'claims-errors' && this.rejectedClaimsSender) {
            await this.rejectedClaimsSender.sendMessages(message);
          } else if (this.flaggedClaimsSender) {
            await this.flaggedClaimsSender.sendMessages(message);
          }
          break;
        case 'reject':
          if (this.rejectedClaimsSender) {
            await this.rejectedClaimsSender.sendMessages(message);
          }
          break;
      }
    } catch (error) {
      console.error(`Failed to route claim ${claim.claimId}:`, error);
    }
  }

  /**
   * Archive claim and validation result to blob storage
   */
  private async archiveClaimResult(claim: X12_837_Claim, result: ClaimValidationResult): Promise<void> {
    if (!this.archiveContainer) return;

    try {
      const date = new Date();
      const path = this.config.storage.archivePathPattern
        .replace('{yyyy}', date.getFullYear().toString())
        .replace('{MM}', (date.getMonth() + 1).toString().padStart(2, '0'))
        .replace('{dd}', date.getDate().toString().padStart(2, '0'))
        .replace('{claimType}', claim.claimType)
        .replace('{status}', result.status);

      const blobName = `${path}/${claim.claimId}.json`;
      const blockBlobClient = this.archiveContainer.getBlockBlobClient(blobName);

      const content = JSON.stringify({
        claim,
        validationResult: result,
        archivedAt: new Date().toISOString(),
      });

      await blockBlobClient.upload(content, content.length, {
        blobHTTPHeaders: { blobContentType: 'application/json' },
      });
    } catch (error) {
      console.error(`Failed to archive claim ${claim.claimId}:`, error);
    }
  }

  /**
   * Publish ClaimValidated event
   */
  private async publishClaimValidatedEvent(claim: X12_837_Claim, result: ClaimValidationResult): Promise<void> {
    const event: ClaimValidatedEvent = {
      id: uuidv4(),
      eventType: 'ClaimValidated',
      subject: claim.claimId,
      eventTime: new Date().toISOString(),
      dataVersion: '1.0',
      data: {
        claimId: claim.claimId,
        claimType: claim.claimType,
        patientControlNumber: claim.claimHeader.patientControlNumber,
        status: result.status,
        errorCount: result.errorCount,
        warningCount: result.warningCount,
        routingDestination: result.routing.destination,
        totalClaimedAmount: claim.totalClaimedAmount,
        billingProviderNpi: claim.billingProvider.npi,
        memberId: claim.subscriber.memberId,
        validationTimeMs: result.totalValidationTimeMs,
        firstPassEligible: result.firstPassEligible,
        editCodes: result.results
          .filter(r => !r.passed && r.editCode)
          .map(r => r.editCode as string),
      },
    };

    // If using Dapr, publish via Dapr pub/sub
    if (this.config.dapr?.enabled) {
      try {
        const response = await fetch(
          `http://localhost:${this.config.dapr.httpPort}/v1.0/publish/${this.config.dapr.pubSubName}/claim-validated`,
          {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(event),
          }
        );
        if (!response.ok) {
          console.error('Failed to publish event via Dapr:', response.statusText);
        }
      } catch (error) {
        console.error('Failed to publish event via Dapr:', error);
      }
    }
  }

  /**
   * Audit the validation to Cosmos DB
   */
  private async auditValidation(
    claim: X12_837_Claim,
    result: ClaimValidationResult,
    correlationId: string
  ): Promise<void> {
    if (!this.auditContainer) return;

    try {
      const auditRecord = {
        id: uuidv4(),
        claimId: claim.claimId,
        claimType: claim.claimType,
        patientControlNumber: claim.claimHeader.patientControlNumber,
        billingProviderNpi: claim.billingProvider.npi,
        memberId: claim.subscriber.memberId,
        validationStatus: result.status,
        errorCount: result.errorCount,
        warningCount: result.warningCount,
        rulesExecuted: result.rulesExecuted,
        rulesPassed: result.rulesPassed,
        rulesFailed: result.rulesFailed,
        routingDestination: result.routing.destination,
        editCodes: result.results
          .filter(r => !r.passed && r.editCode)
          .map(r => r.editCode),
        validationTimeMs: result.totalValidationTimeMs,
        correlationId,
        timestamp: new Date().toISOString(),
        ttl: 90 * 24 * 60 * 60, // 90 days TTL
      };

      await this.auditContainer.items.create(auditRecord);
    } catch (error) {
      console.error(`Failed to audit claim ${claim.claimId}:`, error);
    }
  }

  /**
   * Add a custom validation rule
   */
  async addCustomRule(rule: CustomRule): Promise<void> {
    // Save to Cosmos DB
    if (this.rulesContainer) {
      await this.rulesContainer.items.create(rule);
    }
    
    // Add to in-memory rule engine
    this.ruleEngine.addCustomRule(rule);
  }

  /**
   * Get all validation rules
   */
  getRules(): ValidationRule[] {
    return this.ruleEngine.getRules();
  }

  /**
   * Get rules by category
   */
  getRulesByCategory(category: string): ValidationRule[] {
    return this.ruleEngine.getRulesByCategory(category);
  }

  /**
   * Get service health status
   */
  async getHealth(): Promise<HealthStatus> {
    const checks: HealthStatus['checks'] = {
      serviceBus: await this.checkServiceBusHealth(),
      cosmosDb: await this.checkCosmosDbHealth(),
      storage: await this.checkStorageHealth(),
      ruleEngine: this.checkRuleEngineHealth(),
    };

    if (this.config.dapr?.enabled) {
      checks.dapr = await this.checkDaprHealth();
    }

    const allHealthy = Object.values(checks).every(c => c.status === 'healthy');
    const anyUnhealthy = Object.values(checks).some(c => c.status === 'unhealthy');

    const avgValidationTime = this.metrics.claimsProcessed > 0
      ? this.metrics.totalValidationTimeMs / this.metrics.claimsProcessed
      : 0;

    const firstPassRate = this.metrics.claimsProcessed > 0
      ? (this.metrics.claimsClean / this.metrics.claimsProcessed) * 100
      : 100;

    return {
      status: allHealthy ? 'healthy' : anyUnhealthy ? 'unhealthy' : 'degraded',
      version: '1.0.0',
      uptime: Date.now() - this.startTime,
      timestamp: new Date().toISOString(),
      checks,
      metrics: {
        claimsProcessed: this.metrics.claimsProcessed,
        claimsClean: this.metrics.claimsClean,
        claimsFlagged: this.metrics.claimsFlagged,
        claimsRejected: this.metrics.claimsRejected,
        averageValidationTimeMs: avgValidationTime,
        firstPassRate,
      },
    };
  }

  /**
   * Check Service Bus health
   */
  private async checkServiceBusHealth(): Promise<ComponentHealth> {
    const start = Date.now();
    try {
      if (!this.serviceBusClient) {
        return {
          status: 'unhealthy',
          lastCheck: new Date().toISOString(),
          error: 'Service Bus client not initialized',
        };
      }
      // Simple health check - verify sender is available
      return {
        status: 'healthy',
        latencyMs: Date.now() - start,
        lastCheck: new Date().toISOString(),
      };
    } catch (error) {
      return {
        status: 'unhealthy',
        latencyMs: Date.now() - start,
        lastCheck: new Date().toISOString(),
        error: error instanceof Error ? error.message : 'Unknown error',
      };
    }
  }

  /**
   * Check Cosmos DB health
   */
  private async checkCosmosDbHealth(): Promise<ComponentHealth> {
    const start = Date.now();
    try {
      if (!this.database) {
        return {
          status: 'unhealthy',
          lastCheck: new Date().toISOString(),
          error: 'Cosmos DB client not initialized',
        };
      }
      await this.database.read();
      return {
        status: 'healthy',
        latencyMs: Date.now() - start,
        lastCheck: new Date().toISOString(),
      };
    } catch (error) {
      return {
        status: 'unhealthy',
        latencyMs: Date.now() - start,
        lastCheck: new Date().toISOString(),
        error: error instanceof Error ? error.message : 'Unknown error',
      };
    }
  }

  /**
   * Check Storage health
   */
  private async checkStorageHealth(): Promise<ComponentHealth> {
    const start = Date.now();
    try {
      if (!this.archiveContainer) {
        return {
          status: 'unhealthy',
          lastCheck: new Date().toISOString(),
          error: 'Storage client not initialized',
        };
      }
      await this.archiveContainer.exists();
      return {
        status: 'healthy',
        latencyMs: Date.now() - start,
        lastCheck: new Date().toISOString(),
      };
    } catch (error) {
      return {
        status: 'unhealthy',
        latencyMs: Date.now() - start,
        lastCheck: new Date().toISOString(),
        error: error instanceof Error ? error.message : 'Unknown error',
      };
    }
  }

  /**
   * Check Rule Engine health
   */
  private checkRuleEngineHealth(): ComponentHealth {
    const rules = this.ruleEngine.getRules();
    return {
      status: 'healthy',
      lastCheck: new Date().toISOString(),
      details: {
        totalRules: rules.length,
        enabledRules: rules.filter(r => r.enabled).length,
      },
    };
  }

  /**
   * Check Dapr health
   */
  private async checkDaprHealth(): Promise<ComponentHealth> {
    if (!this.config.dapr?.enabled) {
      return {
        status: 'healthy',
        lastCheck: new Date().toISOString(),
      };
    }

    const start = Date.now();
    try {
      const response = await fetch(
        `http://localhost:${this.config.dapr.httpPort}/v1.0/healthz`
      );
      return {
        status: response.ok ? 'healthy' : 'unhealthy',
        latencyMs: Date.now() - start,
        lastCheck: new Date().toISOString(),
      };
    } catch (error) {
      return {
        status: 'unhealthy',
        latencyMs: Date.now() - start,
        lastCheck: new Date().toISOString(),
        error: error instanceof Error ? error.message : 'Unknown error',
      };
    }
  }

  /**
   * Close connections and cleanup
   */
  async close(): Promise<void> {
    if (this.cleanClaimsSender) await this.cleanClaimsSender.close();
    if (this.flaggedClaimsSender) await this.flaggedClaimsSender.close();
    if (this.rejectedClaimsSender) await this.rejectedClaimsSender.close();
    if (this.inboundReceiver) await this.inboundReceiver.close();
    if (this.serviceBusClient) await this.serviceBusClient.close();
  }
}

export { ValidationRuleEngine, DEFAULT_STANDARD_RULES } from './rule-engine';
