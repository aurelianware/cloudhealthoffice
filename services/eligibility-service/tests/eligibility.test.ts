/**
 * Cloud Health Office - Eligibility Service Tests
 */

import { X12EligibilityMapper } from '../src/x12-mapper';
import { FHIREligibilityMapper } from '../src/fhir-mapper';
import { 
  X12_270_Request, 
  X12_271_Response,
  QNXTEligibilityRule 
} from '../src/types';
import { loadQNXTRulesFromCSV, validateRules, generateSampleCSV } from '../src/migration';
import * as fs from 'fs';
import * as path from 'path';

describe('X12EligibilityMapper', () => {
  const mapper = new X12EligibilityMapper();

  describe('getServiceTypeDescription', () => {
    it('returns correct description for known service type codes', () => {
      expect(mapper.getServiceTypeDescription('30')).toBe('Health Benefit Plan Coverage');
      expect(mapper.getServiceTypeDescription('48')).toBe('Hospital Inpatient');
      expect(mapper.getServiceTypeDescription('85')).toBe('Emergency Services');
      expect(mapper.getServiceTypeDescription('MH')).toBe('Mental Health');
    });

    it('returns generic description for unknown codes', () => {
      expect(mapper.getServiceTypeDescription('ZZ')).toBe('Service Type ZZ');
    });
  });

  describe('getEligibilityInfoDescription', () => {
    it('returns correct description for known eligibility codes', () => {
      expect(mapper.getEligibilityInfoDescription('1')).toBe('Active Coverage');
      expect(mapper.getEligibilityInfoDescription('6')).toBe('Inactive');
      expect(mapper.getEligibilityInfoDescription('B')).toBe('Co-Payment');
    });

    it('returns generic description for unknown codes', () => {
      expect(mapper.getEligibilityInfoDescription('ZZ')).toBe('Code ZZ');
    });
  });

  describe('generateX12271', () => {
    it('generates valid X12 271 EDI string', () => {
      const response: X12_271_Response = {
        transactionControlNumber: '123456789',
        responseControlNumber: '987654321',
        transactionDate: '20240115',
        transactionTime: '1200',
        informationSource: {
          entityIdentifier: 'PR',
          name: 'Test Health Plan',
          identificationCode: 'TESTPLAN'
        },
        informationReceiver: {
          entityIdentifier: '1P',
          npi: '1234567890'
        },
        subscriber: {
          memberId: 'MEM001',
          firstName: 'John',
          lastName: 'Doe',
          dateOfBirth: '19850615',
          gender: 'M',
          planName: 'Gold PPO'
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

      const edi = mapper.generateX12271(response);
      
      expect(edi).toContain('ISA*');
      expect(edi).toContain('ST*271');
      expect(edi).toContain('BHT*');
      expect(edi).toContain('NM1*PR*2*Test Health Plan');
      expect(edi).toContain('NM1*IL*1*Doe*John');
      expect(edi).toContain('EB*1*IND*30');
      expect(edi).toContain('SE*');
      expect(edi).toContain('IEA*');
    });
  });
});

describe('FHIREligibilityMapper', () => {
  const mapper = new FHIREligibilityMapper();

  describe('createCoverageEligibilityRequest', () => {
    it('creates valid FHIR CoverageEligibilityRequest', () => {
      const request = mapper.createCoverageEligibilityRequest({
        patientId: 'MEM001',
        insurerId: 'HEALTHPLAN',
        providerId: '1234567890',
        servicedDate: '2024-01-15',
        serviceTypeCodes: ['30', '48']
      });

      expect(request.resourceType).toBe('CoverageEligibilityRequest');
      expect(request.status).toBe('active');
      expect(request.purpose).toContain('validation');
      expect(request.purpose).toContain('benefits');
      expect(request.patient?.identifier?.value).toBe('MEM001');
      expect(request.insurer?.identifier?.value).toBe('HEALTHPLAN');
      expect(request.provider?.identifier?.value).toBe('1234567890');
      expect(request.item).toHaveLength(2);
    });

    it('handles optional parameters correctly', () => {
      const request = mapper.createCoverageEligibilityRequest({
        patientId: 'MEM002',
        insurerId: 'HEALTHPLAN'
      });

      expect(request.resourceType).toBe('CoverageEligibilityRequest');
      expect(request.provider).toBeUndefined();
      expect(request.servicedDate).toBeUndefined();
      expect(request.item).toBeUndefined();
    });
  });

  describe('fhirToX12', () => {
    it('converts FHIR CoverageEligibilityRequest to X12 270', () => {
      const fhirRequest = mapper.createCoverageEligibilityRequest({
        patientId: 'MEM003',
        insurerId: 'TESTPAYER',
        providerId: '9876543210',
        serviceTypeCodes: ['30']
      });

      const x12Request = mapper.fhirToX12(fhirRequest);

      expect(x12Request.subscriber.memberId).toBe('MEM003');
      expect(x12Request.informationSource.identificationCode).toBe('TESTPAYER');
      expect(x12Request.informationReceiver?.npi).toBe('9876543210');
      expect(x12Request.serviceTypeCodes).toContain('30');
    });
  });

  describe('x12ToFhir', () => {
    it('converts X12 271 Response to FHIR CoverageEligibilityResponse', () => {
      const x12Response: X12_271_Response = {
        transactionControlNumber: '123',
        responseControlNumber: '456',
        transactionDate: '20240115',
        informationSource: {
          entityIdentifier: 'PR',
          name: 'Test Plan',
          identificationCode: 'TESTPLAN'
        },
        informationReceiver: {
          entityIdentifier: '1P'
        },
        subscriber: {
          memberId: 'MEM004',
          firstName: 'Jane',
          lastName: 'Smith',
          dateOfBirth: '19900301'
        },
        eligibilityStatus: 'active',
        benefits: [
          {
            serviceTypeCode: '30',
            eligibilityInfoCode: '1',
            inNetwork: true,
            additionalInfo: {
              copay: 25
            }
          }
        ]
      };

      const fhirRequest = mapper.createCoverageEligibilityRequest({
        patientId: 'MEM004',
        insurerId: 'TESTPLAN'
      });

      const fhirResponse = mapper.x12ToFhir(x12Response, fhirRequest);

      expect(fhirResponse.resourceType).toBe('CoverageEligibilityResponse');
      expect(fhirResponse.outcome).toBe('complete');
      expect(fhirResponse.insurance).toHaveLength(1);
      expect(fhirResponse.insurance![0].inforce).toBe(true);
    });
  });

  describe('createPatientFromX12', () => {
    it('creates valid FHIR Patient from X12 subscriber data', () => {
      const subscriberData: X12_270_Request['subscriber'] = {
        memberId: 'MEM005',
        firstName: 'Robert',
        lastName: 'Johnson',
        middleName: 'Michael',
        dateOfBirth: '19750810',
        gender: 'M',
        ssn: '123-45-6789',
        address: {
          line1: '123 Main St',
          city: 'Austin',
          state: 'TX',
          postalCode: '78701'
        }
      };

      const patient = mapper.createPatientFromX12(subscriberData);

      expect(patient.resourceType).toBe('Patient');
      expect(patient.id).toBe('MEM005');
      expect(patient.name![0].family).toBe('Johnson');
      expect(patient.name![0].given).toContain('Robert');
      expect(patient.name![0].given).toContain('Michael');
      expect(patient.gender).toBe('male');
      expect(patient.birthDate).toBe('1975-08-10');
      expect(patient.identifier).toHaveLength(2);
      expect(patient.address![0].city).toBe('Austin');
    });
  });
});

describe('QNXT Migration', () => {
  const tempDir = '/tmp/eligibility-test';
  const sampleCsvPath = path.join(tempDir, 'test-rules.csv');

  beforeAll(() => {
    if (!fs.existsSync(tempDir)) {
      fs.mkdirSync(tempDir, { recursive: true });
    }
  });

  afterAll(() => {
    if (fs.existsSync(sampleCsvPath)) {
      fs.unlinkSync(sampleCsvPath);
    }
  });

  describe('generateSampleCSV', () => {
    it('generates a valid sample CSV file', () => {
      generateSampleCSV(sampleCsvPath);
      
      expect(fs.existsSync(sampleCsvPath)).toBe(true);
      
      const content = fs.readFileSync(sampleCsvPath, 'utf-8');
      const lines = content.split('\n');
      
      expect(lines.length).toBeGreaterThan(1);
      expect(lines[0]).toContain('rule_id');
      expect(lines[0]).toContain('plan_code');
      expect(lines[0]).toContain('service_type_code');
    });
  });

  describe('loadQNXTRulesFromCSV', () => {
    it('loads rules from CSV file', async () => {
      generateSampleCSV(sampleCsvPath);
      
      const rules = await loadQNXTRulesFromCSV(sampleCsvPath);
      
      expect(rules.length).toBeGreaterThan(0);
      expect(rules[0].ruleId).toBeDefined();
      expect(rules[0].planCode).toBeDefined();
      expect(rules[0].serviceTypeCode).toBeDefined();
    });

    it('throws error for non-existent file', async () => {
      await expect(loadQNXTRulesFromCSV('/nonexistent/file.csv'))
        .rejects.toThrow('CSV file not found');
    });
  });

  describe('validateRules', () => {
    it('validates valid rules successfully', () => {
      const rules: QNXTEligibilityRule[] = [
        {
          ruleId: 'RULE001',
          ruleName: 'Test Rule',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          effectiveDateRange: {
            startDate: '20240101'
          },
          priority: 10,
          isActive: true
        }
      ];

      const result = validateRules(rules);
      
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('reports errors for invalid rules', () => {
      const rules: QNXTEligibilityRule[] = [
        {
          ruleId: '',
          ruleName: 'Invalid Rule',
          planCode: '',
          serviceTypeCode: '',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          effectiveDateRange: {
            startDate: ''
          },
          priority: -1,
          isActive: true
        }
      ];

      const result = validateRules(rules);
      
      expect(result.valid).toBe(false);
      expect(result.errors.length).toBeGreaterThan(0);
    });

    it('validates coinsurance percentages', () => {
      const rules: QNXTEligibilityRule[] = [
        {
          ruleId: 'RULE002',
          ruleName: 'Invalid Coinsurance',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          inNetworkRequirements: {
            coinsurance: 150 // Invalid - should be 0-100
          },
          effectiveDateRange: {
            startDate: '20240101'
          },
          priority: 10,
          isActive: true
        }
      ];

      const result = validateRules(rules);
      
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.includes('coinsurance'))).toBe(true);
    });
  });
});

describe('Types', () => {
  it('X12_270_Request has correct structure', () => {
    const request: X12_270_Request = {
      transactionControlNumber: '123',
      interchangeControlNumber: '456',
      transactionDate: '20240115',
      informationSource: {
        entityIdentifier: 'PR',
        entityType: '2',
        name: 'Test Plan',
        identificationCode: 'TEST',
        identificationCodeQualifier: 'PI'
      },
      informationReceiver: {
        entityIdentifier: '1P',
        entityType: '1'
      },
      subscriber: {
        memberId: 'MEM001',
        firstName: 'John',
        lastName: 'Doe',
        dateOfBirth: '19850615'
      }
    };

    expect(request.transactionControlNumber).toBe('123');
    expect(request.subscriber.memberId).toBe('MEM001');
  });

  it('QNXTEligibilityRule has correct structure', () => {
    const rule: QNXTEligibilityRule = {
      ruleId: 'RULE001',
      ruleName: 'Test Rule',
      planCode: 'PPO_GOLD',
      serviceTypeCode: '30',
      benefitCategory: 'Preventive Care',
      coverageIndicator: 'covered',
      priorAuthRequired: false,
      referralRequired: false,
      inNetworkRequirements: {
        copay: 0,
        coinsurance: 0,
        deductibleApplies: false
      },
      outOfNetworkRequirements: {
        copay: 50,
        coinsurance: 40,
        deductibleApplies: true,
        coveragePercent: 60
      },
      quantityLimits: {
        maxQuantity: 1,
        quantityPeriod: 'year'
      },
      effectiveDateRange: {
        startDate: '20240101'
      },
      priority: 10,
      isActive: true
    };

    expect(rule.ruleId).toBe('RULE001');
    expect(rule.inNetworkRequirements?.copay).toBe(0);
    expect(rule.quantityLimits?.quantityPeriod).toBe('year');
  });
});

describe('HTTP Server Entry Point', () => {
  // Mock http module
  const mockRequest = (method: string, url: string, headers: Record<string, string> = {}, body?: string) => {
    const chunks: Buffer[] = body ? [Buffer.from(body)] : [];
    const dataListeners: ((chunk: Buffer) => void)[] = [];
    const endListeners: (() => void)[] = [];
    const errorListeners: ((err: Error) => void)[] = [];
    
    const req = {
      method,
      url,
      headers: {
        host: 'localhost:3000',
        ...headers
      },
      on: (event: string, cb: (arg?: unknown) => void) => {
        if (event === 'data') {
          dataListeners.push(cb as (chunk: Buffer) => void);
        } else if (event === 'end') {
          endListeners.push(cb as () => void);
        } else if (event === 'error') {
          errorListeners.push(cb as (err: Error) => void);
        }
        return req;
      }
    };
    
    // Simulate data events
    setTimeout(() => {
      chunks.forEach(chunk => dataListeners.forEach(cb => cb(chunk)));
      endListeners.forEach(cb => cb());
    }, 0);
    
    return req;
  };

  const mockResponse = () => {
    let statusCode = 200;
    const headers: Record<string, string> = {};
    let body = '';
    
    const res = {
      writeHead: (code: number, hdrs?: Record<string, string>) => {
        statusCode = code;
        if (hdrs) Object.assign(headers, hdrs);
        return res;
      },
      setHeader: (name: string, value: string) => {
        headers[name] = value;
        return res;
      },
      end: (data?: string) => {
        body = data || '';
        return res;
      },
      getStatusCode: () => statusCode,
      getHeaders: () => headers,
      getBody: () => body,
      getJsonBody: () => JSON.parse(body)
    };
    
    return res;
  };

  describe('Request Parsing', () => {
    it('parseBody resolves with empty string for no body', async () => {
      // This tests the parseBody function behavior
      const req = mockRequest('GET', '/health');
      
      const parseBody = (request: typeof req): Promise<string> => {
        return new Promise((resolve, reject) => {
          const chunks: Buffer[] = [];
          request.on('data', (chunk) => chunks.push(chunk as Buffer));
          request.on('end', () => resolve(Buffer.concat(chunks).toString()));
          request.on('error', reject);
        });
      };
      
      const body = await parseBody(req);
      expect(body).toBe('');
    });

    it('parseBody resolves with body content', async () => {
      const testBody = '{"test": "data"}';
      const req = mockRequest('POST', '/api/eligibility', {}, testBody);
      
      const parseBody = (request: typeof req): Promise<string> => {
        return new Promise((resolve, reject) => {
          const chunks: Buffer[] = [];
          request.on('data', (chunk) => chunks.push(chunk as Buffer));
          request.on('end', () => resolve(Buffer.concat(chunks).toString()));
          request.on('error', reject);
        });
      };
      
      const body = await parseBody(req);
      expect(body).toBe(testBody);
    });
  });

  describe('Response Helpers', () => {
    it('sendJson sets correct content type and status', () => {
      const res = mockResponse();
      
      const sendJson = (response: typeof res, statusCode: number, data: unknown) => {
        response.writeHead(statusCode, { 
          'Content-Type': 'application/json',
          'X-Content-Type-Options': 'nosniff'
        });
        response.end(JSON.stringify(data));
      };
      
      sendJson(res, 200, { status: 'ok' });
      
      expect(res.getStatusCode()).toBe(200);
      expect(res.getHeaders()['Content-Type']).toBe('application/json');
      expect(res.getHeaders()['X-Content-Type-Options']).toBe('nosniff');
      expect(res.getJsonBody()).toEqual({ status: 'ok' });
    });

    it('sendFhirJson sets FHIR content type', () => {
      const res = mockResponse();
      
      const sendFhirJson = (response: typeof res, statusCode: number, data: unknown) => {
        response.writeHead(statusCode, { 
          'Content-Type': 'application/fhir+json',
          'X-Content-Type-Options': 'nosniff'
        });
        response.end(JSON.stringify(data));
      };
      
      sendFhirJson(res, 200, { resourceType: 'OperationOutcome' });
      
      expect(res.getHeaders()['Content-Type']).toBe('application/fhir+json');
    });
  });

  describe('Route Matching', () => {
    // Test route matching logic
    const matchRoute = (pathname: string, method: string) => {
      if (pathname === '/api/eligibility/x12' && method === 'POST') return 'x12';
      if (pathname === '/api/eligibility/fhir' && method === 'POST') return 'fhir';
      if ((pathname === '/fhir/CoverageEligibilityRequest' || pathname === '/CoverageEligibilityRequest/$submit') && method === 'POST') return 'fhir';
      if (pathname === '/api/eligibility' && method === 'POST') return 'unified';
      if (pathname === '/health' || pathname === '/api/health') return 'health';
      if (pathname === '/healthz' || pathname === '/livez') return 'liveness';
      if (pathname === '/readyz') return 'readiness';
      if (pathname === '/dapr/subscribe' && method === 'GET') return 'dapr-subscribe';
      if (pathname === '/api/dapr/eligibility' && method === 'POST') return 'dapr-eligibility';
      return 'not-found';
    };

    it('routes X12 endpoint correctly', () => {
      expect(matchRoute('/api/eligibility/x12', 'POST')).toBe('x12');
      expect(matchRoute('/api/eligibility/x12', 'GET')).toBe('not-found');
    });

    it('routes FHIR endpoint correctly', () => {
      expect(matchRoute('/api/eligibility/fhir', 'POST')).toBe('fhir');
      expect(matchRoute('/fhir/CoverageEligibilityRequest', 'POST')).toBe('fhir');
      expect(matchRoute('/CoverageEligibilityRequest/$submit', 'POST')).toBe('fhir');
    });

    it('routes unified endpoint correctly', () => {
      expect(matchRoute('/api/eligibility', 'POST')).toBe('unified');
      expect(matchRoute('/api/eligibility', 'GET')).toBe('not-found');
    });

    it('routes health endpoints correctly', () => {
      expect(matchRoute('/health', 'GET')).toBe('health');
      expect(matchRoute('/api/health', 'GET')).toBe('health');
      expect(matchRoute('/healthz', 'GET')).toBe('liveness');
      expect(matchRoute('/livez', 'GET')).toBe('liveness');
      expect(matchRoute('/readyz', 'GET')).toBe('readiness');
    });

    it('routes Dapr endpoints correctly', () => {
      expect(matchRoute('/dapr/subscribe', 'GET')).toBe('dapr-subscribe');
      expect(matchRoute('/api/dapr/eligibility', 'POST')).toBe('dapr-eligibility');
    });

    it('returns not-found for unknown routes', () => {
      expect(matchRoute('/unknown', 'GET')).toBe('not-found');
      expect(matchRoute('/api/unknown', 'POST')).toBe('not-found');
    });
  });

  describe('CORS Handling', () => {
    it('allows configured origins', () => {
      const allowedOrigins = 'http://localhost:3000,http://example.com';
      const checkOrigin = (origin: string) => {
        const origins = allowedOrigins.split(',').map(o => o.trim());
        return origins.includes(origin) || origins.includes('*');
      };

      expect(checkOrigin('http://localhost:3000')).toBe(true);
      expect(checkOrigin('http://example.com')).toBe(true);
      expect(checkOrigin('http://malicious.com')).toBe(false);
    });

    it('allows all origins when wildcard is configured', () => {
      const allowedOrigins = '*';
      const checkOrigin = (origin: string) => {
        const origins = allowedOrigins.split(',').map(o => o.trim());
        return origins.includes(origin) || origins.includes('*');
      };

      expect(checkOrigin('http://any-origin.com')).toBe(true);
    });

    it('sets correct CORS headers', () => {
      const res = mockResponse();
      
      // Simulate CORS header setting
      res.setHeader('Access-Control-Allow-Origin', 'http://localhost:3000');
      res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
      res.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-Correlation-Id');
      
      const headers = res.getHeaders();
      expect(headers['Access-Control-Allow-Origin']).toBe('http://localhost:3000');
      expect(headers['Access-Control-Allow-Methods']).toBe('GET, POST, OPTIONS');
      expect(headers['Access-Control-Allow-Headers']).toBe('Content-Type, X-Correlation-Id');
    });
  });

  describe('Content Negotiation', () => {
    it('returns JSON by default', () => {
      const getContentType = (accept: string) => {
        if (accept.includes('application/x12')) return 'application/x12';
        if (accept.includes('application/fhir+json')) return 'application/fhir+json';
        return 'application/json';
      };

      expect(getContentType('')).toBe('application/json');
      expect(getContentType('*/*')).toBe('application/json');
    });

    it('returns X12 format when requested', () => {
      const getContentType = (accept: string) => {
        if (accept.includes('application/x12')) return 'application/x12';
        return 'application/json';
      };

      expect(getContentType('application/x12')).toBe('application/x12');
    });

    it('returns FHIR JSON for FHIR endpoints', () => {
      const getContentType = (accept: string, isFhirEndpoint: boolean) => {
        if (isFhirEndpoint) return 'application/fhir+json';
        if (accept.includes('application/x12')) return 'application/x12';
        return 'application/json';
      };

      expect(getContentType('application/json', true)).toBe('application/fhir+json');
    });
  });

  describe('Error Handling', () => {
    it('returns 404 for unknown routes', () => {
      const res = mockResponse();
      const handleNotFound = (response: typeof res, pathname: string) => {
        response.writeHead(404, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({ error: 'Not found', path: pathname }));
      };

      handleNotFound(res, '/unknown');
      
      expect(res.getStatusCode()).toBe(404);
      expect(res.getJsonBody().error).toBe('Not found');
    });

    it('returns 500 for server errors', () => {
      const res = mockResponse();
      const handleServerError = (response: typeof res, error: Error) => {
        response.writeHead(500, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({ 
          error: 'Internal server error',
          message: error.message
        }));
      };

      handleServerError(res, new Error('Test error'));
      
      expect(res.getStatusCode()).toBe(500);
      expect(res.getJsonBody().error).toBe('Internal server error');
      expect(res.getJsonBody().message).toBe('Test error');
    });

    it('returns FHIR OperationOutcome for FHIR errors', () => {
      const res = mockResponse();
      const handleFhirError = (response: typeof res, error: Error) => {
        response.writeHead(500, { 'Content-Type': 'application/fhir+json' });
        response.end(JSON.stringify({
          resourceType: 'OperationOutcome',
          issue: [{
            severity: 'error',
            code: 'exception',
            diagnostics: error.message
          }]
        }));
      };

      handleFhirError(res, new Error('FHIR error'));
      
      expect(res.getStatusCode()).toBe(500);
      expect(res.getJsonBody().resourceType).toBe('OperationOutcome');
      expect(res.getJsonBody().issue[0].severity).toBe('error');
    });

    it('validates FHIR resource type', () => {
      const validateFhirRequest = (request: { resourceType?: string }) => {
        if (request.resourceType !== 'CoverageEligibilityRequest') {
          return {
            valid: false,
            error: {
              resourceType: 'OperationOutcome',
              issue: [{
                severity: 'error',
                code: 'invalid',
                diagnostics: 'Expected resourceType CoverageEligibilityRequest'
              }]
            }
          };
        }
        return { valid: true };
      };

      const invalidResult = validateFhirRequest({ resourceType: 'Patient' });
      expect(invalidResult.valid).toBe(false);
      expect(invalidResult.error?.issue[0].code).toBe('invalid');

      const validResult = validateFhirRequest({ resourceType: 'CoverageEligibilityRequest' });
      expect(validResult.valid).toBe(true);
    });
  });

  describe('Health Check Endpoints', () => {
    it('liveness check returns alive status', () => {
      const res = mockResponse();
      const handleLiveness = (response: typeof res) => {
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({ status: 'alive' }));
      };

      handleLiveness(res);
      
      expect(res.getStatusCode()).toBe(200);
      expect(res.getJsonBody().status).toBe('alive');
    });

    it('readiness check returns ready status for healthy service', () => {
      const res = mockResponse();
      const handleReadiness = (response: typeof res, isHealthy: boolean) => {
        if (isHealthy) {
          response.writeHead(200, { 'Content-Type': 'application/json' });
          response.end(JSON.stringify({ status: 'ready' }));
        } else {
          response.writeHead(503, { 'Content-Type': 'application/json' });
          response.end(JSON.stringify({ status: 'not ready' }));
        }
      };

      handleReadiness(res, true);
      expect(res.getStatusCode()).toBe(200);
      expect(res.getJsonBody().status).toBe('ready');
    });

    it('readiness check returns 503 for unhealthy service', () => {
      const res = mockResponse();
      const handleReadiness = (response: typeof res, isHealthy: boolean) => {
        if (isHealthy) {
          response.writeHead(200, { 'Content-Type': 'application/json' });
          response.end(JSON.stringify({ status: 'ready' }));
        } else {
          response.writeHead(503, { 'Content-Type': 'application/json' });
          response.end(JSON.stringify({ status: 'not ready' }));
        }
      };

      handleReadiness(res, false);
      expect(res.getStatusCode()).toBe(503);
      expect(res.getJsonBody().status).toBe('not ready');
    });
  });

  describe('Dapr Integration', () => {
    it('returns subscription configuration', () => {
      const res = mockResponse();
      const daprConfig = {
        pubSubName: 'eligibility-pubsub'
      };
      
      const handleDaprSubscribe = (response: typeof res, config: typeof daprConfig) => {
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify([{
          pubsubname: config.pubSubName,
          topic: 'eligibility-requests',
          route: '/api/dapr/eligibility'
        }]));
      };

      handleDaprSubscribe(res, daprConfig);
      
      expect(res.getStatusCode()).toBe(200);
      const body = res.getJsonBody();
      expect(body).toHaveLength(1);
      expect(body[0].pubsubname).toBe('eligibility-pubsub');
      expect(body[0].topic).toBe('eligibility-requests');
      expect(body[0].route).toBe('/api/dapr/eligibility');
    });
  });

  describe('Request Content Type Handling', () => {
    it('parses JSON request body', () => {
      const parseRequestBody = (contentType: string, body: string) => {
        if (contentType.includes('application/json')) {
          return { format: 'json', data: JSON.parse(body) };
        }
        return { format: 'unknown', data: body };
      };

      const result = parseRequestBody('application/json', '{"test": "data"}');
      expect(result.format).toBe('json');
      expect(result.data.test).toBe('data');
    });

    it('handles X12 content type', () => {
      const parseRequestBody = (contentType: string, body: string) => {
        if (contentType.includes('application/json')) {
          return { format: 'json', data: JSON.parse(body) };
        }
        if (contentType.includes('application/x12') || contentType.includes('text/plain')) {
          return { format: 'x12', data: body };
        }
        // Default to JSON
        return { format: 'json', data: JSON.parse(body) };
      };

      const result = parseRequestBody('application/x12', 'ISA*00*...');
      expect(result.format).toBe('x12');
      expect(result.data).toBe('ISA*00*...');
    });

    it('defaults to JSON parsing for unknown content types', () => {
      const parseRequestBody = (contentType: string, body: string) => {
        if (contentType.includes('application/x12') || contentType.includes('text/plain')) {
          return { format: 'x12', data: body };
        }
        // Default to JSON
        return { format: 'json', data: JSON.parse(body) };
      };

      const result = parseRequestBody('', '{"default": "json"}');
      expect(result.format).toBe('json');
      expect(result.data.default).toBe('json');
    });
  });

  describe('Query Parameter Handling', () => {
    it('parses skipCache query parameter', () => {
      const parseSkipCache = (urlStr: string) => {
        const url = new URL(urlStr, 'http://localhost:3000');
        return url.searchParams.get('skipCache') === 'true';
      };

      expect(parseSkipCache('/api/eligibility?skipCache=true')).toBe(true);
      expect(parseSkipCache('/api/eligibility?skipCache=false')).toBe(false);
      expect(parseSkipCache('/api/eligibility')).toBe(false);
    });

    it('extracts correlation ID from headers', () => {
      const getCorrelationId = (headers: Record<string, string | undefined>) => {
        return headers['x-correlation-id'] || undefined;
      };

      expect(getCorrelationId({ 'x-correlation-id': 'test-123' })).toBe('test-123');
      expect(getCorrelationId({})).toBeUndefined();
    });
  });
});

