/**
 * Provider Access API Examples
 * 
 * Demonstrates how to use the Provider Access API for CMS-0057-F compliance.
 * These examples show real-world scenarios for provider data access.
 */

import {
  createProviderAccessApi,
  QnxtPatient,
  QnxtClaim,
  QnxtEncounter
} from './provider-access-api';

/**
 * Example 1: Basic Patient Search
 * Provider searches for a patient's demographic information
 */
async function examplePatientSearch() {
  console.log('\n=== Example 1: Patient Search ===\n');
  
  const api = createProviderAccessApi();
  const providerToken = 'provider:NPI12345:sample-token-abc123';
  const patientId = 'PAT001';

  try {
    // Search for patient
    const bundle = await api.searchResources(
      'Patient',
      { resourceType: 'Patient', patient: patientId },
      providerToken
    );

    console.log(`Found ${bundle.total} patient(s)`);
    console.log('Patient Resource:', JSON.stringify(bundle.entry![0].resource, null, 2));
  } catch (error) {
    console.error('Error:', error);
  }
}

/**
 * Example 2: Claim History Search
 * Provider retrieves patient's claim history
 */
async function exampleClaimSearch() {
  console.log('\n=== Example 2: Claim History Search ===\n');
  
  const api = createProviderAccessApi();
  const providerToken = 'provider:NPI12345:sample-token-abc123';
  const patientId = 'PAT001';

  try {
    // Search for claims
    const bundle = await api.searchResources(
      'Claim',
      { resourceType: 'Claim', patient: patientId },
      providerToken
    );

    console.log(`Found ${bundle.total} claim(s)`);
    bundle.entry?.forEach((entry, index) => {
      const claim = entry.resource as any;
      console.log(`Claim ${index + 1}:`, {
        id: claim.id,
        status: claim.status,
        total: claim.total
      });
    });
  } catch (error) {
    console.error('Error:', error);
  }
}

/**
 * Example 3: Clinical Data Search (USCDI)
 * Provider retrieves patient's clinical conditions
 */
async function exampleConditionSearch() {
  console.log('\n=== Example 3: Clinical Conditions Search ===\n');
  
  const api = createProviderAccessApi();
  const providerToken = 'provider:NPI12345:sample-token-abc123';
  const patientId = 'PAT001';

  try {
    // Search for active conditions
    const bundle = await api.searchResources(
      'Condition',
      { 
        resourceType: 'Condition', 
        patient: patientId,
        'clinical-status': 'active'
      },
      providerToken
    );

    console.log(`Found ${bundle.total} active condition(s)`);
    bundle.entry?.forEach((entry, index) => {
      const condition = entry.resource as any;
      console.log(`Condition ${index + 1}:`, {
        id: condition.id,
        code: condition.code.coding[0].display,
        onsetDate: condition.onsetDateTime
      });
    });
  } catch (error) {
    console.error('Error:', error);
  }
}

/**
 * Example 4: Laboratory Results Search
 * Provider retrieves patient's lab observations
 */
async function exampleObservationSearch() {
  console.log('\n=== Example 4: Laboratory Results Search ===\n');
  
  const api = createProviderAccessApi();
  const providerToken = 'provider:NPI12345:sample-token-abc123';
  const patientId = 'PAT001';

  try {
    // Search for lab observations
    const bundle = await api.searchResources(
      'Observation',
      { 
        resourceType: 'Observation', 
        patient: patientId,
        category: 'laboratory'
      },
      providerToken
    );

    console.log(`Found ${bundle.total} lab result(s)`);
    bundle.entry?.forEach((entry, index) => {
      const obs = entry.resource as any;
      console.log(`Lab ${index + 1}:`, {
        id: obs.id,
        test: obs.code.coding[0].display,
        value: `${obs.valueQuantity.value} ${obs.valueQuantity.unit}`,
        date: obs.effectiveDateTime
      });
    });
  } catch (error) {
    console.error('Error:', error);
  }
}

/**
 * Example 5: Read Specific Resource
 * Provider reads a specific patient resource by ID
 */
async function exampleReadResource() {
  console.log('\n=== Example 5: Read Specific Patient ===\n');
  
  const api = createProviderAccessApi();
  const providerToken = 'provider:NPI12345:sample-token-abc123';
  const patientId = 'PAT001';

  try {
    // Read specific patient
    const patient = await api.readResource('Patient', patientId, providerToken);

    console.log('Patient Details:', JSON.stringify(patient, null, 2));
  } catch (error) {
    console.error('Error:', error);
  }
}

/**
 * Example 6: Authentication Error Handling
 * Demonstrates handling invalid token errors
 */
async function exampleAuthError() {
  console.log('\n=== Example 6: Authentication Error Handling ===\n');
  
  const api = createProviderAccessApi();
  const invalidToken = ''; // Invalid token

  try {
    await api.searchResources(
      'Patient',
      { resourceType: 'Patient', patient: 'PAT001' },
      invalidToken
    );
  } catch (error: any) {
    console.log('Expected Error:', error.name);
    console.log('Error Message:', error.message);
    
    // Handle authentication error
    const response = api.handleAuthError(error);
    console.log('HTTP Status:', response.status);
    console.log('OperationOutcome:', JSON.stringify(response.body, null, 2));
  }
}

