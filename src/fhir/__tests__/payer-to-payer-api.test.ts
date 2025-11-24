import { PayerToPayerAPI, PayerToPayerConfig, BulkExportRequest, BulkImportRequest, MemberMatchRequest } from '../payer-to-payer-api';
import { Patient, Claim } from 'fhir/r4';

// Mock Azure SDK modules
jest.mock('@azure/storage-blob');
jest.mock('@azure/service-bus');

import { BlobServiceClient, ContainerClient, BlockBlobClient } from '@azure/storage-blob';
import { ServiceBusClient, ServiceBusSender } from '@azure/service-bus';

describe('PayerToPayerAPI', () => {
  let api: PayerToPayerAPI;
  let mockConfig: PayerToPayerConfig;
  let mockContainerClient: jest.Mocked<ContainerClient>;
  let mockBlockBlobClient: jest.Mocked<BlockBlobClient>;
  let mockSender: jest.Mocked<ServiceBusSender>;

  beforeEach(() => {
    mockConfig = {
      storageConnectionString: 'DefaultEndpointsProtocol=https;AccountName=test;AccountKey=test==;EndpointSuffix=core.windows.net',
      serviceBusConnectionString: 'Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=test=',
      bulkDataContainerName: 'payer-exchange',
      exportNotificationTopic: 'export-notifications',
      importRequestTopic: 'import-requests',
      sourcePayer: 'PAYER-SOURCE',
      targetPayer: 'PAYER-TARGET'
    };

    // Mock Azure Blob Storage
    const createMockStream = (): any => ({
      on: jest.fn((event: string, handler: any) => {
        if (event === 'data') {
          handler(Buffer.from('{"resourceType":"Patient","id":"test"}'));
        }
        if (event === 'end') {
          handler();
        }
        return createMockStream();
      })
    });

    mockBlockBlobClient = {
      upload: jest.fn().mockResolvedValue({}),
      download: jest.fn().mockResolvedValue({
        readableStreamBody: createMockStream()
      }),
      url: 'https://test.blob.core.windows.net/payer-exchange/test.ndjson'
    } as any;

    mockContainerClient = {
      createIfNotExists: jest.fn().mockResolvedValue({}),
      getBlockBlobClient: jest.fn().mockReturnValue(mockBlockBlobClient)
    } as any;

    (BlobServiceClient.fromConnectionString as jest.Mock) = jest.fn().mockReturnValue({
      getContainerClient: jest.fn().mockReturnValue(mockContainerClient)
    });

    // Mock Azure Service Bus
    mockSender = {
      sendMessages: jest.fn().mockResolvedValue({}),
      close: jest.fn().mockResolvedValue({})
    } as any;

    (ServiceBusClient as jest.MockedClass<typeof ServiceBusClient>) = jest.fn().mockImplementation(() => ({
      createSender: jest.fn().mockReturnValue(mockSender),
      close: jest.fn().mockResolvedValue({})
    } as any));

    api = new PayerToPayerAPI(mockConfig);
  });

  afterEach(async () => {
    await api.close();
  });

  describe('Bulk Data Export', () => {
    it('exports patient data to NDJSON format', async () => {
      const samplePatients: Patient[] = [
        {
          resourceType: 'Patient',
          id: 'patient-001',
          meta: {
            profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient']
          },
          identifier: [{
            system: 'http://hospital.example.org',
            value: 'MRN123456'
          }],
          name: [{
            family: 'Doe',
            given: ['John']
          }],
          gender: 'male',
          birthDate: '1985-06-15'
        },
        {
          resourceType: 'Patient',
          id: 'patient-002',
          meta: {
            profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient']
          },
          identifier: [{
            system: 'http://hospital.example.org',
            value: 'MRN654321'
          }],
          name: [{
            family: 'Smith',
            given: ['Jane']
          }],
          gender: 'female',
          birthDate: '1990-03-21'
        }
      ];

      const request: BulkExportRequest = {
        resourceTypes: ['Patient'],
        includeHistorical: true
      };

      const result = await api.exportBulkData(request, [
        { resourceType: 'Patient', data: samplePatients }
      ]);

      expect(result.status).toBe('completed');
      expect(result.exportId).toBeDefined();
      expect(result.outputUrls).toHaveLength(1);
      expect(result.outputUrls[0].resourceType).toBe('Patient');
      expect(result.outputUrls[0].count).toBe(2);
      expect(mockContainerClient.createIfNotExists).toHaveBeenCalled();
      expect(mockBlockBlobClient.upload).toHaveBeenCalled();
      expect(mockSender.sendMessages).toHaveBeenCalled();
    });

    it('exports multiple resource types', async () => {
      const samplePatient: Patient = {
        resourceType: 'Patient',
        id: 'patient-001',
        name: [{ family: 'Doe', given: ['John'] }],
        gender: 'male'
      };

      const sampleClaim: Claim = {
        resourceType: 'Claim',
        id: 'claim-001',
        status: 'active',
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/claim-type',
            code: 'professional'
          }]
        },
        use: 'claim',
        patient: { reference: 'Patient/patient-001' },
        created: '2024-01-15',
        provider: { reference: 'Organization/org-001' },
        priority: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/processpriority',
            code: 'normal'
          }]
        },
        insurance: [{
          sequence: 1,
          focal: true,
          coverage: { reference: 'Coverage/cov-001' }
        }]
      };

      const request: BulkExportRequest = {
        resourceTypes: ['Patient', 'Claim']
      };

      const result = await api.exportBulkData(request, [
        { resourceType: 'Patient', data: [samplePatient] },
        { resourceType: 'Claim', data: [sampleClaim] }
      ]);

      expect(result.status).toBe('completed');
      expect(result.outputUrls).toHaveLength(2);
      expect(result.outputUrls.map(u => u.resourceType)).toEqual(['Patient', 'Claim']);
    });

    it('filters data by date range', async () => {
      const patient: Patient = {
        resourceType: 'Patient',
        id: 'patient-001',
        meta: {
          lastUpdated: '2024-06-15T10:00:00Z'
        },
        name: [{ family: 'Doe', given: ['John'] }],
        gender: 'male'
      };

      const request: BulkExportRequest = {
        resourceTypes: ['Patient'],
        startDate: '2024-01-01',
        endDate: '2024-12-31'
      };

      const result = await api.exportBulkData(request, [
        { resourceType: 'Patient', data: [patient] }
      ]);

      expect(result.status).toBe('completed');
      expect(result.outputUrls[0].count).toBe(1);
    });
  });

  describe('Bulk Data Import', () => {
    it('imports NDJSON patient data', async () => {
      const request: BulkImportRequest = {
        inputUrls: [
          {
            resourceType: 'Patient',
            url: 'https://test.blob.core.windows.net/payer-exchange/exports/test-id/Patient.ndjson'
          }
        ],
        deduplicate: false,
        validateUsCore: false
      };

      const result = await api.importBulkData(request);

      expect(result.status).toBe('completed');
      expect(result.importId).toBeDefined();
      expect(result.imported).toHaveLength(1);
      expect(result.imported[0].resourceType).toBe('Patient');
    });

    it('deduplicates resources during import', async () => {
      // Mock duplicate data
      const createDuplicateStream = (): any => ({
        on: jest.fn((event: string, handler: any) => {
          if (event === 'data') {
            const ndjson = '{"resourceType":"Patient","id":"dup-001"}\n{"resourceType":"Patient","id":"dup-001"}\n{"resourceType":"Patient","id":"unique-002"}';
            handler(Buffer.from(ndjson));
          }
          if (event === 'end') {
            handler();
          }
          return createDuplicateStream();
        })
      });

      mockBlockBlobClient.download = jest.fn().mockResolvedValue({
        readableStreamBody: createDuplicateStream()
      });

      const request: BulkImportRequest = {
        inputUrls: [
          {
            resourceType: 'Patient',
            url: 'https://test.blob.core.windows.net/payer-exchange/test.ndjson'
          }
        ],
        deduplicate: true,
        validateUsCore: false
      };

      const result = await api.importBulkData(request);

      expect(result.status).toBe('completed');
      expect(result.imported[0].count).toBe(2); // 2 unique out of 3
      expect(result.imported[0].duplicatesSkipped).toBe(1);
    });

    it('validates US Core profiles during import', async () => {
      const createInvalidStream = (): any => ({
        on: jest.fn((event: string, handler: any) => {
          if (event === 'data') {
            // Patient missing required fields
            const ndjson = '{"resourceType":"Patient","id":"invalid-001"}';
            handler(Buffer.from(ndjson));
          }
          if (event === 'end') {
            handler();
          }
          return createInvalidStream();
        })
      });

      mockBlockBlobClient.download = jest.fn().mockResolvedValue({
        readableStreamBody: createInvalidStream()
      });

      const request: BulkImportRequest = {
        inputUrls: [
          {
            resourceType: 'Patient',
            url: 'https://test.blob.core.windows.net/payer-exchange/test.ndjson'
          }
        ],
        deduplicate: false,
        validateUsCore: true
      };

      const result = await api.importBulkData(request);

      expect(result.status).toBe('partial');
      expect(result.errors).toBeDefined();
      expect(result.errors!.length).toBeGreaterThan(0);
    });
  });

  describe('Member Consent Management', () => {
    it('creates active consent for data exchange', async () => {
      const consent = await api.manageMemberConsent('patient-001', true);

      expect(consent.patientId).toBe('patient-001');
      expect(consent.status).toBe('active');
      expect(consent.sourcePayer).toBe('PAYER-SOURCE');
      expect(consent.targetPayer).toBe('PAYER-TARGET');
      expect(consent.fhirConsent.resourceType).toBe('Consent');
      expect(consent.fhirConsent.status).toBe('active');
      expect(consent.fhirConsent.provision?.type).toBe('permit');
    });

    it('creates inactive consent when consent not given', async () => {
      const consent = await api.manageMemberConsent('patient-002', false);

      expect(consent.status).toBe('inactive');
      expect(consent.fhirConsent.status).toBe('inactive');
      expect(consent.fhirConsent.provision?.type).toBe('deny');
    });

    it('includes CMS policy reference in consent', async () => {
      const consent = await api.manageMemberConsent('patient-001', true);

      expect(consent.fhirConsent.policy).toBeDefined();
      expect(consent.fhirConsent.policy![0].uri).toContain('cms-0057');
    });
  });

  describe('Member Matching', () => {
    it('matches patient by exact demographics', async () => {
      const sourcePatient: Patient = {
        resourceType: 'Patient',
        id: 'source-001',
        identifier: [{
          system: 'http://hl7.org/fhir/sid/us-ssn',
          value: '123-45-6789'
        }],
        name: [{
          family: 'Doe',
          given: ['John']
        }],
        gender: 'male',
        birthDate: '1985-06-15',
        address: [{
          city: 'Seattle',
          state: 'WA',
          postalCode: '98101'
        }],
        telecom: [{
          system: 'phone',
          value: '206-555-0100'
        }]
      };

      const candidatePatients: Patient[] = [
        {
          resourceType: 'Patient',
          id: 'target-001',
          identifier: [{
            system: 'http://hl7.org/fhir/sid/us-ssn',
            value: '123-45-6789'
          }],
          name: [{
            family: 'Doe',
            given: ['John']
          }],
          gender: 'male',
          birthDate: '1985-06-15',
          address: [{
            city: 'Seattle',
            state: 'WA',
            postalCode: '98101'
          }],
          telecom: [{
            system: 'phone',
            value: '206-555-0100'
          }]
        }
      ];

      const request: MemberMatchRequest = {
        patient: sourcePatient
      };

      const result = await api.matchMember(request, candidatePatients);

      expect(result.matched).toBe(true);
      expect(result.confidence).toBeGreaterThan(0.8);
      expect(result.matchedPatientId).toBe('target-001');
      expect(result.matchDetails?.matchedOn).toContain('name');
      expect(result.matchDetails?.matchedOn).toContain('birthDate');
      expect(result.matchDetails?.matchedOn).toContain('gender');
      expect(result.matchDetails?.matchedOn).toContain('identifier');
    });

    it('does not match with low confidence', async () => {
      const sourcePatient: Patient = {
        resourceType: 'Patient',
        id: 'source-001',
        name: [{
          family: 'Doe',
          given: ['John']
        }],
        gender: 'male',
        birthDate: '1985-06-15'
      };

      const candidatePatients: Patient[] = [
        {
          resourceType: 'Patient',
          id: 'target-002',
          name: [{
            family: 'Smith',
            given: ['Jane']
          }],
          gender: 'female',
          birthDate: '1990-03-21'
        }
      ];

      const request: MemberMatchRequest = {
        patient: sourcePatient
      };

      const result = await api.matchMember(request, candidatePatients);

      expect(result.matched).toBe(false);
      expect(result.confidence).toBeLessThan(0.8);
      expect(result.matchedPatientId).toBeUndefined();
    });

    it('calculates confidence score with partial demographics', async () => {
      const sourcePatient: Patient = {
        resourceType: 'Patient',
        id: 'source-001',
        name: [{
          family: 'Doe',
          given: ['John']
        }],
        gender: 'male',
        birthDate: '1985-06-15'
      };

      const candidatePatients: Patient[] = [
        {
          resourceType: 'Patient',
          id: 'target-001',
          name: [{
            family: 'Doe',
            given: ['John']
          }],
          gender: 'male',
          birthDate: '1985-06-15'
        }
      ];

      const request: MemberMatchRequest = {
        patient: sourcePatient
      };

      const result = await api.matchMember(request, candidatePatients);

      // name + birthDate + gender = 0.65, below 0.8 threshold
      expect(result.matched).toBe(false);
      expect(result.confidence).toBe(0.65); // name (0.25) + birthDate (0.25) + gender (0.15) = 0.65
      expect(result.matchedPatientId).toBeUndefined();
    });
  });

  describe('Error Handling', () => {
    it('handles export errors gracefully', async () => {
      mockBlockBlobClient.upload = jest.fn().mockRejectedValue(new Error('Upload failed'));

      const request: BulkExportRequest = {
        resourceTypes: ['Patient']
      };

      const result = await api.exportBulkData(request, [
        { resourceType: 'Patient', data: [] }
      ]);

      expect(result.status).toBe('error');
      expect(result.error).toBeDefined();
      expect(result.error?.issue[0].diagnostics).toContain('Export failed');
    });

    it('handles import errors gracefully', async () => {
      mockBlockBlobClient.download = jest.fn().mockRejectedValue(new Error('Download failed'));

      const request: BulkImportRequest = {
        inputUrls: [
          {
            resourceType: 'Patient',
            url: 'https://test.blob.core.windows.net/invalid/test.ndjson'
          }
        ]
      };

      const result = await api.importBulkData(request);

      expect(result.status).toBe('error');
      expect(result.errors).toBeDefined();
      expect(result.errors![0].issue[0].diagnostics).toContain('Import failed');
    });
  });

  describe('NDJSON Format', () => {
    it('converts resources to NDJSON format correctly', async () => {
      const patients: Patient[] = [
        {
          resourceType: 'Patient',
          id: 'patient-001',
          name: [{ family: 'Doe', given: ['John'] }]
        },
        {
          resourceType: 'Patient',
          id: 'patient-002',
          name: [{ family: 'Smith', given: ['Jane'] }]
        }
      ];

      const request: BulkExportRequest = {
        resourceTypes: ['Patient']
      };

      await api.exportBulkData(request, [
        { resourceType: 'Patient', data: patients }
      ]);

      const uploadCall = mockBlockBlobClient.upload.mock.calls[0];
      const ndjsonContent = uploadCall[0] as string;

      const lines = ndjsonContent.split('\n');
      expect(lines).toHaveLength(2);
      
      const parsed1 = JSON.parse(lines[0]);
      expect(parsed1.resourceType).toBe('Patient');
      expect(parsed1.id).toBe('patient-001');

      const parsed2 = JSON.parse(lines[1]);
      expect(parsed2.resourceType).toBe('Patient');
      expect(parsed2.id).toBe('patient-002');
    });
  });
});
