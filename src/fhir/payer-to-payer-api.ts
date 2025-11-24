/**
 * CMS-0057-F Payer-to-Payer Data Exchange API Implementation
 * 
 * Implements FHIR R4 Bulk Data Access for Payer-to-Payer exchange as mandated by CMS-0057-F.
 * Supports bulk export/import of FHIR resources with member consent validation,
 * Azure Service Bus integration for async workflows, and Azure Data Lake storage.
 * 
 * References:
 * - CMS-0057-F Final Rule (Payer-to-Payer Data Exchange)
 * - HL7 Da Vinci PDex Implementation Guide
 * - FHIR Bulk Data Access IG (http://hl7.org/fhir/uv/bulkdata/)
 * - US Core IG v3.1.1+ (FHIR R4)
 */

import { 
  Patient, 
  Claim, 
  Encounter, 
  ExplanationOfBenefit, 
  ServiceRequest,
  Consent,
  Bundle,
  Resource
} from 'fhir/r4';
import { ServiceBusClient, ServiceBusSender, ServiceBusReceiver } from '@azure/service-bus';
import { BlobServiceClient, ContainerClient, BlockBlobClient } from '@azure/storage-blob';
import { DefaultAzureCredential } from '@azure/identity';

/**
 * Configuration for Payer-to-Payer API
 */
export interface PayerToPayerConfig {
  /** Azure Service Bus connection string or namespace */
  serviceBusConnectionString?: string;
  serviceBusNamespace?: string;
  
  /** Azure Storage connection string or account name */
  storageConnectionString?: string;
  storageAccountName?: string;
  storageContainerName: string;
  
  /** Topic/Queue names for async workflows */
  exportRequestTopic: string;
  importRequestTopic: string;
  
  /** Base URL for this payer's FHIR server */
  fhirServerBaseUrl: string;
  
  /** Organization identifier for this payer */
  payerOrganizationId: string;
}

/**
 * Bulk export request parameters
 */
export interface BulkExportRequest {
  /** Member IDs to export (must have consent) */
  patientIds: string[];
  
  /** Resource types to export */
  resourceTypes: ('Patient' | 'Claim' | 'Encounter' | 'ExplanationOfBenefit' | 'ServiceRequest')[];
  
  /** Optional date range filters */
  since?: Date;
  until?: Date;
  
  /** Requesting payer organization ID */
  requestingPayerId: string;
  
  /** Export job ID */
  exportId: string;
}

/**
 * Bulk import request parameters
 */
export interface BulkImportRequest {
  /** Import job ID */
  importId: string;
  
  /** Blob URLs for NDJSON files to import */
  ndjsonBlobUrls: string[];
  
  /** Source payer organization ID */
  sourcePayerId: string;
  
  /** Flag to enable reconciliation */
  enableReconciliation: boolean;
}

/**
 * Member consent record for data sharing
 */
export interface MemberConsent {
  /** Member/Patient ID */
  patientId: string;
  
  /** Target payer organization ID */
  targetPayerId: string;
  
  /** Consent granted date */
  consentDate: Date;
  
  /** Consent expiration date (if any) */
  expirationDate?: Date;
  
  /** Consent status */
  status: 'active' | 'revoked' | 'expired';
  
  /** Resource types authorized for sharing */
  authorizedResourceTypes: string[];
}

/**
 * Bulk export result
 */
export interface BulkExportResult {
  exportId: string;
  status: 'in-progress' | 'completed' | 'failed';
  timestamp: Date;
  ndjsonFiles: {
    resourceType: string;
    url: string;
    count: number;
  }[];
  error?: string;
}

/**
 * Bulk import result
 */
export interface BulkImportResult {
  importId: string;
  status: 'in-progress' | 'completed' | 'failed';
  timestamp: Date;
  resourcesImported: {
    resourceType: string;
    count: number;
    duplicatesSkipped: number;
  }[];
  error?: string;
}

/**
 * Main Payer-to-Payer API class
 */
export class PayerToPayerAPI {
  private config: PayerToPayerConfig;
  private serviceBusClient?: ServiceBusClient;
  private blobServiceClient?: BlobServiceClient;
  private containerClient?: ContainerClient;
  private consentRegistry: Map<string, MemberConsent[]> = new Map();

