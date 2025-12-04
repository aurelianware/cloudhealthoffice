import http from 'http';
import type { AddressInfo } from 'net';
import type { CoverageEligibilityRequest, CoverageEligibilityResponse } from 'fhir/r4';
import { handleRequest, initializeEligibilityService, resetEligibilityService } from '../src/index';
import type { EligibilityService } from '../src/eligibility-service';
import type {
  EligibilityCheckRequest,
  EligibilityCheckResponse,
  HealthStatus,
  X12_270_Request,
  X12_271_Response
} from '../src/types';

class StubEligibilityService
  implements Pick<EligibilityService, 'checkX12Eligibility' | 'checkFHIREligibility' | 'checkEligibility' | 'getHealth'>
{
  public lastX12Request?: {
    request: X12_270_Request;
    skipCache?: boolean;
    correlationId?: string;
  };

  public lastFhirRequest?: {
    request: CoverageEligibilityRequest;
    skipCache?: boolean;
    correlationId?: string;
  };

  private readonly x12Response: X12_271_Response;
  private readonly fhirResponse: CoverageEligibilityResponse;
  private readonly healthStatus: HealthStatus;
  private healthy = true;
  private nextX12FromCache = false;
  private nextFhirFromCache = false;

  constructor(
    x12Response: X12_271_Response,
    fhirResponse: CoverageEligibilityResponse,
    healthStatus: HealthStatus
  ) {
    this.x12Response = x12Response;
    this.fhirResponse = fhirResponse;
    this.healthStatus = healthStatus;
  }

  async checkX12Eligibility(
    request: X12_270_Request,
    skipCache?: boolean,
    correlationId?: string
  ): Promise<EligibilityCheckResponse> {
    this.lastX12Request = { request, skipCache, correlationId };
    const now = new Date().toISOString();
    const fromCache = this.nextX12FromCache;
    this.nextX12FromCache = false;
    return {
      format: 'X12',
      x12Response: this.x12Response,
      fromCache,
      timestamp: now,
      responseTimeMs: 5,
      cacheKey: 'stub-x12-cache-key',
      correlationId
    };
  }

  async checkFHIREligibility(
    request: CoverageEligibilityRequest,
    skipCache?: boolean,
    correlationId?: string
  ): Promise<EligibilityCheckResponse> {
    this.lastFhirRequest = { request, skipCache, correlationId };
    const now = new Date().toISOString();
    const fromCache = this.nextFhirFromCache;
    this.nextFhirFromCache = false;
    return {
      format: 'FHIR',
      fhirResponse: this.fhirResponse,
      fromCache,
      timestamp: now,
      responseTimeMs: 7,
      cacheKey: 'stub-fhir-cache-key',
      correlationId
    };
  }

  async checkEligibility(request: EligibilityCheckRequest): Promise<EligibilityCheckResponse> {
    if (request.format === 'X12' && request.x12Request) {
      return this.checkX12Eligibility(request.x12Request, request.skipCache, request.correlationId);
    }
    if (request.format === 'FHIR' && request.fhirRequest) {
      return this.checkFHIREligibility(request.fhirRequest, request.skipCache, request.correlationId);
    }
    throw new Error('Unsupported format in stub');
  }

  async getHealth(): Promise<HealthStatus> {
    if (!this.healthy) {
      return {
        ...this.healthStatus,
        status: 'unhealthy'
      };
    }
    return this.healthStatus;
  }

  setHealthy(value: boolean): void {
    this.healthy = value;
  }

  setNextX12FromCache(value: boolean): void {
    this.nextX12FromCache = value;
  }

  setNextFhirFromCache(value: boolean): void {
    this.nextFhirFromCache = value;
  }
}

