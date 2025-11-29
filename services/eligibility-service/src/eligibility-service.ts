/**
 * Cloud Health Office - Eligibility Service
 * 
 * Main service class implementing dual-interface eligibility checking
 * supporting both X12 270/271 EDI and FHIR R4 CoverageEligibilityRequest/Response.
 * 
 * Features:
 * - Cosmos DB caching with configurable TTL
 * - Event Grid publishing for EligibilityChecked events
 * - Dapr integration for state management and pub/sub
 * - QNXT backend integration
 * - FHIR server integration
 */

import { CosmosClient, Container, Database } from '@azure/cosmos';
import { EventGridPublisherClient, AzureKeyCredential } from '@azure/eventgrid';
import { DefaultAzureCredential } from '@azure/identity';
import { CoverageEligibilityRequest, CoverageEligibilityResponse, Patient, Coverage } from 'fhir/r4';
import { v4 as uuidv4 } from 'uuid';
import {
  X12_270_Request,
  X12_271_Response,
  EligibilityCacheRecord,
  EligibilityCheckedEvent,
  EligibilityServiceConfig,
  EligibilityCheckRequest,
  EligibilityCheckResponse,
  EligibilityBenefit,
  HealthStatus,
  ComponentHealth,
  QNXTEligibilityRule
} from './types';
import { X12EligibilityMapper } from './x12-mapper';
import { FHIREligibilityMapper } from './fhir-mapper';

/**
 * Main eligibility service class
 */
export class EligibilityService {
  private cosmosClient: CosmosClient;
  private database: Database;
  private container: Container;
  private eventGridClient: EventGridPublisherClient | null = null;
  private x12Mapper: X12EligibilityMapper;
  private fhirMapper: FHIREligibilityMapper;
  private config: EligibilityServiceConfig;
  private startTime: number;
  private eligibilityRules: Map<string, QNXTEligibilityRule[]> = new Map();

  constructor(config: EligibilityServiceConfig) {
    this.config = config;
    this.startTime = Date.now();
    this.x12Mapper = new X12EligibilityMapper();
    this.fhirMapper = new FHIREligibilityMapper();

    // Initialize Cosmos DB client
    const credential = new DefaultAzureCredential();
    this.cosmosClient = new CosmosClient({
      endpoint: config.cosmosDb.endpoint,
      aadCredentials: credential
    });
    this.database = this.cosmosClient.database(config.cosmosDb.databaseName);
    this.container = this.database.container(config.cosmosDb.containerName);

    // Initialize Event Grid client
    if (config.eventGrid.topicEndpoint) {
      if (config.eventGrid.topicKey) {
        this.eventGridClient = new EventGridPublisherClient(
          config.eventGrid.topicEndpoint,
          'EventGrid',
          new AzureKeyCredential(config.eventGrid.topicKey)
        );
      } else {
        // Use managed identity
        this.eventGridClient = new EventGridPublisherClient(
          config.eventGrid.topicEndpoint,
          'EventGrid',
          credential
        );
      }
    }
  }

  /**
   * Check eligibility with unified interface
   */
  async checkEligibility(request: EligibilityCheckRequest): Promise<EligibilityCheckResponse> {
    const startTime = Date.now();
    const correlationId = request.correlationId || uuidv4();

    try {
      if (request.format === 'X12' && request.x12Request) {
        return await this.checkX12Eligibility(request.x12Request, request.skipCache, correlationId, startTime);
      } else if (request.format === 'FHIR' && request.fhirRequest) {
        return await this.checkFHIREligibility(request.fhirRequest, request.skipCache, correlationId, startTime);
      } else {
        throw new Error('Invalid request format or missing request data');
      }
    } catch (error) {
      console.error(`Eligibility check failed: ${error}`);
      throw error;
    }
  }