  constructor(config: PayerToPayerConfig) {
    this.config = config;
    this.initializeClients();
  }

  /**
   * Initialize Azure clients
   */
  private initializeClients(): void {
    // Initialize Service Bus client
    if (this.config.serviceBusConnectionString) {
      this.serviceBusClient = new ServiceBusClient(this.config.serviceBusConnectionString);
    } else if (this.config.serviceBusNamespace) {
      const credential = new DefaultAzureCredential();
      this.serviceBusClient = new ServiceBusClient(
        `${this.config.serviceBusNamespace}.servicebus.windows.net`,
        credential
      );
    }

    // Initialize Storage client
    if (this.config.storageConnectionString) {
      this.blobServiceClient = BlobServiceClient.fromConnectionString(
        this.config.storageConnectionString
      );
    } else if (this.config.storageAccountName) {
      const credential = new DefaultAzureCredential();
      this.blobServiceClient = new BlobServiceClient(
        `https://${this.config.storageAccountName}.blob.core.windows.net`,
        credential
      );
    }

    if (this.blobServiceClient) {
      this.containerClient = this.blobServiceClient.getContainerClient(
        this.config.storageContainerName
      );
    }
  }

  /**
   * Register member consent for data sharing
   */
  async registerConsent(consent: MemberConsent): Promise<void> {
    const key = `${consent.patientId}:${consent.targetPayerId}`;
    const existingConsents = this.consentRegistry.get(key) || [];
    
    // Update or add consent
    const index = existingConsents.findIndex(c => 
      c.patientId === consent.patientId && c.targetPayerId === consent.targetPayerId
    );
    
    if (index >= 0) {
      existingConsents[index] = consent;
    } else {
      existingConsents.push(consent);
    }
    
    this.consentRegistry.set(key, existingConsents);
  }

  /**
   * Check if member has consented to share data with target payer
   */
  async hasConsent(patientId: string, targetPayerId: string, resourceTypes: string[]): Promise<boolean> {
    const key = `${patientId}:${targetPayerId}`;
    const consents = this.consentRegistry.get(key) || [];
    
    const activeConsent = consents.find(c => {
      if (c.status !== 'active') return false;
      if (c.expirationDate && c.expirationDate < new Date()) return false;
      
      // Check if all requested resource types are authorized
      return resourceTypes.every(rt => c.authorizedResourceTypes.includes(rt));
    });
    
    return !!activeConsent;
  }

  /**
   * Initiate bulk export of FHIR resources
   * Validates consent before exporting member data
   */
  async initiateExport(request: BulkExportRequest): Promise<BulkExportResult> {
    if (!this.serviceBusClient || !this.containerClient) {
      throw new Error('Azure clients not initialized');
    }

    // Validate consent for all patients
    const authorizedPatients: string[] = [];
    for (const patientId of request.patientIds) {
      const hasConsent = await this.hasConsent(
        patientId, 
        request.requestingPayerId, 
        request.resourceTypes
      );
      
      if (hasConsent) {
        authorizedPatients.push(patientId);
      }
    }

    if (authorizedPatients.length === 0) {
      throw new Error('No patients with valid consent for export');
    }

    // Create export result structure
    const result: BulkExportResult = {
      exportId: request.exportId,
      status: 'in-progress',
      timestamp: new Date(),
      ndjsonFiles: []
    };

    // Queue export request to Service Bus for async processing
    const sender = this.serviceBusClient.createSender(this.config.exportRequestTopic);
    try {
      await sender.sendMessages({
        body: {
          exportId: request.exportId,
          patientIds: authorizedPatients,
          resourceTypes: request.resourceTypes,
          since: request.since?.toISOString(),
          until: request.until?.toISOString(),
          requestingPayerId: request.requestingPayerId
        }
      });
    } finally {
      await sender.close();
    }

    return result;
  }

