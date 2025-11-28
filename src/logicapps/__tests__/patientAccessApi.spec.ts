import * as fs from 'fs';
import * as path from 'path';
import SwaggerParser from '@apidevtools/swagger-parser';
import { generatePatientAccessSpecs } from '../../../scripts/generate-patient-access-specs';
import { AzureFHIRValidator } from '../../fhir/azure-fhir-validator';
import { ProviderAccessApi } from '../../fhir/provider-access-api';
import {
  patientsToBundle,
  coverageToBundle,
  claimsToBundle,
  paymentsToEobBundle,
  PaymentDocument
} from '../../fhir/patient-access-mapper';
import { evaluateRateLimit } from '../patient-access-rate-limit';
import { QnxtPatient, QnxtClaim } from '../../fhir/provider-access-api';

describe('Patient Access API specifications', () => {
  const outputDir = path.join(process.cwd(), 'generated', 'infra', 'patient-access-api');
  const capabilityStatementPath = path.join(outputDir, 'capabilitystatement.json');
  const openApiPath = path.join(outputDir, 'openapi.yaml');

  beforeAll(() => {
    generatePatientAccessSpecs();
  });

  it('produces a CapabilityStatement that passes simulated FHIR validation', async () => {
    const validator = new AzureFHIRValidator({
      baseUrl: 'https://example.fhir.local',
      accessToken: 'fake-token'
    });
    const capabilityStatement = JSON.parse(fs.readFileSync(capabilityStatementPath, 'utf-8'));
    const result = await validator.validateResource(capabilityStatement);
    expect(result.valid).toBe(true);
    expect(result.errors).toHaveLength(0);
  });

  it('emits an OpenAPI specification that passes swagger-parser validation', async () => {
    await SwaggerParser.validate(openApiPath);
  });
});

describe('Patient Access API helper logic', () => {
  const selfLink = 'https://example.azurelogicapps.net/Patient?patient=MEM123';
  const member: QnxtPatient = {
    memberId: 'MEM123',
    firstName: 'Test',
    lastName: 'Member',
    dob: '1980-01-01',
    gender: 'F'
  };

  it('builds Bundle resources with a self link and redacted entries', () => {
    const bundle = patientsToBundle([member], selfLink);
    expect(bundle.resourceType).toBe('Bundle');
    expect(bundle.link?.[0]?.url).toBe(selfLink);
    const patient = bundle.entry?.[0]?.resource as any;
    expect(patient?.resourceType).toBe('Patient');
    expect(patient?.name?.[0]?.family).toBe('***');
  });

  it('redacts PHI through mapping helpers', () => {
    const coverageBundle = coverageToBundle([member], selfLink);
    const coverage = coverageBundle.entry?.[0]?.resource;
    expect(coverage?.resourceType).toBe('Coverage');
    // Subscriber ID is retained, but address/telecom are redacted via shared helper
    expect((coverage as any).subscriberId).toBe('MEM123');
  });

  it('converts claim and payment documents', () => {
    const claim: QnxtClaim = {
      claimId: 'CLM001',
      memberId: 'MEM123',
      providerId: 'NPI123',
      claimType: 'Professional',
      serviceDate: '2024-01-15',
      diagnosisCodes: ['E11.9'],
      procedureCodes: ['99213'],
      totalCharged: 250,
      totalPaid: 200,
      status: 'active'
    };
    const claimBundle = claimsToBundle([claim], 'https://example/Claim');
    expect(claimBundle.entry?.[0]?.resource?.resourceType).toBe('Claim');

    const payment: PaymentDocument = {
      paymentId: 'PAY001',
      claimId: 'CLM001',
      memberId: 'MEM123',
      paymentDate: '2024-02-01',
      totalPaid: 200
    };
    const eobBundle = paymentsToEobBundle([payment], 'https://example/ExplanationOfBenefit');
    expect(eobBundle.entry?.[0]?.resource?.resourceType).toBe('ExplanationOfBenefit');
  });

  it('enforces rate limits when the threshold is exceeded', () => {
    const now = new Date('2024-01-01T00:00:00Z');
    const evaluation = evaluateRateLimit(
      'MEM123-2024010100',
      'MEM123',
      { id: 'MEM123-2024010100', memberId: 'MEM123', count: 1000, ttl: 3600, expiresAt: now.toISOString() },
      1000,
      3600,
      now
    );
    expect(evaluation.breached).toBe(true);
    expect(evaluation.remaining).toBe(0);
  });

  it('resets the rate limit window when the prior entry expired', () => {
    const expired = new Date('2024-01-01T00:00:00Z');
    const now = new Date('2024-01-01T02:00:00Z');
    const evaluation = evaluateRateLimit(
      'MEM123-2024010102',
      'MEM123',
      { id: 'MEM123-2024010100', memberId: 'MEM123', count: 500, ttl: 3600, expiresAt: expired.toISOString() },
      1000,
      3600,
      now
    );
    expect(evaluation.breached).toBe(false);
    expect(evaluation.remaining).toBe(999);
  });

  it('produces FHIR OperationOutcome responses for errors', () => {
    const api = new ProviderAccessApi();
    const outcome = api.createErrorOutcome('error', 'too-costly', 'Rate limit triggered');
    expect(outcome.resourceType).toBe('OperationOutcome');
    expect(outcome.issue?.[0]?.code).toBe('too-costly');
  });
});