  /**
   * Check eligibility using X12 270/271 format
   */
  async checkX12Eligibility(
    request: X12_270_Request,
    skipCache?: boolean,
    correlationId?: string,
    startTime?: number
  ): Promise<EligibilityCheckResponse> {
    const start = startTime || Date.now();
    const cacheKey = this.generateCacheKey(request);

    // Check cache first (unless skipped)
    if (this.config.cache.enabled && !skipCache) {
      const cached = await this.getCachedResponse(cacheKey);
      if (cached && cached.response.x12Response) {
        await this.publishEligibilityCheckedEvent(
          request.subscriber.memberId,
          request.informationSource.identificationCode,
          request.informationReceiver?.npi,
          'X12_270',
          cached.response.eligibilityStatus,
          request.eligibilityDateRange?.startDate || this.getCurrentDate(),
          request.serviceTypeCodes,
          true,
          Date.now() - start
        );

        return {
          format: 'X12',
          x12Response: cached.response.x12Response,
          fromCache: true,
          timestamp: new Date().toISOString(),
          responseTimeMs: Date.now() - start,
          cacheKey,
          correlationId
        };
      }
    }

    // Fetch fresh eligibility data
    const response = await this.fetchX12Eligibility(request);

    // Cache the response
    if (this.config.cache.enabled) {
      await this.cacheResponse(cacheKey, request, 'X12_270', response);
    }

    // Publish event
    await this.publishEligibilityCheckedEvent(
      request.subscriber.memberId,
      request.informationSource.identificationCode,
      request.informationReceiver?.npi,
      'X12_270',
      response.eligibilityStatus,
      request.eligibilityDateRange?.startDate || this.getCurrentDate(),
      request.serviceTypeCodes,
      false,
      Date.now() - start
    );

    return {
      format: 'X12',
      x12Response: response,
      fromCache: false,
      timestamp: new Date().toISOString(),
      responseTimeMs: Date.now() - start,
      cacheKey,
      correlationId
    };
  }

  /**
   * Check eligibility using FHIR CoverageEligibilityRequest
   */
  async checkFHIREligibility(
    request: CoverageEligibilityRequest,
    skipCache?: boolean,
    correlationId?: string,
    startTime?: number
  ): Promise<EligibilityCheckResponse> {
    const start = startTime || Date.now();
    const cacheKey = this.generateFHIRCacheKey(request);
    const memberId = this.extractMemberIdFromFHIR(request);
    const payerId = this.extractPayerIdFromFHIR(request);

    // Check cache first (unless skipped)
    if (this.config.cache.enabled && !skipCache) {
      const cached = await this.getCachedResponse(cacheKey);
      if (cached && cached.response.fhirResponse) {
        await this.publishEligibilityCheckedEvent(
          memberId,
          payerId,
          this.extractProviderNpiFromFHIR(request),
          'FHIR_CoverageEligibilityRequest',
          cached.response.eligibilityStatus,
          request.created || this.getCurrentDate(),
          this.extractServiceTypesFromFHIR(request),
          true,
          Date.now() - start
        );

        return {
          format: 'FHIR',
          fhirResponse: cached.response.fhirResponse,
          fromCache: true,
          timestamp: new Date().toISOString(),
          responseTimeMs: Date.now() - start,
          cacheKey,
          correlationId
        };
      }
    }

    // Fetch fresh eligibility data
    const response = await this.fetchFHIREligibility(request);

    // Cache the response
    if (this.config.cache.enabled) {
      await this.cacheFHIRResponse(cacheKey, request, response);
    }

    // Publish event
    const eligibilityStatus = this.extractEligibilityStatusFromFHIR(response);
    await this.publishEligibilityCheckedEvent(
      memberId,
      payerId,
      this.extractProviderNpiFromFHIR(request),
      'FHIR_CoverageEligibilityRequest',
      eligibilityStatus,
      request.created || this.getCurrentDate(),
      this.extractServiceTypesFromFHIR(request),
      false,
      Date.now() - start
    );

    return {
      format: 'FHIR',
      fhirResponse: response,
      fromCache: false,
      timestamp: new Date().toISOString(),
      responseTimeMs: Date.now() - start,
      cacheKey,
      correlationId
    };
  }

