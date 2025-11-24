/**
 * Payer-to-Payer API Implementation
 * 
 * Implements CMS-0057-F Payer-to-Payer data exchange requirements for secure
 * FHIR R4-compliant data exchange between payers during plan transitions.
 * 
 * Features:
 * - FHIR R4 bulk data export/import (NDJSON format per FHIR Bulk Data Access IG)
 * - Azure Service Bus integration for asynchronous workflows
 * - Azure Data Lake storage for bulk data
 * - Support for Patient, Claim, Encounter, ExplanationOfBenefit, PriorAuthorizationRequest
 * - Opt-in consent flows
 * - Data reconciliation and deduplication
 * - Member matching per HL7 Da Vinci PDex IG
 * - US Core profile validation
 * 
 * References:
 * - CMS-0057-F Final Rule (Payer-to-Payer Exchange)
 * - HL7 Da Vinci Payer Data Exchange (PDex) Implementation Guide
 * - FHIR Bulk Data Access IG (Flat FHIR)
 * - US Core Implementation Guide v3.1.1+
 * - FHIR R4.0.1
 */

import {
  Patient,
  Claim,
  Encounter,
  ExplanationOfBenefit,
  ServiceRequest,
  Consent,
  OperationOutcome
} from 'fhir/r4';
import { BlobServiceClient, ContainerClient } from '@azure/storage-blob';
import { ServiceBusClient, ServiceBusSender } from '@azure/service-bus';

/**
 * Supported FHIR resource types for payer-to-payer exchange
 */
export type PayerExchangeResourceType = 
  | 'Patient' 
  | 'Claim' 
  | 'Encounter' 
  | 'ExplanationOfBenefit' 
  | 'ServiceRequest';

/**
 * Configuration for payer-to-payer exchange
 */
export interface PayerToPayerConfig {
  /** Azure Storage connection string for Data Lake */
  storageConnectionString: string;
  /** Azure Service Bus connection string */
  serviceBusConnectionString: string;
  /** Data Lake container name for bulk exports */
  bulkDataContainerName: string;
  /** Service Bus topic for export notifications */
  exportNotificationTopic: string;
  /** Service Bus topic for import requests */
  importRequestTopic: string;
  /** Source payer identifier */
  sourcePayer: string;
  /** Target payer identifier */
  targetPayer: string;
}

/**
 * Bulk export request parameters
 */
export interface BulkExportRequest {
  /** Patient ID for single-patient export (optional) */
  patientId?: string;
  /** Resource types to export */
  resourceTypes: PayerExchangeResourceType[];
  /** Start date for data range (YYYY-MM-DD) */
  startDate?: string;
  /** End date for data range (YYYY-MM-DD) */
  endDate?: string;
  /** Include historical data (5 years per CMS requirement) */
  includeHistorical?: boolean;
}

/**
 * Bulk export status and results
 */
export interface BulkExportResult {
  /** Unique export job ID */
  exportId: string;
  /** Export status: processing, completed, error */
  status: 'processing' | 'completed' | 'error';
  /** Timestamp when export was initiated */
  initiatedAt: string;
  /** Timestamp when export completed */
  completedAt?: string;
  /** NDJSON file URLs in Data Lake */
  outputUrls: {
    resourceType: PayerExchangeResourceType;
    url: string;
    count: number;
  }[];
  /** Error information if status is 'error' */
  error?: OperationOutcome;
}

/**
 * Bulk import request parameters
 */
export interface BulkImportRequest {
  /** NDJSON file URLs to import */
  inputUrls: {
    resourceType: PayerExchangeResourceType;
    url: string;
  }[];
  /** Whether to perform deduplication */
  deduplicate?: boolean;
  /** Whether to validate against US Core profiles */
  validateUsCore?: boolean;
}

/**
 * Bulk import status and results
 */
export interface BulkImportResult {
  /** Unique import job ID */
  importId: string;
  /** Import status */
  status: 'processing' | 'completed' | 'error' | 'partial';
  /** Timestamp when import was initiated */
  initiatedAt: string;
  /** Timestamp when import completed */
  completedAt?: string;
  /** Count of resources imported by type */
  imported: {
    resourceType: PayerExchangeResourceType;
    count: number;
    duplicatesSkipped?: number;
  }[];
  /** Validation errors if any */
  errors?: OperationOutcome[];
}

