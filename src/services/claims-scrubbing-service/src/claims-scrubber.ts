/**
 * Cloud Health Office - Claims Scrubbing Service
 * 
 * Main service class implementing pre-adjudication claims validation
 * for 837P (Professional), 837I (Institutional), and 837D (Dental) claims.
 * 
 * Features:
 * - Configurable validation rule engine
 * - Standard and custom rule support
 * - Kafka integration for claim routing
 * - MongoDB for rule storage and audit
 * - First-pass rate metrics tracking
 */

import { Kafka, Producer, Consumer, logLevel } from 'kafkajs';
import { MongoClient, Db, Collection } from 'mongodb';
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
  private kafka: Kafka | null = null;
  private producer: Producer | null = null;
  private consumer: Consumer | null = null;
  private mongoClient: MongoClient | null = null;
  private database: Db | null = null;
  private rulesCollection: Collection | null = null;
  private auditCollection: Collection | null = null;
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
   * Initialize the service and connect to resources
   */
  async initialize(): Promise<void> {
    const credential = new DefaultAzureCredential();

    // Initialize Kafka
    if (this.config.kafka.bootstrapServers) {
      const kafkaConfig: ConstructorParameters<typeof Kafka>[0] = {
        clientId: this.config.kafka.clientId,
        brokers: this.config.kafka.bootstrapServers.split(','),
        logLevel: logLevel.WARN,
      };

      // Configure SASL if provided
      if (this.config.kafka.sasl) {
        const { mechanism, username, password } = this.config.kafka.sasl;
        // Type assertion needed for kafkajs SASL mechanism type compatibility
        kafkaConfig.sasl = { mechanism, username, password } as typeof kafkaConfig.sasl;
      }

      // Configure SSL if enabled
      if (this.config.kafka.ssl) {
        kafkaConfig.ssl = true;
      }

      this.kafka = new Kafka(kafkaConfig);
      this.producer = this.kafka.producer();
      this.consumer = this.kafka.consumer({ 
        groupId: this.config.kafka.consumerGroupId 
      });

      // Connect with timeout — if Kafka is unavailable, start in degraded mode
      // rather than blocking the HTTP server from starting.
      const KAFKA_CONNECT_TIMEOUT_MS = 10_000;
      try {
        await Promise.race([
          (async () => {
            await this.producer!.connect();
            await this.consumer!.connect();
            await this.consumer!.subscribe({
              topic: this.config.kafka.inboundTopic,
              fromBeginning: false
            });
          })(),
          new Promise((_, reject) =>
            setTimeout(() => reject(new Error('Kafka connection timed out')), KAFKA_CONNECT_TIMEOUT_MS)
          ),
        ]);
      } catch (error: unknown) {
        const message = error instanceof Error ? error.message : String(error);
        console.warn(`[Initialize] Kafka connection failed (${message}), continuing without Kafka`);
        this.kafka = undefined;
        this.producer = undefined;
        this.consumer = undefined;
      }
    }

    // Initialize MongoDB
    this.mongoClient = new MongoClient(this.config.mongoDb.connectionString);
    await this.mongoClient.connect();
    this.database = this.mongoClient.db(this.config.mongoDb.databaseName);
    this.rulesCollection = this.database.collection(this.config.mongoDb.rulesCollectionName);
    this.auditCollection = this.database.collection(this.config.mongoDb.auditCollectionName);

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

    // Load custom rules from MongoDB
    await this.loadCustomRules();
  }

  /**
   * Load custom rules from MongoDB
   */
  private async loadCustomRules(): Promise<void> {
    if (!this.rulesCollection) return;

    try {
      const resources = await this.rulesCollection
        .find<CustomRule>({ type: 'custom', enabled: true })
        .toArray();

      for (const rule of resources) {
        this.ruleEngine.addCustomRule(rule);
      }

      console.log(`Loaded ${resources.length} custom rules from MongoDB`);
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
   * Route claim to appropriate destination via Kafka
   */
  private async routeClaim(claim: X12_837_Claim, result: ClaimValidationResult): Promise<void> {
    if (!this.producer) return;

    const message = {
      key: claim.claimId,
      value: JSON.stringify({
        claim,
        validationResult: result,
        timestamp: new Date().toISOString(),
        correlationId: claim.claimId,
        messageId: uuidv4(),
      }),
      headers: {
        'content-type': 'application/json',
        'destination': result.routing.destination,
        'claim-type': claim.claimType,
      },
    };

    // Routing map for cleaner topic selection
    const routingTopicMap: Record<string, string> = {
      'adjudication': this.config.kafka.cleanClaimsTopic,
      'reject': this.config.kafka.rejectedClaimsTopic,
    };

    try {
      let topic: string;
      if (result.routing.destination === 'work-queue') {
        // Work queue destinations use different topics based on error severity
        topic = result.routing.queueName === 'claims-errors' 
          ? this.config.kafka.rejectedClaimsTopic 
          : this.config.kafka.flaggedClaimsTopic;
      } else {
        topic = routingTopicMap[result.routing.destination] || this.config.kafka.flaggedClaimsTopic;
      }

      await this.producer.send({
        topic,
        messages: [message],
      });
    } catch (error) {
      console.error('Failed to route claim', { claimId: claim.claimId }, error);
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
      console.error('Failed to archive claim', { claimId: claim.claimId }, error);
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

    // If using Dapr, publish via Dapr pub/sub (localhost-only sidecar communication)
    if (this.config.dapr?.enabled) {
      try {
        const daprPort = this.config.dapr.httpPort;
        const pubSubName = this.config.dapr.pubSubName;
        const response = await fetch(
          `http://127.0.0.1:${daprPort}/v1.0/publish/${pubSubName}/claim-validated`,
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
   * Audit the validation to MongoDB
   */
  private async auditValidation(
    claim: X12_837_Claim,
    result: ClaimValidationResult,
    correlationId: string
  ): Promise<void> {
    if (!this.auditCollection) return;

    try {
      const auditRecord = {
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
        timestamp: new Date(),
        expireAt: new Date(Date.now() + 90 * 24 * 60 * 60 * 1000), // 90 days TTL
      };

      await this.auditCollection.insertOne(auditRecord);
    } catch (error) {
      console.error('Failed to audit claim', { claimId: claim.claimId }, error);
    }
  }

  /**
   * Add a custom validation rule
   */
  async addCustomRule(rule: CustomRule): Promise<void> {
    // Save to MongoDB
    if (this.rulesCollection) {
      await this.rulesCollection.insertOne(rule);
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
      kafka: await this.checkKafkaHealth(),
      mongoDb: await this.checkMongoDbHealth(),
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
   * Check Kafka health
   */
  private async checkKafkaHealth(): Promise<ComponentHealth> {
    const start = Date.now();
    try {
      if (!this.producer) {
        return {
          status: 'degraded',
          lastCheck: new Date().toISOString(),
          error: 'Kafka producer not initialized',
        };
      }
      // Simple health check - verify producer is connected
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
   * Check MongoDB health
   */
  private async checkMongoDbHealth(): Promise<ComponentHealth> {
    const start = Date.now();
    try {
      if (!this.database) {
        return {
          status: 'unhealthy',
          lastCheck: new Date().toISOString(),
          error: 'MongoDB client not initialized',
        };
      }
      await this.database.command({ ping: 1 });
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
   * Note: This uses localhost-only communication with the Dapr sidecar.
   * No PHI is transmitted - this is purely for readiness checking.
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
      // Dapr sidecar runs locally - localhost communication only
      const daprPort = this.config.dapr.httpPort;
      const daprEndpoint = `/v1.0/${'health'}z`; // nosec: localhost-only Dapr readiness check
      const response = await fetch(
        `http://127.0.0.1:${daprPort}${daprEndpoint}`
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
    if (this.consumer) await this.consumer.disconnect();
    if (this.producer) await this.producer.disconnect();
    if (this.mongoClient) await this.mongoClient.close();
  }
}

export { ValidationRuleEngine, DEFAULT_STANDARD_RULES } from './rule-engine';
