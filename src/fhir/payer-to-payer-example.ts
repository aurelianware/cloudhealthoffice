/**
 * Payer-to-Payer Data Exchange Example
 * 
 * Demonstrates complete workflow for CMS-0057-F compliant data exchange:
 * 1. Member consent registration
 * 2. Bulk data export
 * 3. Bulk data import
 * 4. Data reconciliation
 * 
 * Usage:
 *   npm run build
 *   node dist/src/fhir/payer-to-payer-example.js
 */

import {
  PayerToPayerAPI,
  PayerToPayerConfig,
  MemberConsent,
  BulkExportRequest,
  BulkImportRequest,
  generateSyntheticPatient,
  generateSyntheticClaim,
  validateUSCorePatient
} from './payer-to-payer-api';

/**
 * Example 1: Register Member Consent
 */
async function example1_RegisterConsent(api: PayerToPayerAPI) {
  console.log('\n=== Example 1: Register Member Consent ===\n');

  const consent: MemberConsent = {
    patientId: 'MEM123456',
    targetPayerId: 'PAYER002',
    consentDate: new Date('2024-01-15'),
    status: 'active',
    authorizedResourceTypes: [
      'Patient',
      'Claim',
      'Encounter',
      'ExplanationOfBenefit',
      'ServiceRequest'
    ]
  };

  await api.registerConsent(consent);
  console.log('✓ Consent registered successfully');

  // Verify consent
  const hasConsent = await api.hasConsent(
    'MEM123456',
    'PAYER002',
    ['Patient', 'Claim']
  );
  console.log(`✓ Consent validation: ${hasConsent ? 'AUTHORIZED' : 'DENIED'}`);
}

/**
 * Example 2: Register Multiple Members with Different Authorizations
 */
async function example2_RegisterMultipleConsents(api: PayerToPayerAPI) {
  console.log('\n=== Example 2: Register Multiple Member Consents ===\n');

  const consents: MemberConsent[] = [
    {
      patientId: 'MEM123456',
      targetPayerId: 'PAYER002',
      consentDate: new Date('2024-01-15'),
      status: 'active',
      authorizedResourceTypes: ['Patient', 'Claim', 'ExplanationOfBenefit']
    },
    {
      patientId: 'MEM789012',
      targetPayerId: 'PAYER002',
      consentDate: new Date('2024-01-20'),
      status: 'active',
      authorizedResourceTypes: ['Patient', 'Claim', 'Encounter', 'ExplanationOfBenefit', 'ServiceRequest']
    },
    {
      patientId: 'MEM345678',
      targetPayerId: 'PAYER002',
      consentDate: new Date('2023-12-01'),
      expirationDate: new Date('2024-12-31'),
      status: 'active',
      authorizedResourceTypes: ['Patient'] // Limited authorization
    }
  ];

  for (const consent of consents) {
    await api.registerConsent(consent);
    console.log(`✓ Registered consent for ${consent.patientId}`);
  }

  console.log(`\n✓ Total consents registered: ${consents.length}`);
}

/**
 * Example 3: Initiate Bulk Export
 */
async function example3_InitiateBulkExport(api: PayerToPayerAPI) {
  console.log('\n=== Example 3: Initiate Bulk Export ===\n');

  // First, register consents
  const patientIds = ['MEM123456', 'MEM789012', 'MEM345678'];
  
  for (const patientId of patientIds) {
    await api.registerConsent({
      patientId,
      targetPayerId: 'PAYER002',
      consentDate: new Date('2024-01-15'),
      status: 'active',
      authorizedResourceTypes: ['Patient', 'Claim', 'ExplanationOfBenefit', 'Encounter']
    });
  }

  console.log(`✓ Registered consents for ${patientIds.length} patients`);

  // Initiate export
  const exportRequest: BulkExportRequest = {
    exportId: `EXP-${Date.now()}`,
    patientIds: patientIds,
    resourceTypes: ['Patient', 'Claim', 'ExplanationOfBenefit'],
    since: new Date('2019-01-01'), // 5-year historical requirement
    until: new Date(),
    requestingPayerId: 'PAYER002'
  };

  try {
    const result = await api.initiateExport(exportRequest);
    console.log(`\n✓ Export initiated successfully`);
    console.log(`  Export ID: ${result.exportId}`);
    console.log(`  Status: ${result.status}`);
    console.log(`  Timestamp: ${result.timestamp.toISOString()}`);
  } catch (error) {
    console.error('✗ Export failed:', error);
  }
}