/**
 * Member consent record for payer-to-payer exchange
 */
export interface MemberConsent {
  /** Patient/Member ID */
  patientId: string;
  /** Source payer ID */
  sourcePayer: string;
  /** Target payer ID */
  targetPayer: string;
  /** Consent status */
  status: 'active' | 'inactive' | 'revoked';
  /** Date consent was given */
  consentDate: string;
  /** Date consent expires (optional) */
  expirationDate?: string;
  /** FHIR Consent resource */
  fhirConsent: Consent;
}

/**
 * Member matching parameters per Da Vinci PDex IG
 */
export interface MemberMatchRequest {
  /** Patient demographics from source payer */
  patient: Patient;
  /** Coverage information (optional) */
  coverageToMatch?: {
    memberId: string;
    subscriberId?: string;
  };
}

/**
 * Member matching result
 */
export interface MemberMatchResult {
  /** Whether a match was found */
  matched: boolean;
  /** Confidence score (0-1) */
  confidence: number;
  /** Matched patient ID in target system */
  matchedPatientId?: string;
  /** Matched patient resource */
  matchedPatient?: Patient;
  /** Match details */
  matchDetails?: {
    matchedOn: string[];
    score: number;
  };
}

/**
 * Payer-to-Payer API implementation
 */
export class PayerToPayerAPI {
  private config: PayerToPayerConfig;
  private blobServiceClient: BlobServiceClient;
  private serviceBusClient: ServiceBusClient;
  private containerClient: ContainerClient;

  constructor(config: PayerToPayerConfig) {
    this.config = config;
    this.blobServiceClient = BlobServiceClient.fromConnectionString(config.storageConnectionString);
    this.serviceBusClient = new ServiceBusClient(config.serviceBusConnectionString);
    this.containerClient = this.blobServiceClient.getContainerClient(config.bulkDataContainerName);
  }

  /**
   * Initiate bulk data export for payer-to-payer exchange
   * Creates NDJSON files in Data Lake and publishes notification to Service Bus
   * 
   * @param request Export parameters
   * @param resources Array of FHIR resources to export
   * @returns Export result with status and output URLs
   */
  async exportBulkData(
    request: BulkExportRequest,
    resources: {
      resourceType: PayerExchangeResourceType;
      data: (Patient | Claim | Encounter | ExplanationOfBenefit | ServiceRequest)[];
    }[]
  ): Promise<BulkExportResult> {
    const exportId = this.generateExportId();
    const initiatedAt = new Date().toISOString();

    try {
      // Ensure container exists
      await this.containerClient.createIfNotExists();

      const outputUrls: BulkExportResult['outputUrls'] = [];

      // Export each resource type to NDJSON
      for (const resourceGroup of resources) {
        const { resourceType, data } = resourceGroup;

        // Filter by date range if specified
        let filteredData = data;
        if (request.startDate || request.endDate) {
          filteredData = this.filterByDateRange(data, request.startDate, request.endDate);
        }

        // Convert to NDJSON format
        const ndjson = this.convertToNDJSON(filteredData);

        // Upload to Data Lake
        const blobName = `exports/${exportId}/${resourceType}.ndjson`;
        const blockBlobClient = this.containerClient.getBlockBlobClient(blobName);
        await blockBlobClient.upload(ndjson, Buffer.byteLength(ndjson), {
          blobHTTPHeaders: { blobContentType: 'application/fhir+ndjson' }
        });

        outputUrls.push({
          resourceType,
          url: blockBlobClient.url,
          count: filteredData.length
        });
      }

      // Publish export completion notification to Service Bus
      const sender = this.serviceBusClient.createSender(this.config.exportNotificationTopic);
      await sender.sendMessages({
        body: {
          exportId,
          sourcePayer: this.config.sourcePayer,
          targetPayer: this.config.targetPayer,
          initiatedAt,
          completedAt: new Date().toISOString(),
          outputUrls
        }
      });
      await sender.close();

      return {
        exportId,
        status: 'completed',
        initiatedAt,
        completedAt: new Date().toISOString(),
        outputUrls
      };
    } catch (error) {
      return {
        exportId,
        status: 'error',
        initiatedAt,
        outputUrls: [],
        error: this.createOperationOutcome(
          'error',
          `Export failed: ${error instanceof Error ? error.message : 'Unknown error'}`
        )
      };
    }
  }

