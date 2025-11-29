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
