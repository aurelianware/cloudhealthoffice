import { Resource } from 'fhir/r4';
import { AzureFHIRValidator, createAzureFHIRValidator, quickValidate } from '../azure-fhir-validator';

describe('AzureFHIRValidator', () => {
  const baseConfig = {
    baseUrl: 'https://example.azurehealthcareapis.com',
    accessToken: 'fake-token'
  };

  const buildValidator = () => new AzureFHIRValidator(baseConfig);

  it('returns warnings when resource has no declared profiles', async () => {
    const validator = buildValidator();
    const resource: Resource = {
      resourceType: 'Patient'
    } as Resource;

    const result = await validator.validateResource(resource);

    expect(result.valid).toBe(true);
    expect(result.errors).toHaveLength(0);
    expect(result.warnings.some(warning => warning.message.includes('does not declare conformance'))).toBe(true);
  });

  it('captures profile validation errors for US Core patient profile', async () => {
    const validator = buildValidator();
    const resource: Resource = {
      resourceType: 'Patient',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient']
      }
    } as Resource;

    const result = await validator.validateResource(resource);

    expect(result.valid).toBe(false);
    expect(result.errors).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ path: 'identifier' }),
        expect.objectContaining({ path: 'name' }),
        expect.objectContaining({ path: 'gender' })
      ])
    );
  });

  it('validates a supported US Core resource via helper', async () => {
    const validator = buildValidator();
    const resource: Resource = {
      resourceType: 'Patient',
      identifier: [{ system: 'http://hl7.org/fhir/sid/us-ssn', value: '123456789' }],
      name: [{ family: 'Doe', given: ['Jane'] }],
      gender: 'female'
    } as Resource;

    const result = await validator.validateUSCoreProfile(resource);

    expect(result.valid).toBe(true);
    expect(result.errors).toHaveLength(0);
  });

  it('returns error when US Core profile is unavailable for a resource type', async () => {
    const validator = buildValidator();
    const observation: Resource = {
      resourceType: 'Observation'
    } as Resource;

    const result = await validator.validateUSCoreProfile(observation);

    expect(result.valid).toBe(false);
    expect(result.errors[0].message).toContain('No US Core profile defined');
  });

  it('validates Da Vinci PAS ServiceRequest profile requirements', async () => {
    const validator = buildValidator();
    const servRequest: Resource = {
      resourceType: 'ServiceRequest',
      meta: {
        profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest']
      }
    } as Resource;

    const result = await validator.validateDaVinciProfile(servRequest, 'pas');

    expect(result.valid).toBe(false);
    expect(result.errors).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ path: 'status' }),
        expect.objectContaining({ path: 'intent' }),
        expect.objectContaining({ path: 'code' }),
        expect.objectContaining({ path: 'subject' })
      ])
    );
  });

  it('supports batch validation of multiple resources', async () => {
    const validator = buildValidator();
    const resources: Resource[] = [
      { resourceType: 'Patient' } as Resource,
      {
        resourceType: 'Patient',
        meta: { profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient'] }
      } as Resource
    ];

    const results = await validator.batchValidate(resources);

    expect(results).toHaveLength(2);
    expect(results[0].valid).toBe(true);
    expect(results[1].valid).toBe(false);
  });

  it('maps unexpected errors into validation responses', async () => {
    const validator = buildValidator();
    const resource: Resource = { resourceType: 'Patient' } as Resource;
    const spy = jest.spyOn(validator as any, 'simulateAzureFHIRValidation').mockRejectedValueOnce(new Error('upstream failure'));

    const result = await validator.validateResource(resource);

    expect(result.valid).toBe(false);
    expect(result.errors[0].message).toContain('Validation request failed');
    spy.mockRestore();
  });

  it('createAzureFHIRValidator returns working validator instance', async () => {
    const validator = createAzureFHIRValidator(baseConfig);
    const result = await validator.validateResource({ resourceType: 'Patient' } as Resource);

    expect(result.valid).toBe(true);
  });

  it('quickValidate yields boolean validity flag', async () => {
    const success = await quickValidate({ resourceType: 'Patient' } as Resource, baseConfig);

    expect(success).toBe(true);
  });
});