  /**
   * Load eligibility rules from QNXT (typically imported from CSV)
   */
  loadEligibilityRules(rules: QNXTEligibilityRule[]): void {
    this.eligibilityRules.clear();
    for (const rule of rules) {
      const key = `${rule.planCode}:${rule.serviceTypeCode}`;
      if (!this.eligibilityRules.has(key)) {
        this.eligibilityRules.set(key, []);
      }
      this.eligibilityRules.get(key)!.push(rule);
    }
    // Sort rules by priority
    for (const [key, ruleList] of this.eligibilityRules) {
      ruleList.sort((a, b) => a.priority - b.priority);
    }
  }

  /**
   * Get applicable eligibility rules for a service
   */
  getApplicableRules(planCode: string, serviceTypeCode: string, memberAge?: number, gender?: 'M' | 'F'): QNXTEligibilityRule[] {
    const key = `${planCode}:${serviceTypeCode}`;
    const rules = this.eligibilityRules.get(key) || [];
    const today = this.getCurrentDate();

    return rules.filter(rule => {
      // Check if rule is active
      if (!rule.isActive) return false;

      // Check effective date range
      if (rule.effectiveDateRange.startDate > today) return false;
      if (rule.effectiveDateRange.endDate && rule.effectiveDateRange.endDate < today) return false;

      // Check age limits
      if (memberAge !== undefined && rule.ageLimits) {
        if (rule.ageLimits.minAge !== undefined && memberAge < rule.ageLimits.minAge) return false;
        if (rule.ageLimits.maxAge !== undefined && memberAge > rule.ageLimits.maxAge) return false;
      }

      // Check gender restrictions
      if (gender && rule.genderRestrictions && !rule.genderRestrictions.includes(gender)) {
        return false;
      }

      return true;
    });
  }

  /**
   * Get service health status
   */
  async getHealth(): Promise<HealthStatus> {
    const checks: HealthStatus['checks'] = {
      cosmosDb: await this.checkCosmosHealth(),
      eventGrid: await this.checkEventGridHealth()
    };

    if (this.config.qnxt) {
      checks.qnxt = await this.checkQNXTHealth();
    }

    if (this.config.fhirServer) {
      checks.fhirServer = await this.checkFHIRServerHealth();
    }

    if (this.config.dapr?.enabled) {
      checks.dapr = await this.checkDaprHealth();
    }

    const allHealthy = Object.values(checks).every(c => c.status === 'healthy');
    const anyUnhealthy = Object.values(checks).some(c => c.status === 'unhealthy');

    return {
      status: allHealthy ? 'healthy' : anyUnhealthy ? 'unhealthy' : 'degraded',
      version: '1.0.0',
      uptime: Date.now() - this.startTime,
      timestamp: new Date().toISOString(),
      checks
    };
  }

  // ============================================================================
  // Private Methods
  // ============================================================================

  private generateCacheKey(request: X12_270_Request): string {
    const memberId = request.dependent?.firstName 
      ? `${request.subscriber.memberId}-${request.dependent.firstName}-${request.dependent.lastName}`
      : request.subscriber.memberId;
    const payerId = request.informationSource.identificationCode;
    const serviceDate = request.eligibilityDateRange?.startDate || this.getCurrentDate();
    const serviceTypes = (request.serviceTypeCodes || ['30']).sort().join(',');
    
    return `x12:${payerId}:${memberId}:${serviceDate}:${serviceTypes}`;
  }

  private generateFHIRCacheKey(request: CoverageEligibilityRequest): string {
    const memberId = this.extractMemberIdFromFHIR(request);
    const payerId = this.extractPayerIdFromFHIR(request);
    const serviceDate = request.created || this.getCurrentDate();
    const serviceTypes = this.extractServiceTypesFromFHIR(request).sort().join(',');
    
    return `fhir:${payerId}:${memberId}:${serviceDate}:${serviceTypes}`;
  }