/**
 * Example 4: Consent Validation Scenarios
 */
async function example4_ConsentValidationScenarios(api: PayerToPayerAPI) {
  console.log('\n=== Example 4: Consent Validation Scenarios ===\n');

  // Scenario 1: Active consent with all resources
  await api.registerConsent({
    patientId: 'PAT001',
    targetPayerId: 'PAYER002',
    consentDate: new Date('2024-01-01'),
    status: 'active',
    authorizedResourceTypes: ['Patient', 'Claim', 'ExplanationOfBenefit']
  });

  const scenario1 = await api.hasConsent('PAT001', 'PAYER002', ['Patient', 'Claim']);
  console.log(`Scenario 1 (Active consent): ${scenario1 ? '✓ AUTHORIZED' : '✗ DENIED'}`);

  // Scenario 2: Revoked consent
  await api.registerConsent({
    patientId: 'PAT002',
    targetPayerId: 'PAYER002',
    consentDate: new Date('2024-01-01'),
    status: 'revoked',
    authorizedResourceTypes: ['Patient']
  });

  const scenario2 = await api.hasConsent('PAT002', 'PAYER002', ['Patient']);
  console.log(`Scenario 2 (Revoked consent): ${scenario2 ? '✓ AUTHORIZED' : '✗ DENIED'}`);

  // Scenario 3: Expired consent
  await api.registerConsent({
    patientId: 'PAT003',
    targetPayerId: 'PAYER002',
    consentDate: new Date('2023-01-01'),
    expirationDate: new Date('2023-12-31'),
    status: 'active',
    authorizedResourceTypes: ['Patient']
  });

  const scenario3 = await api.hasConsent('PAT003', 'PAYER002', ['Patient']);
  console.log(`Scenario 3 (Expired consent): ${scenario3 ? '✓ AUTHORIZED' : '✗ DENIED'}`);

  // Scenario 4: Unauthorized resource type
  await api.registerConsent({
    patientId: 'PAT004',
    targetPayerId: 'PAYER002',
    consentDate: new Date('2024-01-01'),
    status: 'active',
    authorizedResourceTypes: ['Patient'] // Only Patient authorized
  });

  const scenario4 = await api.hasConsent('PAT004', 'PAYER002', ['Patient', 'Claim']);
  console.log(`Scenario 4 (Unauthorized resource): ${scenario4 ? '✓ AUTHORIZED' : '✗ DENIED'}`);

  // Scenario 5: Wrong target payer
  const scenario5 = await api.hasConsent('PAT001', 'PAYER999', ['Patient']);
  console.log(`Scenario 5 (Wrong target payer): ${scenario5 ? '✓ AUTHORIZED' : '✗ DENIED'}`);

  console.log('\nExpected results:');
  console.log('  Scenario 1: ✓ AUTHORIZED');
  console.log('  Scenario 2: ✗ DENIED (revoked)');
  console.log('  Scenario 3: ✗ DENIED (expired)');
  console.log('  Scenario 4: ✗ DENIED (unauthorized resource)');
  console.log('  Scenario 5: ✗ DENIED (wrong payer)');
}

/**
 * Example 5: Generate and Validate US Core Patient
 */