describe('Eligibility service HTTP integration', () => {
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
      entityType: '2',
      name: 'Healthcare Provider',
      npi: '1234567890'
    },
    subscriber: {
      memberId: 'MEM001',
      firstName: 'John',
      lastName: 'Doe',
      dateOfBirth: '19800101'
    },
    eligibilityDateRange: {
      startDate: '20240115'
    },
    serviceTypeCodes: ['30']
  };

  const sampleX12Response: X12_271_Response = {
    transactionControlNumber: sampleX12Request.transactionControlNumber,
    responseControlNumber: '987654321',
    transactionDate: '20240115',
    informationSource: {
      entityIdentifier: 'PR',
      name: 'Test Health Plan',
      identificationCode: 'TESTPLAN'
    },
    informationReceiver: {
      entityIdentifier: '1P',
      name: 'Healthcare Provider',
      npi: '1234567890'
    },
    subscriber: {
      memberId: sampleX12Request.subscriber.memberId,
      firstName: sampleX12Request.subscriber.firstName,
      lastName: sampleX12Request.subscriber.lastName,
      dateOfBirth: sampleX12Request.subscriber.dateOfBirth,
      planName: 'Comprehensive Medical Plan'
    },
    eligibilityStatus: 'active',
    benefits: [
      {
        serviceTypeCode: '30',
        eligibilityInfoCode: '1',
        coverageLevelCode: 'IND',
        inNetwork: true
      }
    ]
  };

  const sampleFhirRequest: CoverageEligibilityRequest = {
    resourceType: 'CoverageEligibilityRequest',
    status: 'active',
    purpose: ['validation'],
    created: '2024-01-15',
    patient: {
      identifier: {
        system: 'http://cloudhealthoffice/patient-id',
        value: 'MEM001'
      }
    },
    insurer: {
      identifier: {
        system: 'http://cloudhealthoffice/payer-id',
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
            {
              system: 'http://terminology.hl7.org/CodeSystem/benefit-plan',
              code: '30',
              display: 'Health Benefit Plan Coverage'
            }
          ]
        }
      }
    ]
  };

  const sampleFhirResponse: CoverageEligibilityResponse = {
    resourceType: 'CoverageEligibilityResponse',
    id: 'stub-response',
    status: 'active',
    purpose: ['validation'],
    created: '2024-01-15',
    request: {
      reference: 'CoverageEligibilityRequest/stub-request'
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
        coverage: {
          reference: 'Coverage/123456'
        },
        inforce: true,
        item: [
          {
            category: {
              coding: [
                {
                  system: 'http://terminology.hl7.org/CodeSystem/benefit-plan',
                  code: '30',
                  display: 'Health Benefit Plan Coverage'
                }
              ]
            },
            benefit: [
              {
                type: {
                  text: 'In Network Coverage'
                },
                allowedUnsignedInt: 100
              }
            ]
          }
        ]
      }
    ]
  };
  let server: http.Server;
  let baseUrl: string;
  const nowIso = new Date().toISOString();
  const healthStatus: HealthStatus = {
    status: 'healthy',
    version: 'stub',
    uptime: 1,
    timestamp: nowIso,
    checks: {
      cosmosDb: { status: 'healthy', lastCheck: nowIso },
      eventGrid: { status: 'healthy', lastCheck: nowIso }
    }
  };
  const stub = new StubEligibilityService(sampleX12Response, sampleFhirResponse, healthStatus);
  const x12CorrelationId = 'corr-x12-123';
  const fhirCorrelationId = 'corr-fhir-456';
  const unifiedFhirCorrelationId = 'corr-unified-fhir-789';
  const unifiedX12CorrelationId = 'corr-unified-x12-321';
  beforeAll(async () => {
    initializeEligibilityService(undefined, stub as unknown as EligibilityService);
    server = http.createServer(handleRequest);
    await new Promise<void>(resolve => {
      server.listen(0, () => resolve());
    });
    const address = server.address() as AddressInfo;
    baseUrl = `http://127.0.0.1:${address.port}`;
  });

  afterAll(async () => {
    await new Promise<void>((resolve, reject) => {
      server.close(err => {
        if (err) {
          reject(err);
        } else {
          resolve();
        }
      });
    });
    resetEligibilityService();
  });

  it('returns JSON payload for 270 requests', async () => {
    const response = await fetch(`${baseUrl}/api/eligibility/x12?skipCache=true`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json',
        'X-Correlation-Id': x12CorrelationId
      },
      body: JSON.stringify(sampleX12Request)
    });

    expect(response.status).toBe(200);
    expect(response.headers.get('x-from-cache')).toBe('false');

    const payload = (await response.json()) as EligibilityCheckResponse;
    expect(payload.format).toBe('X12');
    expect(payload.correlationId).toBe(x12CorrelationId);
    expect(payload.x12Response?.transactionControlNumber).toBe(sampleX12Request.transactionControlNumber);
    expect(stub.lastX12Request?.skipCache).toBe(true);
    expect(stub.lastX12Request?.correlationId).toBe(x12CorrelationId);
  });

  it('supports raw X12 271 responses', async () => {
    const response = await fetch(`${baseUrl}/api/eligibility/x12`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/x12'
      },
      body: JSON.stringify(sampleX12Request)
    });

    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toContain('application/x12');
    expect(response.headers.get('x-from-cache')).toBe('false');

    const ediPayload = await response.text();
    expect(ediPayload).toContain('ISA*');
    expect(ediPayload).toContain('ST*271');
    expect(ediPayload).toContain(sampleX12Request.subscriber.lastName);
    expect(stub.lastX12Request?.skipCache).toBe(false);
  });

  it('propagates cache hits for X12 responses', async () => {
    stub.setNextX12FromCache(true);

    const response = await fetch(`${baseUrl}/api/eligibility/x12`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json'
      },
      body: JSON.stringify(sampleX12Request)
    });

    expect(response.status).toBe(200);
    expect(response.headers.get('x-from-cache')).toBe('true');

    const payload = (await response.json()) as EligibilityCheckResponse;
    expect(payload.fromCache).toBe(true);
    expect(stub.lastX12Request?.skipCache).toBe(false);
  });

  it('propagates cache hits for FHIR responses', async () => {
    stub.setNextFhirFromCache(true);

    const response = await fetch(`${baseUrl}/api/eligibility/fhir?skipCache=false`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/fhir+json',
        Accept: 'application/fhir+json',
        'X-Correlation-Id': fhirCorrelationId
      },
      body: JSON.stringify(sampleFhirRequest)
    });

    expect(response.status).toBe(200);
    expect(response.headers.get('x-from-cache')).toBe('true');

    const payload = (await response.json()) as CoverageEligibilityResponse;
    expect(payload.resourceType).toBe('CoverageEligibilityResponse');
    expect(stub.lastFhirRequest?.skipCache).toBe(false);
    expect(stub.lastFhirRequest?.correlationId).toBe(fhirCorrelationId);
  });

  it('returns FHIR payload for CoverageEligibilityRequest', async () => {
    const response = await fetch(`${baseUrl}/api/eligibility/fhir`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/fhir+json',
        Accept: 'application/fhir+json',
        'X-Correlation-Id': fhirCorrelationId
      },
      body: JSON.stringify(sampleFhirRequest)
    });

    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toContain('application/fhir+json');
    expect(response.headers.get('x-from-cache')).toBe('false');

    const payload = (await response.json()) as CoverageEligibilityResponse;
    expect(payload.resourceType).toBe('CoverageEligibilityResponse');
    expect(payload.outcome).toBe(sampleFhirResponse.outcome);
    expect(stub.lastFhirRequest?.correlationId).toBe(fhirCorrelationId);
    expect(stub.lastFhirRequest?.skipCache).toBe(false);
    expect(stub.lastFhirRequest?.request.patient?.identifier?.value).toBe(
      sampleFhirRequest.patient?.identifier?.value
    );
  });

  it('handles unified eligibility requests with FHIR format', async () => {
    const response = await fetch(`${baseUrl}/api/eligibility`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json'
      },
      body: JSON.stringify({
        format: 'FHIR',
        fhirRequest: sampleFhirRequest,
        skipCache: true,
        correlationId: unifiedFhirCorrelationId
      })
    });

    expect(response.status).toBe(200);

    const payload = (await response.json()) as EligibilityCheckResponse;
    expect(payload.format).toBe('FHIR');
    expect(payload.correlationId).toBe(unifiedFhirCorrelationId);
    expect(payload.fhirResponse?.resourceType).toBe('CoverageEligibilityResponse');
    expect(stub.lastFhirRequest?.skipCache).toBe(true);
    expect(stub.lastFhirRequest?.correlationId).toBe(unifiedFhirCorrelationId);
  });

  it('handles unified eligibility requests with X12 format', async () => {
    const response = await fetch(`${baseUrl}/api/eligibility`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json'
      },
      body: JSON.stringify({
        format: 'X12',
        x12Request: sampleX12Request,
        skipCache: false,
        correlationId: unifiedX12CorrelationId
      })
    });

    expect(response.status).toBe(200);

    const payload = (await response.json()) as EligibilityCheckResponse;
    expect(payload.format).toBe('X12');
    expect(payload.correlationId).toBe(unifiedX12CorrelationId);
    expect(payload.x12Response?.transactionControlNumber).toBe(sampleX12Request.transactionControlNumber);
    expect(stub.lastX12Request?.skipCache).toBe(false);
    expect(stub.lastX12Request?.correlationId).toBe(unifiedX12CorrelationId);
  });

  it('propagates cache hits for unified FHIR requests', async () => {
    stub.setNextFhirFromCache(true);

    const response = await fetch(`${baseUrl}/api/eligibility`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json'
      },
      body: JSON.stringify({
        format: 'FHIR',
        fhirRequest: sampleFhirRequest,
        skipCache: false,
        correlationId: unifiedFhirCorrelationId
      })
    });

    expect(response.status).toBe(200);

    const payload = (await response.json()) as EligibilityCheckResponse;
    expect(payload.fromCache).toBe(true);
    expect(payload.format).toBe('FHIR');
    expect(stub.lastFhirRequest?.skipCache).toBe(false);
  });

  it('propagates cache hits for unified X12 requests', async () => {
    stub.setNextX12FromCache(true);

    const response = await fetch(`${baseUrl}/api/eligibility`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json'
      },
      body: JSON.stringify({
        format: 'X12',
        x12Request: sampleX12Request,
        skipCache: false,
        correlationId: unifiedX12CorrelationId
      })
    });

    expect(response.status).toBe(200);

    const payload = (await response.json()) as EligibilityCheckResponse;
    expect(payload.fromCache).toBe(true);
    expect(payload.format).toBe('X12');
    expect(stub.lastX12Request?.skipCache).toBe(false);
  });

  it('reports degraded health responses', async () => {
    stub.setHealthy(false);

    const response = await fetch(`${baseUrl}/health`);
    expect(response.status).toBe(503);

    const payload = (await response.json()) as HealthStatus;
    expect(payload.status).toBe('unhealthy');
    expect(payload.checks.cosmosDb.status).toBe('healthy');

    stub.setHealthy(true);
  });
});