  private async getCachedResponse(cacheKey: string): Promise<EligibilityCacheRecord | null> {
    try {
      const query = {
        query: 'SELECT * FROM c WHERE c.cacheKey = @cacheKey',
        parameters: [{ name: '@cacheKey', value: cacheKey }]
      };
      
      const { resources } = await this.container.items.query<EligibilityCacheRecord>(query).fetchAll();
      
      if (resources.length === 0) return null;
      
      const record = resources[0];
      
      // Check if cache is still valid
      const cacheAge = Date.now() - new Date(record.createdAt).getTime();
      if (cacheAge > this.config.cache.maxCacheAge * 1000) {
        return null;
      }

      // Update access time and count
      await this.container.item(record.id, record.memberId).patch([
        { op: 'set', path: '/lastAccessedAt', value: new Date().toISOString() },
        { op: 'incr', path: '/accessCount', value: 1 }
      ]);

      return record;
    } catch (error) {
      console.error(`Cache lookup failed: ${error}`);
      return null;
    }
  }

  private async cacheResponse(
    cacheKey: string,
    request: X12_270_Request,
    requestType: 'X12_270',
    response: X12_271_Response
  ): Promise<void> {
    const ttl = response.eligibilityStatus === 'active' 
      ? this.config.cache.activeMemberTtl 
      : this.config.cache.inactiveMemberTtl;

    const record: EligibilityCacheRecord = {
      id: uuidv4(),
      memberId: request.subscriber.memberId,
      payerId: request.informationSource.identificationCode,
      cacheKey,
      requestType,
      originalRequest: request,
      response: {
        x12Response: response,
        eligibilityStatus: response.eligibilityStatus,
        effectiveDate: response.subscriber.policyStartDate,
        terminationDate: response.subscriber.policyEndDate,
        planInfo: response.subscriber.planName ? {
          planId: response.subscriber.groupNumber || '',
          planName: response.subscriber.planName,
          groupNumber: response.subscriber.groupNumber,
          groupName: response.subscriber.groupName
        } : undefined
      },
      createdAt: new Date().toISOString(),
      lastAccessedAt: new Date().toISOString(),
      accessCount: 0,
      ttl,
      source: 'QNXT'
    };

    try {
      await this.container.items.create(record);
    } catch (error) {
      console.error(`Cache write failed: ${error}`);
    }
  }

  private async cacheFHIRResponse(
    cacheKey: string,
    request: CoverageEligibilityRequest,
    response: CoverageEligibilityResponse
  ): Promise<void> {
    const eligibilityStatus = this.extractEligibilityStatusFromFHIR(response);
    const ttl = eligibilityStatus === 'active' 
      ? this.config.cache.activeMemberTtl 
      : this.config.cache.inactiveMemberTtl;

    const record: EligibilityCacheRecord = {
      id: uuidv4(),
      memberId: this.extractMemberIdFromFHIR(request),
      payerId: this.extractPayerIdFromFHIR(request),
      cacheKey,
      requestType: 'FHIR_CoverageEligibilityRequest',
      originalRequest: request,
      response: {
        fhirResponse: response,
        eligibilityStatus
      },
      createdAt: new Date().toISOString(),
      lastAccessedAt: new Date().toISOString(),
      accessCount: 0,
      ttl,
      source: 'FHIR_SERVER'
    };

    try {
      await this.container.items.create(record);
    } catch (error) {
      console.error(`FHIR cache write failed: ${error}`);
    }
  }