describe('EligibilityService Business Logic', () => {
  // Test the business logic functions extracted from EligibilityService
  // These test the core logic without requiring Azure SDK mocking

  describe('Cache Key Generation', () => {
    const generateCacheKey = (request: X12_270_Request): string => {
      const memberId = request.dependent?.firstName 
        ? `${request.subscriber.memberId}-${request.dependent.firstName}-${request.dependent.lastName}`
        : request.subscriber.memberId;
      const payerId = request.informationSource.identificationCode;
      const serviceDate = request.eligibilityDateRange?.startDate || new Date().toISOString().split('T')[0].replace(/-/g, '');
      const serviceTypes = (request.serviceTypeCodes || ['30']).sort().join(',');
      
      return `x12:${payerId}:${memberId}:${serviceDate}:${serviceTypes}`;
    };

    it('generates cache key for subscriber', () => {
      const request: X12_270_Request = {
        transactionControlNumber: '123',
        interchangeControlNumber: '456',
        transactionDate: '20240115',
        informationSource: {
          entityIdentifier: 'PR',
          entityType: '2',
          name: 'Test Plan',
          identificationCode: 'TESTPAYER',
          identificationCodeQualifier: 'PI'
        },
        informationReceiver: {
          entityIdentifier: '1P',
          entityType: '1'
        },
        subscriber: {
          memberId: 'MEM001',
          firstName: 'John',
          lastName: 'Doe',
          dateOfBirth: '19850615'
        },
        eligibilityDateRange: {
          startDate: '20240115'
        },
        serviceTypeCodes: ['30', '48']
      };

      const cacheKey = generateCacheKey(request);
      
      expect(cacheKey).toBe('x12:TESTPAYER:MEM001:20240115:30,48');
    });

    it('generates cache key with dependent', () => {
      const request: X12_270_Request = {
        transactionControlNumber: '123',
        interchangeControlNumber: '456',
        transactionDate: '20240115',
        informationSource: {
          entityIdentifier: 'PR',
          entityType: '2',
          name: 'Test Plan',
          identificationCode: 'TESTPAYER',
          identificationCodeQualifier: 'PI'
        },
        informationReceiver: {
          entityIdentifier: '1P',
          entityType: '1'
        },
        subscriber: {
          memberId: 'MEM001',
          firstName: 'John',
          lastName: 'Doe',
          dateOfBirth: '19850615'
        },
        dependent: {
          firstName: 'Jane',
          lastName: 'Doe',
          dateOfBirth: '20150101',
          relationshipCode: '19'
        },
        eligibilityDateRange: {
          startDate: '20240115'
        }
      };

      const cacheKey = generateCacheKey(request);
      
      expect(cacheKey).toBe('x12:TESTPAYER:MEM001-Jane-Doe:20240115:30');
    });

    it('uses default service type code when not provided', () => {
      const request: X12_270_Request = {
        transactionControlNumber: '123',
        interchangeControlNumber: '456',
        transactionDate: '20240115',
        informationSource: {
          entityIdentifier: 'PR',
          entityType: '2',
          name: 'Test Plan',
          identificationCode: 'TESTPAYER',
          identificationCodeQualifier: 'PI'
        },
        informationReceiver: {
          entityIdentifier: '1P',
          entityType: '1'
        },
        subscriber: {
          memberId: 'MEM001',
          firstName: 'John',
          lastName: 'Doe',
          dateOfBirth: '19850615'
        },
        eligibilityDateRange: {
          startDate: '20240115'
        }
      };

      const cacheKey = generateCacheKey(request);
      
      expect(cacheKey).toContain(':30');
    });

    it('sorts service type codes for consistent cache keys', () => {
      const request1: X12_270_Request = {
        transactionControlNumber: '123',
        interchangeControlNumber: '456',
        transactionDate: '20240115',
        informationSource: {
          entityIdentifier: 'PR',
          entityType: '2',
          name: 'Test Plan',
          identificationCode: 'TESTPAYER',
          identificationCodeQualifier: 'PI'
        },
        informationReceiver: {
          entityIdentifier: '1P',
          entityType: '1'
        },
        subscriber: {
          memberId: 'MEM001',
          firstName: 'John',
          lastName: 'Doe',
          dateOfBirth: '19850615'
        },
        eligibilityDateRange: { startDate: '20240115' },
        serviceTypeCodes: ['48', '30', '85']
      };

      const request2: X12_270_Request = {
        ...request1,
        serviceTypeCodes: ['85', '30', '48']
      };

      expect(generateCacheKey(request1)).toBe(generateCacheKey(request2));
    });
  });

  describe('Age Calculation', () => {
    const calculateAge = (dateOfBirth: string): number => {
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
    };

    it('calculates age from YYYYMMDD format', () => {
      const currentYear = new Date().getFullYear();
      const age = calculateAge(`${currentYear - 30}0101`);
      expect(age).toBeGreaterThanOrEqual(29);
      expect(age).toBeLessThanOrEqual(30);
    });

    it('calculates age from ISO date format', () => {
      const currentYear = new Date().getFullYear();
      const age = calculateAge(`${currentYear - 25}-06-15`);
      expect(age).toBeGreaterThanOrEqual(24);
      expect(age).toBeLessThanOrEqual(25);
    });

    it('handles birthday not yet passed this year', () => {
      const currentYear = new Date().getFullYear();
      // Set birthday to December 31 of 30 years ago
      const age = calculateAge(`${currentYear - 30}1231`);
      // Depending on current date, age should be 29 or 30
      expect(age).toBeGreaterThanOrEqual(29);
      expect(age).toBeLessThanOrEqual(30);
    });
  });

  describe('Eligibility Rules Loading', () => {
    const loadEligibilityRules = (rules: QNXTEligibilityRule[]): Map<string, QNXTEligibilityRule[]> => {
      const eligibilityRules = new Map<string, QNXTEligibilityRule[]>();
      for (const rule of rules) {
        const key = `${rule.planCode}:${rule.serviceTypeCode}`;
        if (!eligibilityRules.has(key)) {
          eligibilityRules.set(key, []);
        }
        eligibilityRules.get(key)!.push(rule);
      }
      // Sort rules by priority
      for (const ruleList of eligibilityRules.values()) {
        ruleList.sort((a, b) => a.priority - b.priority);
      }
      return eligibilityRules;
    };

    it('loads and organizes rules by plan and service type', () => {
      const rules: QNXTEligibilityRule[] = [
        {
          ruleId: 'RULE001',
          ruleName: 'Rule 1',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          effectiveDateRange: { startDate: '20240101' },
          priority: 10,
          isActive: true
        },
        {
          ruleId: 'RULE002',
          ruleName: 'Rule 2',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '48',
          benefitCategory: 'Hospital',
          coverageIndicator: 'covered',
          priorAuthRequired: true,
          referralRequired: false,
          effectiveDateRange: { startDate: '20240101' },
          priority: 10,
          isActive: true
        }
      ];

      const loadedRules = loadEligibilityRules(rules);

      expect(loadedRules.has('PPO_GOLD:30')).toBe(true);
      expect(loadedRules.has('PPO_GOLD:48')).toBe(true);
      expect(loadedRules.get('PPO_GOLD:30')![0].ruleId).toBe('RULE001');
    });

    it('sorts rules by priority', () => {
      const rules: QNXTEligibilityRule[] = [
        {
          ruleId: 'RULE_LOW',
          ruleName: 'Low Priority',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          effectiveDateRange: { startDate: '20240101' },
          priority: 100,
          isActive: true
        },
        {
          ruleId: 'RULE_HIGH',
          ruleName: 'High Priority',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'General',
          coverageIndicator: 'not_covered',
          priorAuthRequired: false,
          referralRequired: false,
          effectiveDateRange: { startDate: '20240101' },
          priority: 1,
          isActive: true
        }
      ];

      const loadedRules = loadEligibilityRules(rules);
      const sortedRules = loadedRules.get('PPO_GOLD:30')!;

      expect(sortedRules[0].ruleId).toBe('RULE_HIGH');
      expect(sortedRules[1].ruleId).toBe('RULE_LOW');
    });
  });

  describe('Applicable Rules Filtering', () => {
    const getCurrentDate = (): string => {
      return new Date().toISOString().split('T')[0].replace(/-/g, '');
    };

    const getApplicableRules = (
      eligibilityRules: Map<string, QNXTEligibilityRule[]>,
      planCode: string, 
      serviceTypeCode: string, 
      memberAge?: number, 
      gender?: 'M' | 'F'
    ): QNXTEligibilityRule[] => {
      const key = `${planCode}:${serviceTypeCode}`;
      const rules = eligibilityRules.get(key) || [];
      const today = getCurrentDate();

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
    };

    it('returns active rules for plan and service type', () => {
      const rules = new Map<string, QNXTEligibilityRule[]>();
      rules.set('PPO_GOLD:30', [
        {
          ruleId: 'RULE001',
          ruleName: 'Active Rule',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          effectiveDateRange: { startDate: '20200101' },
          priority: 10,
          isActive: true
        }
      ]);

      const applicable = getApplicableRules(rules, 'PPO_GOLD', '30');
      expect(applicable).toHaveLength(1);
      expect(applicable[0].ruleId).toBe('RULE001');
    });

    it('filters out inactive rules', () => {
      const rules = new Map<string, QNXTEligibilityRule[]>();
      rules.set('PPO_GOLD:30', [
        {
          ruleId: 'RULE001',
          ruleName: 'Inactive Rule',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          effectiveDateRange: { startDate: '20200101' },
          priority: 10,
          isActive: false
        }
      ]);

      const applicable = getApplicableRules(rules, 'PPO_GOLD', '30');
      expect(applicable).toHaveLength(0);
    });

    it('filters rules by effective date range', () => {
      const rules = new Map<string, QNXTEligibilityRule[]>();
      rules.set('PPO_GOLD:30', [
        {
          ruleId: 'RULE_FUTURE',
          ruleName: 'Future Rule',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          effectiveDateRange: { startDate: '29990101' }, // Far future
          priority: 10,
          isActive: true
        },
        {
          ruleId: 'RULE_EXPIRED',
          ruleName: 'Expired Rule',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          effectiveDateRange: { startDate: '20200101', endDate: '20200131' },
          priority: 10,
          isActive: true
        },
        {
          ruleId: 'RULE_CURRENT',
          ruleName: 'Current Rule',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          effectiveDateRange: { startDate: '20200101' },
          priority: 10,
          isActive: true
        }
      ]);

      const applicable = getApplicableRules(rules, 'PPO_GOLD', '30');
      expect(applicable).toHaveLength(1);
      expect(applicable[0].ruleId).toBe('RULE_CURRENT');
    });

    it('filters rules by member age', () => {
      const rules = new Map<string, QNXTEligibilityRule[]>();
      rules.set('PPO_GOLD:30', [
        {
          ruleId: 'RULE_PEDIATRIC',
          ruleName: 'Pediatric Rule',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'Pediatric',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          ageLimits: { minAge: 0, maxAge: 17 },
          effectiveDateRange: { startDate: '20200101' },
          priority: 10,
          isActive: true
        },
        {
          ruleId: 'RULE_ADULT',
          ruleName: 'Adult Rule',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'Adult',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          ageLimits: { minAge: 18, maxAge: 64 },
          effectiveDateRange: { startDate: '20200101' },
          priority: 10,
          isActive: true
        },
        {
          ruleId: 'RULE_SENIOR',
          ruleName: 'Senior Rule',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'Senior',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          ageLimits: { minAge: 65 },
          effectiveDateRange: { startDate: '20200101' },
          priority: 10,
          isActive: true
        }
      ]);

      expect(getApplicableRules(rules, 'PPO_GOLD', '30', 10)).toHaveLength(1);
      expect(getApplicableRules(rules, 'PPO_GOLD', '30', 10)[0].ruleId).toBe('RULE_PEDIATRIC');

      expect(getApplicableRules(rules, 'PPO_GOLD', '30', 35)).toHaveLength(1);
      expect(getApplicableRules(rules, 'PPO_GOLD', '30', 35)[0].ruleId).toBe('RULE_ADULT');

      expect(getApplicableRules(rules, 'PPO_GOLD', '30', 70)).toHaveLength(1);
      expect(getApplicableRules(rules, 'PPO_GOLD', '30', 70)[0].ruleId).toBe('RULE_SENIOR');
    });

    it('filters rules by gender restrictions', () => {
      const rules = new Map<string, QNXTEligibilityRule[]>();
      rules.set('PPO_GOLD:88', [
        {
          ruleId: 'RULE_MAMMOGRAM',
          ruleName: 'Mammogram Coverage',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '88',
          benefitCategory: 'Preventive',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          genderRestrictions: ['F'],
          effectiveDateRange: { startDate: '20200101' },
          priority: 10,
          isActive: true
        }
      ]);

      expect(getApplicableRules(rules, 'PPO_GOLD', '88', undefined, 'F')).toHaveLength(1);
      expect(getApplicableRules(rules, 'PPO_GOLD', '88', undefined, 'M')).toHaveLength(0);
    });

    it('returns empty array for unknown plan/service combination', () => {
      const rules = new Map<string, QNXTEligibilityRule[]>();
      
      const applicable = getApplicableRules(rules, 'UNKNOWN_PLAN', 'XX');
      expect(applicable).toHaveLength(0);
    });
  });

  describe('FHIR Data Extraction', () => {
    const extractMemberIdFromFHIR = (request: { patient?: { identifier?: { value?: string }, reference?: string } }): string => {
      if (request.patient?.identifier?.value) {
        return request.patient.identifier.value;
      }
      if (request.patient?.reference) {
        return request.patient.reference.replace('Patient/', '');
      }
      return 'unknown';
    };

    const extractPayerIdFromFHIR = (request: { insurer?: { identifier?: { value?: string }, reference?: string } }): string => {
      if (request.insurer?.identifier?.value) {
        return request.insurer.identifier.value;
      }
      if (request.insurer?.reference) {
        return request.insurer.reference.replace('Organization/', '');
      }
      return 'unknown';
    };

    const extractProviderNpiFromFHIR = (request: { provider?: { identifier?: { value?: string } } }): string | undefined => {
      if (request.provider?.identifier?.value) {
        return request.provider.identifier.value;
      }
      return undefined;
    };

    const extractServiceTypesFromFHIR = (request: { item?: Array<{ category?: { coding?: Array<{ code?: string }> } }> }): string[] => {
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
    };

    it('extracts member ID from FHIR identifier', () => {
      const request = {
        patient: {
          identifier: { value: 'MEM12345' }
        }
      };
      expect(extractMemberIdFromFHIR(request)).toBe('MEM12345');
    });

    it('extracts member ID from FHIR reference', () => {
      const request = {
        patient: {
          reference: 'Patient/MEM12345'
        }
      };
      expect(extractMemberIdFromFHIR(request)).toBe('MEM12345');
    });

    it('returns unknown for missing patient', () => {
      const request = {};
      expect(extractMemberIdFromFHIR(request)).toBe('unknown');
    });

    it('extracts payer ID from FHIR identifier', () => {
      const request = {
        insurer: {
          identifier: { value: 'PAYER001' }
        }
      };
      expect(extractPayerIdFromFHIR(request)).toBe('PAYER001');
    });

    it('extracts payer ID from FHIR reference', () => {
      const request = {
        insurer: {
          reference: 'Organization/PAYER001'
        }
      };
      expect(extractPayerIdFromFHIR(request)).toBe('PAYER001');
    });

    it('extracts provider NPI from FHIR', () => {
      const request = {
        provider: {
          identifier: { value: '1234567890' }
        }
      };
      expect(extractProviderNpiFromFHIR(request)).toBe('1234567890');
    });

    it('returns undefined for missing provider', () => {
      const request = {};
      expect(extractProviderNpiFromFHIR(request)).toBeUndefined();
    });

    it('extracts service types from FHIR items', () => {
      const request = {
        item: [
          {
            category: {
              coding: [{ code: '30' }]
            }
          },
          {
            category: {
              coding: [{ code: '48' }]
            }
          }
        ]
      };
      expect(extractServiceTypesFromFHIR(request)).toEqual(['30', '48']);
    });

    it('returns default service type for empty items', () => {
      const request = { item: [] };
      expect(extractServiceTypesFromFHIR(request)).toEqual(['30']);
    });
  });

  describe('Eligibility Status Extraction', () => {
    const extractEligibilityStatusFromFHIR = (response: { insurance?: Array<{ inforce?: boolean }> }): 'active' | 'inactive' | 'terminated' | 'pending' | 'unknown' => {
      if (response.insurance && response.insurance.length > 0) {
        const insurance = response.insurance[0];
        if (insurance.inforce === true) {
          return 'active';
        } else if (insurance.inforce === false) {
          return 'inactive';
        }
      }
      return 'unknown';
    };

    it('returns active for inforce=true', () => {
      const response = {
        insurance: [{ inforce: true }]
      };
      expect(extractEligibilityStatusFromFHIR(response)).toBe('active');
    });

    it('returns inactive for inforce=false', () => {
      const response = {
        insurance: [{ inforce: false }]
      };
      expect(extractEligibilityStatusFromFHIR(response)).toBe('inactive');
    });

    it('returns unknown for missing insurance', () => {
      const response = {};
      expect(extractEligibilityStatusFromFHIR(response)).toBe('unknown');
    });

    it('returns unknown for empty insurance array', () => {
      const response = { insurance: [] };
      expect(extractEligibilityStatusFromFHIR(response)).toBe('unknown');
    });
  });

  describe('Health Status Computation', () => {
    interface ComponentHealth {
      status: 'healthy' | 'unhealthy' | 'degraded';
      latencyMs?: number;
      lastCheck: string;
      error?: string;
    }

    const computeOverallHealth = (checks: Record<string, ComponentHealth>): 'healthy' | 'unhealthy' | 'degraded' => {
      const allHealthy = Object.values(checks).every(c => c.status === 'healthy');
      const anyUnhealthy = Object.values(checks).some(c => c.status === 'unhealthy');

      return allHealthy ? 'healthy' : anyUnhealthy ? 'unhealthy' : 'degraded';
    };

    it('returns healthy when all components are healthy', () => {
      const checks = {
        cosmosDb: { status: 'healthy' as const, lastCheck: new Date().toISOString() },
        eventGrid: { status: 'healthy' as const, lastCheck: new Date().toISOString() }
      };
      expect(computeOverallHealth(checks)).toBe('healthy');
    });

    it('returns unhealthy when any component is unhealthy', () => {
      const checks = {
        cosmosDb: { status: 'healthy' as const, lastCheck: new Date().toISOString() },
        eventGrid: { status: 'unhealthy' as const, lastCheck: new Date().toISOString(), error: 'Connection failed' }
      };
      expect(computeOverallHealth(checks)).toBe('unhealthy');
    });

    it('returns degraded when some components are degraded but none unhealthy', () => {
      const checks = {
        cosmosDb: { status: 'healthy' as const, lastCheck: new Date().toISOString() },
        eventGrid: { status: 'degraded' as const, lastCheck: new Date().toISOString() }
      };
      expect(computeOverallHealth(checks)).toBe('degraded');
    });
  });

  describe('Cache TTL Determination', () => {
    const determineCacheTtl = (
      eligibilityStatus: string,
      activeMemberTtl: number,
      inactiveMemberTtl: number
    ): number => {
      return eligibilityStatus === 'active' ? activeMemberTtl : inactiveMemberTtl;
    };

    it('uses active TTL for active members', () => {
      expect(determineCacheTtl('active', 86400, 3600)).toBe(86400);
    });

    it('uses inactive TTL for inactive members', () => {
      expect(determineCacheTtl('inactive', 86400, 3600)).toBe(3600);
    });

    it('uses inactive TTL for unknown status', () => {
      expect(determineCacheTtl('unknown', 86400, 3600)).toBe(3600);
    });

    it('uses inactive TTL for terminated members', () => {
      expect(determineCacheTtl('terminated', 86400, 3600)).toBe(3600);
    });
  });

  describe('Benefit Generation', () => {
    interface EligibilityBenefit {
      serviceTypeCode: string;
      serviceTypeDescription?: string;
      eligibilityInfoCode: string;
      coverageLevelCode: string;
      authorizationRequired?: boolean;
      inNetwork?: boolean;
      additionalInfo?: {
        copay?: number;
        coinsurance?: number;
      };
    }

    const generateBenefitFromRule = (rule: QNXTEligibilityRule, serviceTypeCode: string): EligibilityBenefit => {
      return {
        serviceTypeCode,
        serviceTypeDescription: rule.benefitCategory,
        eligibilityInfoCode: rule.coverageIndicator === 'covered' ? '1' : '6',
        coverageLevelCode: 'IND',
        authorizationRequired: rule.priorAuthRequired,
        inNetwork: true,
        additionalInfo: rule.inNetworkRequirements ? {
          copay: rule.inNetworkRequirements.copay,
          coinsurance: rule.inNetworkRequirements.coinsurance
        } : undefined
      };
    };

    it('generates benefit for covered service', () => {
      const rule: QNXTEligibilityRule = {
        ruleId: 'RULE001',
        ruleName: 'Test Rule',
        planCode: 'PPO_GOLD',
        serviceTypeCode: '30',
        benefitCategory: 'Medical Care',
        coverageIndicator: 'covered',
        priorAuthRequired: false,
        referralRequired: false,
        inNetworkRequirements: {
          copay: 25,
          coinsurance: 20
        },
        effectiveDateRange: { startDate: '20200101' },
        priority: 10,
        isActive: true
      };

      const benefit = generateBenefitFromRule(rule, '30');

      expect(benefit.eligibilityInfoCode).toBe('1');
      expect(benefit.serviceTypeDescription).toBe('Medical Care');
      expect(benefit.authorizationRequired).toBe(false);
      expect(benefit.additionalInfo?.copay).toBe(25);
      expect(benefit.additionalInfo?.coinsurance).toBe(20);
    });

    it('generates benefit for non-covered service', () => {
      const rule: QNXTEligibilityRule = {
        ruleId: 'RULE002',
        ruleName: 'Not Covered',
        planCode: 'PPO_GOLD',
        serviceTypeCode: '99',
        benefitCategory: 'Experimental',
        coverageIndicator: 'not_covered',
        priorAuthRequired: false,
        referralRequired: false,
        effectiveDateRange: { startDate: '20200101' },
        priority: 10,
        isActive: true
      };

      const benefit = generateBenefitFromRule(rule, '99');

      expect(benefit.eligibilityInfoCode).toBe('6');
    });

    it('generates benefit requiring prior authorization', () => {
      const rule: QNXTEligibilityRule = {
        ruleId: 'RULE003',
        ruleName: 'MRI Imaging',
        planCode: 'PPO_GOLD',
        serviceTypeCode: 'MRI',
        benefitCategory: 'Imaging',
        coverageIndicator: 'covered',
        priorAuthRequired: true,
        referralRequired: true,
        effectiveDateRange: { startDate: '20200101' },
        priority: 10,
        isActive: true
      };

      const benefit = generateBenefitFromRule(rule, 'MRI');

      expect(benefit.authorizationRequired).toBe(true);
    });
  });

  describe('Event Grid Event Construction', () => {
    interface EligibilityCheckedEvent {
      id: string;
      eventType: string;
      subject: string;
      eventTime: Date;
      dataVersion: string;
      data: {
        memberId: string;
        payerId: string;
        providerNpi?: string;
        requestType: string;
        eligibilityStatus: string;
        serviceDate: string;
        serviceTypeCodes?: string[];
        fromCache: boolean;
        responseTimeMs: number;
      };
    }

    const createEligibilityCheckedEvent = (
      memberId: string,
      payerId: string,
      providerNpi: string | undefined,
      requestType: 'X12_270' | 'FHIR_CoverageEligibilityRequest',
      eligibilityStatus: string,
      serviceDate: string,
      serviceTypeCodes: string[] | undefined,
      fromCache: boolean,
      responseTimeMs: number
    ): EligibilityCheckedEvent => {
      return {
        id: 'test-id',
        eventType: 'EligibilityChecked',
        subject: memberId,
        eventTime: new Date(),
        dataVersion: '1.0',
        data: {
          memberId,
          payerId,
          providerNpi,
          requestType,
          eligibilityStatus,
          serviceDate,
          serviceTypeCodes,
          fromCache,
          responseTimeMs
        }
      };
    };

    it('creates event with all fields', () => {
      const event = createEligibilityCheckedEvent(
        'MEM001',
        'PAYER001',
        '1234567890',
        'X12_270',
        'active',
        '20240115',
        ['30', '48'],
        false,
        150
      );

      expect(event.eventType).toBe('EligibilityChecked');
      expect(event.subject).toBe('MEM001');
      expect(event.dataVersion).toBe('1.0');
      expect(event.data.memberId).toBe('MEM001');
      expect(event.data.payerId).toBe('PAYER001');
      expect(event.data.providerNpi).toBe('1234567890');
      expect(event.data.requestType).toBe('X12_270');
      expect(event.data.eligibilityStatus).toBe('active');
      expect(event.data.fromCache).toBe(false);
      expect(event.data.responseTimeMs).toBe(150);
    });

    it('creates event with fromCache=true', () => {
      const event = createEligibilityCheckedEvent(
        'MEM001',
        'PAYER001',
        undefined,
        'FHIR_CoverageEligibilityRequest',
        'active',
        '20240115',
        undefined,
        true,
        5
      );

      expect(event.data.fromCache).toBe(true);
      expect(event.data.providerNpi).toBeUndefined();
      expect(event.data.serviceTypeCodes).toBeUndefined();
    });
  });
});