/**
 * Example 7: Backend Data Mapping
 * Transform QNXT patient data to FHIR Patient
 */
function exampleQnxtMapping() {
  console.log('\n=== Example 7: QNXT to FHIR Mapping ===\n');
  
  const api = createProviderAccessApi();

  // Sample QNXT patient data from backend
  const qnxtPatient: QnxtPatient = {
    memberId: 'MEM789',
    firstName: 'Sarah',
    lastName: 'Johnson',
    middleName: 'Ann',
    dob: '19920315',
    gender: 'F',
    address: {
      street1: '789 Pine Street',
      street2: 'Suite 200',
      city: 'Seattle',
      state: 'WA',
      zip: '98101'
    },
    phone: '206-555-7890',
    email: 'sarah.johnson@example.com'
  };

  // Transform to FHIR Patient
  const fhirPatient = api.mapQnxtPatientToFhir(qnxtPatient);

  console.log('QNXT Patient:', qnxtPatient);
  console.log('\nFHIR Patient:', JSON.stringify(fhirPatient, null, 2));
}

/**
 * Example 8: PHI Encryption
 * Demonstrates encrypting and decrypting PHI
 */
function exampleEncryption() {
  console.log('\n=== Example 8: PHI Encryption ===\n');
  
  const api = createProviderAccessApi();

  // Sensitive PHI data
  const phi = 'Patient: John Doe, SSN: 123-45-6789, DOB: 1980-01-01';
  
  console.log('Original PHI:', phi);

  // Encrypt
  const encrypted = api.encryptPhi(phi);
  console.log('Encrypted:', encrypted.substring(0, 50) + '...');

  // Decrypt
  const decrypted = api.decryptPhi(encrypted);
  console.log('Decrypted:', decrypted);
  
  console.log('\nEncryption verified:', phi === decrypted);
}

/**
 * Example 9: PHI Redaction for Logging
 * Demonstrates redacting PHI before logging
 */
function exampleRedaction() {
  console.log('\n=== Example 9: PHI Redaction ===\n');
  
  const api = createProviderAccessApi();

  const qnxtPatient: QnxtPatient = {
    memberId: 'MEM456',
    firstName: 'Michael',
    lastName: 'Brown',
    dob: '19750820',
    gender: 'M',
    address: {
      street1: '456 Oak Avenue',
      city: 'Boston',
      state: 'MA',
      zip: '02101'
    },
    phone: '617-555-4567',
    email: 'michael.brown@example.com'
  };

  const fhirPatient = api.mapQnxtPatientToFhir(qnxtPatient);
  
  console.log('Original Patient:', JSON.stringify(fhirPatient, null, 2));

  // Redact PHI for safe logging
  const redacted = api.redactPhi(fhirPatient);
  
  console.log('\nRedacted Patient (safe for logs):', JSON.stringify(redacted, null, 2));
}

/**
 * Example 10: Audit Log Review
 * Review audit logs for compliance reporting
 */
async function exampleAuditLogs() {
  console.log('\n=== Example 10: Audit Log Review ===\n');
  
  const api = createProviderAccessApi();
  const providerToken = 'provider:NPI12345:sample-token-abc123';

  // Perform some operations
  await api.searchResources(
    'Patient',
    { resourceType: 'Patient', patient: 'PAT001' },
    providerToken
  );

  await api.readResource('Patient', 'PAT001', providerToken);

  // Get audit logs
  const auditLogs = api.getAuditLogs();
  
  console.log(`Total audit log entries: ${auditLogs.length}\n`);
  
  auditLogs.forEach((log, index) => {
    console.log(`Log ${index + 1}:`, {
      timestamp: log.timestamp,
      eventType: log.eventType,
      userId: log.userId,
      result: log.result
    });
  });
}

/**
 * Run all examples
 */
async function runAllExamples() {
  console.log('╔════════════════════════════════════════════════════════════╗');
  console.log('║  Provider Access API Examples (CMS-0057-F Compliance)      ║');
  console.log('╚════════════════════════════════════════════════════════════╝');

  await examplePatientSearch();
  await exampleClaimSearch();
  await exampleConditionSearch();
  await exampleObservationSearch();
  await exampleReadResource();
  await exampleAuthError();
  exampleQnxtMapping();
  exampleEncryption();
  exampleRedaction();
  await exampleAuditLogs();

  console.log('\n✅ All examples completed successfully!\n');
}

// Run examples if this file is executed directly
if (require.main === module) {
  runAllExamples().catch(console.error);
}

export {
  examplePatientSearch,
  exampleClaimSearch,
  exampleConditionSearch,
  exampleObservationSearch,
  exampleReadResource,
  exampleAuthError,
  exampleQnxtMapping,
  exampleEncryption,
  exampleRedaction,
  exampleAuditLogs,
  runAllExamples
};