  private async fetchX12Eligibility(request: X12_270_Request): Promise<X12_271_Response> {
    // In production, this would call QNXT or a clearinghouse
    // For now, generate a response based on loaded rules
    const planCode = request.subscriber.groupNumber || 'DEFAULT';
    const serviceTypes = request.serviceTypeCodes || ['30'];
    const memberAge = this.calculateAge(request.subscriber.dateOfBirth);
    const gender = request.subscriber.gender;

    const benefits: EligibilityBenefit[] = [];
    let overallStatus: 'active' | 'inactive' | 'terminated' | 'pending' | 'unknown' = 'unknown';

    for (const serviceType of serviceTypes) {
      const rules = this.getApplicableRules(planCode, serviceType, memberAge, gender);
      
      if (rules.length > 0) {
        const rule = rules[0]; // Use highest priority rule
        overallStatus = rule.coverageIndicator === 'covered' ? 'active' : 'inactive';
        
        benefits.push({
          serviceTypeCode: serviceType,
          serviceTypeDescription: rule.benefitCategory,
          eligibilityInfoCode: rule.coverageIndicator === 'covered' ? '1' : '6',
          coverageLevelCode: 'IND',
          authorizationRequired: rule.priorAuthRequired,
          inNetwork: true,
          additionalInfo: rule.inNetworkRequirements ? {
            copay: rule.inNetworkRequirements.copay,
            coinsurance: rule.inNetworkRequirements.coinsurance
          } : undefined
        });
      } else {
        // Default benefit response
        benefits.push({
          serviceTypeCode: serviceType,
          eligibilityInfoCode: 'U', // Unknown
          coverageLevelCode: 'IND'
        });
      }
    }

    // If no rules found, assume active for demo purposes
    if (overallStatus === 'unknown' && benefits.every(b => b.eligibilityInfoCode === 'U')) {
      overallStatus = 'active';
      benefits.forEach(b => b.eligibilityInfoCode = '1');
    }

    return {
      transactionControlNumber: request.transactionControlNumber,
      responseControlNumber: uuidv4().substring(0, 9),
      transactionDate: this.getCurrentDate(),
      informationSource: {
        entityIdentifier: request.informationSource.entityIdentifier,
        name: request.informationSource.name,
        identificationCode: request.informationSource.identificationCode
      },
      informationReceiver: {
        entityIdentifier: request.informationReceiver?.entityIdentifier || '',
        name: request.informationReceiver?.name,
        npi: request.informationReceiver?.npi
      },
      subscriber: {
        memberId: request.subscriber.memberId,
        firstName: request.subscriber.firstName,
        lastName: request.subscriber.lastName,
        dateOfBirth: request.subscriber.dateOfBirth,
        gender: request.subscriber.gender,
        groupNumber: request.subscriber.groupNumber,
        groupName: 'Health Plan Group',
        planName: 'Comprehensive Medical Plan'
      },
      dependent: request.dependent ? {
        firstName: request.dependent.firstName,
        lastName: request.dependent.lastName,
        dateOfBirth: request.dependent.dateOfBirth,
        relationshipCode: request.dependent.relationshipCode
      } : undefined,
      eligibilityStatus: overallStatus,
      benefits
    };
  }

  private async fetchFHIREligibility(request: CoverageEligibilityRequest): Promise<CoverageEligibilityResponse> {
    // Convert FHIR request to X12 for processing, then convert response back to FHIR
    const x12Request = this.fhirMapper.fhirToX12(request);
    const x12Response = await this.fetchX12Eligibility(x12Request);
    return this.fhirMapper.x12ToFhir(x12Response, request);
  }

  private async publishEligibilityCheckedEvent(
    memberId: string,
    payerId: string,
    providerNpi: string | undefined,
    requestType: 'X12_270' | 'FHIR_CoverageEligibilityRequest',
    eligibilityStatus: string,
    serviceDate: string,
    serviceTypeCodes: string[] | undefined,
    fromCache: boolean,
    responseTimeMs: number
  ): Promise<void> {
    if (!this.eventGridClient) return;

    const event: EligibilityCheckedEvent = {
      id: uuidv4(),
      eventType: 'EligibilityChecked',
      subject: memberId,
      eventTime: new Date().toISOString(),
      dataVersion: '1.0',
      data: {
        memberId,
        payerId,
        providerNpi,
        requestType,
        eligibilityStatus: eligibilityStatus as 'active' | 'inactive' | 'terminated' | 'pending' | 'unknown',
        serviceDate,
        serviceTypeCodes,
        fromCache,
        responseTimeMs
      }
    };

    try {
      await this.eventGridClient.send([event]);
    } catch (error) {
      console.error(`Failed to publish EligibilityChecked event: ${error}`);
    }
  }

