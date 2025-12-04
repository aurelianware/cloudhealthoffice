import { EligibilityService } from '../src/eligibility-service';
import {
  BackendEligibilityRule,
  EligibilityCacheRecord,
  EligibilityServiceConfig,
  X12_270_Request,
  X12_271_Response
} from '../src/types';
import type { CoverageEligibilityRequest, CoverageEligibilityResponse } from 'fhir/r4';

const mockPatch = jest.fn();
const mockFetchAll = jest.fn();
const mockCreate = jest.fn();
const mockCosmosQuery = jest.fn();
const mockContainerItem = jest.fn();
const mockDatabaseRead = jest.fn();
const mockCosmosDatabase = { container: jest.fn(), read: mockDatabaseRead } as const;
const mockCosmosClient = { database: jest.fn() } as const;
const mockEventGridSend = jest.fn();
const mockKeyCredential = { source: 'key' };
const mockManagedCredential = { source: 'managed' };

mockCosmosQuery.mockImplementation(() => ({ fetchAll: mockFetchAll }));
mockContainerItem.mockImplementation(() => ({ patch: mockPatch }));
mockCosmosClient.database.mockImplementation(() => mockCosmosDatabase);
mockCosmosDatabase.container.mockImplementation(() => ({
  items: {
    query: mockCosmosQuery,
    create: mockCreate
  },
  item: mockContainerItem
}));
mockDatabaseRead.mockResolvedValue(undefined);
mockFetchAll.mockResolvedValue({ resources: [] });
mockCreate.mockResolvedValue(undefined);
mockPatch.mockResolvedValue(undefined);
mockEventGridSend.mockResolvedValue(undefined);

jest.mock('@azure/cosmos', () => ({
  CosmosClient: jest.fn().mockImplementation(() => mockCosmosClient)
}));

jest.mock('@azure/identity', () => ({
  DefaultAzureCredential: jest.fn().mockImplementation(() => mockManagedCredential)
}));

jest.mock('@azure/eventgrid', () => ({
  EventGridPublisherClient: jest.fn().mockImplementation(() => ({ send: mockEventGridSend })),
  AzureKeyCredential: jest.fn().mockImplementation(() => mockKeyCredential)
}));

jest.mock('uuid', () => ({ v4: jest.fn().mockReturnValue('fixed-uuid') }));

const { v4: mockUuid } = require('uuid') as { v4: jest.Mock };
const { EventGridPublisherClient } = require('@azure/eventgrid') as { EventGridPublisherClient: jest.Mock };

