import {
  PayerToPayerAPI,
  PayerToPayerConfig,
  BulkExportRequest,
  BulkImportRequest,
  MemberConsent,
  generateSyntheticPatient,
  generateSyntheticClaim,
  validateUSCorePatient
} from '../payer-to-payer-api';
import { Patient } from 'fhir/r4';

describe('PayerToPayerAPI', () => {
  let api: PayerToPayerAPI;
  let config: PayerToPayerConfig;

  beforeEach(() => {
    // Test configuration without actual Azure connections
    config = {
      storageConnectionString: 'UseDevelopmentStorage=true',
      storageContainerName: 'test-container',
      serviceBusConnectionString: 'Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=test',
      exportRequestTopic: 'export-requests',
      importRequestTopic: 'import-requests',
      fhirServerBaseUrl: 'https://test-fhir.example.com',
      payerOrganizationId: 'PAYER001'
    };

    api = new PayerToPayerAPI(config);
  });

  afterEach(async () => {
    await api.close();
  });

  describe('Consent Management', () => {
    it('should register member consent successfully', async () => {
      const consent: MemberConsent = {
        patientId: 'PAT001',
        targetPayerId: 'PAYER002',
        consentDate: new Date('2024-01-01'),
        status: 'active',
        authorizedResourceTypes: ['Patient', 'Claim', 'ExplanationOfBenefit']
      };

      await api.registerConsent(consent);

      const hasConsent = await api.hasConsent('PAT001', 'PAYER002', ['Patient', 'Claim']);
      expect(hasConsent).toBe(true);
    });

    it('should deny access when consent is not present', async () => {
      const hasConsent = await api.hasConsent('PAT999', 'PAYER002', ['Patient']);
      expect(hasConsent).toBe(false);
    });

    it('should deny access when consent is revoked', async () => {
      const consent: MemberConsent = {
        patientId: 'PAT002',
        targetPayerId: 'PAYER002',
        consentDate: new Date('2024-01-01'),
        status: 'revoked',
        authorizedResourceTypes: ['Patient']
      };

      await api.registerConsent(consent);

      const hasConsent = await api.hasConsent('PAT002', 'PAYER002', ['Patient']);
      expect(hasConsent).toBe(false);
    });

    it('should deny access when consent is expired', async () => {
      const consent: MemberConsent = {
        patientId: 'PAT003',
        targetPayerId: 'PAYER002',
        consentDate: new Date('2023-01-01'),
        expirationDate: new Date('2023-12-31'),
        status: 'active',
        authorizedResourceTypes: ['Patient']
      };

      await api.registerConsent(consent);

      const hasConsent = await api.hasConsent('PAT003', 'PAYER002', ['Patient']);
      expect(hasConsent).toBe(false);
    });

    it('should deny access when requested resource types are not authorized', async () => {
      const consent: MemberConsent = {
        patientId: 'PAT004',
        targetPayerId: 'PAYER002',
        consentDate: new Date('2024-01-01'),
        status: 'active',
        authorizedResourceTypes: ['Patient'] // Only Patient authorized
      };

      await api.registerConsent(consent);

      const hasConsent = await api.hasConsent('PAT004', 'PAYER002', ['Patient', 'Claim']);
      expect(hasConsent).toBe(false);
    });

    it('should allow access when all requested resource types are authorized', async () => {
      const consent: MemberConsent = {
        patientId: 'PAT005',
        targetPayerId: 'PAYER002',
        consentDate: new Date('2024-01-01'),
        status: 'active',
        authorizedResourceTypes: ['Patient', 'Claim', 'Encounter', 'ExplanationOfBenefit']
      };

      await api.registerConsent(consent);

      const hasConsent = await api.hasConsent('PAT005', 'PAYER002', ['Patient', 'Claim']);
      expect(hasConsent).toBe(true);
    });

    it('should update existing consent when registered again', async () => {
      const consent1: MemberConsent = {
        patientId: 'PAT006',
        targetPayerId: 'PAYER002',
        consentDate: new Date('2024-01-01'),
        status: 'active',
        authorizedResourceTypes: ['Patient']
      };

      await api.registerConsent(consent1);

      const consent2: MemberConsent = {
        patientId: 'PAT006',
        targetPayerId: 'PAYER002',
        consentDate: new Date('2024-02-01'),
        status: 'active',
        authorizedResourceTypes: ['Patient', 'Claim', 'Encounter']
      };

      await api.registerConsent(consent2);

      const hasConsent = await api.hasConsent('PAT006', 'PAYER002', ['Patient', 'Claim']);
      expect(hasConsent).toBe(true);
    });
  });

  describe('Bulk Export', () => {
    it('should reject export when no patients have consent', async () => {
      const exportRequest: BulkExportRequest = {
        exportId: 'EXP001',
        patientIds: ['PAT100', 'PAT101'],
        resourceTypes: ['Patient', 'Claim'],
        requestingPayerId: 'PAYER002'
      };

      await expect(api.initiateExport(exportRequest)).rejects.toThrow(
        'No patients with valid consent for export'
      );
    });

    it('should initiate export for patients with consent', async () => {
      // Register consent for one patient
      const consent: MemberConsent = {
        patientId: 'PAT200',
        targetPayerId: 'PAYER002',
        consentDate: new Date('2024-01-01'),
        status: 'active',
        authorizedResourceTypes: ['Patient', 'Claim']
      };
      await api.registerConsent(consent);

      const exportRequest: BulkExportRequest = {
        exportId: 'EXP002',
        patientIds: ['PAT200', 'PAT201'], // One with consent, one without
        resourceTypes: ['Patient', 'Claim'],
        requestingPayerId: 'PAYER002'
      };

      // Mock Service Bus to avoid actual connection
      const mockSender = {
        sendMessages: jest.fn().mockResolvedValue(undefined),
        close: jest.fn().mockResolvedValue(undefined)
      };
      
      if ((api as any).serviceBusClient) {
        (api as any).serviceBusClient.createSender = jest.fn().mockReturnValue(mockSender);
      }

      const result = await api.initiateExport(exportRequest);

      expect(result).toBeDefined();
      expect(result.exportId).toBe('EXP002');
      expect(result.status).toBe('in-progress');
      expect(result.timestamp).toBeInstanceOf(Date);
    });

    it('should filter out patients without consent during export', async () => {
      // Register consent for multiple patients
      const consent1: MemberConsent = {
        patientId: 'PAT300',
        targetPayerId: 'PAYER002',
        consentDate: new Date('2024-01-01'),
        status: 'active',
        authorizedResourceTypes: ['Patient', 'Claim', 'Encounter']
      };
      await api.registerConsent(consent1);

      const consent2: MemberConsent = {
        patientId: 'PAT301',
        targetPayerId: 'PAYER002',
        consentDate: new Date('2024-01-01'),
        status: 'active',
        authorizedResourceTypes: ['Patient', 'Claim', 'Encounter']
      };
      await api.registerConsent(consent2);

      const exportRequest: BulkExportRequest = {
        exportId: 'EXP003',
        patientIds: ['PAT300', 'PAT301', 'PAT302'], // Two with consent, one without
        resourceTypes: ['Patient', 'Claim'],
        requestingPayerId: 'PAYER002'
      };

      // Mock Service Bus
      const mockSender = {
        sendMessages: jest.fn().mockResolvedValue(undefined),
        close: jest.fn().mockResolvedValue(undefined)
      };
      
      if ((api as any).serviceBusClient) {
        (api as any).serviceBusClient.createSender = jest.fn().mockReturnValue(mockSender);
      }

      await api.initiateExport(exportRequest);

      // Verify only authorized patients are in the message
      expect(mockSender.sendMessages).toHaveBeenCalled();
      const sentMessage = mockSender.sendMessages.mock.calls[0][0];
      expect(sentMessage.body.patientIds).toHaveLength(2);
      expect(sentMessage.body.patientIds).toContain('PAT300');
      expect(sentMessage.body.patientIds).toContain('PAT301');
      expect(sentMessage.body.patientIds).not.toContain('PAT302');
    });
  });

  describe('Bulk Import', () => {
    it('should initiate bulk import successfully', async () => {
      const importRequest: BulkImportRequest = {
        importId: 'IMP001',
        ndjsonBlobUrls: [
          'https://storage.example.com/exports/exp001/Patient.ndjson',
          'https://storage.example.com/exports/exp001/Claim.ndjson'
        ],
        sourcePayerId: 'PAYER002',
        enableReconciliation: true
      };

      // Mock Service Bus
      const mockSender = {
        sendMessages: jest.fn().mockResolvedValue(undefined),
        close: jest.fn().mockResolvedValue(undefined)
      };
      
      if ((api as any).serviceBusClient) {
        (api as any).serviceBusClient.createSender = jest.fn().mockReturnValue(mockSender);
      }

      const result = await api.initiateImport(importRequest);

      expect(result).toBeDefined();
      expect(result.importId).toBe('IMP001');
      expect(result.status).toBe('in-progress');
      expect(result.timestamp).toBeInstanceOf(Date);
      expect(mockSender.sendMessages).toHaveBeenCalled();
    });

    it('should include reconciliation flag in import message', async () => {
      const importRequest: BulkImportRequest = {
        importId: 'IMP002',
        ndjsonBlobUrls: ['https://storage.example.com/exports/exp001/Patient.ndjson'],
        sourcePayerId: 'PAYER002',
        enableReconciliation: true
      };

      // Mock Service Bus
      const mockSender = {
        sendMessages: jest.fn().mockResolvedValue(undefined),
        close: jest.fn().mockResolvedValue(undefined)
      };
      
      if ((api as any).serviceBusClient) {
        (api as any).serviceBusClient.createSender = jest.fn().mockReturnValue(mockSender);
      }

      await api.initiateImport(importRequest);

      expect(mockSender.sendMessages).toHaveBeenCalled();
      const sentMessage = mockSender.sendMessages.mock.calls[0][0];
      expect(sentMessage.body.enableReconciliation).toBe(true);
      expect(sentMessage.body.sourcePayerId).toBe('PAYER002');
    });
  });

  describe('Synthetic Data Generation', () => {
    it('should generate valid synthetic Patient', () => {
      const patient = generateSyntheticPatient('TEST001');

      expect(patient.resourceType).toBe('Patient');
      expect(patient.id).toBe('TEST001');
      expect(patient.identifier).toBeDefined();
      expect(patient.identifier!.length).toBeGreaterThan(0);
      expect(patient.name).toBeDefined();
      expect(patient.gender).toBe('unknown');
      expect(patient.birthDate).toBe('2000-01-01');
    });

    it('should generate synthetic Patient with US Core profile', () => {
      const patient = generateSyntheticPatient('TEST002');

      expect(patient.meta?.profile).toBeDefined();
      expect(patient.meta!.profile![0]).toContain('us-core-patient');
    });

    it('should generate valid synthetic Claim', () => {
      const claim = generateSyntheticClaim('PAT001', 'CLM001');

      expect(claim.resourceType).toBe('Claim');
      expect(claim.id).toBe('CLM001');
      expect(claim.patient.reference).toBe('Patient/PAT001');
      expect(claim.status).toBe('active');
      expect(claim.type).toBeDefined();
      expect(claim.insurance).toBeDefined();
      expect(claim.insurance!.length).toBeGreaterThan(0);
    });

    it('should generate synthetic Claim with required fields', () => {
      const claim = generateSyntheticClaim('PAT002', 'CLM002');

      expect(claim.use).toBe('claim');
      expect(claim.created).toBeDefined();
      expect(claim.provider).toBeDefined();
      expect(claim.priority).toBeDefined();
    });
  });

  describe('US Core Patient Validation', () => {
    it('should validate compliant US Core Patient', () => {
      const patient = generateSyntheticPatient('TEST003');
      const result = validateUSCorePatient(patient);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('should reject Patient without identifier', () => {
      const patient: Patient = {
        resourceType: 'Patient',
        name: [{ family: 'Doe', given: ['John'] }],
        gender: 'male'
      };

      const result = validateUSCorePatient(patient);

      expect(result.valid).toBe(false);
      expect(result.errors).toContain('Patient must have at least one identifier');
    });

    it('should reject Patient without name', () => {
      const patient: Patient = {
        resourceType: 'Patient',
        identifier: [{ value: 'TEST001' }],
        gender: 'male'
      };

      const result = validateUSCorePatient(patient);

      expect(result.valid).toBe(false);
      expect(result.errors).toContain('Patient must have at least one name');
    });

    it('should reject Patient without gender', () => {
      const patient: Patient = {
        resourceType: 'Patient',
        identifier: [{ value: 'TEST001' }],
        name: [{ family: 'Doe', given: ['John'] }]
      };

      const result = validateUSCorePatient(patient);

      expect(result.valid).toBe(false);
      expect(result.errors).toContain('Patient must have gender');
    });

    it('should reject Patient without US Core profile', () => {
      const patient: Patient = {
        resourceType: 'Patient',
        identifier: [{ value: 'TEST001' }],
        name: [{ family: 'Doe', given: ['John'] }],
        gender: 'male'
      };

      const result = validateUSCorePatient(patient);

      expect(result.valid).toBe(false);
      expect(result.errors).toContain('Patient must declare US Core Patient profile in meta.profile');
    });

    it('should validate Patient with all required fields', () => {
      const patient: Patient = {
        resourceType: 'Patient',
        meta: {
          profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient']
        },
        identifier: [{ value: 'TEST004' }],
        name: [{ family: 'Smith', given: ['Jane'] }],
        gender: 'female',
        birthDate: '1990-01-01'
      };

      const result = validateUSCorePatient(patient);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });
  });

  describe('NDJSON Serialization', () => {
    it('should serialize resources to NDJSON format', () => {
      const patient1 = generateSyntheticPatient('PAT001');
      const patient2 = generateSyntheticPatient('PAT002');
      const resources = [patient1, patient2];

      const ndjson = (api as any).serializeToNDJSON(resources);

      const lines = ndjson.split('\n');
      expect(lines).toHaveLength(2);
      
      const parsed1 = JSON.parse(lines[0]);
      const parsed2 = JSON.parse(lines[1]);
      
      expect(parsed1.id).toBe('PAT001');
      expect(parsed2.id).toBe('PAT002');
    });

    it('should handle empty resource array', () => {
      const ndjson = (api as any).serializeToNDJSON([]);
      expect(ndjson).toBe('');
    });

    it('should serialize Claims to NDJSON', () => {
      const claim1 = generateSyntheticClaim('PAT001', 'CLM001');
      const claim2 = generateSyntheticClaim('PAT002', 'CLM002');
      const resources = [claim1, claim2];

      const ndjson = (api as any).serializeToNDJSON(resources);

      const lines = ndjson.split('\n');
      expect(lines).toHaveLength(2);
      
      const parsed1 = JSON.parse(lines[0]);
      const parsed2 = JSON.parse(lines[1]);
      
      expect(parsed1.resourceType).toBe('Claim');
      expect(parsed2.resourceType).toBe('Claim');
    });
  });

  describe('Error Handling', () => {
    it('should throw error when Azure clients not initialized for export', async () => {
      const uninitializedApi = new PayerToPayerAPI({
        storageContainerName: 'test',
        exportRequestTopic: 'test',
        importRequestTopic: 'test',
        fhirServerBaseUrl: 'https://test.com',
        payerOrganizationId: 'TEST'
      });

      const exportRequest: BulkExportRequest = {
        exportId: 'EXP999',
        patientIds: ['PAT001'],
        resourceTypes: ['Patient'],
        requestingPayerId: 'PAYER002'
      };

      await expect(uninitializedApi.initiateExport(exportRequest)).rejects.toThrow(
        'Azure clients not initialized'
      );

      await uninitializedApi.close();
    });

    it('should throw error when Azure clients not initialized for import', async () => {
      const uninitializedApi = new PayerToPayerAPI({
        storageContainerName: 'test',
        exportRequestTopic: 'test',
        importRequestTopic: 'test',
        fhirServerBaseUrl: 'https://test.com',
        payerOrganizationId: 'TEST'
      });

      const importRequest: BulkImportRequest = {
        importId: 'IMP999',
        ndjsonBlobUrls: ['https://test.com/file.ndjson'],
        sourcePayerId: 'PAYER002',
        enableReconciliation: true
      };

      await expect(uninitializedApi.initiateImport(importRequest)).rejects.toThrow(
        'Service Bus client not initialized'
      );

      await uninitializedApi.close();
    });
  });
});