  /**
   * Import bulk data from another payer
   * Reads NDJSON files from Data Lake, performs deduplication and validation
   * 
   * @param request Import parameters with NDJSON URLs
   * @returns Import result with counts and status
   */
  async importBulkData(request: BulkImportRequest): Promise<BulkImportResult> {
    const importId = this.generateImportId();
    const initiatedAt = new Date().toISOString();

    try {
      const imported: BulkImportResult['imported'] = [];
      const errors: OperationOutcome[] = [];

      for (const input of request.inputUrls) {
        const { resourceType, url } = input;

        // Download NDJSON from Data Lake
        const blobName = this.extractBlobNameFromUrl(url);
        const blockBlobClient = this.containerClient.getBlockBlobClient(blobName);
        const downloadResponse = await blockBlobClient.download();
        const ndjsonContent = await this.streamToString(downloadResponse.readableStreamBody!);

        // Parse NDJSON
        const resources = this.parseNDJSON(ndjsonContent);

        // Validate against US Core profiles if requested
        if (request.validateUsCore) {
          const validationErrors = this.validateUsCoreProfiles(resources, resourceType);
          if (validationErrors.length > 0) {
            errors.push(...validationErrors);
          }
        }

        // Deduplicate if requested
        let finalResources = resources;
        let duplicatesSkipped = 0;
        if (request.deduplicate) {
          const { unique, duplicates } = this.deduplicateResources(resources);
          finalResources = unique;
          duplicatesSkipped = duplicates;
        }

        // Import resources (actual persistence would happen here)
        // This is a placeholder - real implementation would persist to database

        imported.push({
          resourceType,
          count: finalResources.length,
          duplicatesSkipped: request.deduplicate ? duplicatesSkipped : undefined
        });
      }

      return {
        importId,
        status: errors.length > 0 ? 'partial' : 'completed',
        initiatedAt,
        completedAt: new Date().toISOString(),
        imported,
        errors: errors.length > 0 ? errors : undefined
      };
    } catch (error) {
      return {
        importId,
        status: 'error',
        initiatedAt,
        completedAt: new Date().toISOString(),
        imported: [],
        errors: [
          this.createOperationOutcome(
            'error',
            `Import failed: ${error instanceof Error ? error.message : 'Unknown error'}`
          )
        ]
      };
    }
  }