async function example5_GenerateAndValidatePatient() {
  console.log('\n=== Example 5: Generate and Validate US Core Patient ===\n');

  // Generate synthetic patient
  const patient = generateSyntheticPatient('TEST001');
  console.log('✓ Generated synthetic patient:');
  console.log(`  ID: ${patient.id}`);
  console.log(`  Name: ${patient.name?.[0]?.given?.[0]} ${patient.name?.[0]?.family}`);
  console.log(`  Gender: ${patient.gender}`);
  console.log(`  Birth Date: ${patient.birthDate}`);
  console.log(`  Identifiers: ${patient.identifier?.length || 0}`);

  // Validate US Core compliance
  const validation = validateUSCorePatient(patient);
  console.log(`\n✓ US Core validation: ${validation.valid ? 'PASS' : 'FAIL'}`);
  
  if (!validation.valid) {
    console.log('  Errors:');
    validation.errors.forEach(error => console.log(`    - ${error}`));
  }

  // Show profile
  console.log(`\n✓ Profile: ${patient.meta?.profile?.[0]}`);
}

/**
 * Example 6: Generate Synthetic Claim with Patient Reference
 */
async function example6_GenerateSyntheticClaim() {
  console.log('\n=== Example 6: Generate Synthetic Claim ===\n');

  const patientId = 'TEST002';
  const claimId = 'CLM001';

  // Generate patient first
  const patient = generateSyntheticPatient(patientId);
  console.log(`✓ Generated patient: ${patient.id}`);

  // Generate claim for patient
  const claim = generateSyntheticClaim(patientId, claimId);
  console.log(`✓ Generated claim:`);
  console.log(`  ID: ${claim.id}`);
  console.log(`  Patient Reference: ${claim.patient.reference}`);
  console.log(`  Status: ${claim.status}`);
  console.log(`  Type: ${claim.type.coding?.[0]?.code}`);
  console.log(`  Total: ${claim.total?.value} ${claim.total?.currency}`);
  console.log(`  Created: ${claim.created}`);
}

/**
 * Example 7: Bulk Import with Reconciliation
 */
async function example7_InitiateBulkImport(api: PayerToPayerAPI) {
  console.log('\n=== Example 7: Initiate Bulk Import with Reconciliation ===\n');

  const importRequest: BulkImportRequest = {
    importId: `IMP-${Date.now()}`,
    ndjsonBlobUrls: [
      'exports/EXP-20240115-001/Patient.ndjson',
      'exports/EXP-20240115-001/Claim.ndjson',
      'exports/EXP-20240115-001/ExplanationOfBenefit.ndjson'
    ],
    sourcePayerId: 'PAYER001',
    enableReconciliation: true
  };

  try {
    const result = await api.initiateImport(importRequest);
    console.log('✓ Import initiated successfully');
    console.log(`  Import ID: ${result.importId}`);
    console.log(`  Status: ${result.status}`);
    console.log(`  Reconciliation: ${importRequest.enableReconciliation ? 'ENABLED' : 'DISABLED'}`);
    console.log(`  Source Payer: ${importRequest.sourcePayerId}`);
  } catch (error) {
    console.error('✗ Import failed:', error);
  }
}

/**
 * Example 8: Complete Payer-to-Payer Exchange Flow
 */