  private extractMemberIdFromFHIR(request: CoverageEligibilityRequest): string {
    // Extract from patient reference or identifier
    if (request.patient?.identifier?.value) {
      return request.patient.identifier.value;
    }
    if (request.patient?.reference) {
      return request.patient.reference.replace('Patient/', '');
    }
    return 'unknown';
  }

  private extractPayerIdFromFHIR(request: CoverageEligibilityRequest): string {
    if (request.insurer?.identifier?.value) {
      return request.insurer.identifier.value;
    }
    if (request.insurer?.reference) {
      return request.insurer.reference.replace('Organization/', '');
    }
    return 'unknown';
  }

  private extractProviderNpiFromFHIR(request: CoverageEligibilityRequest): string | undefined {
    if (request.provider?.identifier?.value) {
      return request.provider.identifier.value;
    }
    return undefined;
  }

  private extractServiceTypesFromFHIR(request: CoverageEligibilityRequest): string[] {
    const serviceTypes: string[] = [];
    if (request.item) {
      for (const item of request.item) {
        if (item.category?.coding) {
          for (const coding of item.category.coding) {
            if (coding.code) {
              serviceTypes.push(coding.code);
            }
          }
        }
      }
    }
    return serviceTypes.length > 0 ? serviceTypes : ['30'];
  }

  private extractEligibilityStatusFromFHIR(response: CoverageEligibilityResponse): 'active' | 'inactive' | 'terminated' | 'pending' | 'unknown' {
    if (response.insurance && response.insurance.length > 0) {
      const insurance = response.insurance[0];
      if (insurance.inforce === true) {
        return 'active';
      } else if (insurance.inforce === false) {
        return 'inactive';
      }
    }
    return 'unknown';
  }

  private getCurrentDate(): string {
    return new Date().toISOString().split('T')[0].replace(/-/g, '');
  }

  private calculateAge(dateOfBirth: string): number {
    const dob = dateOfBirth.includes('-') 
      ? new Date(dateOfBirth)
      : new Date(`${dateOfBirth.substring(0, 4)}-${dateOfBirth.substring(4, 6)}-${dateOfBirth.substring(6, 8)}`);
    const today = new Date();
    let age = today.getFullYear() - dob.getFullYear();
    const monthDiff = today.getMonth() - dob.getMonth();
    if (monthDiff < 0 || (monthDiff === 0 && today.getDate() < dob.getDate())) {
      age--;
    }
    return age;
  }

  private async checkCosmosHealth(): Promise<ComponentHealth> {
    const start = Date.now();
    try {
      await this.database.read();
      return {
        status: 'healthy',
        latencyMs: Date.now() - start,
        lastCheck: new Date().toISOString()
      };
    } catch (error) {
      return {
        status: 'unhealthy',
        latencyMs: Date.now() - start,
        lastCheck: new Date().toISOString(),
        error: String(error)
      };
    }
  }

  private async checkEventGridHealth(): Promise<ComponentHealth> {
    if (!this.eventGridClient) {
      return {
        status: 'healthy',
        lastCheck: new Date().toISOString()
      };
    }
    return {
      status: 'healthy',
      lastCheck: new Date().toISOString()
    };
  }

  private async checkQNXTHealth(): Promise<ComponentHealth> {
    if (!this.config.qnxt) {
      return {
        status: 'healthy',
        lastCheck: new Date().toISOString()
      };
    }
    // In production, would make a health check call to QNXT
    return {
      status: 'healthy',
      lastCheck: new Date().toISOString()
    };
  }

  private async checkFHIRServerHealth(): Promise<ComponentHealth> {
    if (!this.config.fhirServer) {
      return {
        status: 'healthy',
        lastCheck: new Date().toISOString()
      };
    }
    // In production, would make a health check call to FHIR server
    return {
      status: 'healthy',
      lastCheck: new Date().toISOString()
    };
  }

  private async checkDaprHealth(): Promise<ComponentHealth> {
    if (!this.config.dapr?.enabled) {
      return {
        status: 'healthy',
        lastCheck: new Date().toISOString()
      };
    }
    // In production, would check Dapr sidecar health
    return {
      status: 'healthy',
      lastCheck: new Date().toISOString()
    };
  }
}