  /**
   * Create or update member consent for payer-to-payer exchange
   * Implements opt-in consent flow per CMS requirements
   * 
   * @param patientId Patient/Member ID
   * @param consentGiven Whether consent is given
   * @returns Consent record
   */
  async manageMemberConsent(
    patientId: string,
    consentGiven: boolean
  ): Promise<MemberConsent> {
    const consentDate = new Date().toISOString();

    const fhirConsent: Consent = {
      resourceType: 'Consent',
      id: `consent-${patientId}-${Date.now()}`,
      status: consentGiven ? 'active' : 'inactive',
      scope: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/consentscope',
          code: 'patient-privacy',
          display: 'Privacy Consent'
        }]
      },
      category: [{
        coding: [{
          system: 'http://loinc.org',
          code: '59284-0',
          display: 'Consent Document'
        }]
      }],
      patient: {
        reference: `Patient/${patientId}`
      },
      dateTime: consentDate,
      organization: [{
        reference: `Organization/${this.config.sourcePayer}`
      }],
      policy: [{
        uri: 'https://www.cms.gov/regulations-and-guidance/cms-0057-f'
      }],
      provision: {
        type: consentGiven ? 'permit' : 'deny',
        period: {
          start: consentDate
        },
        actor: [{
          role: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/v3-ParticipationType',
              code: 'IRCP',
              display: 'Information Recipient'
            }]
          },
          reference: {
            reference: `Organization/${this.config.targetPayer}`
          }
        }],
        action: [{
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/consentaction',
            code: 'disclose',
            display: 'Disclose'
          }]
        }]
      }
    };

    return {
      patientId,
      sourcePayer: this.config.sourcePayer,
      targetPayer: this.config.targetPayer,
      status: consentGiven ? 'active' : 'inactive',
      consentDate,
      fhirConsent
    };
  }

  /**
   * Match member between payers using demographic data
   * Implements HL7 Da Vinci PDex IG member matching algorithm
   * 
   * @param request Member match request with patient demographics
   * @param candidatePatients Array of potential matches from target payer
   * @returns Match result with confidence score
   */
  async matchMember(
    request: MemberMatchRequest,
    candidatePatients: Patient[]
  ): Promise<MemberMatchResult> {
    const sourcePatient = request.patient;
    let bestMatch: { patient: Patient; score: number; matchedOn: string[] } | null = null;

    for (const candidate of candidatePatients) {
      const matchScore = this.calculateMatchScore(sourcePatient, candidate);
      
      if (!bestMatch || matchScore.score > bestMatch.score) {
        bestMatch = {
          patient: candidate,
          score: matchScore.score,
          matchedOn: matchScore.matchedOn
        };
      }
    }

    // Match threshold per Da Vinci PDex IG (configurable, typically 0.8)
    const matchThreshold = 0.8;
    const matched = bestMatch !== null && bestMatch.score >= matchThreshold;

    return {
      matched,
      confidence: bestMatch?.score || 0,
      matchedPatientId: matched ? bestMatch?.patient.id : undefined,
      matchedPatient: matched ? bestMatch?.patient : undefined,
      matchDetails: bestMatch ? {
        matchedOn: bestMatch.matchedOn,
        score: bestMatch.score
      } : undefined
    };
  }

  /**
   * Calculate match score between two patients
   * Uses weighted algorithm based on demographic attributes
   */
  private calculateMatchScore(
    patient1: Patient,
    patient2: Patient
  ): { score: number; matchedOn: string[] } {
    let score = 0;
    const matchedOn: string[] = [];
    const weights = {
      name: 0.25,
      birthDate: 0.25,
      gender: 0.15,
      identifier: 0.20,
      address: 0.10,
      telecom: 0.05
    };

    // Match on name
    if (patient1.name && patient2.name) {
      const name1 = patient1.name[0];
      const name2 = patient2.name[0];
      if (
        name1.family?.toLowerCase() === name2.family?.toLowerCase() &&
        name1.given?.[0]?.toLowerCase() === name2.given?.[0]?.toLowerCase()
      ) {
        score += weights.name;
        matchedOn.push('name');
      }
    }

    // Match on birth date
    if (patient1.birthDate === patient2.birthDate) {
      score += weights.birthDate;
      matchedOn.push('birthDate');
    }

    // Match on gender
    if (patient1.gender === patient2.gender) {
      score += weights.gender;
      matchedOn.push('gender');
    }

    // Match on identifier (SSN, member ID)
    if (patient1.identifier && patient2.identifier) {
      outerIdentifier: for (const id1 of patient1.identifier) {
        for (const id2 of patient2.identifier) {
          if (id1.value === id2.value && id1.system === id2.system) {
            score += weights.identifier;
            matchedOn.push('identifier');
            break outerIdentifier;
          }
        }
      }
    }

    // Match on address
    if (patient1.address && patient2.address) {
      const addr1 = patient1.address[0];
      const addr2 = patient2.address[0];
      if (
        addr1.postalCode === addr2.postalCode &&
        addr1.city?.toLowerCase() === addr2.city?.toLowerCase()
      ) {
        score += weights.address;
        matchedOn.push('address');
      }
    }

    // Match on telecom
    if (patient1.telecom && patient2.telecom) {
      outerTelecom: for (const tel1 of patient1.telecom) {
        for (const tel2 of patient2.telecom) {
          if (tel1.value === tel2.value) {
            score += weights.telecom;
            matchedOn.push('telecom');
            break outerTelecom;
          }
        }
      }
    }

    return { score, matchedOn };
  }

  /**
   * Convert FHIR resources to NDJSON format
   * For large datasets, consider processing in batches to reduce memory footprint
   */
  private convertToNDJSON(resources: any[]): string {
    return resources.map(r => JSON.stringify(r)).join('\n');
  }

  /**
   * Parse NDJSON format to FHIR resources
   * Includes error handling for malformed JSON lines
   */
  private parseNDJSON(ndjson: string): any[] {
    const resources: any[] = [];
    const lines = ndjson.split('\n').filter(line => line.trim());
    
    for (let i = 0; i < lines.length; i++) {
      try {
        const resource = JSON.parse(lines[i]);
        resources.push(resource);
      } catch (error) {
        // Skip malformed lines silently to avoid logging potential PHI
        // Error details not logged for security reasons
      }
    }
    
    return resources;
  }

  /**
   * Filter resources by date range
   */
  private filterByDateRange(
    resources: any[],
    startDate?: string,
    endDate?: string
  ): any[] {
    return resources.filter(resource => {
      // Extract date from resource (varies by resource type)
      let resourceDate: string | undefined;

      if (resource.meta?.lastUpdated) {
        resourceDate = resource.meta.lastUpdated;
      } else if (resource.period?.start) {
        resourceDate = resource.period.start;
      } else if (resource.billablePeriod?.start) {
        resourceDate = resource.billablePeriod.start;
      }

      if (!resourceDate) return true;

      if (startDate && resourceDate < startDate) return false;
      if (endDate && resourceDate > endDate) return false;

      return true;
    });
  }

  /**
   * Deduplicate resources by ID
   * Resources without IDs are treated as unique
   */
  private deduplicateResources(resources: any[]): {
    unique: any[];
    duplicates: number;
  } {
    const seen = new Set<string>();
    const unique: any[] = [];
    let duplicates = 0;

    for (const resource of resources) {
      // Resources without IDs are always treated as unique
      if (!resource.id) {
        unique.push(resource);
        continue;
      }

      const key = `${resource.resourceType}/${resource.id}`;
      if (!seen.has(key)) {
        seen.add(key);
        unique.push(resource);
      } else {
        duplicates++;
      }
    }

    return { unique, duplicates };
  }

  /**
   * Validate resources against US Core profiles
   */
  private validateUsCoreProfiles(
    resources: any[],
    resourceType: string
  ): OperationOutcome[] {
    const errors: OperationOutcome[] = [];

    // Basic US Core validation
    // In production, use a FHIR validator library
    for (const resource of resources) {
      // Check US Core profile is declared
      if (
        !resource.meta?.profile ||
        !resource.meta.profile.some((p: string) => p.includes('us-core'))
      ) {
        errors.push(
          this.createOperationOutcome(
            'warning',
            `Resource ${resource.id} missing US Core profile declaration`
          )
        );
      }

      // Validate required elements per US Core
      if (resourceType === 'Patient') {
        if (!resource.identifier || resource.identifier.length === 0) {
          errors.push(
            this.createOperationOutcome(
              'error',
              `Patient ${resource.id} missing required identifier`
            )
          );
        }
        if (!resource.name || resource.name.length === 0) {
          errors.push(
            this.createOperationOutcome(
              'error',
              `Patient ${resource.id} missing required name`
            )
          );
        }
        if (!resource.gender) {
          errors.push(
            this.createOperationOutcome(
              'error',
              `Patient ${resource.id} missing required gender`
            )
          );
        }
      }
    }

    return errors;
  }

  /**
   * Create FHIR OperationOutcome for errors
   */
  private createOperationOutcome(
    severity: 'error' | 'warning' | 'information',
    message: string
  ): OperationOutcome {
    return {
      resourceType: 'OperationOutcome',
      issue: [{
        severity,
        code: 'processing',
        diagnostics: message
      }]
    };
  }

  /**
   * Extract blob name from full URL
   */
  private extractBlobNameFromUrl(url: string): string {
    const urlParts = new URL(url);
    return urlParts.pathname.split('/').slice(2).join('/');
  }

  /**
   * Convert stream to string
   */
  private async streamToString(
    readableStream: NodeJS.ReadableStream
  ): Promise<string> {
    return new Promise((resolve, reject) => {
      const chunks: Buffer[] = [];
      readableStream.on('data', (data) => {
        chunks.push(data instanceof Buffer ? data : Buffer.from(data));
      });
      readableStream.on('end', () => {
        resolve(Buffer.concat(chunks).toString('utf8'));
      });
      readableStream.on('error', reject);
    });
  }

  /**
   * Generate unique export ID
   */
  private generateExportId(): string {
    return `export-${Date.now()}-${Math.random().toString(36).substring(7)}`;
  }

  /**
   * Generate unique import ID
   */
  private generateImportId(): string {
    return `import-${Date.now()}-${Math.random().toString(36).substring(7)}`;
  }

  /**
   * Close connections
   */
  async close(): Promise<void> {
    await this.serviceBusClient.close();
  }
}