describe('EligibilityService core utilities', () => {
  const baseConfig: EligibilityServiceConfig = {
    cosmosDb: {
      endpoint: 'https://localhost:8081',
      databaseName: 'eligibility-db',
      containerName: 'eligibility-cache',
      defaultTtlSeconds: 86400
    },
    eventGrid: {
      topicEndpoint: 'https://eventgrid.example.com',
      topicKey: 'event-grid-key'
    },
    backendConfig: {
      baseUrl: 'https://backend.example.com',
      timeout: 5000
    },
    fhirServer: {
      baseUrl: 'https://fhir.example.com',
      authType: 'none'
    },
    cache: {
      enabled: true,
      activeMemberTtl: 86400,
      inactiveMemberTtl: 3600,
      maxCacheAge: 43200
    },
    dapr: {
      enabled: true,
      httpPort: 3500,
      grpcPort: 50001,
      appId: 'eligibility-service',
      stateStoreName: 'eligibility-state',
      pubSubName: 'eligibility-pubsub'
    }
  };

  const sampleX12Request: X12_270_Request = {
    transactionControlNumber: '123456789',
    interchangeControlNumber: '000000001',
    transactionDate: '20240115',
    informationSource: {
      entityIdentifier: 'PR',
      entityType: '2',
      name: 'Test Health Plan',
      identificationCode: 'TESTPLAN',
      identificationCodeQualifier: 'PI'
    },
    informationReceiver: {
      entityIdentifier: '1P',
      entityType: '1',
      name: 'Provider One',
      npi: '1234567890'
    },
    subscriber: {
      memberId: 'MEM001',
      firstName: 'John',
      lastName: 'Doe',
      dateOfBirth: '19800101'
    },
    dependent: {
      firstName: 'Jane',
      lastName: 'Doe',
      dateOfBirth: '20100101',
      relationshipCode: '19'
    },
    eligibilityDateRange: {
      startDate: '20240301'
    },
    serviceTypeCodes: ['48', '30']
  };

  const sampleFhirRequest: CoverageEligibilityRequest = {
    resourceType: 'CoverageEligibilityRequest',
    status: 'active',
    purpose: ['validation'],
    created: '2024-03-01',
    patient: {
      identifier: {
        system: 'http://example.com/member-id',
        value: 'MEM001'
      }
    },
    insurer: {
      identifier: {
        system: 'http://example.com/payer-id',
        value: 'TESTPLAN'
      }
    },
    provider: {
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: '1234567890'
      }
    },
    item: [
      {
        category: {
          coding: [
            { code: '48' },
            { code: '30' }
          ]
        }
      }
    ]
  };

  const sampleX12Response: X12_271_Response = {
    transactionControlNumber: '123456789',
    responseControlNumber: '987654321',
    transactionDate: '20240301',
    informationSource: {
      entityIdentifier: 'PR',
      name: 'Test Health Plan',
      identificationCode: 'TESTPLAN'
    },
    informationReceiver: {
      entityIdentifier: '1P',
      name: 'Provider One',
      npi: '1234567890'
    },
    subscriber: {
      memberId: 'MEM001',
      firstName: 'John',
      lastName: 'Doe',
      dateOfBirth: '19800101'
    },
    eligibilityStatus: 'active',
    benefits: [
      {
        serviceTypeCode: '30',
        eligibilityInfoCode: '1',
        coverageLevelCode: 'IND'
      }
    ]
  };

  const sampleFhirResponse: CoverageEligibilityResponse = {
    resourceType: 'CoverageEligibilityResponse',
    status: 'active',
    purpose: ['validation'],
    created: '2024-03-01',
    request: {
      reference: 'CoverageEligibilityRequest/sample'
    },
    patient: {
      reference: 'Patient/MEM001'
    },
    insurer: {
      reference: 'Organization/TESTPLAN'
    },
    outcome: 'complete',
    insurance: [
      {
        inforce: true,
        coverage: {
          reference: 'Coverage/123456'
        }
      }
    ]
  };

  const formatDate = (offsetDays: number): string => {
    const date = new Date(Date.now() + offsetDays * 24 * 60 * 60 * 1000);
    return date.toISOString().split('T')[0].replace(/-/g, '');
  };

  const createRequestWithoutCreated = (): CoverageEligibilityRequest => {
    const clone = { ...sampleFhirRequest } as unknown as CoverageEligibilityRequest;
    delete (clone as any).created;
    return clone;
  };

  let service: EligibilityService;

  beforeEach(() => {
    jest.clearAllMocks();

    mockFetchAll.mockResolvedValue({ resources: [] });
    mockDatabaseRead.mockResolvedValue(undefined);
    mockCreate.mockResolvedValue(undefined);
    mockPatch.mockResolvedValue(undefined);
    mockEventGridSend.mockResolvedValue(undefined);

    service = new EligibilityService(baseConfig);
  });

  it('generates cache keys with dependent identifiers and sorted service types', () => {
    const key = (service as any).generateCacheKey(sampleX12Request);
    expect(key).toBe('x12:TESTPLAN:MEM001-Jane-Doe:20240301:30,48');
  });

  it('generates FHIR cache keys using created date and service codes', () => {
    const key = (service as any).generateFHIRCacheKey(sampleFhirRequest);
    expect(key).toBe('fhir:TESTPLAN:MEM001:2024-03-01:30,48');
  });

  it('returns cached response and updates access metadata when entry is fresh', async () => {
    const recentRecord: EligibilityCacheRecord = {
      id: 'cache-1',
      memberId: 'MEM001',
      payerId: 'TESTPLAN',
      cacheKey: 'x12:TESTPLAN:MEM001:20240301:30',
      requestType: 'X12_270',
      originalRequest: sampleX12Request,
      response: {
        x12Response: sampleX12Response,
        eligibilityStatus: 'active'
      },
      createdAt: new Date().toISOString(),
      lastAccessedAt: new Date().toISOString(),
      accessCount: 2,
      ttl: 86400,
      source: 'BACKEND_SYSTEM'
    };

    mockFetchAll.mockResolvedValueOnce({ resources: [recentRecord] });

    const result = await (service as any).getCachedResponse('x12:TESTPLAN:MEM001:20240301:30');

    expect(result).toEqual(recentRecord);
    expect(mockContainerItem).toHaveBeenCalledWith('cache-1', 'MEM001');
    expect(mockPatch).toHaveBeenCalledWith([
      expect.objectContaining({ path: '/lastAccessedAt' }),
      { op: 'incr', path: '/accessCount', value: 1 }
    ]);
  });

  it('returns null for expired cache entries without patching storage', async () => {
    const expired = new Date(Date.now() - (baseConfig.cache.maxCacheAge + 10) * 1000).toISOString();
    const expiredRecord: EligibilityCacheRecord = {
      id: 'cache-expired',
      memberId: 'MEM001',
      payerId: 'TESTPLAN',
      cacheKey: 'x12:TESTPLAN:MEM001:20230101:30',
      requestType: 'X12_270',
      originalRequest: sampleX12Request,
      response: {
        x12Response: sampleX12Response,
        eligibilityStatus: 'inactive'
      },
      createdAt: expired,
      lastAccessedAt: expired,
      accessCount: 1,
      ttl: 3600,
      source: 'BACKEND_SYSTEM'
    };

    mockFetchAll.mockResolvedValueOnce({ resources: [expiredRecord] });

    const result = await (service as any).getCachedResponse('x12:TESTPLAN:MEM001:20230101:30');

    expect(result).toBeNull();
    expect(mockPatch).not.toHaveBeenCalled();
  });

  it('gracefully handles cache lookup failures', async () => {
    mockFetchAll.mockRejectedValueOnce(new Error('cosmos offline'));

    const result = await (service as any).getCachedResponse('missing-key');

    expect(result).toBeNull();
  });

  it('writes X12 cache entries with active TTL', async () => {
    await (service as any).cacheResponse('cache-key', sampleX12Request, 'X12_270', sampleX12Response);

    expect(mockCreate).toHaveBeenCalledTimes(1);
    const created = mockCreate.mock.calls[0][0];
    expect(created.ttl).toBe(baseConfig.cache.activeMemberTtl);
    expect(created.response.eligibilityStatus).toBe('active');
  });

  it('writes FHIR cache entries with inactive TTL when inforce is false', async () => {
    const fhirRequest: CoverageEligibilityRequest = {
      ...sampleFhirRequest,
      created: '2024-03-01'
    };

    const inactiveResponse: CoverageEligibilityResponse = {
      resourceType: 'CoverageEligibilityResponse',
      status: 'active',
      purpose: ['validation'],
      created: '2024-03-01',
      request: {
        reference: 'CoverageEligibilityRequest/sample'
      },
      patient: {
        reference: 'Patient/MEM001'
      },
      insurer: {
        reference: 'Organization/TESTPLAN'
      },
      outcome: 'complete',
      insurance: [
        {
          inforce: false,
          coverage: {
            reference: 'Coverage/123456'
          }
        }
      ]
    };

    await (service as any).cacheFHIRResponse('fhir-cache', fhirRequest, inactiveResponse);

    const created = mockCreate.mock.calls.pop()?.[0];
    expect(created?.ttl).toBe(baseConfig.cache.inactiveMemberTtl);
    expect(created?.response.eligibilityStatus).toBe('inactive');
  });

  it('returns early when Event Grid client is not configured', async () => {
    const minimalConfig: EligibilityServiceConfig = {
      ...baseConfig,
      eventGrid: {
        topicEndpoint: '',
        topicKey: undefined
      }
    };

    const noEventClientService = new EligibilityService(minimalConfig);

    await (noEventClientService as any).publishEligibilityCheckedEvent(
      'MEM001',
      'TESTPLAN',
      undefined,
      'X12_270',
      'active',
      '20240301',
      ['30'],
      true,
      10
    );

    expect(mockEventGridSend).not.toHaveBeenCalled();
  });

  it('initializes Event Grid client with managed identity when topic key is missing', () => {
    const identityConfig: EligibilityServiceConfig = {
      ...baseConfig,
      eventGrid: {
        topicEndpoint: 'https://eventgrid.example.com',
        topicKey: undefined
      }
    };

    new EligibilityService(identityConfig);

    const managedCall = EventGridPublisherClient.mock.calls.find(
      ([endpoint, _schema, credential]) =>
        endpoint === identityConfig.eventGrid.topicEndpoint && credential === mockManagedCredential
    );

    expect(managedCall).toBeDefined();
  });

  it('publishes eligibility checked events with expected payload', async () => {
    await (service as any).publishEligibilityCheckedEvent(
      'MEM001',
      'TESTPLAN',
      '1234567890',
      'X12_270',
      'active',
      '20240301',
      ['30'],
      false,
      42
    );

    expect(mockEventGridSend).toHaveBeenCalledTimes(1);
    const [event] = mockEventGridSend.mock.calls[0][0];
    expect(event.data.memberId).toBe('MEM001');
    expect(event.data.requestType).toBe('X12_270');
    expect(event.data.fromCache).toBe(false);
    expect(event.data.responseTimeMs).toBe(42);
  });

  it('reports healthy status when dependencies respond successfully', async () => {
    const health = await service.getHealth();

    expect(mockDatabaseRead).toHaveBeenCalled();
    expect(health.status).toBe('healthy');
    expect(health.checks.cosmosDb.status).toBe('healthy');
    expect(health.checks.eventGrid.status).toBe('healthy');
    expect(health.checks.backendConfig?.status).toBe('healthy');
    expect(health.checks.fhirServer?.status).toBe('healthy');
    expect(health.checks.dapr?.status).toBe('healthy');
  });

  it('reports unhealthy status when Cosmos DB read fails', async () => {
    mockDatabaseRead.mockRejectedValueOnce(new Error('read failure'));

    const health = await service.getHealth();

    expect(health.status).toBe('unhealthy');
    expect(health.checks.cosmosDb.status).toBe('unhealthy');
  });

  it('reports degraded status when components are neither fully healthy nor unhealthy', async () => {
    const cosmosSpy = jest.spyOn(service as any, 'checkCosmosHealth').mockResolvedValue({
      status: 'healthy',
      lastCheck: new Date().toISOString()
    });

    const backendSpy = jest.spyOn(service as any, 'checkBackendHealth').mockResolvedValue({
      status: 'degraded',
      lastCheck: new Date().toISOString()
    });

    const health = await service.getHealth();

    expect(health.status).toBe('degraded');
    expect(health.checks.backendConfig?.status).toBe('degraded');

    cosmosSpy.mockRestore();
    backendSpy.mockRestore();
  });

  it('reports healthy defaults when optional integrations are disabled', async () => {
    const minimalConfig: EligibilityServiceConfig = {
      ...baseConfig,
      backendConfig: undefined,
      fhirServer: undefined,
      dapr: {
        ...baseConfig.dapr,
        enabled: false
      },
      eventGrid: {
        topicEndpoint: baseConfig.eventGrid.topicEndpoint,
        topicKey: baseConfig.eventGrid.topicKey
      }
    };

    const minimalService = new EligibilityService(minimalConfig);

    const backendHealth = await (minimalService as any).checkBackendHealth();
    const fhirHealth = await (minimalService as any).checkFHIRServerHealth();
    const daprHealth = await (minimalService as any).checkDaprHealth();

    expect(backendHealth.status).toBe('healthy');
    expect(fhirHealth.status).toBe('healthy');
    expect(daprHealth.status).toBe('healthy');
  });

  it('returns null when cache lookup finds no documents', async () => {
    mockFetchAll.mockResolvedValueOnce({ resources: [] });

    const result = await (service as any).getCachedResponse('non-existent');

    expect(result).toBeNull();
  });

  it('derives benefits using highest priority eligibility rules', async () => {
    const rules: BackendEligibilityRule[] = [
      {
        ruleId: 'rule-1',
        ruleName: 'Covered Primary',
        planCode: 'GOLD',
        serviceTypeCode: '30',
        benefitCategory: 'Primary Care',
        coverageIndicator: 'covered',
        priorAuthRequired: false,
        referralRequired: false,
        effectiveDateRange: {
          startDate: '20220101'
        },
        priority: 1,
        isActive: true
      },
      {
        ruleId: 'rule-2',
        ruleName: 'Limited',
        planCode: 'GOLD',
        serviceTypeCode: '30',
        benefitCategory: 'Primary Care',
        coverageIndicator: 'not_covered',
        priorAuthRequired: true,
        referralRequired: false,
        effectiveDateRange: {
          startDate: '20200101'
        },
        priority: 5,
        isActive: true
      }
    ];

    service.loadEligibilityRules(rules);

    const response = await (service as any).fetchX12Eligibility({
      ...sampleX12Request,
      serviceTypeCodes: ['30'],
      subscriber: {
        ...sampleX12Request.subscriber,
        groupNumber: 'GOLD'
      }
    });

    expect(response.eligibilityStatus).toBe('active');
    expect(response.benefits[0].authorizationRequired).toBe(false);
  });

  it('delegates X12 eligibility requests through checkEligibility', async () => {
    mockUuid.mockClear();

    const checkX12Spy = jest
      .spyOn(service as any, 'checkX12Eligibility')
      .mockResolvedValue({
        format: 'X12',
        fromCache: false,
        timestamp: new Date().toISOString(),
        responseTimeMs: 5,
        cacheKey: 'cache-key',
        correlationId: 'fixed-uuid',
        x12Response: sampleX12Response
      });

    const result = await service.checkEligibility({
      format: 'X12',
      x12Request: sampleX12Request
    });

    expect(checkX12Spy).toHaveBeenCalledWith(
      sampleX12Request,
      undefined,
      'fixed-uuid',
      expect.any(Number)
    );
    expect(result.format).toBe('X12');
    expect(mockUuid).toHaveBeenCalledTimes(1);

    checkX12Spy.mockRestore();
  });

  it('delegates FHIR eligibility requests through checkEligibility and preserves correlation id', async () => {
    mockUuid.mockClear();

    const checkFhirSpy = jest
      .spyOn(service as any, 'checkFHIREligibility')
      .mockResolvedValue({
        format: 'FHIR',
        fromCache: true,
        timestamp: new Date().toISOString(),
        responseTimeMs: 7,
        cacheKey: 'fhir-cache',
        correlationId: 'corr-123',
        fhirResponse: sampleFhirResponse
      });

    const result = await service.checkEligibility({
      format: 'FHIR',
      fhirRequest: sampleFhirRequest,
      correlationId: 'corr-123',
      skipCache: true
    });

    expect(checkFhirSpy).toHaveBeenCalledWith(
      sampleFhirRequest,
      true,
      'corr-123',
      expect.any(Number)
    );
    expect(result.correlationId).toBe('corr-123');
    expect(mockUuid).not.toHaveBeenCalled();

    checkFhirSpy.mockRestore();
  });

  it('throws an error for invalid eligibility requests', async () => {
    const consoleSpy = jest.spyOn(console, 'error').mockImplementation(() => undefined);

    await expect(
      service.checkEligibility({ format: 'FHIR' } as any)
    ).rejects.toThrow('Invalid request format or missing request data');

    expect(consoleSpy).toHaveBeenCalled();
    consoleSpy.mockRestore();
  });

  it('returns cached X12 responses when cache hits', async () => {
    const cachedRecord: EligibilityCacheRecord = {
      id: 'cache-hit',
      memberId: 'MEM001',
      payerId: 'TESTPLAN',
      cacheKey: 'x12:TESTPLAN:MEM001:20240301:30,48',
      requestType: 'X12_270',
      originalRequest: sampleX12Request,
      response: {
        x12Response: sampleX12Response,
        eligibilityStatus: 'active'
      },
      createdAt: new Date().toISOString(),
      lastAccessedAt: new Date().toISOString(),
      accessCount: 1,
      ttl: 86400,
      source: 'BACKEND_SYSTEM'
    };

    const getCacheSpy = jest
      .spyOn(service as any, 'getCachedResponse')
      .mockResolvedValue(cachedRecord);
    const fetchSpy = jest
      .spyOn(service as any, 'fetchX12Eligibility')
      .mockResolvedValue(sampleX12Response);
    const cacheSpy = jest.spyOn(service as any, 'cacheResponse');
    const publishSpy = jest
      .spyOn(service as any, 'publishEligibilityCheckedEvent')
      .mockResolvedValue(undefined);

    const result = await service.checkX12Eligibility(sampleX12Request);

    expect(result.fromCache).toBe(true);
    expect(getCacheSpy).toHaveBeenCalled();
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(cacheSpy).not.toHaveBeenCalled();
    expect(publishSpy).toHaveBeenCalledWith(
      sampleX12Request.subscriber.memberId,
      sampleX12Request.informationSource.identificationCode,
      sampleX12Request.informationReceiver?.npi,
      'X12_270',
      'active',
      expect.any(String),
      sampleX12Request.serviceTypeCodes,
      true,
      expect.any(Number)
    );

    getCacheSpy.mockRestore();
    fetchSpy.mockRestore();
    cacheSpy.mockRestore();
    publishSpy.mockRestore();
  });

  it('uses current date fallback for cached X12 responses without eligibility start date', async () => {
    const cachedRecord: EligibilityCacheRecord = {
      id: 'cache-hit-no-date',
      memberId: 'MEM001',
      payerId: 'TESTPLAN',
      cacheKey: 'x12:TESTPLAN:MEM001::30,48',
      requestType: 'X12_270',
      originalRequest: { ...sampleX12Request, eligibilityDateRange: undefined },
      response: {
        x12Response: sampleX12Response,
        eligibilityStatus: 'active'
      },
      createdAt: new Date().toISOString(),
      lastAccessedAt: new Date().toISOString(),
      accessCount: 1,
      ttl: 86400,
      source: 'BACKEND_SYSTEM'
    };

    const currentDateSpy = jest
      .spyOn(service as any, 'getCurrentDate')
      .mockReturnValue('20240515');
    const getCacheSpy = jest
      .spyOn(service as any, 'getCachedResponse')
      .mockResolvedValue(cachedRecord);
    const publishSpy = jest
      .spyOn(service as any, 'publishEligibilityCheckedEvent')
      .mockResolvedValue(undefined);

    const requestWithoutDate = { ...sampleX12Request, eligibilityDateRange: undefined };
    const result = await service.checkX12Eligibility(requestWithoutDate);

    expect(result.fromCache).toBe(true);
    expect(publishSpy).toHaveBeenCalledWith(
      requestWithoutDate.subscriber.memberId,
      requestWithoutDate.informationSource.identificationCode,
      requestWithoutDate.informationReceiver?.npi,
      'X12_270',
      'active',
      '20240515',
      requestWithoutDate.serviceTypeCodes,
      true,
      expect.any(Number)
    );

    currentDateSpy.mockRestore();
    getCacheSpy.mockRestore();
    publishSpy.mockRestore();
  });

  it('uses current date fallback for fresh X12 responses without eligibility start date', async () => {
    const currentDateSpy = jest
      .spyOn(service as any, 'getCurrentDate')
      .mockReturnValue('20240516');
    const getCacheSpy = jest
      .spyOn(service as any, 'getCachedResponse')
      .mockResolvedValue(null);
    const fetchSpy = jest
      .spyOn(service as any, 'fetchX12Eligibility')
      .mockResolvedValue(sampleX12Response);
    const cacheSpy = jest
      .spyOn(service as any, 'cacheResponse')
      .mockResolvedValue(undefined);
    const publishSpy = jest
      .spyOn(service as any, 'publishEligibilityCheckedEvent')
      .mockResolvedValue(undefined);

    const requestWithoutDate = { ...sampleX12Request, eligibilityDateRange: undefined };
    const result = await service.checkX12Eligibility(requestWithoutDate);

    expect(result.fromCache).toBe(false);
    expect(publishSpy).toHaveBeenCalledWith(
      requestWithoutDate.subscriber.memberId,
      requestWithoutDate.informationSource.identificationCode,
      requestWithoutDate.informationReceiver?.npi,
      'X12_270',
      sampleX12Response.eligibilityStatus,
      '20240516',
      requestWithoutDate.serviceTypeCodes,
      false,
      expect.any(Number)
    );

    currentDateSpy.mockRestore();
    getCacheSpy.mockRestore();
    fetchSpy.mockRestore();
    cacheSpy.mockRestore();
    publishSpy.mockRestore();
  });

  it('fetches fresh X12 responses when cache misses', async () => {
    const getCacheSpy = jest
      .spyOn(service as any, 'getCachedResponse')
      .mockResolvedValue(null);
    const fetchSpy = jest
      .spyOn(service as any, 'fetchX12Eligibility')
      .mockResolvedValue(sampleX12Response);
    const cacheSpy = jest
      .spyOn(service as any, 'cacheResponse')
      .mockResolvedValue(undefined);
    const publishSpy = jest
      .spyOn(service as any, 'publishEligibilityCheckedEvent')
      .mockResolvedValue(undefined);

    const result = await service.checkX12Eligibility(sampleX12Request);

    expect(result.fromCache).toBe(false);
    expect(getCacheSpy).toHaveBeenCalled();
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(cacheSpy).toHaveBeenCalledWith(
      expect.any(String),
      sampleX12Request,
      'X12_270',
      sampleX12Response
    );
    expect(publishSpy).toHaveBeenCalledWith(
      sampleX12Request.subscriber.memberId,
      sampleX12Request.informationSource.identificationCode,
      sampleX12Request.informationReceiver?.npi,
      'X12_270',
      sampleX12Response.eligibilityStatus,
      expect.any(String),
      sampleX12Request.serviceTypeCodes,
      false,
      expect.any(Number)
    );

    getCacheSpy.mockRestore();
    fetchSpy.mockRestore();
    cacheSpy.mockRestore();
    publishSpy.mockRestore();
  });

  it('skips cache lookup when skipCache is true for X12 requests', async () => {
    const getCacheSpy = jest.spyOn(service as any, 'getCachedResponse');
    const fetchSpy = jest
      .spyOn(service as any, 'fetchX12Eligibility')
      .mockResolvedValue(sampleX12Response);
    const cacheSpy = jest
      .spyOn(service as any, 'cacheResponse')
      .mockResolvedValue(undefined);
    const publishSpy = jest
      .spyOn(service as any, 'publishEligibilityCheckedEvent')
      .mockResolvedValue(undefined);

    const result = await service.checkX12Eligibility(sampleX12Request, true, 'corr', Date.now());

    expect(getCacheSpy).not.toHaveBeenCalled();
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(result.fromCache).toBe(false);

    getCacheSpy.mockRestore();
    fetchSpy.mockRestore();
    cacheSpy.mockRestore();
    publishSpy.mockRestore();
  });

  it('returns cached FHIR responses when cache hits', async () => {
    const cachedRecord: EligibilityCacheRecord = {
      id: 'fhir-cache',
      memberId: 'MEM001',
      payerId: 'TESTPLAN',
      cacheKey: 'fhir:TESTPLAN:MEM001:2024-03-01:30,48',
      requestType: 'FHIR_CoverageEligibilityRequest',
      originalRequest: sampleFhirRequest,
      response: {
        fhirResponse: sampleFhirResponse,
        eligibilityStatus: 'active'
      },
      createdAt: new Date().toISOString(),
      lastAccessedAt: new Date().toISOString(),
      accessCount: 2,
      ttl: 86400,
      source: 'FHIR_SERVER'
    };

    const getCacheSpy = jest
      .spyOn(service as any, 'getCachedResponse')
      .mockResolvedValue(cachedRecord);
    const fetchSpy = jest
      .spyOn(service as any, 'fetchFHIREligibility')
      .mockResolvedValue(sampleFhirResponse);
    const cacheSpy = jest.spyOn(service as any, 'cacheFHIRResponse');
    const publishSpy = jest
      .spyOn(service as any, 'publishEligibilityCheckedEvent')
      .mockResolvedValue(undefined);

    const result = await service.checkFHIREligibility(sampleFhirRequest);

    expect(result.fromCache).toBe(true);
    expect(getCacheSpy).toHaveBeenCalled();
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(cacheSpy).not.toHaveBeenCalled();
    expect(publishSpy).toHaveBeenCalledWith(
      'MEM001',
      'TESTPLAN',
      '1234567890',
      'FHIR_CoverageEligibilityRequest',
      'active',
      '2024-03-01',
      ['48', '30'],
      true,
      expect.any(Number)
    );

    getCacheSpy.mockRestore();
    fetchSpy.mockRestore();
    cacheSpy.mockRestore();
    publishSpy.mockRestore();
  });

  it('fetches fresh FHIR responses when cache misses', async () => {
    const getCacheSpy = jest
      .spyOn(service as any, 'getCachedResponse')
      .mockResolvedValue(null);
    const fetchSpy = jest
      .spyOn(service as any, 'fetchFHIREligibility')
      .mockResolvedValue(sampleFhirResponse);
    const cacheSpy = jest
      .spyOn(service as any, 'cacheFHIRResponse')
      .mockResolvedValue(undefined);
    const publishSpy = jest
      .spyOn(service as any, 'publishEligibilityCheckedEvent')
      .mockResolvedValue(undefined);

    const result = await service.checkFHIREligibility(sampleFhirRequest);

    expect(result.fromCache).toBe(false);
    expect(getCacheSpy).toHaveBeenCalled();
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(cacheSpy).toHaveBeenCalledWith(
      expect.any(String),
      sampleFhirRequest,
      sampleFhirResponse
    );
    expect(publishSpy).toHaveBeenCalledWith(
      'MEM001',
      'TESTPLAN',
      '1234567890',
      'FHIR_CoverageEligibilityRequest',
      'active',
      '2024-03-01',
      ['48', '30'],
      false,
      expect.any(Number)
    );

    getCacheSpy.mockRestore();
    fetchSpy.mockRestore();
    cacheSpy.mockRestore();
    publishSpy.mockRestore();
  });

  it('skips cache lookup when skipCache is true for FHIR requests', async () => {
    const getCacheSpy = jest.spyOn(service as any, 'getCachedResponse');
    const fetchSpy = jest
      .spyOn(service as any, 'fetchFHIREligibility')
      .mockResolvedValue(sampleFhirResponse);
    const cacheSpy = jest
      .spyOn(service as any, 'cacheFHIRResponse')
      .mockResolvedValue(undefined);
    const publishSpy = jest
      .spyOn(service as any, 'publishEligibilityCheckedEvent')
      .mockResolvedValue(undefined);

    const result = await service.checkFHIREligibility(sampleFhirRequest, true, 'corr', Date.now());

    expect(getCacheSpy).not.toHaveBeenCalled();
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(result.fromCache).toBe(false);

    getCacheSpy.mockRestore();
    fetchSpy.mockRestore();
    cacheSpy.mockRestore();
    publishSpy.mockRestore();
  });

  it('uses current date fallback for cached FHIR responses without created date', async () => {
    const requestWithoutCreated = createRequestWithoutCreated();

    const cachedRecord: EligibilityCacheRecord = {
      id: 'fhir-cache-no-date',
      memberId: 'MEM001',
      payerId: 'TESTPLAN',
      cacheKey: 'fhir:TESTPLAN:MEM001::30,48',
      requestType: 'FHIR_CoverageEligibilityRequest',
      originalRequest: requestWithoutCreated,
      response: {
        fhirResponse: sampleFhirResponse,
        eligibilityStatus: 'active'
      },
      createdAt: new Date().toISOString(),
      lastAccessedAt: new Date().toISOString(),
      accessCount: 2,
      ttl: 86400,
      source: 'FHIR_SERVER'
    };

    const currentDateSpy = jest
      .spyOn(service as any, 'getCurrentDate')
      .mockReturnValue('20240517');
    const getCacheSpy = jest
      .spyOn(service as any, 'getCachedResponse')
      .mockResolvedValue(cachedRecord);
    const publishSpy = jest
      .spyOn(service as any, 'publishEligibilityCheckedEvent')
      .mockResolvedValue(undefined);

    const result = await service.checkFHIREligibility(requestWithoutCreated);

    expect(result.fromCache).toBe(true);
    expect(publishSpy).toHaveBeenCalledWith(
      'MEM001',
      'TESTPLAN',
      '1234567890',
      'FHIR_CoverageEligibilityRequest',
      'active',
      '20240517',
      ['48', '30'],
      true,
      expect.any(Number)
    );

    currentDateSpy.mockRestore();
    getCacheSpy.mockRestore();
    publishSpy.mockRestore();
  });

  it('uses current date fallback for fresh FHIR responses without created date', async () => {
    const requestWithoutCreated = createRequestWithoutCreated();

    const currentDateSpy = jest
      .spyOn(service as any, 'getCurrentDate')
      .mockReturnValue('20240518');
    const getCacheSpy = jest
      .spyOn(service as any, 'getCachedResponse')
      .mockResolvedValue(null);
    const fetchSpy = jest
      .spyOn(service as any, 'fetchFHIREligibility')
      .mockResolvedValue(sampleFhirResponse);
    const cacheSpy = jest
      .spyOn(service as any, 'cacheFHIRResponse')
      .mockResolvedValue(undefined);
    const publishSpy = jest
      .spyOn(service as any, 'publishEligibilityCheckedEvent')
      .mockResolvedValue(undefined);

    const result = await service.checkFHIREligibility(requestWithoutCreated);

    expect(result.fromCache).toBe(false);
    expect(publishSpy).toHaveBeenCalledWith(
      'MEM001',
      'TESTPLAN',
      '1234567890',
      'FHIR_CoverageEligibilityRequest',
      'active',
      '20240518',
      ['48', '30'],
      false,
      expect.any(Number)
    );

    currentDateSpy.mockRestore();
    getCacheSpy.mockRestore();
    fetchSpy.mockRestore();
    cacheSpy.mockRestore();
    publishSpy.mockRestore();
  });

  it('handles cache write failures for X12 responses without throwing', async () => {
    mockCreate.mockRejectedValueOnce(new Error('cosmos write failed'));

    await expect(
      (service as any).cacheResponse('cache-key', sampleX12Request, 'X12_270', sampleX12Response)
    ).resolves.toBeUndefined();
  });

  it('handles cache write failures for FHIR responses without throwing', async () => {
    mockCreate.mockRejectedValueOnce(new Error('cosmos write failed'));

    await expect(
      (service as any).cacheFHIRResponse('cache-key', sampleFhirRequest, sampleFhirResponse)
    ).resolves.toBeUndefined();
  });

  it('filters rules based on activity, effective dates, age, and gender', () => {
    const baselineRule: BackendEligibilityRule = {
      ruleId: 'BASE',
      ruleName: 'Baseline',
      planCode: 'GOLD',
      serviceTypeCode: '30',
      benefitCategory: 'Baseline',
      coverageIndicator: 'covered',
      priorAuthRequired: false,
      referralRequired: false,
      effectiveDateRange: { startDate: formatDate(-10) },
      priority: 1,
      isActive: true
    };

    const rules: BackendEligibilityRule[] = [
      baselineRule,
      {
        ...baselineRule,
        ruleId: 'INACTIVE',
        isActive: false
      },
      {
        ...baselineRule,
        ruleId: 'FUTURE',
        effectiveDateRange: { startDate: formatDate(30) }
      },
      {
        ...baselineRule,
        ruleId: 'EXPIRED',
        effectiveDateRange: { startDate: formatDate(-30), endDate: formatDate(-1) }
      },
      {
        ...baselineRule,
        ruleId: 'MIN_AGE',
        ageLimits: { minAge: 30 }
      },
      {
        ...baselineRule,
        ruleId: 'MAX_AGE',
        ageLimits: { maxAge: 20 }
      },
      {
        ...baselineRule,
        ruleId: 'GENDER',
        genderRestrictions: ['F']
      }
    ];

    service.loadEligibilityRules(rules);

    const applicable = service.getApplicableRules('GOLD', '30', 25, 'M');

    expect(applicable).toHaveLength(1);
    expect(applicable[0].ruleId).toBe('BASE');
  });

  it('produces default benefit when no rules match a service type', async () => {
    service.loadEligibilityRules([]);

    const response = await (service as any).fetchX12Eligibility({
      ...sampleX12Request,
      serviceTypeCodes: ['99'],
      subscriber: {
        ...sampleX12Request.subscriber,
        groupNumber: 'UNKNOWN'
      }
    });

    expect(response.eligibilityStatus).toBe('active');
    expect(response.benefits[0].eligibilityInfoCode).toBe('1');
  });
});