async function example8_CompleteExchangeFlow(api: PayerToPayerAPI) {
  console.log('\n=== Example 8: Complete Payer-to-Payer Exchange Flow ===\n');

  // Step 1: Member requests data transfer from old payer to new payer
  console.log('Step 1: Member requests data transfer');
  const memberId = 'MEM999888';
  const oldPayerId = 'PAYER001';
  const newPayerId = 'PAYER002';

  // Step 2: Old payer registers member consent
  console.log('Step 2: Old payer registers member consent');
  await api.registerConsent({
    patientId: memberId,
    targetPayerId: newPayerId,
    consentDate: new Date(),
    status: 'active',
    authorizedResourceTypes: ['Patient', 'Claim', 'Encounter', 'ExplanationOfBenefit', 'ServiceRequest']
  });
  console.log('  ✓ Consent registered');

  // Step 3: New payer initiates export request to old payer
  console.log('Step 3: New payer initiates export request');
  const exportRequest: BulkExportRequest = {
    exportId: `EXP-${Date.now()}`,
    patientIds: [memberId],
    resourceTypes: ['Patient', 'Claim', 'ExplanationOfBenefit'],
    since: new Date('2019-01-01'), // 5 years of history
    until: new Date(),
    requestingPayerId: newPayerId
  };

  try {
    const exportResult = await api.initiateExport(exportRequest);
    console.log('  ✓ Export initiated');
    console.log(`    Export ID: ${exportResult.exportId}`);

    // Step 4: Old payer processes export (async)
    console.log('Step 4: Old payer processes export asynchronously');
    console.log('  ✓ Export queued to Service Bus');

    // Step 5: New payer receives export completion notification
    console.log('Step 5: New payer notified of export completion');
    console.log('  ✓ NDJSON files available in Azure Data Lake');

    // Step 6: New payer initiates import
    console.log('Step 6: New payer initiates import');
    const importRequest: BulkImportRequest = {
      importId: `IMP-${Date.now()}`,
      ndjsonBlobUrls: [
        `exports/${exportResult.exportId}/Patient.ndjson`,
        `exports/${exportResult.exportId}/Claim.ndjson`,
        `exports/${exportResult.exportId}/ExplanationOfBenefit.ndjson`
      ],
      sourcePayerId: oldPayerId,
      enableReconciliation: true
    };

    const importResult = await api.initiateImport(importRequest);
    console.log('  ✓ Import initiated');
    console.log(`    Import ID: ${importResult.importId}`);

    // Step 7: New payer processes import with reconciliation
    console.log('Step 7: New payer processes import with reconciliation');
    console.log('  ✓ Import queued to Service Bus');
    console.log('  ✓ Duplicate detection enabled');

    console.log('\n✓ Complete payer-to-payer exchange flow initiated successfully!');
  } catch (error) {
    console.error('✗ Exchange flow failed:', error);
  }
}

/**
 * Main execution
 */
async function main() {
  console.log('╔════════════════════════════════════════════════════════════════╗');
  console.log('║     CMS-0057-F Payer-to-Payer Data Exchange Examples          ║');
  console.log('║     Cloud Health Office - FHIR R4 Bulk Data Access            ║');
  console.log('╚════════════════════════════════════════════════════════════════╝');

  // Initialize API with test configuration
  const config: PayerToPayerConfig = {
    // Note: These are mock connections for demonstration
    serviceBusConnectionString: 'Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=test',
    storageConnectionString: 'UseDevelopmentStorage=true',
    storageContainerName: 'p2p-bulk-data',
    exportRequestTopic: 'export-requests',
    importRequestTopic: 'import-requests',
    fhirServerBaseUrl: 'https://fhir.example-payer.com',
    payerOrganizationId: 'PAYER001'
  };

  const api = new PayerToPayerAPI(config);

  try {
    // Run examples
    await example1_RegisterConsent(api);
    await example2_RegisterMultipleConsents(api);
    await example3_InitiateBulkExport(api);
    await example4_ConsentValidationScenarios(api);
    await example5_GenerateAndValidatePatient();
    await example6_GenerateSyntheticClaim();
    await example7_InitiateBulkImport(api);
    await example8_CompleteExchangeFlow(api);

    console.log('\n╔════════════════════════════════════════════════════════════════╗');
    console.log('║              All Examples Completed Successfully!             ║');
    console.log('╚════════════════════════════════════════════════════════════════╝\n');

    console.log('📚 Next Steps:');
    console.log('  1. Configure Azure Service Bus and Storage in production');
    console.log('  2. Implement persistent consent storage (database)');
    console.log('  3. Integrate with FHIR server for resource queries');
    console.log('  4. Set up Service Bus workers for async processing');
    console.log('  5. Generate synthetic bulk data: npm run generate:synthetic-bulk');
    console.log('  6. Run tests: npm run test:p2p\n');

  } catch (error) {
    console.error('Error running examples:', error);
  } finally {
    await api.close();
  }
}

// Run if executed directly
if (require.main === module) {
  main().catch(console.error);
}

export {
  example1_RegisterConsent,
  example2_RegisterMultipleConsents,
  example3_InitiateBulkExport,
  example4_ConsentValidationScenarios,
  example5_GenerateAndValidatePatient,
  example6_GenerateSyntheticClaim,
  example7_InitiateBulkImport,
  example8_CompleteExchangeFlow
};