  /**
   * Execute bulk export (called by async worker)
   */
  async executeBulkExport(request: BulkExportRequest): Promise<BulkExportResult> {
    if (!this.containerClient) {
      throw new Error('Storage client not initialized');
    }

    const result: BulkExportResult = {
      exportId: request.exportId,
      status: 'in-progress',
      timestamp: new Date(),
      ndjsonFiles: []
    };

    try {
      // Export each resource type
      for (const resourceType of request.resourceTypes) {
        const resources = await this.fetchResourcesForExport(
          resourceType,
          request.patientIds,
          request.since,
          request.until
        );

        if (resources.length > 0) {
          const ndjson = this.serializeToNDJSON(resources);
          const blobName = `exports/${request.exportId}/${resourceType}.ndjson`;
          const blobClient = this.containerClient.getBlockBlobClient(blobName);

          await blobClient.upload(ndjson, Buffer.byteLength(ndjson), {
            blobHTTPHeaders: { blobContentType: 'application/fhir+ndjson' }
          });

          result.ndjsonFiles.push({
            resourceType,
            url: blobClient.url,
            count: resources.length
          });
        }
      }

      result.status = 'completed';
    } catch (error) {
      result.status = 'failed';
      result.error = error instanceof Error ? error.message : 'Unknown error';
    }

    return result;
  }

  /**
   * Initiate bulk import of FHIR resources
   */
  async initiateImport(request: BulkImportRequest): Promise<BulkImportResult> {
    if (!this.serviceBusClient) {
      throw new Error('Service Bus client not initialized');
    }

    const result: BulkImportResult = {
      importId: request.importId,
      status: 'in-progress',
      timestamp: new Date(),
      resourcesImported: []
    };

    // Queue import request to Service Bus for async processing
    const sender = this.serviceBusClient.createSender(this.config.importRequestTopic);
    try {
      await sender.sendMessages({
        body: {
          importId: request.importId,
          ndjsonBlobUrls: request.ndjsonBlobUrls,
          sourcePayerId: request.sourcePayerId,
          enableReconciliation: request.enableReconciliation
        }
      });
    } finally {
      await sender.close();
    }

    return result;
  }

  /**
   * Execute bulk import with reconciliation (called by async worker)
   */
  async executeBulkImport(request: BulkImportRequest): Promise<BulkImportResult> {
    if (!this.containerClient) {
      throw new Error('Storage client not initialized');
    }

    const result: BulkImportResult = {
      importId: request.importId,
      status: 'in-progress',
      timestamp: new Date(),
      resourcesImported: []
    };

    try {
      for (const blobUrl of request.ndjsonBlobUrls) {
        const { resourceType, resources } = await this.downloadAndParseNDJSON(blobUrl);
        
        let importedCount = 0;
        let duplicatesSkipped = 0;

        for (const resource of resources) {
          if (request.enableReconciliation) {
            const isDuplicate = await this.checkForDuplicate(resource);
            if (isDuplicate) {
              duplicatesSkipped++;
              continue;
            }
          }

          // Import resource (in real implementation, this would save to FHIR server)
          await this.importResource(resource);
          importedCount++;
        }

        result.resourcesImported.push({
          resourceType,
          count: importedCount,
          duplicatesSkipped
        });
      }

      result.status = 'completed';
    } catch (error) {
      result.status = 'failed';
      result.error = error instanceof Error ? error.message : 'Unknown error';
    }

    return result;
  }

  /**
   * Fetch resources for export from FHIR server
   */
  private async fetchResourcesForExport(
    resourceType: string,
    patientIds: string[],
    since?: Date,
    until?: Date
  ): Promise<Resource[]> {
    // In real implementation, this would query the FHIR server
    // For now, return empty array (will be implemented with actual FHIR server integration)
    return [];
  }

  /**
   * Serialize FHIR resources to NDJSON format
   */
  private serializeToNDJSON(resources: Resource[]): string {
    return resources.map(r => JSON.stringify(r)).join('\n');
  }

  /**
   * Download and parse NDJSON file from blob storage
   */
  private async downloadAndParseNDJSON(blobUrl: string): Promise<{
    resourceType: string;
    resources: Resource[];
  }> {
    if (!this.containerClient) {
      throw new Error('Storage client not initialized');
    }

    // Extract blob name from URL
    const urlParts = blobUrl.split('/');
    const blobName = urlParts.slice(-3).join('/'); // e.g., "exports/exportId/Patient.ndjson"
    const resourceType = blobName.split('/').pop()?.replace('.ndjson', '') || 'Unknown';

    const blobClient = this.containerClient.getBlockBlobClient(blobName);
    const downloadResponse = await blobClient.download();
    
    if (!downloadResponse.readableStreamBody) {
      throw new Error('Failed to download blob');
    }

    const ndjsonContent = await this.streamToString(downloadResponse.readableStreamBody);
    const resources = ndjsonContent
      .split('\n')
      .filter(line => line.trim())
      .map(line => JSON.parse(line) as Resource);

    return { resourceType, resources };
  }

