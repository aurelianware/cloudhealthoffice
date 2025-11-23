/**
 * Secure FHIR R4 Integration Examples
 * 
 * These examples use only @types/fhir and native fetch (no vulnerable dependencies)
 * Recommended for production use.
 */

import { Patient, CoverageEligibilityRequest, Bundle } from 'fhir/r4';
import { mapX12270ToFhirEligibility } from './fhirEligibilityMapper';
import { X12_270 } from './x12Types';

/**
 * FHIR Client using native fetch (no external dependencies)
 * Safe for production use
 */
export class SecureFhirClient {
  private baseUrl: string;
  private bearerToken: string;

  constructor(baseUrl: string, bearerToken: string) {
    // Validate HTTPS
    if (!baseUrl.startsWith('https://')) {
      throw new Error('FHIR server URL must use HTTPS');
    }
    this.baseUrl = baseUrl.endsWith('/') ? baseUrl.slice(0, -1) : baseUrl;
    this.bearerToken = bearerToken;
  }

  /**
   * Create a FHIR resource on the server
   */
  async create<T extends Patient | CoverageEligibilityRequest>(
    resource: T
  ): Promise<T> {
    const url = `${this.baseUrl}/${resource.resourceType}`;
    
    const response = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/fhir+json',
        'Authorization': `Bearer ${this.bearerToken}`,
        'Accept': 'application/fhir+json'
      },
      body: JSON.stringify(resource)
    });

    if (!response.ok) {
      const errorText = await response.text();
      throw new Error(`FHIR server error (${response.status}): ${errorText}`);
    }

    return response.json() as Promise<T>;
  }

  /**
   * Read a FHIR resource by ID
   */
  async read<T extends Patient | CoverageEligibilityRequest>(
    resourceType: string,
    id: string
  ): Promise<T> {
    const url = `${this.baseUrl}/${resourceType}/${id}`;
    
    const response = await fetch(url, {
      method: 'GET',
      headers: {
        'Authorization': `Bearer ${this.bearerToken}`,
        'Accept': 'application/fhir+json'
      }
    });

    if (!response.ok) {
      throw new Error(`Resource not found: ${resourceType}/${id}`);
    }

    return response.json() as Promise<T>;
  }

  /**
   * Search for FHIR resources
   */
  async search(
    resourceType: string,
    params: Record<string, string>
  ): Promise<Bundle> {
    const queryString = new URLSearchParams(params).toString();
    const url = `${this.baseUrl}/${resourceType}?${queryString}`;
    
    const response = await fetch(url, {
      method: 'GET',
      headers: {
        'Authorization': `Bearer ${this.bearerToken}`,
        'Accept': 'application/fhir+json'
      }
    });

    if (!response.ok) {
      throw new Error(`Search failed: ${response.statusText}`);
    }

    return response.json() as Promise<Bundle>;
  }

  /**
   * Update a FHIR resource
   */
  async update<T extends Patient | CoverageEligibilityRequest>(
    resource: T
  ): Promise<T> {
    if (!resource.id) {
      throw new Error('Resource must have an ID for update');
    }

    const url = `${this.baseUrl}/${resource.resourceType}/${resource.id}`;
    
    const response = await fetch(url, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/fhir+json',
        'Authorization': `Bearer ${this.bearerToken}`,
        'Accept': 'application/fhir+json'
      },
      body: JSON.stringify(resource)
    });

    if (!response.ok) {
      throw new Error(`Update failed: ${response.statusText}`);
    }

    return response.json() as Promise<T>;
  }
}

/**
 * Example 1: Secure X12 to FHIR transformation and storage
 */
export async function secureExample_transformAndStore(
  x12Data: X12_270,
  fhirServerUrl: string,
  accessToken: string
): Promise<{ patientId: string; eligibilityId: string }> {
  
  console.log('=== Secure Example: Transform and Store ===\n');
  
  // Transform X12 to FHIR (no vulnerable dependencies)
  const { patient, eligibility } = mapX12270ToFhirEligibility(x12Data);
  
  // Create secure FHIR client
  const client = new SecureFhirClient(fhirServerUrl, accessToken);
  
  // Store Patient resource
  console.log('Creating Patient resource...');
  const createdPatient = await client.create(patient);
  console.log(`✅ Patient created: ${createdPatient.id}`);
  
  // Store CoverageEligibilityRequest
  console.log('Creating CoverageEligibilityRequest...');
  const createdEligibility = await client.create(eligibility);
  console.log(`✅ Eligibility request created: ${createdEligibility.id}`);
  
  return {
    patientId: createdPatient.id!,
    eligibilityId: createdEligibility.id!
  };
}

/**
 * Example 2: Search for existing patient by member ID
 */
export async function secureExample_searchPatient(
  memberId: string,
  fhirServerUrl: string,
  accessToken: string
): Promise<Patient | null> {
  
  console.log('=== Secure Example: Search Patient ===\n');
  
  const client = new SecureFhirClient(fhirServerUrl, accessToken);
  
  // Search by identifier
  console.log(`Searching for patient with member ID: ${memberId}`);
  const bundle = await client.search('Patient', {
    identifier: memberId
  });
  
  if (bundle.entry && bundle.entry.length > 0) {
    const patient = bundle.entry[0].resource as Patient;
    console.log(`✅ Found patient: ${patient.id}`);
    console.log(`   Name: ${patient.name?.[0].given?.[0]} ${patient.name?.[0].family}`);
    return patient;
  }
  
  console.log('❌ No patient found');
  return null;
}

/**
 * Example 3: Update patient contact information
 */