  /**
   * Convert readable stream to string
   */
  private async streamToString(readableStream: NodeJS.ReadableStream): Promise<string> {
    return new Promise((resolve, reject) => {
      const chunks: Buffer[] = [];
      readableStream.on('data', (data) => {
        chunks.push(Buffer.isBuffer(data) ? data : Buffer.from(data));
      });
      readableStream.on('end', () => {
        resolve(Buffer.concat(chunks).toString('utf8'));
      });
      readableStream.on('error', reject);
    });
  }

  /**
   * Check for duplicate resources using member matching logic per PDex IG
   */
  private async checkForDuplicate(resource: Resource): Promise<boolean> {
    // Implement member matching per Da Vinci PDex IG
    // Match on: identifier, name, DOB, gender for Patient resources
    // For other resources, match on patient reference + date + identifiers
    
    if (resource.resourceType === 'Patient') {
      return this.checkDuplicatePatient(resource as Patient);
    }
    
    // For claims, encounters, EOBs - match on patient + date + claim ID
    return false; // Placeholder
  }

  /**
   * Check for duplicate Patient using PDex member matching algorithm
   */
  private checkDuplicatePatient(patient: Patient): boolean {
    // PDex member matching criteria:
    // 1. Match on Member ID if available
    // 2. Match on SSN if available
    // 3. Match on combination: Last Name + First Name + DOB + Gender
    
    // This is a simplified implementation
    // Real implementation would query FHIR server for matching patients
    return false;
  }

  /**
   * Import resource to FHIR server
   */
  private async importResource(resource: Resource): Promise<void> {
    // In real implementation, this would POST/PUT to FHIR server
    // For now, this is a placeholder
  }

  /**
   * Close all connections
   */
  async close(): Promise<void> {
    if (this.serviceBusClient) {
      await this.serviceBusClient.close();
    }
  }
}

/**
 * Utility: Generate synthetic FHIR Patient for testing
 */
export function generateSyntheticPatient(memberId: string): Patient {
  return {
    resourceType: 'Patient',
    id: memberId,
    meta: {
      profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient']
    },
    identifier: [
      {
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
            code: 'MB',
            display: 'Member Number'
          }]
        },
        system: 'http://example.org/fhir/sid/member-id',
        value: memberId
      }
    ],
    active: true,
    name: [{
      use: 'official',
      family: 'TestPatient',
      given: ['Synthetic']
    }],
    gender: 'unknown',
    birthDate: '2000-01-01'
  };
}

/**
 * Utility: Generate synthetic FHIR Claim for testing
 */
export function generateSyntheticClaim(patientId: string, claimId: string): Claim {
  return {
    resourceType: 'Claim',
    id: claimId,
    status: 'active',
    type: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/claim-type',
        code: 'professional'
      }]
    },
    use: 'claim',
    patient: {
      reference: `Patient/${patientId}`
    },
    created: new Date().toISOString(),
    provider: {
      reference: 'Organization/example-provider'
    },
    priority: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/processpriority',
        code: 'normal'
      }]
    },
    insurance: [{
      sequence: 1,
      focal: true,
      coverage: {
        reference: 'Coverage/example'
      }
    }]
  };
}

/**
 * Utility: Validate US Core Patient profile compliance
 */
export function validateUSCorePatient(patient: Patient): { valid: boolean; errors: string[] } {
  const errors: string[] = [];

  // Required fields per US Core Patient profile
  if (!patient.identifier || patient.identifier.length === 0) {
    errors.push('Patient must have at least one identifier');
  }

  if (!patient.name || patient.name.length === 0) {
    errors.push('Patient must have at least one name');
  }

  if (!patient.gender) {
    errors.push('Patient must have gender');
  }

  // Check for US Core profile in meta
  const hasUSCoreProfile = patient.meta?.profile?.some(
    p => p.includes('us-core-patient')
  );
  if (!hasUSCoreProfile) {
    errors.push('Patient must declare US Core Patient profile in meta.profile');
  }

  return {
    valid: errors.length === 0,
    errors
  };
}