export async function secureExample_updatePatient(
  patientId: string,
  newPhone: string,
  fhirServerUrl: string,
  accessToken: string
): Promise<void> {
  
  console.log('=== Secure Example: Update Patient ===\n');
  
  const client = new SecureFhirClient(fhirServerUrl, accessToken);
  
  // Read existing patient
  console.log(`Reading patient ${patientId}...`);
  const patient = await client.read<Patient>('Patient', patientId);
  
  // Update phone number
  patient.telecom = patient.telecom || [];
  patient.telecom.push({
    system: 'phone',
    value: newPhone,
    use: 'mobile'
  });
  
  // Save update
  console.log('Updating patient...');
  await client.update(patient);
  console.log(`✅ Patient ${patientId} updated`);
}

/**
 * Example 4: Azure Managed Identity authentication (production pattern)
 */
export async function secureExample_azureManagedIdentity(
  x12Data: X12_270
): Promise<void> {
  
  console.log('=== Secure Example: Azure Managed Identity ===\n');
  
  // In production, use Azure SDK with managed identity
  // This example shows the pattern (requires @azure/identity package)
  
  console.log('Using Azure Managed Identity for authentication...');
  
  // Get token using managed identity (no secrets in code)
  const token = await getAzureManagedIdentityToken();
  
  const fhirServerUrl = 'https://your-fhir-server.azurehealthcareapis.com';
  const client = new SecureFhirClient(fhirServerUrl, token);
  
  // Transform and store
  const { patient, eligibility } = mapX12270ToFhirEligibility(x12Data);
  
  await client.create(patient);
  await client.create(eligibility);
  
  console.log('✅ Resources created using managed identity');
}

/**
 * Helper: Get Azure managed identity token
 * In production, use @azure/identity package
 */
async function getAzureManagedIdentityToken(): Promise<string> {
  // Production implementation would use:
  // import { DefaultAzureCredential } from '@azure/identity';
  // const credential = new DefaultAzureCredential();
  // const token = await credential.getToken('https://<your-workspace-name>.fhir.azurehealthcareapis.com/.default');
  // return token.token;
  
  // For this example, return a placeholder
  console.log('(In production: use DefaultAzureCredential from @azure/identity)');
  return 'managed-identity-token-placeholder';
}

/**
 * Example 5: Batch processing with error handling
 */
export async function secureExample_batchProcessing(
  inquiries: X12_270[],
  fhirServerUrl: string,
  accessToken: string
): Promise<{ success: number; failed: number }> {
  
  console.log('=== Secure Example: Batch Processing ===\n');
  
  const client = new SecureFhirClient(fhirServerUrl, accessToken);
  
  let successCount = 0;
  let failedCount = 0;
  
  for (const inquiry of inquiries) {
    try {
      const { patient, eligibility } = mapX12270ToFhirEligibility(inquiry);
      
      await client.create(patient);
      await client.create(eligibility);
      
      console.log(`✅ Processed inquiry ${inquiry.inquiryId}`);
      successCount++;
      
    } catch (error) {
      console.error(`❌ Failed inquiry ${inquiry.inquiryId}:`, error);
      failedCount++;
    }
  }
  
  console.log(`\nBatch complete: ${successCount} success, ${failedCount} failed`);
  return { success: successCount, failed: failedCount };
}

/**
 * Example 6: Validate FHIR resource before sending
 */
export function secureExample_validateResource(patient: Patient): boolean {
  console.log('=== Secure Example: Resource Validation ===\n');
  
  // Basic validation (in production, use FHIR validator)
  const errors: string[] = [];
  
  if (!patient.resourceType || patient.resourceType !== 'Patient') {
    errors.push('Invalid resourceType');
  }
  
  if (!patient.id) {
    errors.push('Missing required field: id');
  }
  
  if (!patient.name || patient.name.length === 0) {
    errors.push('Missing required field: name');
  }
  
  if (!patient.birthDate) {
    errors.push('Missing required field: birthDate');
  }
  
  if (errors.length > 0) {
    console.error('❌ Validation failed:');
    errors.forEach(err => console.error(`   - ${err}`));
    return false;
  }
  
  console.log('✅ Resource is valid');
  return true;
}

/**
 * Demo: Run all secure examples
 */
export async function runSecureExamples(): Promise<void> {
  console.log('╔════════════════════════════════════════════════════════════════╗');
  console.log('║  Cloud Health Office - Secure FHIR Examples                   ║');
  console.log('║  No vulnerable dependencies - Production ready                 ║');
  console.log('╚════════════════════════════════════════════════════════════════╝\n');
  
  // Sample X12 data
  const sampleX12: X12_270 = {
    inquiryId: 'SECURE-001',
    transactionDate: '20240115-1000',
    informationSource: {
      id: '030240928',
      name: 'Secure Health Plan'
    },
    subscriber: {
      memberId: 'SEC123456',
      firstName: 'Secure',
      lastName: 'Patient',
      dob: '1985-01-01',
      gender: 'F'
    },
    insurerId: 'SECUREPLAN'
  };
  
  // Example 1: Transform (always works, no network needed)
  console.log('Example 1: Transform X12 to FHIR');
  const { patient, eligibility } = mapX12270ToFhirEligibility(sampleX12);
  console.log(`✅ Transformed: Patient ${patient.id}, Eligibility ${eligibility.id}\n`);
  
  // Example 2: Validate
  secureExample_validateResource(patient);
  console.log();
  
  // Network examples would require actual FHIR server
  console.log('Note: Network examples require actual FHIR server configuration');
  console.log('See secureExamples.ts for implementation patterns\n');
  
  console.log('╔════════════════════════════════════════════════════════════════╗');
  console.log('║  Secure examples demonstrated successfully!                    ║');
  console.log('║  ✅ No vulnerable dependencies used                            ║');
  console.log('╚════════════════════════════════════════════════════════════════╝\n');
}

// Allow running this file directly
if (require.main === module) {
  runSecureExamples().catch(console.error);
}
